using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 map-size cap.
    ///
    /// A banded map is THREE playable bands stacked in one Map, and RimWorld 1.6's
    /// pathfinding grid job is an IJobParallelFor over every cell of the map. A 200-wide
    /// colony therefore costs ~120k cells; 325 costs ~317k, on a hot per-request path. This
    /// is the one place V2 is measurably worse than V1, where only maps with pathing pawns
    /// paid at all.
    ///
    /// So colony maps are capped at 200x200 by default. The cap is enforced in TWO places
    /// deliberately: at the map-size chooser (so the player is told, not silently
    /// overridden) and again at generation (so nothing - a scenario, another mod, a loaded
    /// config - can slip past it).
    /// </summary>
    public static class ABMapSizeLimit
    {
        /// <summary>
        /// The offered sizes, and why these three specifically.
        ///
        /// Slot is `ceil((bandHeight + MinGutter) / 64) * 64`, so cost does NOT rise
        /// smoothly with size - it steps. A size just past a 64 boundary pays a whole extra
        /// 64 rows of dead gutter PER BAND for nothing. These are the three sizes that sit
        /// immediately under a boundary, where the gutter collapses to its 2-row minimum:
        ///
        ///   126 -> slot 128, gutter 2   ->  48,384 cells  (1.6% wasted)
        ///   190 -> slot 192, gutter 2   -> 109,440 cells  (1.0% wasted)
        ///   254 -> slot 256, gutter 2   -> 195,072 cells  (0.8% wasted)
        ///
        /// The old 200x200 cap was one of the WORST points on that curve: slot 256, gutter
        /// 56, 153,600 cells of which 33,600 are empty seam - 21.9% wasted. Dropping the cap
        /// to 190 gives up 9.7% of playable area and saves 28.8% of the cells. Intermediate
        /// sizes are never worth offering: 150x150 costs 86,400 for 67,500 playable, while
        /// 190x190 costs 26% more and yields 60% more playable area.
        ///
        /// This matters because 1.6's PathGridJob is an IJobParallelFor over EVERY cell of
        /// the map, so the stacked total - not the per-level size - is what the pathfinder
        /// pays on a hot per-request path.
        /// </summary>
        public static readonly int[] Sizes = { 126, 190, 254 };

        // ================= THE LEVEL PLAN =================
        // Levels are chosen per colony on the advanced-config screen, and the constraint is
        // a TOTAL CELL BUDGET rather than a maximum per-level size. That is the honest
        // model: 1.6's path grid is an IJobParallelFor over EVERY cell of the map, so the
        // stacked total is what the pathfinder pays - a player picking 7 levels is not
        // asking for 7x the cost, they are asking for the same budget sliced differently.
        //
        // The arithmetic that makes this work (heights sit just under a 64 boundary so the
        // gutter collapses to 2 rows):
        //   3 x 190 = 109,440   (the historical default)
        //   5 x 126 =  80,640
        //   7 x 126 = 112,896   <- seven levels for the price of three
        //   7 x 190 = 255,360   <- the footgun the budget exists to refuse
        // ==================================================

        public const int MaxUpperLevels = 3;

        public const int MaxLowerLevels = 3;

        /// <summary>Total cells a banded colony may allocate. Sized so the historical
        /// 3x190 layout and a 7x126 layout both fit, and 5x190 / 3x254 do not.</summary>
        public const int CellBudget = 115000;

        public static int UpperLevels =>
            Mathf.Clamp(ABMod.Settings?.upperLevels ?? 1, 0, MaxUpperLevels);

        public static int LowerLevels =>
            Mathf.Clamp(ABMod.Settings?.lowerLevels ?? 1, 0, MaxLowerLevels);

        /// <summary>Bands per column, surface included. 1 means an ordinary unbanded map.</summary>
        public static int BandCount => UpperLevels + LowerLevels + 1;

        /// <summary>Bands stack along +z from the deepest basement, so the surface index is
        /// simply the number of levels below it.</summary>
        public static int SurfaceBand => LowerLevels;

        public static bool Active => ABV2.Enabled && !(ABMod.Settings?.unclampMapSize ?? false);

        /// <summary>Total cells RimWorld actually allocates and paths over for a banded
        /// colony of this per-level size at a given level count.</summary>
        public static int StackedCells(int size, int bandCount)
        {
            return size * bandCount * ABBandMap.SlotFor(size);
        }

        public static int StackedCells(int size)
        {
            return StackedCells(size, BandCount);
        }

        /// <summary>Does this per-level size fit the budget at this level count? Always
        /// true when the player has lifted the cap.</summary>
        public static bool Fits(int size, int bandCount)
        {
            return !Active || StackedCells(size, bandCount) <= CellBudget;
        }

        /// <summary>Largest offered per-level size that fits the budget at the current
        /// level count. Replaces the old fixed 190 cap: with more levels selected the
        /// affordable per-level size genuinely shrinks, and the UI says so.</summary>
        public static int MaxSize
        {
            get
            {
                int best = Sizes[0];
                for (int i = 0; i < Sizes.Length; i++)
                {
                    if (Fits(Sizes[i], BandCount) && Sizes[i] > best)
                    {
                        best = Sizes[i];
                    }
                }
                return best;
            }
        }

        /// <summary>Labels of the size options that are locked, rebuilt each time the
        /// chooser opens. Matching on the exact rendered label rather than parsing keeps
        /// this robust against localisation.</summary>
        private static readonly HashSet<string> lockedLabels = new HashSet<string>();

        /// <summary>Vanilla label -> our relabelled version, carrying the stacked cost.</summary>
        private static readonly Dictionary<string, string> relabel = new Dictionary<string, string>();

        private static bool inChooser;

        /// <summary>Vanilla's own array, restored when the dialog closes.</summary>
        private static int[] savedSizes;

        private static readonly FieldInfo MapSizesField =
            AccessTools.Field(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.MapSizes));

        public static void BeginChooser()
        {
            lockedLabels.Clear();
            relabel.Clear();
            inChooser = true;
            if (!Active)
            {
                return;
            }

            // Swap vanilla's size list for ours FOR THE DURATION OF THE DIALOG ONLY.
            // MapSizes is `static readonly int[]`, so the reference is settable by
            // reflection; scoping the swap to the dialog means anything else that reads it
            // (other mods, scenario code) still sees vanilla's list.
            try
            {
                if (MapSizesField != null && savedSizes == null)
                {
                    savedSizes = (int[])MapSizesField.GetValue(null);
                    MapSizesField.SetValue(null, (int[])Sizes.Clone());
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: could not replace the map size list, "
                    + "falling back to locking oversized options: " + e.Message, 762195911);
            }

            foreach (int size in Sizes)
            {
                string vanilla = "MapSizeDesc".Translate(size, size * size);
                relabel[vanilla] = "AB_MapSizeBanded".Translate(size,
                    (size * size).ToString("N0"), StackedCells(size).ToString("N0"));
                if (!Fits(size, BandCount))
                {
                    lockedLabels.Add(vanilla);
                }
            }
            // Test sizes are appended by the dialog itself and bypass our list entirely.
            int[] test = { 350, 400 };
            for (int i = 0; i < test.Length; i++)
            {
                lockedLabels.Add("MapSizeDesc".Translate(test[i], test[i] * test[i]));
            }
        }

        public static void EndChooser()
        {
            inChooser = false;
            lockedLabels.Clear();
            relabel.Clear();
            try
            {
                if (savedSizes != null && MapSizesField != null)
                {
                    MapSizesField.SetValue(null, savedSizes);
                }
            }
            catch
            {
            }
            finally
            {
                savedSizes = null;
            }
        }

        /// <summary>The relabelled text for a size option, or null if it is not one of
        /// ours.</summary>
        public static string RelabelFor(string label)
        {
            if (!inChooser || !Active || label == null)
            {
                return null;
            }
            return relabel.TryGetValue(label, out string s) ? s : null;
        }

        public static bool IsLocked(string label)
        {
            return inChooser && Active && label != null && lockedLabels.Contains(label);
        }

        /// <summary>Clamp a requested colony map size. Applied at generation so nothing can
        /// bypass the chooser.</summary>
        public static int Clamp(int size)
        {
            if (!Active)
            {
                return size;
            }
            // Snap to the largest OFFERED size that fits the budget. A plain Min() would
            // happily return something like 175 - inside the budget but sitting badly on
            // the 64-row slot boundary, paying a full extra band of gutter for nothing.
            int best = -1;
            for (int i = 0; i < Sizes.Length; i++)
            {
                int s = Sizes[i];
                if (s <= size && Fits(s, BandCount) && s > best)
                {
                    best = s;
                }
            }
            // Nothing at or below the request fits: the smallest offered size is the best
            // available without breaking the budget.
            return best > 0 ? best : Sizes[0];
        }
    }

    /// <summary>
    /// The size chooser. A prefix/postfix pair brackets the dialog so the RadioButton patch
    /// below only applies inside it, and the selection is snapped back afterwards in case
    /// anything set it out of range.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.DoWindowContents))]
    public static class Patch_AdvancedGameConfig_ABMapSizeLock
    {
        private static void Prefix()
        {
            ABMapSizeLimit.BeginChooser();
        }

        private static void Postfix(Rect inRect)
        {
            try
            {
                DrawLevelChooser(inRect);

                // Snap whatever is selected onto an offered size that fits the budget.
                // Vanilla's default is 250, which is not in our list, and raising the level
                // count can make a previously-legal size unaffordable - so this runs every
                // frame, not just on open.
                if (ABMapSizeLimit.Active && Find.GameInitData != null
                    && (Array.IndexOf(ABMapSizeLimit.Sizes, Find.GameInitData.mapSize) < 0
                        || !ABMapSizeLimit.Fits(Find.GameInitData.mapSize, ABMapSizeLimit.BandCount)))
                {
                    Find.GameInitData.mapSize = ABMapSizeLimit.Clamp(Find.GameInitData.mapSize);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: level chooser failed: " + e, 762195912);
            }
            finally
            {
                ABMapSizeLimit.EndChooser();
            }
        }

        /// <summary>
        /// The level chooser, drawn under vanilla's map-size column.
        ///
        /// Placed by hand rather than appended to vanilla's Listing_Standard because the
        /// listing has already been End()ed by the time a postfix runs. y=210 clears the
        /// size column in both configurations: our three sizes occupy ~112px, and ~188px
        /// with Prefs.TestMapSizes adding its extra group.
        /// </summary>
        private static void DrawLevelChooser(Rect inRect)
        {
            if (!ABV2.Enabled || Find.GameInitData == null)
            {
                return;
            }
            ABSettings s = ABMod.Settings;
            if (s == null)
            {
                return;
            }
            float x = 0f;
            float width = 200f;
            float y = 210f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x, y, width, 32f), "AB_LevelsHeading".Translate());
            Text.Font = GameFont.Small;
            y += 34f;

            int upper = s.upperLevels;
            int lower = s.lowerLevels;
            y = Spinner(x, y, width, "AB_LevelsAbove".Translate(), ref upper,
                0, ABMapSizeLimit.MaxUpperLevels);
            y = Spinner(x, y, width, "AB_LevelsBelow".Translate(), ref lower,
                0, ABMapSizeLimit.MaxLowerLevels);

            if (upper != s.upperLevels || lower != s.lowerLevels)
            {
                s.upperLevels = upper;
                s.lowerLevels = lower;
                s.Write();
                // The affordable size may have just changed under the selection.
                Find.GameInitData.mapSize = ABMapSizeLimit.Clamp(Find.GameInitData.mapSize);
            }

            int bandCount = ABMapSizeLimit.BandCount;
            int size = Find.GameInitData.mapSize;
            int cells = ABMapSizeLimit.StackedCells(size, bandCount);
            y += 4f;
            Widgets.Label(new Rect(x, y, width, 24f),
                "AB_LevelsSummary".Translate(bandCount, size));
            y += 24f;

            bool over = !ABMapSizeLimit.Fits(size, bandCount);
            Color old = GUI.color;
            if (over)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
            }
            Widgets.Label(new Rect(x, y, width, 24f), "AB_LevelsCells".Translate(
                cells.ToString("N0"), ABMapSizeLimit.CellBudget.ToString("N0")));
            GUI.color = old;
            y += 26f;

            if (bandCount <= 1)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.62f);
                Widgets.Label(new Rect(x, y, width, 48f), "AB_LevelsNoneNote".Translate());
                GUI.color = old;
            }
        }

        /// <summary>Label plus [-] n [+]. Deliberately not Widgets.IntEntry: that needs a
        /// persistent string buffer per field and allows typing values outside the
        /// range.</summary>
        private static float Spinner(float x, float y, float width, string label,
            ref int value, int min, int max)
        {
            Widgets.Label(new Rect(x, y + 2f, width - 80f, 24f), label);
            Rect minus = new Rect(x + width - 78f, y, 24f, 24f);
            Rect num = new Rect(x + width - 52f, y, 26f, 24f);
            Rect plus = new Rect(x + width - 24f, y, 24f, 24f);
            if (Widgets.ButtonText(minus, "-") && value > min)
            {
                value--;
            }
            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(num, value.ToString());
            Text.Anchor = anchor;
            if (Widgets.ButtonText(plus, "+") && value < max)
            {
                value++;
            }
            return y + 28f;
        }
    }

    /// <summary>
    /// Renders the oversized options as locked. Vanilla's RadioButton already has a
    /// `disabled` mode with the right greyed styling, so this just switches it on and
    /// explains why - no custom widget, and the option cannot be picked.
    /// </summary>
    [HarmonyPatch(typeof(Listing_Standard), nameof(Listing_Standard.RadioButton),
        new Type[] { typeof(string), typeof(bool), typeof(float), typeof(float),
            typeof(string), typeof(float?), typeof(bool) })]
    public static class Patch_ListingStandard_ABLockMapSizes
    {
        private static void Prefix(ref string label, ref string tooltip, ref bool disabled)
        {
            try
            {
                string relabelled = ABMapSizeLimit.RelabelFor(label);
                bool locked = ABMapSizeLimit.IsLocked(label);
                if (relabelled == null && !locked)
                {
                    return;
                }
                if (locked)
                {
                    label = "AB_MapSizeLocked".Translate(relabelled ?? label);
                    tooltip = "AB_MapSizeLockedTip".Translate(
                        ABMapSizeLimit.CellBudget.ToString("N0"), ABMapSizeLimit.BandCount);
                    disabled = true;
                    return;
                }
                // One of ours, and allowed: show the stacked cost, because that - not the
                // per-level size - is what the pathfinder actually pays.
                label = relabelled;
                tooltip = "AB_MapSizeBandedTip".Translate(ABV2.BandCount);
            }
            catch
            {
            }
        }
    }
}
