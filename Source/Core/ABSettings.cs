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
        /// the budget exists: 1.6's path grid is an IJobParallelFor over EVERY cell, and a
        /// banded map is every level plus gutters tall, so the stacked total is what the
        /// pathfinder pays.
        ///
        /// ONLY EVER SET TRUE THROUGH THE CONFIRMATION DIALOG. The checkbox writes to a
        /// local, so the state itself encodes "the player read the warning and accepted it"
        /// and no separate acknowledgement flag is needed. Turning it back OFF is immediate -
        /// there is never a reason to make returning to a supported configuration harder.</summary>
        public bool unclampMapSize;

        // ---- climate -------------------------------------------------------
        //
        // Per-level temperature offsets and wind factors. SNAPSHOTTED onto ABBandMap at
        // generation (see ABBandMap.SnapshotClimate) so changing them never re-climates an
        // existing colony. Always three entries each: the level plan tops out at 3 up and
        // 3 down, and showing all six regardless of the current plan lets a player tune a
        // layout before they choose it.

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
        public void EnsureClimateLists()
        {
            skyTempOffsets = Fix(skyTempOffsets, ABBandEnv.DefaultSkyTempOffsets);
            deepTempOffsets = Fix(deepTempOffsets, ABBandEnv.DefaultDeepTempOffsets);
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

        /// <summary>Levels above the surface (0-3).</summary>
        public int upperLevels = 1;

        /// <summary>Levels below the surface (0-3).</summary>
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

        // --------------------------------------------------------------------

        /// <summary>Display label for the current basement-biome choice, resolving a stored
        /// defName to its in-game label so the button never shows a raw defName.</summary>
        private string LabelForBiomeChoice()
        {
            if (basementBiomeChoice == BiomesCavernsCompat.NoneChoice)
            {
                return "AB_BasementBiomeNone".Translate();
            }
            if (string.IsNullOrEmpty(basementBiomeChoice)
                || basementBiomeChoice == BiomesCavernsCompat.RandomChoice)
            {
                return "AB_BasementBiomeRandom".Translate();
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
            Scribe_Values.Look(ref upperLevels, "upperLevels", 1);
            Scribe_Values.Look(ref lowerLevels, "lowerLevels", 1);
            Scribe_Values.Look(ref naturalPeaks, "naturalPeaks", true);
            Scribe_Values.Look(ref skyBiomeInherit, "skyBiomeInherit", true);
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakMeadowCutoff, "peakMeadowCutoff", 0.60f);
            Scribe_Values.Look(ref peakMeadowScale, "peakMeadowScale", 0.024f);
            Scribe_Values.Look(ref peakTerraceMax, "peakTerraceMax", 4);
            Scribe_Values.Look(ref skyVegetationDensity, "skyVegetationDensity", 1f);
            Scribe_Values.Look(ref basementOreDensity, "basementOreDensity", 6f);
            Scribe_Values.Look(ref basementBiomeChoice, "basementBiomeChoice",
                BiomesCavernsCompat.RandomChoice);
            Scribe_Values.Look(ref cavernOpenness, "cavernOpenness", 0.3f);
            Scribe_Values.Look(ref cavernChamberFreq, "cavernChamberFreq", 0.02f);
            Scribe_Values.Look(ref cavernFormations, "cavernFormations", 1f);
            Scribe_Values.Look(ref depthFalloff, "depthFalloff", true);
            Scribe_Values.Look(ref depthFalloffPerLevel, "depthFalloffPerLevel",
                ABDepthView.DefaultFalloff);
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
            Diagnostics
        }

        private Tab tab = Tab.Performance;

        private Vector2 scroll;

        /// <summary>Content height PER TAB, measured from the previous frame's listing. A
        /// scroll view needs its content height up front and a Listing_Standard only knows
        /// how tall it was after drawing, so the value always lags a frame. Kept per tab
        /// rather than shared: one shared value meant switching from a short tab to a tall
        /// one sized the scroll region from the WRONG tab for a frame.</summary>
        private readonly float[] viewHeights = { 600f, 600f, 600f, 600f, 600f, 600f };

        private static readonly Color TabActive = new Color(0.32f, 0.36f, 0.42f);

        /// <summary>
        /// A hand-drawn tab row rather than <c>TabDrawer.DrawTabs</c>.
        ///
        /// TabDrawer paints its row ABOVE the rect it is handed, which means it depends on
        /// the host window leaving free space there. Inside <c>Dialog_ModSettings</c> that
        /// space is not ours, and the result in play was tabs that rendered but never
        /// switched - every pane except the default came up blank, with no exception logged
        /// because nothing had thrown. Rather than keep guessing at a widget whose layout
        /// contract is owned by the host dialog, the row is drawn explicitly: five buttons in
        /// a rect we allocated, with the active one tinted. Plainer than vanilla tabs and it
        /// cannot silently fail.
        /// </summary>
        public void DoWindowContents(Rect inRect)
        {
            EnsureClimateLists();

            const float TabH = 32f;
            Rect tabRow = new Rect(inRect.x, inRect.y, inRect.width, TabH);
            DrawTabButton(tabRow, 0, 6, "AB_TabPerformance", Tab.Performance);
            DrawTabButton(tabRow, 1, 6, "AB_TabVisuals", Tab.Visuals);
            DrawTabButton(tabRow, 2, 6, "AB_TabClimate", Tab.Climate);
            DrawTabButton(tabRow, 3, 6, "AB_TabSky", Tab.Sky);
            DrawTabButton(tabRow, 4, 6, "AB_TabBasement", Tab.Basement);
            DrawTabButton(tabRow, 5, 6, "AB_TabDiagnostics", Tab.Diagnostics);

            Rect body = new Rect(inRect.x, inRect.y + TabH + 6f,
                inRect.width, inRect.height - TabH - 6f);
            Widgets.DrawMenuSection(body);

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

        private void DrawTabButton(Rect row, int slot, int count, string key, Tab which)
        {
            float w = row.width / count;
            Rect r = new Rect(row.x + w * slot, row.y, w - 4f, row.height);
            if (tab == which)
            {
                Widgets.DrawBoxSolid(r, TabActive);
            }
            if (Widgets.ButtonText(r, key.Translate(), drawBackground: tab != which))
            {
                tab = which;
                scroll = Vector2.zero; // a fresh tab starts at its top, not where the last one sat
            }
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
            list.Label("AB_LevelsDefault".Translate(upperLevels, lowerLevels,
                ABMapSizeLimit.BandCount));

            Color dim = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_BudgetExplain".Translate(budget));
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

            Rect presetRow = list.GetRect(30f);
            float w = presetRow.width / 3f - 6f;
            if (Widgets.ButtonText(new Rect(presetRow.x, presetRow.y, w, 30f),
                "AB_PresetGentle".Translate()))
            {
                ApplyPreset(0);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + w + 9f, presetRow.y, w, 30f),
                "AB_PresetStandard".Translate()))
            {
                ApplyPreset(1);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + (w + 9f) * 2f, presetRow.y, w, 30f),
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
        }

        // ---- basement tab ----------------------------------------------------

        private void DoBasement(Listing_Standard list)
        {
            Text.Font = GameFont.Medium;
            list.Label("AB_BasementHeading".Translate());
            Text.Font = GameFont.Small;

            list.Label("AB_BasementOreDensity".Translate(basementOreDensity.ToString("0.#")));
            basementOreDensity = list.Slider(basementOreDensity, 0f, 12f);

            // Cavern options only exist when Biomes! Caverns is actually loaded - showing
            // dead sliders would imply the mod does something it cannot.
            if (BiomesCavernsCompat.Active)
            {
                list.Gap(6f);
                if (list.ButtonText("AB_BasementBiome".Translate(LabelForBiomeChoice())))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("AB_BasementBiomeRandom".Translate(),
                            () => basementBiomeChoice = BiomesCavernsCompat.RandomChoice),
                        new FloatMenuOption("AB_BasementBiomeNone".Translate(),
                            () => basementBiomeChoice = BiomesCavernsCompat.NoneChoice)
                    };
                    foreach (BiomeDef b in BiomesCavernsCompat.CavernBiomes())
                    {
                        BiomeDef local = b;
                        opts.Add(new FloatMenuOption(local.LabelCap,
                            () => basementBiomeChoice = local.defName));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }

                if (basementBiomeChoice != BiomesCavernsCompat.NoneChoice)
                {
                    list.Label("AB_CavernOpenness".Translate(cavernOpenness.ToString("0.00")));
                    cavernOpenness = list.Slider(cavernOpenness, 0.1f, 0.6f);

                    list.Label("AB_CavernChamberFreq".Translate(cavernChamberFreq.ToString("0.000")));
                    cavernChamberFreq = list.Slider(cavernChamberFreq, 0.01f, 0.05f);

                    list.Label("AB_CavernFormations".Translate(cavernFormations.ToString("0.0")));
                    cavernFormations = list.Slider(cavernFormations, 0f, 2f);
                }
            }
        }

        // ---- diagnostics tab -------------------------------------------------

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
        }
    }
}
