using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mod settings.
    ///
    /// REWRITTEN FOR V2. The V1 version carried 79 fields and a seven-tab window covering
    /// pocket-level generation, vertical links, per-level biomes, incident policy and a
    /// dozen compat bridges - all of which described a model V2 does not have. Rather than
    /// prune it field by field, this is the settings surface V2 actually reads, which the
    /// compiler can confirm: nine fields, every one of them consumed by a live V2 call site.
    ///
    /// Dropped settings simply stop being scribed. Old saves and old config files keep their
    /// unknown keys in Config/Mod_*.xml and RimWorld ignores them, so downgrading is lossy
    /// but nothing errors on load.
    /// </summary>
    public class ABSettings : ModSettings
    {
        // ---- diagnostics ---------------------------------------------------

        /// <summary>Chatty logging through ABLog.Dev. Off by default.</summary>
        public bool verboseLogging;

        // ---- performance ---------------------------------------------------

        /// <summary>Lift the total-cell budget on colony maps. See ABMapSizeLimit for why
        /// the budget exists: a banded map is every level plus gutters tall, and the
        /// pathfinder allocates and CLEARS map-sized arrays per request, so the stacked
        /// total is what it pays. (⚠ The old wording here said "the path grid is an
        /// IJobParallelFor over EVERY cell, on a hot per-request path". That is wrong on
        /// the second half and sent optimisation work to the wrong place twice: PathGridJob
        /// is deduped per variant per tick. The genuinely per-request map-sized cost is
        /// calcGrid.Clear() inside PathFinderJob. Both are Burst compiled and unpatchable;
        /// see ABPathBandScope.)
        ///
        /// ONLY EVER SET TRUE THROUGH THE CONFIRMATION DIALOG. The checkbox writes to a
        /// local, so the state itself encodes "the player read the warning and accepted it"
        /// and no separate acknowledgement flag is needed. Turning it back OFF is immediate -
        /// there is never a reason to make returning to a supported configuration harder.</summary>
        public bool unclampMapSize;

        /// <summary>Multiplier on the A* heuristic for pawns on a banded map. 1.0 is vanilla
        /// accuracy; higher searches greedily, expanding far fewer cells for a path that may
        /// be slightly longer. See ABPathBandScope for why this is a vanilla trade rather
        /// than a new one, and for the two pathing costs that are NOT reachable by any
        /// patch.</summary>
        public float pathHeuristic = 1f;

        // ---- climate -------------------------------------------------------
        //
        // Per-level temperature offsets and wind factors. SNAPSHOTTED onto ABBandMap at
        // generation (see ABBandMap.SnapshotClimate) so changing them never re-climates an
        // existing colony. Always three entries each: the level plan tops out at 3 up and
        // 3 down, and showing all six regardless of the current plan lets a player tune a
        // layout before they choose it.

        /// <summary>Master switch for altitude temperature (B of the 2026-08 perf pass).
        /// OFF does not merely zero the effect - ABPatchLifecycle REMOVES the
        /// GenTemperature postfix entirely, so the 174k+ calls/window pay nothing. The
        /// lifecycle also auto-idles the patch when every offset is zero (the shipped
        /// default) or no banded map exists, so this toggle is the manual override on
        /// top of that automatic behaviour, not the only gate.</summary>
        public bool bandTemperatureOffsets = true;

        /// <summary>⚠ ALTITUDE TEMPERATURE SHIPS OFF: these default to all-zero via
        /// <see cref="OffTempDefaults"/> in EnsureClimateLists. The classic curves live
        /// on as the presets below; ABBandEnv.Default* remain only as FromTable's
        /// last-ditch fallback for a null settings object. Existing installs keep
        /// whatever their config XML already scribes.</summary>
        public List<float> skyTempOffsets;

        public List<float> deepTempOffsets;

        public List<float> skyWindFactors;

        /// <summary>Climate presets. Not just convenience: they give the numbers a story, so
        /// a player who does not want to reason about lapse rates still gets a coherent
        /// mountain rather than whatever the sliders happened to be left at.</summary>
        public static readonly float[][] SkyPresets =
        {
            new[] { -4f, -9f, -18f },   // Gentle - the peak is cold, not lethal
            new[] { -7f, -16f, -35f },  // Standard - +2 is the seasonal line, +3 a permanent cap
            new[] { -10f, -24f, -50f }  // Alpine - only the surface is comfortable
        };

        public static readonly float[][] DeepPresets =
        {
            new[] { -4f, -4f, -4f },
            new[] { -6f, -6f, -6f },
            new[] { -8f, -10f, -12f }
        };

        /// <summary>Rebuild any climate list that is missing or the wrong length. Called on
        /// load and before every read: a hand-edited config or a version that shipped a
        /// different level cap must not produce an index-out-of-range deep inside the
        /// temperature patch.</summary>
        private static readonly float[] OffTempDefaults = { 0f, 0f, 0f };

        public void EnsureClimateLists()
        {
            skyTempOffsets = Fix(skyTempOffsets, OffTempDefaults);
            deepTempOffsets = Fix(deepTempOffsets, OffTempDefaults);
            skyWindFactors = Fix(skyWindFactors, ABBandEnv.DefaultSkyWindFactors);
        }

        private static List<float> Fix(List<float> list, float[] defaults)
        {
            if (list == null)
            {
                return new List<float>(defaults);
            }
            while (list.Count < defaults.Length)
            {
                list.Add(defaults[list.Count]);
            }
            if (list.Count > defaults.Length)
            {
                list.RemoveRange(defaults.Length, list.Count - defaults.Length);
            }
            return list;
        }

        // ---- level plan ----------------------------------------------------
        //
        // Chosen per colony on the advanced-config screen; stored here so the choice
        // persists as the default for the next colony. The GENERATED layout is scribed on
        // ABBandMap, so an existing save never depends on these.

        /// <summary>
        /// The master switch for the whole level plan, toggled by the "Enable multiple
        /// levels" checkbox on the advanced-config screen.
        ///
        /// ⚠ IT IS NOT THE SAME THING AS upperLevels = lowerLevels = 0, even though both
        /// end at BandCount 1. Zeroing the spinners DESTROYS the player's level plan; this
        /// suspends it, so re-checking the box restores whatever they had chosen. It also
        /// switches off <c>ABMapSizeLimit.Active</c>, which is what hands the map-size
        /// radio buttons back to vanilla - a single-level colony has no stacked cell cost
        /// to budget, so there is nothing left for us to lock.
        /// </summary>
        public bool multiLevel = true;

        /// <summary>Levels above the surface (0-3). Read through
        /// <c>ABMapSizeLimit.UpperLevels</c>, which returns 0 while multiLevel is off -
        /// this field is the stored plan, not the effective one.</summary>
        public int upperLevels = 1;

        /// <summary>Levels below the surface (0-3). See upperLevels.</summary>
        public int lowerLevels = 1;

        // ---- camera ---------------------------------------------------------
        //
        // Nothing here any more, deliberately. `freeCameraPan` was a binary between two
        // unsatisfying extremes that asked the player to reason about rendering internals.
        // How the view behaves at the edge of a level is an authored, per-level decision:
        // see ABCameraBounds for the baked table and the in-game calibration tool that
        // produced its numbers.

        // ---- visuals ---------------------------------------------------------
        //
        // The see-below depth cue, documented in full on ABDepthView. Presentation only:
        // nothing here changes what generates or what a pawn can reach, so it may be
        // flipped mid-colony.
        //
        // A second cue (camera-anchored Perspective Mode) shipped here briefly and was
        // removed - see ABDepthView for the reasoning and for what re-adding it would cost.
        // Its settings keys are simply no longer scribed; an old config file keeps them and
        // RimWorld ignores unknown keys, so nothing errors on a downgrade.

        /// <summary>Content on a level below the one being viewed draws smaller, once per
        /// level of drop. V1 had this and V2 dropped it; restored ON by default because it
        /// is the cue that makes a three-deep column read as depth rather than as four
        /// flat maps stacked in a list.</summary>
        public bool depthFalloff = true;

        /// <summary>Shrink applied per level of drop. BAKED INTO THE PRINTED VERTICES, so
        /// changing it forces a map-mesh regeneration - see ABMod.WriteSettings.</summary>
        public float depthFalloffPerLevel = ABDepthView.DefaultFalloff;

        /// <summary>§73 transit clips: tread-step stairs, peek-and-drop ladders, the
        /// freight elevator, plus the short post-hop hold that gives them time to read.
        /// Off = the old instant hop: no clips, no hold, no stagger.</summary>
        public bool transitAnim = true;

        /// <summary>Whether pawns below the viewed band may render from vanilla's pawn atlas
        /// (one blit) instead of walking their whole render tree every frame. Off / Auto /
        /// Aggressive - see ABBelowRenderCache. Auto is lossless by construction: it engages
        /// only while the atlas holds at least as many pixels per cell as the screen shows,
        /// which on most displays is every legal zoom. Realtime only, nothing baked, so
        /// changing it never needs a mesh regeneration.</summary>
        public int belowPawnCache = ABBelowRenderCache.ModeAuto;

        /// <summary>
        /// Whether the camera may zoom out further than the level being viewed can fill.
        ///
        /// ON is the shipped behaviour and the right default: a level is only ever a band of
        /// the map, so past half its height the extra screen is guaranteed to be backdrop.
        /// OFF exists because camera mods that widen the zoom range (Simple Camera Setting,
        /// Perspective Shift and friends) are widening it on purpose, and a player who has
        /// installed one would rather see the curtain than have the zoom refuse to move.
        /// Nothing leaks either way - Patch_CameraDriver_ABClipViewToBand keeps the
        /// neighbouring level from drawing and ABBandCurtain paints what is left with
        /// vanilla's own map-edge material.
        /// </summary>
        public bool clampZoomToLevel = true;

        /// <summary>Perspective Shift only: freeze the avatar while the player is looking at
        /// a level the avatar is not standing on. Implemented with PS's own
        /// State.CameraLockPosition, which is the same lever PS uses when its camera is sent
        /// somewhere other than the avatar.</summary>
        public bool psFreezeAvatarWhilePeeking = true;

        /// <summary>Immersive Opening only. Off = the opening cinematic stays on the
        /// colony's level (ferny's design, just aimed correctly). On = it walks the
        /// viewable bands top-down, ending on the colony.</summary>
        public bool ioStackTour;

        // ---- sky band generation -------------------------------------------

        /// <summary>Meadow-Perlin peaks (varied ledges and plateaus) rather than a plain
        /// projection of the solid mass below.</summary>
        public bool naturalPeaks = true;

        /// <summary>Sky band inherits the surface tile's biome for plants and temperature.</summary>
        public bool skyBiomeInherit = true;

        /// <summary>Share of peak surface that is soil rather than bare rock.</summary>
        public float peakSoilFraction = 0.15f;

        /// <summary>Rock-vs-meadow noise threshold.</summary>
        public float peakMeadowCutoff = 0.60f;

        /// <summary>Meadow noise frequency, i.e. feature size.</summary>
        public float peakMeadowScale = 0.024f;

        /// <summary>Deepest walkable edge band, in cells.</summary>
        public int peakTerraceMax = 4;

        /// <summary>Multiplier on the plateau's starting vegetation. 0 leaves the summit
        /// bare (the pre-2026-07-28 behaviour, which was not a choice so much as a
        /// missing feature).</summary>
        public float skyVegetationDensity = 1f;

        // ---- band dressing (§99) --------------------------------------------

        /// <summary>Re-run the map's own feature gensteps once per non-surface band, so
        /// upper and lower levels get the geysers, gas vents, rock chunks and boulders the
        /// ground level gets. Also confines the ORIGINAL generation-time pass to the surface
        /// band, which is what finally gives the ground level its full count - see
        /// ABBandDressing for why it was only ever receiving a seventh of it.</summary>
        public bool bandFeatures = true;

        /// <summary>Run vanilla's own initial plant pass on every band, at the band biome's
        /// real density, instead of our thinner hand-rolled seeders. Off falls back to the
        /// pre-§99 behaviour (sparse, with an alpine rim curve on summits).</summary>
        public bool bandVegetationParity = true;

        /// <summary>Re-run the tile's SAFE landmark mutators (hot springs, terrain patch
        /// families, obsidian) on every level, so a landmark tile reads as a landmark from
        /// top to bottom. Water, coast, river, cave-shape, elevation-shape and man-made
        /// families are deliberately never re-run - see ABBandMutators for the per-family
        /// reasoning.</summary>
        public bool bandLandmarks = true;

        // ---- basement generation -------------------------------------------

        /// <summary>Ore lumps per 10k basement cells.</summary>
        public float basementOreDensity = 6f;

        /// <summary>Which Biomes! Caverns biome the basement is carved as: a defName,
        /// "Random" for a weighted pick, or "None" for plain solid rock. Ignored entirely
        /// when Biomes! Caverns is not loaded.</summary>
        public string basementBiomeChoice = BiomesCavernsCompat.RandomChoice;

        /// <summary>How much of the basement the tunnel network opens up.</summary>
        public float cavernOpenness = 0.3f;

        /// <summary>Chance per worm step of widening into a chamber.</summary>
        public float cavernChamberFreq = 0.02f;

        /// <summary>How many passes of Biomes! Caverns' stalagmite scatterer to run.</summary>
        public float cavernFormations = 1f;

        // ---- cross-level travel (arrivals and departures) -------------------
        // Read at DECISION time only (an arrival being resolved, an exit being picked),
        // never cached into map state - which is what makes every one of these safe to
        // flip mid-save. Defaults per the user's call: UPPER routes on, LOWER routes off,
        // for every category - ridge paths are the default fiction, tunnel raids are the
        // opt-in spice.

        public bool crossLevelTravel = true;

        public bool raiderArriveUpper = true;

        public bool raiderArriveLower;

        public bool raiderLeaveUpper = true;

        public bool raiderLeaveLower;

        public bool friendlyArriveUpper = true;

        public bool friendlyArriveLower;

        public bool friendlyLeaveUpper = true;

        public bool friendlyLeaveLower;

        public bool animalArriveUpper = true;

        public bool animalArriveLower;

        public bool animalLeaveUpper = true;

        public bool animalLeaveLower;

        // --------------------------------------------------------------------

        /// <summary>Display label for the current basement-biome choice, resolving a stored
        /// defName to its in-game label so the button never shows a raw defName.</summary>
        private string LabelForBiomeChoice()
        {
            if (basementBiomeChoice == BiomesCavernsCompat.NoneChoice)
            {
                return "AB_BasementBiomeNone".Translate();
            }
            if (basementBiomeChoice == BiomesCavernsCompat.VanillaChoice)
            {
                return "AB_BasementBiomeVanilla".Translate();
            }
            if (string.IsNullOrEmpty(basementBiomeChoice)
                || basementBiomeChoice == BiomesCavernsCompat.RandomChoice)
            {
                // "Random" with no cavern biomes available resolves to vanilla caves, and
                // the button must say so - a label that promises a cavern the generator will
                // not produce is the kind of quiet lie that gets reported as a bug.
                return BiomesCavernsCompat.Active
                    ? "AB_BasementBiomeRandom".Translate()
                    : "AB_BasementBiomeVanilla".Translate();
            }
            BiomeDef def = DefDatabase<BiomeDef>.GetNamedSilentFail(basementBiomeChoice);
            return def != null ? def.LabelCap.ToString() : basementBiomeChoice;
        }

        private static readonly Color WarnRed = new Color(1f, 0.25f, 0.25f);
        private static readonly Color NoteDim = new Color(1f, 1f, 1f, 0.62f);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref unclampMapSize, "unclampMapSize", false);
            Scribe_Values.Look(ref pathHeuristic, "pathHeuristic", 1f);
            Scribe_Values.Look(ref multiLevel, "multiLevel", true);
            Scribe_Values.Look(ref upperLevels, "upperLevels", 1);
            Scribe_Values.Look(ref lowerLevels, "lowerLevels", 1);
            Scribe_Values.Look(ref naturalPeaks, "naturalPeaks", true);
            Scribe_Values.Look(ref skyBiomeInherit, "skyBiomeInherit", true);
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakMeadowCutoff, "peakMeadowCutoff", 0.60f);
            Scribe_Values.Look(ref peakMeadowScale, "peakMeadowScale", 0.024f);
            Scribe_Values.Look(ref peakTerraceMax, "peakTerraceMax", 4);
            Scribe_Values.Look(ref skyVegetationDensity, "skyVegetationDensity", 1f);
            Scribe_Values.Look(ref bandFeatures, "bandFeatures", true);
            Scribe_Values.Look(ref bandVegetationParity, "bandVegetationParity", true);
            Scribe_Values.Look(ref bandLandmarks, "bandLandmarks", true);
            Scribe_Values.Look(ref basementOreDensity, "basementOreDensity", 6f);
            Scribe_Values.Look(ref basementBiomeChoice, "basementBiomeChoice",
                BiomesCavernsCompat.RandomChoice);
            Scribe_Values.Look(ref cavernOpenness, "cavernOpenness", 0.3f);
            Scribe_Values.Look(ref cavernChamberFreq, "cavernChamberFreq", 0.02f);
            Scribe_Values.Look(ref cavernFormations, "cavernFormations", 1f);
            Scribe_Values.Look(ref crossLevelTravel, "crossLevelTravel", true);
            Scribe_Values.Look(ref raiderArriveUpper, "raiderArriveUpper", true);
            Scribe_Values.Look(ref raiderArriveLower, "raiderArriveLower", false);
            Scribe_Values.Look(ref raiderLeaveUpper, "raiderLeaveUpper", true);
            Scribe_Values.Look(ref raiderLeaveLower, "raiderLeaveLower", false);
            Scribe_Values.Look(ref friendlyArriveUpper, "friendlyArriveUpper", true);
            Scribe_Values.Look(ref friendlyArriveLower, "friendlyArriveLower", false);
            Scribe_Values.Look(ref friendlyLeaveUpper, "friendlyLeaveUpper", true);
            Scribe_Values.Look(ref friendlyLeaveLower, "friendlyLeaveLower", false);
            Scribe_Values.Look(ref animalArriveUpper, "animalArriveUpper", true);
            Scribe_Values.Look(ref animalArriveLower, "animalArriveLower", false);
            Scribe_Values.Look(ref animalLeaveUpper, "animalLeaveUpper", true);
            Scribe_Values.Look(ref animalLeaveLower, "animalLeaveLower", false);
            Scribe_Values.Look(ref depthFalloff, "depthFalloff", true);
            Scribe_Values.Look(ref depthFalloffPerLevel, "depthFalloffPerLevel",
                ABDepthView.DefaultFalloff);
            Scribe_Values.Look(ref transitAnim, "transitAnim", true);
            Scribe_Values.Look(ref clampZoomToLevel, "clampZoomToLevel", true);
            Scribe_Values.Look(ref ioStackTour, "ioStackTour", false);
            Scribe_Values.Look(ref psFreezeAvatarWhilePeeking, "psFreezeAvatarWhilePeeking",
                true);
            Scribe_Values.Look(ref belowPawnCache, "belowPawnCache",
                ABBelowRenderCache.ModeAuto);
            Scribe_Values.Look(ref bandTemperatureOffsets, "bandTemperatureOffsets", true);
            Scribe_Collections.Look(ref skyTempOffsets, "skyTempOffsets", LookMode.Value);
            Scribe_Collections.Look(ref deepTempOffsets, "deepTempOffsets", LookMode.Value);
            Scribe_Collections.Look(ref skyWindFactors, "skyWindFactors", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
            {
                EnsureClimateLists();
            }
        }

        // ---- window ---------------------------------------------------------

        private enum Tab
        {
            Performance,
            Visuals,
            Climate,
            Sky,
            Basement,
            Arrivals,
            Diagnostics
        }

        private Tab tab = Tab.Performance;

        private Vector2 scroll;

        /// <summary>Content height PER TAB, measured from the previous frame's listing. A
        /// scroll view needs its content height up front and a Listing_Standard only knows
        /// how tall it was after drawing, so the value always lags a frame. Kept per tab
        /// rather than shared: one shared value meant switching from a short tab to a tall
        /// one sized the scroll region from the WRONG tab for a frame.</summary>
        private readonly float[] viewHeights = { 600f, 600f, 600f, 600f, 600f, 600f, 600f };

        /// <summary>Rebuilt every frame because <see cref="TabRecord.selected"/> is captured
        /// by value at construction. One reused list rather than seven allocations a frame.</summary>
        private readonly List<TabRecord> tabRecords = new List<TabRecord>(7);

        /// <summary>
        /// VANILLA TABS, DRAWN THE WAY EVERY VANILLA WINDOW DRAWS THEM.
        ///
        /// ⚠ THIS WAS A HAND-DRAWN BUTTON ROW AND THE NOTE EXPLAINING WHY WAS WRONG. The
        /// old comment blamed <c>TabDrawer.DrawTabs</c> for tabs that "rendered but never
        /// switched", on the theory that its layout contract is owned by the host dialog.
        /// It was not TabDrawer: the blank panes were the scroll-view height latch fixed
        /// further down this method, which clipped every tab that did not happen to fit the
        /// initial 600px on its first frame. Swapping the widget out only hid the real bug
        /// behind a row of buttons that does not look like tabs - the selected one was a
        /// flat tinted box with no tab shape at all, which is exactly what the report showed.
        ///
        /// TabDrawer paints its row in the strip ABOVE the rect it is handed, so the body
        /// rect starts one tab-height down and the row lands INSIDE inRect rather than over
        /// the dialog's own header. That is the same two lines vanilla uses in every
        /// MainTabWindow and ITab, and it is the whole of the layout contract.
        /// </summary>
        public void DoWindowContents(Rect inRect)
        {
            EnsureClimateLists();

            Rect body = new Rect(inRect.x, inRect.y + TabDrawer.TabHeight,
                inRect.width, inRect.height - TabDrawer.TabHeight);
            Widgets.DrawMenuSection(body);

            tabRecords.Clear();
            AddTab("AB_TabPerformance", Tab.Performance);
            AddTab("AB_TabVisuals", Tab.Visuals);
            AddTab("AB_TabClimate", Tab.Climate);
            AddTab("AB_TabSky", Tab.Sky);
            AddTab("AB_TabBasement", Tab.Basement);
            AddTab("AB_TabArrivals", Tab.Arrivals);
            AddTab("AB_TabDiagnostics", Tab.Diagnostics);
            TabDrawer.DrawTabs(body, tabRecords);

            int index = (int)tab;
            Rect inner = body.ContractedBy(10f);


            Rect view = new Rect(0f, 0f, inner.width - 20f, viewHeights[index]);
            Widgets.BeginScrollView(inner, ref scroll, view);
            Listing_Standard list = new Listing_Standard();
            list.Begin(view);
            // A settings panel that shows its own failure beats one that silently draws
            // nothing. RimWorld catches exceptions per-window and, depending on which GUI
            // event they land in, a pane can come up blank with nothing reaching the log at
            // all - which is precisely the state this panel was in.
            try
            {
                switch (tab)
                {
                    case Tab.Performance: DoPerformance(list); break;
                    case Tab.Visuals: DoVisuals(list); break;
                    case Tab.Climate: DoClimate(list); break;
                    case Tab.Sky: DoSky(list); break;
                    case Tab.Basement: DoBasement(list); break;
                    case Tab.Arrivals: DoArrivals(list); break;
                    default: DoDiagnostics(list); break;
                }
            }
            catch (Exception e)
            {
                Color prev = GUI.color;
                GUI.color = WarnRed;
                list.Label("Settings tab '" + tab + "' failed to draw:");
                list.Label(e.GetType().Name + ": " + e.Message);
                GUI.color = prev;
                Log.ErrorOnce(ABLog.Tag + " settings tab " + tab + " threw: " + e,
                    0x5E77 ^ (int)tab);
            }
            // ⚠ NEVER LET THE MEASURED HEIGHT FALL BELOW THE VIEWPORT.
            //
            // This is the whole of the blank-tab bug, and it is a self-sustaining latch.
            // The scroll view needs its content height a frame in advance, so it is measured
            // from the previous frame's CurHeight. If any frame draws short - because the
            // content threw partway, or simply because it was the first frame - the next
            // frame gets a window that small, which CLIPS everything past it, so the frame
            // after measures short again. It never recovers. Climate latched at 40px: the
            // heading drew, everything below it was clipped, and the clipped region included
            // the error text that would have explained why.
            //
            // The Performance tab escaped only because its content happened to fit inside the
            // initial 600px on the very first frame, which is why exactly one tab worked and
            // made this look like a dispatch problem for four launches.
            //
            // Flooring at the viewport height is also just correct: content shorter than the
            // viewport should not scroll, and can never need a smaller rect than the window
            // it sits in.
            viewHeights[index] = Mathf.Max(list.CurHeight + 16f, inner.height);
            list.End();
            Widgets.EndScrollView();
        }

        private void AddTab(string key, Tab which)
        {
            Tab target = which; // captured by the closure, so it must be a local
            tabRecords.Add(new TabRecord(key.Translate(), delegate
            {
                tab = target;
                scroll = Vector2.zero; // a fresh tab starts at its top, not where the last one sat
            }, tab == which));
        }

        // ---- performance tab -------------------------------------------------

        /// <summary>
        /// The cap, and the one place this mod tells a player it will not help them.
        ///
        /// The budget is deliberately NOT a slider. A number a player can nudge invites
        /// nudging, and every nudge lands them a little further into a configuration that
        /// cannot be supported without ever showing them a warning. One locked value and one
        /// gated escape hatch means anyone running an unsupported map made an explicit,
        /// dated decision to do so.
        /// </summary>
        private void DoPerformance(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_TabPerformance".Translate());
            Text.Font = GameFont.Small;

            string budget = ABMapSizeLimit.CellBudget.ToString("N0");

            if (unclampMapSize)
            {
                Color old = GUI.color;
                GUI.color = WarnRed;
                Text.Font = GameFont.Medium;
                list.Label("AB_UnclampBannerTitle".Translate());
                Text.Font = GameFont.Small;
                list.Label("AB_UnclampMapSizeWarning".Translate());
                GUI.color = old;
                list.Gap(6f);
            }

            // The checkbox writes to a LOCAL. unclampMapSize itself only ever becomes true
            // inside the confirmation callback, so the tick cannot latch on a misclick and
            // the stored value always means "warning read and accepted".
            bool want = unclampMapSize;
            list.CheckboxLabeled("AB_UnclampMapSize".Translate(budget), ref want,
                "AB_UnclampMapSizeTip".Translate(budget));
            if (want != unclampMapSize)
            {
                if (want)
                {
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "AB_UnclampConfirm".Translate(budget),
                        delegate
                        {
                            unclampMapSize = true;
                            Write();
                        },
                        destructive: true,
                        "AB_UnclampConfirmTitle".Translate()));
                }
                else
                {
                    // Going back to a supported configuration is never gated.
                    unclampMapSize = false;
                    Write();
                }
            }

            list.Gap(8f);
            // ⚠ Reports the STORED plan and its band count, not ABMapSizeLimit.BandCount:
            // with the level plan suspended that property returns 1, and this line would
            // then read "1 above, 1 below (1 levels)", which is nonsense.
            list.Label("AB_LevelsDefault".Translate(upperLevels, lowerLevels,
                upperLevels + lowerLevels + 1));
            if (!multiLevel)
            {
                Color off = GUI.color;
                GUI.color = NoteDim;
                list.Label("AB_LevelsSuspendedNote".Translate());
                GUI.color = off;
            }

            Color dim = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_BudgetExplain".Translate(budget));
            GUI.color = dim;

            list.Gap(12f);
            list.Label("AB_PathHeadingLabel".Translate());
            list.Label("AB_PathHeuristicAmount".Translate(
                Mathf.Approximately(pathHeuristic, 1f)
                    ? "AB_PathHeuristicOff".Translate().ToString()
                    : pathHeuristic.ToString("0.00") + "x"));
            pathHeuristic = Mathf.Round(list.Slider(pathHeuristic,
                ABPathBandScope.MinHeuristic, ABPathBandScope.MaxHeuristic) * 20f) / 20f;

            GUI.color = NoteDim;
            list.Label("AB_PathHeuristicTip".Translate());
            GUI.color = dim;
        }

        // ---- visuals tab -----------------------------------------------------

        /// <summary>Set when a setting that is BAKED INTO MESH VERTICES changes, so the map
        /// mesh can be regenerated once on close rather than on every frame of a slider
        /// drag. Reading it clears it.</summary>
        private static bool bakedVisualDirty;

        public static bool ConsumeBakedVisualDirty()
        {
            bool was = bakedVisualDirty;
            bakedVisualDirty = false;
            return was;
        }

        /// <summary>
        /// The depth cue. Full reasoning lives on ABDepthView; this pane only has to say
        /// what it costs the player.
        ///
        /// The slider is presented as "per level", not as a final size, because the number
        /// compounds: at 85% a level-3 basement seen from the peak draws at 61%, and a
        /// player who read the slider as "how big is the level below" would be surprised by
        /// that. The readout spells the compounded value out.
        /// </summary>
        private void DoVisuals(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_TabVisuals".Translate());
            Text.Font = GameFont.Small;

            Color dim = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_VisualsNote".Translate());
            GUI.color = dim;
            list.Gap(8f);

            list.CheckboxLabeled("AB_TransitAnim".Translate(), ref transitAnim,
                "AB_TransitAnimTip".Translate());

            bool falloffWas = depthFalloff;
            list.CheckboxLabeled("AB_DepthFalloff".Translate(), ref depthFalloff,
                "AB_DepthFalloffTip".Translate());
            if (falloffWas != depthFalloff)
            {
                bakedVisualDirty = true;
            }
            if (depthFalloff)
            {
                float three = depthFalloffPerLevel * depthFalloffPerLevel * depthFalloffPerLevel;
                list.Label("AB_DepthFalloffAmount".Translate(
                    depthFalloffPerLevel.ToStringPercent(), three.ToStringPercent()));
                float chosen = list.Slider(depthFalloffPerLevel,
                    ABDepthView.MinFalloff, ABDepthView.MaxFalloff);
                if (Mathf.Abs(chosen - depthFalloffPerLevel) > 0.0005f)
                {
                    depthFalloffPerLevel = chosen;
                    bakedVisualDirty = true;
                }
            }

            // No bakedVisualDirty on any of these: the pawn cache is a realtime draw path,
            // nothing about it reaches the printed mesh.
            list.Gap(10f);
            list.Label("AB_BelowPawnCache".Translate());
            if (list.RadioButton("AB_BelowPawnCacheAuto".Translate(),
                    belowPawnCache == ABBelowRenderCache.ModeAuto, 8f,
                    "AB_BelowPawnCacheAutoTip".Translate()))
            {
                belowPawnCache = ABBelowRenderCache.ModeAuto;
            }
            if (list.RadioButton("AB_BelowPawnCacheAggressive".Translate(),
                    belowPawnCache == ABBelowRenderCache.ModeAggressive, 8f,
                    "AB_BelowPawnCacheAggressiveTip".Translate()))
            {
                belowPawnCache = ABBelowRenderCache.ModeAggressive;
            }
            if (list.RadioButton("AB_BelowPawnCacheOff".Translate(),
                    belowPawnCache == ABBelowRenderCache.ModeOff, 8f,
                    "AB_BelowPawnCacheOffTip".Translate()))
            {
                belowPawnCache = ABBelowRenderCache.ModeOff;
            }

            list.Gap(10f);
            list.CheckboxLabeled("AB_ClampZoom".Translate(), ref clampZoomToLevel,
                "AB_ClampZoomTip".Translate());
            // Only shown to players who actually run Perspective Shift: a checkbox about a
            // mod you do not have is noise.
            if (PerspectiveShiftCompat.Present)
            {
                list.CheckboxLabeled("AB_PSFreezePeek".Translate(),
                    ref psFreezeAvatarWhilePeeking, "AB_PSFreezePeekTip".Translate());
            }
            if (ImmersiveOpeningCompat.Present)
            {
                list.CheckboxLabeled("AB_IOStackTour".Translate(), ref ioStackTour,
                    "AB_IOStackTourTip".Translate());
            }
        }

        // ---- climate tab -----------------------------------------------------

        /// <summary>A concrete surface temperature to show the offsets against. The live map
        /// when there is one, otherwise a plain 20 C - an offset alone ("-35") means nothing
        /// to most players, but "20 C surface becomes -15 C here" does.</summary>
        private static float ReferenceTemp()
        {
            Map m = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            if (m != null)
            {
                try
                {
                    return m.mapTemperature.OutdoorTemp;
                }
                catch
                {
                }
            }
            return 20f;
        }

        private void DoClimate(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_TabClimate".Translate());
            Text.Font = GameFont.Small;

            // Defensive: every consumer below indexes these by position. EnsureClimateLists
            // runs at the top of DoWindowContents, but if it ever failed to produce three
            // entries the loops would draw nothing and the pane would look empty rather than
            // broken, which is a much harder thing to diagnose.
            if (skyTempOffsets.Count < 3 || deepTempOffsets.Count < 3 || skyWindFactors.Count < 3)
            {
                Color bad = GUI.color;
                GUI.color = WarnRed;
                list.Label("Climate lists not initialised (" + skyTempOffsets.Count + "/"
                    + deepTempOffsets.Count + "/" + skyWindFactors.Count + ").");
                GUI.color = bad;
                return;
            }

            Color old = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_ClimateNewColonyNote".Translate());
            GUI.color = old;
            list.Gap(4f);

            list.CheckboxLabeled("AB_ClimateMaster".Translate(), ref bandTemperatureOffsets,
                "AB_ClimateMasterTip".Translate());
            GUI.color = NoteDim;
            list.Label((ABPatchLifecycle.Applied
                ? "AB_ClimatePatchActive"
                : "AB_ClimatePatchDormant").Translate());
            list.Label("AB_ClimateOffHint".Translate());
            GUI.color = old;
            list.Gap(4f);

            Rect presetRow = list.GetRect(30f);
            float w = presetRow.width / 4f - 7f;
            if (Widgets.ButtonText(new Rect(presetRow.x, presetRow.y, w, 30f),
                "AB_PresetOff".Translate()))
            {
                ApplyPresetOff();
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + w + 9f, presetRow.y, w, 30f),
                "AB_PresetGentle".Translate()))
            {
                ApplyPreset(0);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + (w + 9f) * 2f, presetRow.y, w, 30f),
                "AB_PresetStandard".Translate()))
            {
                ApplyPreset(1);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + (w + 9f) * 3f, presetRow.y, w, 30f),
                "AB_PresetAlpine".Translate()))
            {
                ApplyPreset(2);
            }
            list.Gap(10f);

            float reference = ReferenceTemp();

            list.Label("AB_ClimateSkyHeading".Translate());
            for (int i = 0; i < skyTempOffsets.Count; i++)
            {
                list.Label("AB_ClimateLevelTemp".Translate("+" + (i + 1),
                    skyTempOffsets[i].ToString("0"),
                    (reference + skyTempOffsets[i]).ToString("0")));
                skyTempOffsets[i] = Mathf.Round(list.Slider(skyTempOffsets[i], -60f, 0f));
            }

            list.Gap(6f);
            list.Label("AB_ClimateDeepHeading".Translate());
            for (int i = 0; i < deepTempOffsets.Count; i++)
            {
                list.Label("AB_ClimateLevelTemp".Translate("-" + (i + 1),
                    deepTempOffsets[i].ToString("0"),
                    (reference + deepTempOffsets[i]).ToString("0")));
                deepTempOffsets[i] = Mathf.Round(list.Slider(deepTempOffsets[i], -30f, 15f));
            }

            list.Gap(6f);
            list.Label("AB_ClimateWindHeading".Translate());
            GUI.color = NoteDim;
            list.Label("AB_ClimateWindNote".Translate());
            GUI.color = old;
            for (int i = 0; i < skyWindFactors.Count; i++)
            {
                list.Label("AB_ClimateLevelWind".Translate("+" + (i + 1),
                    Mathf.RoundToInt((skyWindFactors[i] - 1f) * 100f).ToString()));
                skyWindFactors[i] = Mathf.Round(list.Slider(skyWindFactors[i], 1f, 3f) * 20f) / 20f;
            }
        }

        private void ApplyPreset(int index)
        {
            skyTempOffsets = new List<float>(SkyPresets[index]);
            deepTempOffsets = new List<float>(DeepPresets[index]);
        }

        /// <summary>The shipped default: no altitude temperature. One button beats
        /// dragging six sliders to zero.</summary>
        private void ApplyPresetOff()
        {
            skyTempOffsets = new List<float>(OffTempDefaults);
            deepTempOffsets = new List<float>(OffTempDefaults);
        }

        // ---- sky tab ---------------------------------------------------------

        private void DoSky(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_SkyHeading".Translate());
            Text.Font = GameFont.Small;

            list.CheckboxLabeled("AB_NaturalPeaks".Translate(), ref naturalPeaks,
                "AB_NaturalPeaksTip".Translate());
            list.CheckboxLabeled("AB_SkyBiomeInherit".Translate(), ref skyBiomeInherit,
                "AB_SkyBiomeInheritTip".Translate());

            if (naturalPeaks)
            {
                list.Label("AB_PeakSoilFraction".Translate(peakSoilFraction.ToStringPercent()));
                peakSoilFraction = list.Slider(peakSoilFraction, 0f, 0.5f);

                list.Label("AB_PeakMeadowCutoff".Translate(peakMeadowCutoff.ToString("0.00")));
                peakMeadowCutoff = list.Slider(peakMeadowCutoff, 0.2f, 0.9f);

                list.Label("AB_PeakMeadowScale".Translate(peakMeadowScale.ToString("0.000")));
                peakMeadowScale = list.Slider(peakMeadowScale, 0.005f, 0.08f);

                list.Label("AB_PeakTerraceMax".Translate(peakTerraceMax));
                peakTerraceMax = Mathf.RoundToInt(list.Slider(peakTerraceMax, 1f, 12f));
            }

            list.Label("AB_SkyVegetationDensity".Translate(skyVegetationDensity.ToString("0.0")));
            skyVegetationDensity = list.Slider(skyVegetationDensity, 0f, 2f);

            list.GapLine();
            list.CheckboxLabeled("AB_BandFeatures".Translate(), ref bandFeatures,
                "AB_BandFeaturesDesc".Translate());
            list.CheckboxLabeled("AB_BandVegetationParity".Translate(), ref bandVegetationParity,
                "AB_BandVegetationParityDesc".Translate());
            list.CheckboxLabeled("AB_BandLandmarks".Translate(), ref bandLandmarks,
                "AB_BandLandmarksDesc".Translate());
        }

        // ---- basement tab ----------------------------------------------------

        private void DoBasement(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_BasementHeading".Translate());
            Text.Font = GameFont.Small;

            list.Label("AB_BasementOreDensity".Translate(basementOreDensity.ToString("0.#")));
            basementOreDensity = list.Slider(basementOreDensity, 0f, 12f);

            // The cave options are ALWAYS shown now. They used to be gated on Biomes!
            // Caverns being loaded, on the reasoning that dead sliders imply capabilities the
            // mod does not have - correct in itself, but it hid the whole feature from every
            // player without that mod and left them with a solid rock basement and no visible
            // reason why. Vanilla caves need no dependency, so the cave controls are core;
            // only the cavern BIOMES are conditional.
            list.Gap(6f);
            if (list.ButtonText("AB_BasementBiome".Translate(LabelForBiomeChoice())))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>
                {
                    new FloatMenuOption("AB_BasementBiomeVanilla".Translate(),
                        () => basementBiomeChoice = BiomesCavernsCompat.VanillaChoice),
                    new FloatMenuOption("AB_BasementBiomeNone".Translate(),
                        () => basementBiomeChoice = BiomesCavernsCompat.NoneChoice)
                };
                if (BiomesCavernsCompat.Active)
                {
                    opts.Insert(0, new FloatMenuOption("AB_BasementBiomeRandom".Translate(),
                        () => basementBiomeChoice = BiomesCavernsCompat.RandomChoice));
                    foreach (BiomeDef b in BiomesCavernsCompat.CavernBiomes())
                    {
                        BiomeDef local = b;
                        opts.Add(new FloatMenuOption(local.LabelCap,
                            () => basementBiomeChoice = local.defName));
                    }
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }

            if (basementBiomeChoice != BiomesCavernsCompat.NoneChoice)
            {
                list.Label("AB_CavernOpenness".Translate(cavernOpenness.ToString("0.00")));
                cavernOpenness = list.Slider(cavernOpenness, 0.1f, 0.6f);

                list.Label("AB_CavernChamberFreq".Translate(cavernChamberFreq.ToString("0.000")));
                cavernChamberFreq = list.Slider(cavernChamberFreq, 0.01f, 0.05f);

                // Formations are Biomes! Caverns scatterers; a vanilla cave has none, so the
                // slider would be a lie in both of the other two states.
                if (BiomesCavernsCompat.Active
                    && basementBiomeChoice != BiomesCavernsCompat.VanillaChoice)
                {
                    list.Label("AB_CavernFormations".Translate(cavernFormations.ToString("0.0")));
                    cavernFormations = list.Slider(cavernFormations, 0f, 2f);
                }
            }
        }

        // ---- diagnostics tab -------------------------------------------------

        /// <summary>
        /// Cross-level travel for NPCs: who may arrive on, and leave via, levels other than
        /// the surface. Twelve toggles plus a master switch, per the user's spec - "a robust
        /// set of toggles" - arranged as three category blocks so the rows read as sentences:
        /// raiders / arrive via upper levels / arrive via lower levels / leave via...
        ///
        /// ⚠ EVERY ONE OF THESE IS READ AT DECISION TIME, so flipping them mid-save is safe
        /// and takes effect on the next arrival or departure. The proportional band choice
        /// itself has no slider on purpose: weights come from each level's standable edge
        /// capacity, so terrain does the tuning (user's call).
        /// </summary>
        private void DoArrivals(Listing_Standard list)
        {
            list.CheckboxLabeled("AB_ArrivalsMaster".Translate(), ref crossLevelTravel,
                "AB_ArrivalsMasterTip".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            list.Label("AB_ArrivalsNote".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            list.GapLine();

            if (!crossLevelTravel)
            {
                return; // the twelve below are inert; hiding them says so better than text
            }

            DoArrivalCategory(list, "AB_CatRaiders",
                ref raiderArriveUpper, ref raiderArriveLower,
                ref raiderLeaveUpper, ref raiderLeaveLower);
            DoArrivalCategory(list, "AB_CatFriendlies",
                ref friendlyArriveUpper, ref friendlyArriveLower,
                ref friendlyLeaveUpper, ref friendlyLeaveLower);
            DoArrivalCategory(list, "AB_CatAnimals",
                ref animalArriveUpper, ref animalArriveLower,
                ref animalLeaveUpper, ref animalLeaveLower);
        }

        private static void DoArrivalCategory(Listing_Standard list, string headerKey,
            ref bool arriveUpper, ref bool arriveLower, ref bool leaveUpper,
            ref bool leaveLower)
        {
            Text.Font = GameFont.Medium;
            list.Label(headerKey.Translate());
            Text.Font = GameFont.Small;
            list.CheckboxLabeled("AB_ArriveUpper".Translate(), ref arriveUpper,
                "AB_ArriveUpperTip".Translate());
            list.CheckboxLabeled("AB_ArriveLower".Translate(), ref arriveLower,
                "AB_ArriveLowerTip".Translate());
            list.CheckboxLabeled("AB_LeaveUpper".Translate(), ref leaveUpper,
                "AB_LeaveUpperTip".Translate());
            list.CheckboxLabeled("AB_LeaveLower".Translate(), ref leaveLower,
                "AB_LeaveLowerTip".Translate());
            list.GapLine();
        }

        private void DoDiagnostics(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_TabDiagnostics".Translate());
            Text.Font = GameFont.Small;

            list.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging,
                "AB_VerboseLoggingTip".Translate());

            Color oldColor = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_SettingsGenerationNote".Translate());
            GUI.color = oldColor;

            // ---- subsystem health -------------------------------------------------
            //
            // The V1 panel had this and the V2 rework only rebuilt the DATA half:
            // ABGuard.AllSwitches / LastContext / LastCulprit / ReArm sat with ZERO
            // consumers while their doc comments claimed a settings readout existed.
            // This block is that readout, wired at last - which retroactively makes
            // those comments true. One row per kill switch: vanilla's checkbox texture
            // as the health tick (a Unicode tick glyph is a font gamble in RimWorld's
            // UI face), the trip context and culprit when down, and a re-arm button
            // that gives the subsystem another chance - if the fault persists it trips
            // again on the next error with a fresh report, which is ABGuard.ReArm's
            // documented contract.
            list.GapLine();
            Text.Font = GameFont.Medium;
            list.Label("AB_GuardHealthTitle".Translate());
            Text.Font = GameFont.Small;
            GUI.color = NoteDim;
            list.Label("AB_GuardHealthNote".Translate());
            GUI.color = oldColor;
            list.Gap(4f);
            ABGuardSwitch[] switches = ABGuard.AllSwitches;
            for (int i = 0; i < switches.Length; i++)
            {
                ABGuardSwitch s = switches[i];
                Rect row = list.GetRect(26f);
                Widgets.CheckboxDraw(row.x, row.y + 3f, s.IsOn, disabled: false, 20f);
                Rect name = new Rect(row.x + 26f, row.y, 150f, row.height);
                Widgets.Label(name, s.Name);
                if (s.IsOn)
                {
                    GUI.color = NoteDim;
                    Widgets.Label(new Rect(name.xMax + 4f, row.y,
                        row.width - name.xMax - 4f, row.height),
                        "AB_GuardRunning".Translate());
                    GUI.color = oldColor;
                    continue;
                }
                Rect btn = new Rect(row.xMax - 90f, row.y + 1f, 88f, 24f);
                Rect info = new Rect(name.xMax + 4f, row.y,
                    btn.x - name.xMax - 10f, row.height);
                string why = s.LastContext.NullOrEmpty()
                    ? "AB_GuardTrippedUnknown".Translate().ToString()
                    : s.LastContext;
                if (!s.LastCulprit.NullOrEmpty())
                {
                    why += " (" + "AB_GuardCulprit".Translate(s.LastCulprit) + ")";
                }
                GUI.color = WarnRed;
                Widgets.Label(info, why.Truncate(info.width));
                GUI.color = oldColor;
                TooltipHandler.TipRegion(info, why);
                if (Widgets.ButtonText(btn, "AB_GuardReArm".Translate()))
                {
                    ABGuard.ReArm(s);
                }
            }
        }
    }
}
