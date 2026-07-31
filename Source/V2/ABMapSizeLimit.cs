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
        // What 146,000 buys (per-level size x levels = stacked cells):
        //   4 x 190 = 145,920   <- three upper levels at full size; the reason for 146,000
        //   2 x 254 = 130,048   |  7 x 126 = 112,896
        //   5 x 190 = 182,400   <- refused
        //   7 x 190 = 255,360   <- refused (3 up AND 3 down at 190 drops the size to 126)
        //   7 x 126 = 112,896   <- seven levels for the price of three
        //   7 x 190 = 255,360   <- the footgun the budget exists to refuse
        // ==================================================

        public const int MaxUpperLevels = 3;

        public const int MaxLowerLevels = 3;

        /// <summary>Total cells a banded colony may allocate. Sized so the historical
        /// 3x190 layout and a 7x126 layout both fit, and 5x190 / 3x254 do not.</summary>
        public const int CellBudget = 146000;

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
        /// <summary>How many levels this per-level size can afford. Surfaced in the chooser
        /// so the dimension-versus-levels trade is visible BEFORE the player spends the
        /// budget, rather than discovered by watching a size option grey out.</summary>
        public static int MaxLevelsFor(int size)
        {
            int cap = MaxUpperLevels + MaxLowerLevels + 1;
            for (int levels = cap; levels >= 1; levels--)
            {
                if (Fits(size, levels))
                {
                    return levels;
                }
            }
            return 1;
        }

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
            // Lock every size that is NOT one of ours.
            //
            // The reflection swap of Dialog_AdvancedGameConfig.MapSizes cannot be relied on -
            // observed in play still listing vanilla's 200-325 (its Small/Medium/Large group
            // headers only render for 200/250/300, so their presence proves our array was
            // never in use). Rather than depend on it, vanilla's options are locked outright
            // and the real choice lives in our own strip below, which needs no reflection.
            int[] vanillaAndTest = { 200, 225, 250, 275, 300, 325, 350, 400 };
            for (int i = 0; i < vanillaAndTest.Length; i++)
            {
                int s = vanillaAndTest[i];
                if (Array.IndexOf(Sizes, s) < 0)
                {
                    lockedLabels.Add("MapSizeDesc".Translate(s, s * s));
                }
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
        }

        /// <summary>
        /// ⚠ THE RELEASE FOR BeginChooser, AND IT HAS TO BE A FINALIZER.
        ///
        /// It used to sit in the Postfix's finally block, which covers a throw in OUR code
        /// but not one in VANILLA's - Harmony skips postfixes entirely when the original
        /// method throws. And what BeginChooser takes is global: <c>inChooser</c> gates a
        /// patch on <c>Listing_Standard.RadioButton</c>, a UI primitive used by every mod's
        /// settings window and dozens of vanilla dialogs, and the reflection swap replaces
        /// the static <c>Dialog_AdvancedGameConfig.MapSizes</c> array outright. Left latched,
        /// unrelated radio buttons elsewhere in the game would be relabelled and DISABLED for
        /// the rest of the session, with no plausible way to connect that to this mod.
        ///
        /// Same rule as Patch_PawnRenderUtility_ABAimAngle: state taken in a prefix is
        /// released in a finalizer, never in a postfix.
        /// </summary>
        private static void Finalizer()
        {
            ABMapSizeLimit.EndChooser();
        }

        /// <summary>
        /// The level chooser, in a strip reserved at the BOTTOM of the dialog.
        ///
        /// The first version guessed a free spot inside vanilla's first column (y=210,
        /// 200 wide) and overlapped the size radio buttons, which made them hard to click.
        /// Guessing was the mistake: vanilla's own three columns can grow (test map sizes,
        /// another mod's options) and a postfix cannot append to their Listing_Standard
        /// because it has already been End()ed. So the window is made TALLER (see the
        /// InitialSize patch) and we own the space we added - nothing can overlap because
        /// vanilla lays out from the top and never sees the extra height.
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
            // Bottom strip, clear of the close button vanilla draws below us.
            float stripH = 240f;
            float top = inRect.height - stripH - CloseButtonClearance;
            Widgets.DrawLineHorizontal(0f, top, inRect.width);
            float y = top + 8f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, 300f, 32f), "AB_LevelsHeading".Translate());
            Text.Font = GameFont.Small;
            y += 34f;

            int upper = s.upperLevels;
            int lower = s.lowerLevels;
            float rowY = y;
            rowY = Spinner(0f, rowY, 300f, "AB_LevelsAbove".Translate(), ref upper,
                0, ABMapSizeLimit.MaxUpperLevels);
            rowY = Spinner(0f, rowY, 300f, "AB_LevelsBelow".Translate(), ref lower,
                0, ABMapSizeLimit.MaxLowerLevels);

            // OUR size selector - the authoritative one.
            //
            // Vanilla's column above is locked, because swapping its MapSizes array by
            // reflection proved unreliable and the old code then re-snapped mapSize every
            // frame, which silently undid whatever the player clicked - the "nothing is
            // selected" report. These buttons write GameInitData.mapSize directly, so a
            // click sticks and the snap below never has anything to correct.
            float sizeX = 330f;
            float sizeY = y;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(sizeX, sizeY - 26f, 300f, 24f), "AB_LevelsPerLevelSize".Translate());
            int[] sizes = ABMapSizeLimit.Sizes;
            for (int i = 0; i < sizes.Length; i++)
            {
                int candidate = sizes[i];
                bool affordable = ABMapSizeLimit.Fits(candidate, ABMapSizeLimit.BandCount);
                Rect row = new Rect(sizeX, sizeY, 300f, 26f);
                string label = candidate + "x" + candidate + "  ("
                    + ABMapSizeLimit.StackedCells(candidate, ABMapSizeLimit.BandCount).ToString("N0")
                    + " cells)";
                Color prev = GUI.color;
                if (!affordable)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.4f);
                }
                if (Widgets.RadioButtonLabeled(row, label,
                        Find.GameInitData.mapSize == candidate)
                    && affordable)
                {
                    Find.GameInitData.mapSize = candidate;
                }
                GUI.color = prev;
                sizeY += 28f;
            }

            if (upper != s.upperLevels || lower != s.lowerLevels)
            {
                s.upperLevels = upper;
                s.lowerLevels = lower;
                s.Write();
                // The affordable size may have just changed under the selection.
                Find.GameInitData.mapSize = ABMapSizeLimit.Clamp(Find.GameInitData.mapSize);
            }

            // Readout in its own column, so a long localised string can never reflow over
            // the spinner buttons.
            int bandCount = ABMapSizeLimit.BandCount;
            int size = Find.GameInitData.mapSize;
            int cells = ABMapSizeLimit.StackedCells(size, bandCount);
            float infoX = 660f;
            float infoW = Mathf.Max(220f, inRect.width - infoX);
            float infoY = y;
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f),
                "AB_LevelsSummary".Translate(bandCount, size));
            infoY += 26f;

            // Colour against the BUDGET ITSELF, not against Fits(): once the player has
            // lifted the cap, Fits() is unconditionally true, and that is exactly the state
            // in which the warning matters most. An unlocked map that is 2x over budget must
            // still say so, every time the player looks at this screen.
            Color old = GUI.color;
            bool over = cells > ABMapSizeLimit.CellBudget;
            if (over)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
            }
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f), "AB_LevelsCells".Translate(
                cells.ToString("N0"), ABMapSizeLimit.CellBudget.ToString("N0")));
            infoY += 26f;
            if (over)
            {
                Widgets.Label(new Rect(infoX, infoY, infoW, 44f),
                    "AB_LevelsOverBudget".Translate(
                        (cells / (float)ABMapSizeLimit.CellBudget).ToString("0.0")));
                infoY += 46f;
            }
            GUI.color = old;

            // Headroom: the useful question while spending a budget is "what else could I
            // afford", not only "what am I spending". Makes the dimension-versus-levels
            // trade visible BEFORE a size option greys out.
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f),
                "AB_LevelsHeadroom".Translate(ABMapSizeLimit.MaxLevelsFor(size), size));
            GUI.color = old;
            infoY += 26f;

            if (bandCount <= 1)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.62f);
                Widgets.Label(new Rect(infoX, infoY, infoW, 44f), "AB_LevelsNoneNote".Translate());
                GUI.color = old;
            }
        }

        /// <summary>Vanilla draws its close button over the bottom of the window without
        /// shrinking the rect handed to DoWindowContents, so we stay above it by hand.</summary>
        private const float CloseButtonClearance = 50f;

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
    /// Makes room for the level chooser. Vanilla lays its three columns out from the top of
    /// the window and never consults the height, so adding to it yields space that is ours
    /// alone - which is what stops the chooser overlapping the map-size radio buttons.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.InitialSize),
        MethodType.Getter)]
    public static class Patch_AdvancedGameConfig_ABTallerWindow
    {
        private static void Postfix(ref Vector2 __result)
        {
            if (ABV2.Enabled)
            {
                // Room for the levels + per-level-size strip. Vanilla lays its columns out
                // from the top and never consults the height, so this is space we own.
                __result.y += 260f;
                __result.x = Mathf.Max(__result.x, 1000f);
            }
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
