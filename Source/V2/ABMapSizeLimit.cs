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
    /// So colony maps are capped at the largest offered per-level size the cell budget can
    /// afford at the chosen level count. The cap is enforced in TWO places
    /// deliberately: at the map-size chooser (so the player is told, not silently
    /// overridden) and again at generation (so nothing - a scenario, another mod, a loaded
    /// config - can slip past it).
    /// </summary>
    public static class ABMapSizeLimit
    {
        /// <summary>
        /// The offered sizes, and why these four.
        ///
        /// ⚠ THE 64 CONSTRAINT IS ON *SLOT*, NOT ON BAND HEIGHT. Slot is
        /// `ceil((bandHeight + MinGutter) / 64) * 64` and has to be 64-aligned because
        /// terrain shaders sample by world position (ABBandMap.SlotAlignment). The band
        /// height itself only has to leave MinGutter rows under that boundary. The original
        /// three sizes were all 64k-2 - the tightest possible fit - and for a long time that
        /// coincidence was mistaken for the requirement. It is not one. 64k-2 is merely the
        /// CHEAPEST point on the curve, not the only legal one, and that mistake is what
        /// kept vanilla's own numbers off this list.
        ///
        ///   size  slot  gutter  cells/band  playable  waste
        ///   126    128     2       16,128     15,876    1.6%
        ///   190    192     2       36,480     36,100    1.0%
        ///   254    256     2       65,024     64,516    0.8%
        ///
        /// ⚠ EVERY TIER IS NOW `slot - 2`, WHICH IS WHY THE LADDER LOOKS LIKE THIS.
        /// There is exactly ONE maximally efficient height per 64-step - the one that sits
        /// two rows under a slot boundary - so the efficient sizes are 62, 126, 190, 254,
        /// 318, spaced exactly 64 apart. Anchoring on 190 therefore fixes the whole ladder;
        /// there is nothing efficient BETWEEN these numbers, only heights that pay for a
        /// slot they do not fill.
        ///
        /// The uniformity is the point, and it is worth more than the two vanilla-parity
        /// numbers it replaced: every tier now has the same 2-row gutter and the same 3-cell
        /// band-to-band gap, so gutter behaviour no longer changes with the player's size
        /// pick. That used to be a hidden variable in our own testing - a gutter-crossing
        /// bug reproduced at 126 and 190 and was GEOMETRICALLY IMPOSSIBLE at 250 (gap 7) and
        /// 300 (gap 21), which is the trap §30a walked into.
        ///
        /// ⚠ WHAT REPLACING 250 AND 300 COST, STATED SO IT IS NOT REDISCOVERED AS A BUG:
        ///  - 250 and 300 were vanilla's Medium and Large EXACTLY, and players recognise
        ///    those numbers. 254 and 318 do not appear in vanilla's list. That parity is the
        ///    one real thing given up here.
        ///  - 254 is otherwise a strict upgrade on 250: 1.6% more cells per band buys
        ///    playable 97.7% -> 99.2% and gutter 6 rows -> 2.
        ///  - 318 would likewise beat 300 (93.8% -> 99.4%, reclaiming 18 dead rows a band)
        ///    but is NOT offered: two levels of 318 is 203,520 cells, which exceeds even the
        ///    old 192,000 budget. A second efficient size above 190 cannot exist while the
        ///    budget goes DOWN. See CellBudget.
        ///  - the wide gutter those tiers carried was genuine safety (§14: "a spatial helper
        ///    that reaches 22 rows will cross a 2-row gutter"). At gap 3 every tier is now
        ///    inside the reach of a foreign radius helper. Ours is clamped (§30a,
        ///    ABPowerBandScope); a foreign one is not, and this is where that will show up.
        ///
        /// ⚠ 200 IS THE ONE NUMBER TO AVOID, and it is instructive: it lands just PAST the
        /// 192 boundary, so slot 256, gutter 56, 51,200 cells for 40,000 playable - 21.9%
        /// wasted, the worst point on the curve. Waste is a SAWTOOTH in size, not a smooth
        /// function of it, so recompute the table before ever adding a size.
        ///
        /// ⚠ 62 IS THE MISSING FOURTH TIER AND IT IS DELIBERATELY ABSENT. It is efficient
        /// (slot 64, gutter 2) and would extend the ladder downward, but it yields 3,844
        /// playable cells - 11% of a 190 band - and `ABBandSafety.SeamMarginFor` already
        /// takes 4 rows off each end, 13% of the band. 126 ALREADY produced "could not find
        /// cell to generate at" from two scatterers, which is why that margin became derived
        /// rather than a flat 10. Half of 126 is expected to starve generation, not merely
        /// play cramped. Do not add it without measuring scatterer failures first.
        ///
        /// LEGACY SIZES. 250 and 300 are no longer OFFERED, but existing colonies on them
        /// load and play normally: `bandHeight` is scribed per map and `SlotFor` is generic
        /// arithmetic, not a table lookup. This array is consulted only by the new-game
        /// chooser, Clamp and PlannedSize - never on the load path.
        ///
        /// This all matters because 1.6's PathGridJob is an IJobParallelFor over EVERY cell
        /// of the map, so the stacked total - not the per-level size - is what the
        /// pathfinder pays on a hot per-request path.
        /// </summary>
        public static readonly int[] Sizes = { 126, 190, 254 };

        // ================= THE LEVEL PLAN =================
        // Levels are chosen per colony on the advanced-config screen, and the constraint is
        // a TOTAL CELL BUDGET rather than a maximum per-level size. That is the honest
        // model: 1.6's path grid is an IJobParallelFor over EVERY cell of the map, so the
        // stacked total is what the pathfinder pays - a player picking 7 levels is not
        // asking for 7x the cost, they are asking for the same budget sliced differently.
        //
        // What 146,000 buys (per-level size x levels = stacked cells):
        //   7 x 126 = 112,896   <- the full seven levels, for 77% of the budget
        //   4 x 190 = 145,920   <- lands on the budget; this is what SETS the figure
        //   2 x 254 = 130,048
        //   8 x 126 = 129,024   <- refused by the SEVEN-LEVEL CAP, not by the budget
        //   5 x 190 = 182,400   <- refused
        //   3 x 254 = 195,072   <- refused
        //   2 x 318 = 203,520   <- refused, and this is why 318 is not an offered size
        //
        // The ladder that falls out is 7 / 4 / 2 levels, monotone in size, and EVERY tier
        // still yields real z-levels. A tier whose only affordable plan is one level would
        // be a z-level mod option with no z, which is what rules out 318 and everything
        // above it.
        //
        // ⚠ THE SPREAD IS THE POINT. Totals now run 112,896 / 145,920 / 130,048 - a 1.29x
        // spread, where the old ladder ran 112,896 to 192,000, a 1.70x spread whose ORDER
        // was counterintuitive: the tier with SEVEN levels was the cheapest and the one with
        // TWO was the most expensive. Nobody guesses that. Keeping every tier within ~30% of
        // the same cost is what lets the size pick be about shape rather than about speed.
        // ==================================================

        public const int MaxUpperLevels = 3;

        public const int MaxLowerLevels = 3;

        /// <summary>Total cells a banded colony may allocate.
        ///
        /// 146,000 is set by 4 x 190 = 145,920, the largest plan on the offered ladder.
        ///
        /// ⚠ DOWN FROM 192,000 (-24%), AND THE OLD FIGURE WAS SET BY A TIER NOBODY HAD EVER
        /// GENERATED. 192,000 existed because it is simultaneously 3 x 64,000 (250 at three
        /// levels) and 2 x 96,000 (300 at two) - so the ceiling every player carried was
        /// dictated by the heaviest tier on the list, and 300 was still listed as unverified
        /// in §13 when it was removed. Three separate lines of evidence say that was too
        /// high:
        ///
        ///  1. IT EXCEEDED EVERY MAP VANILLA SHIPS. 192,000 is 2.13x vanilla Large (90,000)
        ///     and 1.20x vanilla's 400x400 (160,000), which is already the option players
        ///     are warned away from. 146,000 is 1.62x Large and 0.91x that 400x400.
        ///
        ///  2. GENERATION COST TRACKS BAND SIZE, NOT TOTAL CELLS, so the budget was
        ///     governing the wrong axis. Measured, same tile class: #238 at 5 x 190 =
        ///     182,400 cells took 8.3 s; #236 at 3 x 250 = 192,000 cells took 12.4 s. Five
        ///     percent more cells, forty-nine percent more time, because FillRock is
        ///     superlinear in BAND size (1.75x cells -> 2.87x time). Dropping the 250 and
        ///     300 tiers removes the slowest generation cases outright.
        ///
        ///  3. THE CEILING WAS ONLY REACHABLE BY THE TWO WORST TIERS. 126 never came within
        ///     41% of it.
        ///
        /// ⚠ WHAT THIS IS *NOT* BASED ON: measured framerate. §10 records that the 59.6 fps
        /// at 96 pawns benchmark measures steady state, while our cost lives in
        /// section-regeneration BURSTS which have never been profiled. This figure rests on
        /// generation timings and the vanilla comparison. If burst cost is ever measured,
        /// revisit whether even 146,000 is right.</summary>
        public const int CellBudget = 146000;

        /// <summary>
        /// The player's per-colony master switch ("Enable multiple levels" on the
        /// advanced-config screen), and the gate on everything below.
        ///
        /// ⚠ IT SUSPENDS THE PLAN, IT DOES NOT ERASE IT. <c>upperLevels</c> and
        /// <c>lowerLevels</c> keep whatever the player chose; only the EFFECTIVE counts
        /// read as 0 here, so BandCount collapses to 1 and
        /// <c>Patch_MapGenerator_GenerateMap</c> takes its existing "single level -
        /// generate an ordinary map" branch. Re-checking the box restores the plan intact.
        ///
        /// ⚠ NO RECURSION, THOUGH IT LOOKS LIKE THERE IS: this reads
        /// <c>ABV2.Enabled</c>, a plain bool field, NOT <c>ABV2.BandCount</c> - which
        /// forwards straight back to <c>BandCount</c> below.
        /// </summary>
        public static bool MultiLevel => ABV2.Enabled && (ABMod.Settings?.multiLevel ?? true);

        public static int UpperLevels => MultiLevel
            ? Mathf.Clamp(ABMod.Settings?.upperLevels ?? 1, 0, MaxUpperLevels)
            : 0;

        public static int LowerLevels => MultiLevel
            ? Mathf.Clamp(ABMod.Settings?.lowerLevels ?? 1, 0, MaxLowerLevels)
            : 0;

        // ================= THE CHOOSER'S BOTTOM STRIP =================
        // The strip is anchored to the BOTTOM of a window we made taller, so the height it
        // occupies and the height we added are one quantity read in two places. They are
        // stated here, once, because when they disagree the strip either overlaps vanilla's
        // size column or floats off past the Close button.

        /// <summary>What the strip needs right now: the full chooser, or just the divider
        /// and the enable checkbox.</summary>
        public const float ExpandedStripHeight = 240f;

        public const float CollapsedStripHeight = 44f;

        /// <summary>Slack between the strip and the bottom of the window.</summary>
        public const float StripPadding = 48f;

        public static float WantedStripHeight =>
            MultiLevel ? ExpandedStripHeight : CollapsedStripHeight;

        /// <summary>
        /// What the OPEN window was actually sized for, which is not always what the strip
        /// wants.
        ///
        /// ⚠ <c>InitialSize</c> is consulted exactly once, by
        /// <c>Window.SetInitialSizeAndPosition</c>, so ticking the checkbox mid-dialog
        /// cannot change the window's height by itself - and drawing a 240px section into a
        /// 44px allowance would put it straight through vanilla's columns. The chooser
        /// therefore lays out against THIS number and requests a resize; the frame in
        /// between draws the collapsed row, which is one frame nobody sees.
        /// </summary>
        internal static float GrantedStripHeight = ExpandedStripHeight;

        /// <summary>Bands per column, surface included. 1 means an ordinary unbanded map.</summary>
        public static int BandCount => UpperLevels + LowerLevels + 1;

        /// <summary>Bands stack along +z from the deepest basement, so the surface index is
        /// simply the number of levels below it.</summary>
        public static int SurfaceBand => LowerLevels;

        /// <summary>
        /// Whether the cell budget governs the map-size chooser at all.
        ///
        /// ⚠ MultiLevel IS PART OF THIS, AND THAT IS THE POINT OF THE CHECKBOX. A
        /// single-level colony stacks nothing, so it has no stacked cell cost to budget and
        /// nothing left for us to lock: with the box unticked, <c>Fits</c> is unconditional,
        /// <c>Clamp</c> is the identity, <c>BeginChooser</c> returns before it locks or
        /// relabels anything, and vanilla's own size list is handed straight back to the
        /// player.
        /// </summary>
        public static bool Active => ABV2.Enabled && MultiLevel
            && !(ABMod.Settings?.unclampMapSize ?? false);

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

        /// <summary>
        /// THE PLAYER'S ACTUAL PER-LEVEL SIZE, which is not the same question as Clamp.
        ///
        /// <c>Clamp</c> answers "snap this arbitrary number DOWN to something affordable",
        /// which is right when sanitising a value that arrived from a scenario or a stale
        /// config. It is WRONG as a way of finding out what the player chose, because it is
        /// lossy: <c>Clamp(250)</c> at four levels returns 190, and did so no matter which
        /// tier was actually selected.
        ///
        /// That is exactly the bug that made Map Preview work only at 190x190. Map Preview
        /// asks how big the map will be and hands us VANILLA's configured size (250 by
        /// default) rather than our per-level pick; the old code ran that through Clamp, got
        /// 190 every time, and generated a 190-based stack. The crop check then compared
        /// that against the real stacked height and bailed as "not one of ours" for every
        /// tier except the one that happened to be 190.
        ///
        /// The selection is not something to re-derive at all - the chooser's radio buttons
        /// write <c>GameInitData.mapSize</c> directly (§2: own the widget, do not patch the
        /// data), so it is simply on record. Aim at the fact, not at a reconstruction of it.
        /// Clamp remains the fallback for the genuinely unknown-value case.
        /// </summary>
        public static int PlannedSize(int requested)
        {
            int chosen = Find.GameInitData?.mapSize ?? 0;
            if (chosen > 0 && Array.IndexOf(Sizes, chosen) >= 0 && Fits(chosen, BandCount))
            {
                return chosen;
            }
            return Clamp(requested);
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

        private static void Postfix(Rect inRect, Dialog_AdvancedGameConfig __instance)
        {
            try
            {
                DrawLevelChooser(inRect, __instance);

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
        private static void DrawLevelChooser(Rect inRect, Window window)
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
            // Bottom strip, clear of the close button vanilla draws below us. Laid out
            // against the height the window WAS GIVEN, never against the height the strip
            // currently wants - see ABMapSizeLimit.GrantedStripHeight.
            float stripH = ABMapSizeLimit.GrantedStripHeight;
            float top = inRect.height - stripH - CloseButtonClearance;
            Widgets.DrawLineHorizontal(0f, top, inRect.width);
            float y = top + 8f;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, 160f, 32f), "AB_LevelsHeading".Translate());
            Text.Font = GameFont.Small;

            // THE MASTER SWITCH, ON THE HEADING ROW. It sits beside the word it governs so
            // there is no doubt about what collapses: unticking it takes the whole section
            // below away AND hands vanilla's map-size column back (ABMapSizeLimit.Active).
            bool multi = s.multiLevel;
            Rect toggle = new Rect(174f, y + 4f, 300f, 24f);
            Widgets.CheckboxLabeled(toggle, "AB_LevelsEnable".Translate(), ref multi);
            TooltipHandler.TipRegion(toggle, "AB_LevelsEnableTip".Translate());
            if (multi != s.multiLevel)
            {
                s.multiLevel = multi;
                s.Write();
                Patch_AdvancedGameConfig_ABTallerWindow.RequestResize(window);
                if (!multi)
                {
                    // Leave the player on a size VANILLA offers, or its radio column comes
                    // back with nothing selected: our tiers (126/190/254) are not in
                    // vanilla's list, and 250 is the value GameInitData starts on anyway.
                    Find.GameInitData.mapSize = 250;
                }
                // Turning it back ON needs no snap here - Active is live, so the postfix's
                // own every-frame snap clamps the vanilla size onto our ladder below.
            }
            y += 34f;

            if (!multi)
            {
                Color offCol = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.62f);
                Widgets.Label(new Rect(0f, y, Mathf.Min(inRect.width, 900f), 24f),
                    "AB_LevelsDisabledNote".Translate());
                GUI.color = offCol;
                return;
            }
            if (stripH < ABMapSizeLimit.ExpandedStripHeight - 0.5f)
            {
                // Just ticked ON inside a window that was sized for the collapsed strip.
                // The resize requested above lands at the end of this frame; drawing the
                // full section into the short allowance would put it through vanilla's
                // columns for exactly one frame, so skip it instead.
                return;
            }

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
            Widgets.Label(new Rect(sizeX, sizeY - 26f, 300f, 24f), ChooserStrings.PerLevelSize);
            int[] sizes = ABMapSizeLimit.Sizes;
            for (int i = 0; i < sizes.Length; i++)
            {
                int candidate = sizes[i];
                bool affordable = ABMapSizeLimit.Fits(candidate, ABMapSizeLimit.BandCount);
                Rect row = new Rect(sizeX, sizeY, 300f, 26f);
                string label = ChooserStrings.SizeLabel(i);
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
            ChooserStrings.Refresh(bandCount, size, cells);
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f), ChooserStrings.Summary);
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
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f), ChooserStrings.Cells);
            infoY += 26f;
            if (over)
            {
                Widgets.Label(new Rect(infoX, infoY, infoW, 44f), ChooserStrings.OverBudget);
                infoY += 46f;
            }
            GUI.color = old;

            // Headroom: the useful question while spending a budget is "what else could I
            // afford", not only "what am I spending". Makes the dimension-versus-levels
            // trade visible BEFORE a size option greys out.
            GUI.color = new Color(1f, 1f, 1f, 0.7f);
            Widgets.Label(new Rect(infoX, infoY, infoW, 24f), ChooserStrings.Headroom);
            GUI.color = old;
            infoY += 26f;

            if (bandCount <= 1)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.62f);
                Widgets.Label(new Rect(infoX, infoY, infoW, 44f), ChooserStrings.NoneNote);
                GUI.color = old;
            }
        }

        /// <summary>
        /// The chooser's derived strings, rebuilt only when an input actually changes.
        ///
        /// DoWindowContents runs EVERY FRAME for as long as the advanced-config page is open,
        /// and this panel was doing twelve <c>Translate()</c> lookups plus four <c>N0</c>
        /// formats per frame to render text that only changes when the player clicks
        /// something. Translate is a dictionary hit plus a format; N0 allocates. None of it
        /// is expensive once, all of it is pure garbage sixty times a second.
        ///
        /// ⚠ KEYED ON THE ACTIVE LANGUAGE TOO. Without that, switching language with this
        /// page open (or opening it again after switching) would keep serving strings from
        /// the old one - a stale-cache bug that is invisible to every English-speaking
        /// tester.
        /// </summary>
        private static class ChooserStrings
        {
            private static object language;
            private static int bandCount = -1;
            private static int size = -1;
            private static int cells = -1;
            private static string[] sizeLabels;

            private static string perLevelSize;
            private static string noneNote;

            /// <summary>Read by the chooser BEFORE Refresh runs on the first frame (the size
            /// buttons are drawn above the info column), so these two warm the cache
            /// themselves rather than handing null to a Widgets call.</summary>
            internal static string PerLevelSize
            {
                get { EnsureWarm(); return perLevelSize; }
                private set { perLevelSize = value; }
            }

            internal static string NoneNote
            {
                get { EnsureWarm(); return noneNote; }
                private set { noneNote = value; }
            }

            internal static string Summary { get; private set; }
            internal static string Cells { get; private set; }
            internal static string OverBudget { get; private set; }
            internal static string Headroom { get; private set; }

            private static void EnsureWarm()
            {
                if (perLevelSize == null)
                {
                    Refresh(ABMapSizeLimit.BandCount,
                        Find.GameInitData != null ? Find.GameInitData.mapSize : 0, 0);
                }
            }

            private static bool LanguageChanged()
            {
                object now = LanguageDatabase.activeLanguage;
                if (ReferenceEquals(now, language) && perLevelSize != null)
                {
                    return false;
                }
                language = now;
                return true;
            }

            internal static void Refresh(int newBandCount, int newSize, int newCells)
            {
                bool rebuildAll = LanguageChanged();
                if (rebuildAll || newBandCount != bandCount)
                {
                    bandCount = newBandCount;
                    PerLevelSize = "AB_LevelsPerLevelSize".Translate();
                    NoneNote = "AB_LevelsNoneNote".Translate();
                    int[] sizes = ABMapSizeLimit.Sizes;
                    if (sizeLabels == null || sizeLabels.Length != sizes.Length)
                    {
                        sizeLabels = new string[sizes.Length];
                    }
                    for (int i = 0; i < sizes.Length; i++)
                    {
                        int candidate = sizes[i];
                        sizeLabels[i] = candidate + "x" + candidate + "  ("
                            + ABMapSizeLimit.StackedCells(candidate, bandCount).ToString("N0")
                            + " cells)";
                    }
                    rebuildAll = true;
                }
                if (rebuildAll || newSize != size || newCells != cells)
                {
                    size = newSize;
                    cells = newCells;
                    Summary = "AB_LevelsSummary".Translate(bandCount, size);
                    Cells = "AB_LevelsCells".Translate(
                        cells.ToString("N0"), ABMapSizeLimit.CellBudget.ToString("N0"));
                    OverBudget = "AB_LevelsOverBudget".Translate(
                        (cells / (float)ABMapSizeLimit.CellBudget).ToString("0.0"));
                    Headroom = "AB_LevelsHeadroom".Translate(
                        ABMapSizeLimit.MaxLevelsFor(size), size);
                }
            }

            /// <summary>The per-size radio labels. Built alongside the rest; the chooser asks
            /// for these BEFORE Refresh runs on the first frame, so build on demand if the
            /// cache is still cold rather than returning null into a Widgets call.</summary>
            internal static string SizeLabel(int i)
            {
                if (sizeLabels == null || i < 0 || i >= sizeLabels.Length)
                {
                    Refresh(ABMapSizeLimit.BandCount,
                        Find.GameInitData != null ? Find.GameInitData.mapSize : 0, 0);
                }
                return sizeLabels != null && i >= 0 && i < sizeLabels.Length
                    ? sizeLabels[i]
                    : string.Empty;
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
        /// <summary>Vanilla's own size, captured BEFORE we add to it, so a mid-dialog
        /// collapse can re-derive the window rather than subtract from itself.</summary>
        private static Vector2 vanillaSize;

        private static void Postfix(ref Vector2 __result)
        {
            vanillaSize = __result;
            if (ABV2.Enabled)
            {
                // Room for the levels + per-level-size strip. Vanilla lays its columns out
                // from the top and never consults the height, so this is space we own.
                // Was a flat 288 (= 240 + 48); it is now derived, because the strip
                // collapses to a single checkbox row when multiple levels are off. Adding a
                // size row means revisiting ExpandedStripHeight, not this line.
                ABMapSizeLimit.GrantedStripHeight = ABMapSizeLimit.WantedStripHeight;
                __result.y += ABMapSizeLimit.GrantedStripHeight + ABMapSizeLimit.StripPadding;
                __result.x = Mathf.Max(__result.x, 1000f);
            }
        }

        /// <summary>
        /// Ask for the open window to be re-sized to the strip's new height.
        ///
        /// ⚠ THE WRITE CANNOT HAPPEN HERE, AND THAT IS NOT OBVIOUS.
        /// <c>Window.WindowOnGUI</c> ends with <c>windowRect = GUI.Window(ID, windowRect,
        /// callback, ...)</c> - it passes the OLD rect in and assigns the return value back
        /// AFTER the callback returns. DoWindowContents runs inside that callback, so any
        /// windowRect written from here is overwritten by Unity within the same frame, and
        /// the resize silently never happens. It is handed to a postfix on WindowOnGUI
        /// instead, which is the one point past that assignment.
        /// </summary>
        internal static void RequestResize(Window window)
        {
            if (window == null || vanillaSize.y <= 0f)
            {
                return;
            }
            float grant = ABMapSizeLimit.WantedStripHeight;
            float w = Mathf.Max(vanillaSize.x, 1000f);
            float h = vanillaSize.y + grant + ABMapSizeLimit.StripPadding;
            Patch_Window_ABResizeAfterDraw.Request(window,
                new Rect((UI.screenWidth - w) / 2f, (UI.screenHeight - h) / 2f, w, h), grant);
        }
    }

    /// <summary>
    /// Applies a pending window resize at the only moment it survives: after
    /// <c>windowRect = GUI.Window(...)</c> has already run. See
    /// <c>Patch_AdvancedGameConfig_ABTallerWindow.RequestResize</c> for why nowhere inside
    /// DoWindowContents works.
    ///
    /// This is a patch on the base <c>Window</c>, so it is on the draw path of every window
    /// in the game - hence the reference-equality bail on the very first line and nothing
    /// allocated or translated above it. It is armed only by a checkbox click and disarms
    /// itself on the next frame.
    /// </summary>
    [HarmonyPatch(typeof(Window), nameof(Window.WindowOnGUI))]
    public static class Patch_Window_ABResizeAfterDraw
    {
        private static Window pending;

        private static Rect pendingRect;

        private static float pendingStrip;

        internal static void Request(Window window, Rect rect, float strip)
        {
            pending = window;
            pendingRect = rect;
            pendingStrip = strip;
        }

        private static void Postfix(Window __instance)
        {
            if (pending == null || !ReferenceEquals(pending, __instance))
            {
                return;
            }
            pending = null;
            __instance.windowRect = pendingRect;
            ABMapSizeLimit.GrantedStripHeight = pendingStrip;
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
