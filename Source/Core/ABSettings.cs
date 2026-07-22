using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mod settings. Reworked 2026-07-22 into five tabs (Generation / View and
    /// camera / Work and logistics / Combat and threats / Advanced), each with
    /// its own measured-height scroll view, per-tab reset, presets on the
    /// Generation tab, and a kill-switch status panel on Advanced.
    ///
    /// IMGUI discipline (lore-derived, do not undo):
    ///  - Tab switches are DEFERRED to after the content draw: a mid-frame
    ///    switch changes the control set between event passes and kills the
    ///    new tab's controls.
    ///  - Checkbox-gated rows draw from a visibility SNAPSHOT taken at the
    ///    top of the tab, so the control set stays constant across passes.
    ///  - Listing runs with maxOneColumn = true and the scroll gutter is
    ///    reserved unconditionally, so measured heights stay truthful and
    ///    content never wraps into a hidden second column.
    /// </summary>
    public class ABSettings : ModSettings
    {
        public bool verboseLogging;
        public bool showLiveBelow = true;
        public bool selectBelowInPlace = true;
        public bool showCeilingHint = true;
        public bool showLevelWidget = true;
        public bool oneColonistBar = true;
        public bool cameraFollowStairs = true;
        public bool cameraLockKeybind = true;
        // Depth-cue removal (2026-07-22, user direction): the below view
        // renders PLUMB (no south shift, no camera parallax) with a low base
        // dim, thin edge hairlines, and the wall-top reveal - no facades,
        // shadows, or lips. The painted-depth experiments are retired.
        public float belowDim = 0.06f;
        public bool drawSlabEdge = true;
        public bool drawWallReveal = true;
        public float wallRevealWidth = 0.5f;
        public float belowThingScale = 0.85f;
        public float climbTimeMultiplier = 1f;
        public bool crossLevelWork = true;
        public bool crossLevelOrders = true;
        public bool crossLevelCombat = true;
        public bool crossLevelAutoEngage = true;
        public bool idleReturnHome = true;
        public bool crossLevelHauling = true;
        public bool crossLevelSupply = true;
        public bool crossLevelNeeds = true;
        public bool crossLevelPrisoners = true;
        public bool crossLevelAnimalWander = true;
        public bool crossLevelRituals = true;
        public bool crossLevelSocial = true;
        public bool crossLevelPipes = true;
        public bool crossLevelTemperature = true;
        public bool podTransit = true;
        public bool threatBasementInfest;
        public bool threatSkyDrops;
        public float threatDivertChance = 0.25f;
        public bool columnWealth = true;
        public bool worldIntegration = true;
        // Biomes! Caverns basements (only meaningful when that mod is loaded).
        public bool cavernBasements = true;
        public string cavernBiome = BiomesCavernsCompat.RandomChoice;
        public float cavernOpenness = 0.35f;
        // Naturalistic mountain peaks on new sky levels.
        public bool naturalPeaks = true;
        // New sky levels take the surface map's biome (greenery, regrowth,
        // weather); off = the stark high-altitude AB_OpenSky placeholder.
        public bool skyBiomeInherit = true;
        public float peakSoilFraction = 0.15f;
        public float peakVegetation = 1f;
        // 2026-07-22 settings rework: generation knobs, applied when a level
        // is GENERATED (existing levels keep their look). Defaults equal the
        // old hardcoded constants, so untouched sliders change nothing.
        public float peakMeadowCutoff = 0.60f;   // rock-vs-meadow noise threshold
        public float peakMeadowScale = 0.024f;   // meadow noise frequency (feature size)
        public int peakTerraceMax = 4;           // deepest walkable edge band, cells
        public float peakOutcropDensity = 1f;    // outcrop lump multiplier
        public float peakTarns = 1f;             // mountain lake multiplier
        public float peakHiddenValleys = 1f;     // share of enclosed meadows kept sealed
        public float skyOreDensity = 6f;         // ore lumps per 10k mass cells
        public float basementOreDensity = 6f;    // ore lumps per 10k basement cells
        public float cavernChamberFreq = 0.02f;  // cavern chamber chance per worm step
        public float cavernFormations = 1f;      // BC stalagmite scatter multiplier

        // --- window state (session only, not scribed) ---
        private int curTab;
        private readonly Vector2[] tabScroll = new Vector2[5];
        private readonly float[] tabHeight = new float[5];

        private static readonly string[] TabKeys =
        {
            "AB_TabGeneration", "AB_TabView", "AB_TabWork", "AB_TabCombat", "AB_TabAdvanced"
        };

        private static readonly Color OkGreen = new Color(0.4f, 0.85f, 0.4f);
        private static readonly Color NoteDim = new Color(1f, 1f, 1f, 0.62f);

        public void DoWindowContents(Rect inRect)
        {
            // Vanilla TabDrawer draws the tab strip above the rect's top edge.
            Rect content = inRect;
            content.yMin += 42f;
            int clickedTab = -1;
            List<TabRecord> tabs = new List<TabRecord>(TabKeys.Length);
            for (int i = 0; i < TabKeys.Length; i++)
            {
                int tabIndex = i;
                tabs.Add(new TabRecord(TabKeys[i].Translate(), () => clickedTab = tabIndex, curTab == i));
            }
            Widgets.DrawMenuSection(content);
            TabDrawer.DrawTabs(content, tabs);
            Rect outRect = content.ContractedBy(9f);
            // Gutter reserved unconditionally so content width (and measured
            // height) never oscillates with scrollbar visibility.
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f,
                Mathf.Max(tabHeight[curTab], outRect.height));
            Widgets.BeginScrollView(outRect, ref tabScroll[curTab], viewRect);
            Listing_Standard listing = new Listing_Standard { maxOneColumn = true };
            listing.Begin(viewRect);
            switch (curTab)
            {
                case 0:
                    DoGenerationTab(listing);
                    break;
                case 1:
                    DoViewTab(listing);
                    break;
                case 2:
                    DoWorkTab(listing);
                    break;
                case 3:
                    DoCombatTab(listing);
                    break;
                default:
                    DoAdvancedTab(listing);
                    break;
            }
            TabResetRow(listing, curTab);
            tabHeight[curTab] = listing.CurHeight + 12f;
            listing.End();
            Widgets.EndScrollView();
            // Deferred switch: the drawn control set stays constant across
            // this frame's Layout/input/Repaint passes.
            if (clickedTab >= 0 && clickedTab != curTab)
            {
                curTab = clickedTab;
            }
        }

        // ------------------------------------------------------------------
        // Tab 0: Generation
        // ------------------------------------------------------------------
        private void DoGenerationTab(Listing_Standard listing)
        {
            bool showPeaks = naturalPeaks;
            bool showCaverns = BiomesCavernsCompat.Active && cavernBasements;

            GUI.color = NoteDim;
            listing.Label("AB_GenNote".Translate());
            GUI.color = Color.white;
            listing.Gap(4f);

            listing.Label("AB_Presets".Translate(), tooltip: "AB_PresetsTip".Translate());
            Rect presetRow = listing.GetRect(30f);
            float bw = (presetRow.width - 18f) / 4f;
            if (Widgets.ButtonText(new Rect(presetRow.x, presetRow.y, bw, 30f), "AB_PresetDefault".Translate()))
            {
                ResetGeneration();
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + bw + 6f, presetRow.y, bw, 30f), "AB_PresetDramatic".Translate()))
            {
                ApplyPeakPreset(0.54f, 0.018f, 5, 1.4f, 1.3f, 0.2f, 1.25f);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + (bw + 6f) * 2f, presetRow.y, bw, 30f), "AB_PresetSubtle".Translate()))
            {
                ApplyPeakPreset(0.68f, 0.03f, 2, 0.6f, 0.4f, 0.1f, 0.75f);
            }
            if (Widgets.ButtonText(new Rect(presetRow.x + (bw + 6f) * 3f, presetRow.y, bw, 30f), "AB_PresetLush".Translate()))
            {
                ApplyPeakPreset(0.52f, 0.02f, 4, 0.8f, 1.6f, 0.35f, 1.6f);
            }
            listing.GapLine(10f);

            listing.CheckboxLabeled("AB_SkyBiomeInherit".Translate(), ref skyBiomeInherit, "AB_SkyBiomeInheritTip".Translate());
            listing.CheckboxLabeled("AB_NaturalPeaks".Translate(), ref naturalPeaks, "AB_NaturalPeaksTip".Translate());
            if (showPeaks)
            {
                listing.Indent(16f);
                listing.ColumnWidth -= 16f;
                float opennessPct = Mathf.InverseLerp(0.75f, 0.45f, peakMeadowCutoff);
                listing.Label("AB_PlateauOpenness".Translate() + ": " + opennessPct.ToStringPercent(), tooltip: "AB_PlateauOpennessTip".Translate());
                peakMeadowCutoff = Mathf.Lerp(0.75f, 0.45f, listing.Slider(opennessPct, 0f, 1f));
                float scalePct = Mathf.InverseLerp(0.048f, 0.012f, peakMeadowScale);
                listing.Label("AB_MeadowScale".Translate() + ": " + scalePct.ToStringPercent(), tooltip: "AB_MeadowScaleTip".Translate());
                peakMeadowScale = Mathf.Lerp(0.048f, 0.012f, listing.Slider(scalePct, 0f, 1f));
                listing.Label("AB_TerraceMax".Translate() + ": " + peakTerraceMax, tooltip: "AB_TerraceMaxTip".Translate());
                peakTerraceMax = Mathf.RoundToInt(listing.Slider(peakTerraceMax, 1f, 6f));
                listing.Label("AB_OutcropDensity".Translate() + ": " + peakOutcropDensity.ToStringPercent(), tooltip: "AB_OutcropDensityTip".Translate());
                peakOutcropDensity = listing.Slider(peakOutcropDensity, 0f, 2f);
                listing.Label("AB_Tarns".Translate() + ": " + peakTarns.ToStringPercent(), tooltip: "AB_TarnsTip".Translate());
                peakTarns = listing.Slider(peakTarns, 0f, 2f);
                listing.Label("AB_HiddenValleys".Translate() + ": " + peakHiddenValleys.ToStringPercent(), tooltip: "AB_HiddenValleysTip".Translate());
                peakHiddenValleys = listing.Slider(peakHiddenValleys, 0f, 1f);
                listing.Label("AB_PeakSoil".Translate() + ": " + peakSoilFraction.ToStringPercent(), tooltip: "AB_PeakSoilTip".Translate());
                peakSoilFraction = listing.Slider(peakSoilFraction, 0f, 0.5f);
                listing.Label("AB_PeakVegetation".Translate() + ": " + peakVegetation.ToStringPercent(), tooltip: "AB_PeakVegetationTip".Translate());
                peakVegetation = listing.Slider(peakVegetation, 0f, 2f);
                listing.ColumnWidth += 16f;
                listing.Outdent(16f);
            }
            listing.Label("AB_SkyOre".Translate() + ": " + skyOreDensity.ToString("0.0"), tooltip: "AB_SkyOreTip".Translate());
            skyOreDensity = listing.Slider(skyOreDensity, 0f, 12f);
            listing.GapLine(10f);

            listing.Label("AB_BasementOre".Translate() + ": " + basementOreDensity.ToString("0.0"), tooltip: "AB_BasementOreTip".Translate());
            basementOreDensity = listing.Slider(basementOreDensity, 0f, 12f);
            if (BiomesCavernsCompat.Active)
            {
                listing.CheckboxLabeled("AB_CavernBasements".Translate(), ref cavernBasements, "AB_CavernBasementsTip".Translate());
                if (showCaverns)
                {
                    listing.Indent(16f);
                    listing.ColumnWidth -= 16f;
                    string current = cavernBiome == BiomesCavernsCompat.RandomChoice
                        ? "AB_CavernBiomeRandom".Translate().ToString()
                        : (DefDatabase<BiomeDef>.GetNamedSilentFail(cavernBiome)?.LabelCap.ToString() ?? cavernBiome);
                    if (listing.ButtonTextLabeled("AB_CavernBiome".Translate(), current, tooltip: "AB_CavernBiomeTip".Translate()))
                    {
                        List<FloatMenuOption> options = new List<FloatMenuOption>
                        {
                            new FloatMenuOption("AB_CavernBiomeRandom".Translate(), delegate
                            {
                                cavernBiome = BiomesCavernsCompat.RandomChoice;
                            })
                        };
                        List<BiomeDef> pool = BiomesCavernsCompat.CavernBiomes();
                        for (int i = 0; i < pool.Count; i++)
                        {
                            BiomeDef b = pool[i];
                            options.Add(new FloatMenuOption(b.LabelCap, delegate
                            {
                                cavernBiome = b.defName;
                            }));
                        }
                        Find.WindowStack.Add(new FloatMenu(options));
                    }
                    listing.Label("AB_CavernOpenness".Translate() + ": " + cavernOpenness.ToStringPercent(), tooltip: "AB_CavernOpennessTip".Translate());
                    cavernOpenness = listing.Slider(cavernOpenness, 0.1f, 0.6f);
                    listing.Label("AB_ChamberFreq".Translate() + ": " + (cavernChamberFreq * 100f).ToString("0.0"), tooltip: "AB_ChamberFreqTip".Translate());
                    cavernChamberFreq = listing.Slider(cavernChamberFreq, 0.01f, 0.05f);
                    listing.Label("AB_CavernFormations".Translate() + ": " + cavernFormations.ToStringPercent(), tooltip: "AB_CavernFormationsTip".Translate());
                    cavernFormations = listing.Slider(cavernFormations, 0f, 2f);
                    listing.ColumnWidth += 16f;
                    listing.Outdent(16f);
                }
            }
        }

        /// <summary>Peak-look preset: cutoff, noise scale, terrace, outcrops,
        /// tarns, soil, vegetation. Hidden valleys and ore stay untouched.</summary>
        private void ApplyPeakPreset(float cutoff, float scale, int terrace, float outcrops,
            float tarns, float soil, float vegetation)
        {
            naturalPeaks = true;
            peakMeadowCutoff = cutoff;
            peakMeadowScale = scale;
            peakTerraceMax = terrace;
            peakOutcropDensity = outcrops;
            peakTarns = tarns;
            peakSoilFraction = soil;
            peakVegetation = vegetation;
        }

        // ------------------------------------------------------------------
        // Tab 1: View and camera
        // ------------------------------------------------------------------
        private void DoViewTab(Listing_Standard listing)
        {
            bool showRevealWidth = drawWallReveal;

            listing.CheckboxLabeled("AB_ShowLiveBelow".Translate(), ref showLiveBelow, "AB_ShowLiveBelowTip".Translate());
            listing.Label("AB_BelowDim".Translate() + ": " + belowDim.ToStringPercent(), tooltip: "AB_BelowDimTip".Translate());
            belowDim = listing.Slider(belowDim, 0f, 0.8f);
            listing.CheckboxLabeled("AB_SlabEdge".Translate(), ref drawSlabEdge, "AB_SlabEdgeTip".Translate());
            listing.CheckboxLabeled("AB_WallReveal".Translate(), ref drawWallReveal, "AB_WallRevealTip".Translate());
            if (showRevealWidth)
            {
                listing.Indent(16f);
                listing.ColumnWidth -= 16f;
                listing.Label("AB_WallRevealWidth".Translate() + ": " + wallRevealWidth.ToString("0.00"), tooltip: "AB_WallRevealWidthTip".Translate());
                float newRevealWidth = listing.Slider(wallRevealWidth, 0.25f, 0.6f);
                if (Mathf.Abs(newRevealWidth - wallRevealWidth) > 0.0005f)
                {
                    // Strip geometry bakes the width into clipped verts;
                    // reprint so the slider applies live.
                    DirtyBelowThingsLayers();
                }
                wallRevealWidth = newRevealWidth;
                listing.ColumnWidth += 16f;
                listing.Outdent(16f);
            }
            listing.Label("AB_BelowScale".Translate() + ": " + belowThingScale.ToStringPercent(), tooltip: "AB_BelowScaleTip".Translate());
            float newBelowScale = listing.Slider(belowThingScale, 0.7f, 1f);
            if (Mathf.Abs(newBelowScale - belowThingScale) > 0.0005f)
            {
                // Printed below-things bake the scale into their vertices;
                // reprint the layers so the slider applies live.
                DirtyBelowThingsLayers();
            }
            belowThingScale = newBelowScale;
            listing.GapLine(10f);
            listing.CheckboxLabeled("AB_SelectBelowInPlace".Translate(), ref selectBelowInPlace, "AB_SelectBelowInPlaceTip".Translate());
            listing.CheckboxLabeled("AB_ShowCeilingHint".Translate(), ref showCeilingHint, "AB_ShowCeilingHintTip".Translate());
            listing.CheckboxLabeled("AB_ShowLevelWidget".Translate(), ref showLevelWidget, "AB_ShowLevelWidgetTip".Translate());
            listing.CheckboxLabeled("AB_OneColonistBar".Translate(), ref oneColonistBar, "AB_OneColonistBarTip".Translate());
            listing.CheckboxLabeled("AB_CameraFollowStairs".Translate(), ref cameraFollowStairs, "AB_CameraFollowStairsTip".Translate());
            listing.CheckboxLabeled("AB_CameraLockKeybind".Translate(), ref cameraLockKeybind, "AB_CameraLockKeybindTip".Translate());
        }

        // ------------------------------------------------------------------
        // Tab 2: Work and logistics
        // ------------------------------------------------------------------
        private void DoWorkTab(Listing_Standard listing)
        {
            listing.CheckboxLabeled("AB_CrossLevelWork".Translate(), ref crossLevelWork, "AB_CrossLevelWorkTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelOrders".Translate(), ref crossLevelOrders, "AB_CrossLevelOrdersTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelHauling".Translate(), ref crossLevelHauling, "AB_CrossLevelHaulingTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelSupply".Translate(), ref crossLevelSupply, "AB_CrossLevelSupplyTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelNeeds".Translate(), ref crossLevelNeeds, "AB_CrossLevelNeedsTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPrisoners".Translate(), ref crossLevelPrisoners, "AB_CrossLevelPrisonersTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelSocial".Translate(), ref crossLevelSocial, "AB_CrossLevelSocialTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelRituals".Translate(), ref crossLevelRituals, "AB_CrossLevelRitualsTip".Translate());
            listing.CheckboxLabeled("AB_AnimalWander".Translate(), ref crossLevelAnimalWander, "AB_AnimalWanderTip".Translate());
            listing.CheckboxLabeled("AB_IdleReturnHome".Translate(), ref idleReturnHome, "AB_IdleReturnHomeTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPipes".Translate(), ref crossLevelPipes, "AB_CrossLevelPipesTip".Translate());
            // crossLevelTemperature checkbox removed with the stairwell heat
            // exchange (user directive); the field stays scribed for old configs.
            listing.GapLine(10f);
            listing.Label("AB_ClimbTime".Translate() + ": " + climbTimeMultiplier.ToStringPercent(), tooltip: "AB_ClimbTimeTip".Translate());
            climbTimeMultiplier = listing.Slider(climbTimeMultiplier, 0.25f, 3f);
        }

        // ------------------------------------------------------------------
        // Tab 3: Combat and threats
        // ------------------------------------------------------------------
        private void DoCombatTab(Listing_Standard listing)
        {
            bool showAutoEngage = crossLevelCombat;
            bool showDivert = threatBasementInfest || threatSkyDrops;

            listing.CheckboxLabeled("AB_CrossLevelCombat".Translate(), ref crossLevelCombat, "AB_CrossLevelCombatTip".Translate());
            if (showAutoEngage)
            {
                listing.Indent(16f);
                listing.ColumnWidth -= 16f;
                listing.CheckboxLabeled("AB_CrossLevelAutoEngage".Translate(), ref crossLevelAutoEngage, "AB_CrossLevelAutoEngageTip".Translate());
                listing.ColumnWidth += 16f;
                listing.Outdent(16f);
            }
            listing.CheckboxLabeled("AB_PodTransit".Translate(), ref podTransit, "AB_PodTransitTip".Translate());
            listing.GapLine(10f);
            listing.CheckboxLabeled("AB_ThreatBasementInfest".Translate(), ref threatBasementInfest, "AB_ThreatBasementInfestTip".Translate());
            listing.CheckboxLabeled("AB_ThreatSkyDrops".Translate(), ref threatSkyDrops, "AB_ThreatSkyDropsTip".Translate());
            if (showDivert)
            {
                listing.Indent(16f);
                listing.ColumnWidth -= 16f;
                listing.Label("AB_ThreatDivertChance".Translate() + ": " + threatDivertChance.ToStringPercent(), tooltip: "AB_ThreatDivertChanceTip".Translate());
                threatDivertChance = listing.Slider(threatDivertChance, 0.05f, 1f);
                listing.ColumnWidth += 16f;
                listing.Outdent(16f);
            }
        }

        // ------------------------------------------------------------------
        // Tab 4: Advanced
        // ------------------------------------------------------------------
        private void DoAdvancedTab(Listing_Standard listing)
        {
            listing.CheckboxLabeled("AB_ColumnWealth".Translate(), ref columnWealth, "AB_ColumnWealthTip".Translate());
            listing.CheckboxLabeled("AB_WorldIntegration".Translate(), ref worldIntegration, "AB_WorldIntegrationTip".Translate());
            listing.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging, "AB_VerboseLoggingTip".Translate());
            listing.GapLine(10f);

            listing.Label("AB_GuardPanel".Translate(), tooltip: "AB_GuardPanelTip".Translate());
            ABGuardSwitch[] guards = ABGuard.AllSwitches;
            int tripped = 0;
            for (int i = 0; i < guards.Length; i++)
            {
                ABGuardSwitch g = guards[i];
                if (g.IsOn)
                {
                    continue;
                }
                tripped++;
                Rect row = listing.GetRect(28f);
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(new Rect(row.x, row.y + 3f, row.width - 100f, 24f),
                    "AB_GuardTripped".Translate(g.Name, g.LastContext ?? "?"));
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 94f, row.y + 1f, 92f, 26f), "AB_ReArm".Translate()))
                {
                    ABGuard.ReArm(g);
                }
            }
            if (tripped == 0)
            {
                GUI.color = OkGreen;
                listing.Label("AB_GuardAllGreen".Translate(guards.Length));
                GUI.color = Color.white;
            }
            listing.GapLine(10f);

            if (listing.ButtonText("AB_ResetAll".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "AB_ResetAllConfirm".Translate(), ResetAll, destructive: true));
            }
        }

        // ------------------------------------------------------------------
        // Resets
        // ------------------------------------------------------------------
        private void TabResetRow(Listing_Standard listing, int tab)
        {
            listing.Gap(14f);
            if (listing.ButtonText("AB_ResetTab".Translate()))
            {
                ResetTab(tab);
            }
        }

        private void ResetTab(int tab)
        {
            switch (tab)
            {
                case 0:
                    ResetGeneration();
                    break;
                case 1:
                    ResetView();
                    break;
                case 2:
                    ResetWork();
                    break;
                case 3:
                    ResetCombat();
                    break;
                default:
                    ResetAdvanced();
                    break;
            }
        }

        // Defaults below must mirror the field initializers above.
        private void ResetGeneration()
        {
            skyBiomeInherit = true;
            naturalPeaks = true;
            peakMeadowCutoff = 0.60f;
            peakMeadowScale = 0.024f;
            peakTerraceMax = 4;
            peakOutcropDensity = 1f;
            peakTarns = 1f;
            peakHiddenValleys = 1f;
            peakSoilFraction = 0.15f;
            peakVegetation = 1f;
            skyOreDensity = 6f;
            basementOreDensity = 6f;
            cavernBasements = true;
            cavernBiome = BiomesCavernsCompat.RandomChoice;
            cavernOpenness = 0.35f;
            cavernChamberFreq = 0.02f;
            cavernFormations = 1f;
        }

        private void ResetView()
        {
            showLiveBelow = true;
            belowDim = 0.06f;
            drawSlabEdge = true;
            drawWallReveal = true;
            wallRevealWidth = 0.5f;
            belowThingScale = 0.85f;
            selectBelowInPlace = true;
            showCeilingHint = true;
            showLevelWidget = true;
            oneColonistBar = true;
            cameraFollowStairs = true;
            cameraLockKeybind = true;
            DirtyBelowThingsLayers();
        }

        private void ResetWork()
        {
            crossLevelWork = true;
            crossLevelOrders = true;
            crossLevelHauling = true;
            crossLevelSupply = true;
            crossLevelNeeds = true;
            crossLevelPrisoners = true;
            crossLevelSocial = true;
            crossLevelRituals = true;
            crossLevelAnimalWander = true;
            idleReturnHome = true;
            crossLevelPipes = true;
            climbTimeMultiplier = 1f;
        }

        private void ResetCombat()
        {
            crossLevelCombat = true;
            crossLevelAutoEngage = true;
            podTransit = true;
            threatBasementInfest = false;
            threatSkyDrops = false;
            threatDivertChance = 0.25f;
        }

        private void ResetAdvanced()
        {
            columnWealth = true;
            worldIntegration = true;
            verboseLogging = false;
        }

        private void ResetAll()
        {
            ResetGeneration();
            ResetView();
            ResetWork();
            ResetCombat();
            ResetAdvanced();
        }

        private static void DirtyBelowThingsLayers()
        {
            if (Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                LevelComp comp = maps[i].Levels();
                if (comp != null && comp.level > 0)
                {
                    maps[i].mapDrawer.WholeMapChanged((ulong)ABDefOf.AB_BelowThings);
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref showLiveBelow, "showLiveBelow", true);
            Scribe_Values.Look(ref selectBelowInPlace, "selectBelowInPlace", true);
            Scribe_Values.Look(ref showCeilingHint, "showCeilingHint", true);
            Scribe_Values.Look(ref showLevelWidget, "showLevelWidget", true);
            Scribe_Values.Look(ref oneColonistBar, "oneColonistBar", true);
            Scribe_Values.Look(ref cameraFollowStairs, "cameraFollowStairs", true);
            Scribe_Values.Look(ref cameraLockKeybind, "cameraLockKeybind", true);
            Scribe_Values.Look(ref climbTimeMultiplier, "climbTimeMultiplier", 1f);
            // Key renamed (belowDimLight -> belowDimHeight) with the height
            // rework so the old pit-strength dim does not carry over.
            Scribe_Values.Look(ref belowDim, "belowDimHeight", 0.06f);
            Scribe_Values.Look(ref drawSlabEdge, "drawSlabEdge", true);
            Scribe_Values.Look(ref drawWallReveal, "drawWallReveal", true);
            Scribe_Values.Look(ref wallRevealWidth, "wallRevealWidth", 0.5f);
            Scribe_Values.Look(ref belowThingScale, "belowThingScale", 0.85f);
            Scribe_Values.Look(ref crossLevelWork, "crossLevelWork", true);
            Scribe_Values.Look(ref crossLevelOrders, "crossLevelOrders", true);
            Scribe_Values.Look(ref crossLevelCombat, "crossLevelCombat", true);
            Scribe_Values.Look(ref crossLevelAutoEngage, "crossLevelAutoEngage", true);
            Scribe_Values.Look(ref idleReturnHome, "idleReturnHome", true);
            Scribe_Values.Look(ref crossLevelHauling, "crossLevelHauling", true);
            Scribe_Values.Look(ref crossLevelSupply, "crossLevelSupply", true);
            Scribe_Values.Look(ref crossLevelNeeds, "crossLevelNeeds", true);
            Scribe_Values.Look(ref crossLevelPrisoners, "crossLevelPrisoners", true);
            Scribe_Values.Look(ref crossLevelAnimalWander, "crossLevelAnimalWander", true);
            Scribe_Values.Look(ref crossLevelRituals, "crossLevelRituals", true);
            Scribe_Values.Look(ref crossLevelSocial, "crossLevelSocial", true);
            Scribe_Values.Look(ref crossLevelPipes, "crossLevelPipes", true);
            Scribe_Values.Look(ref podTransit, "podTransit", true);
            Scribe_Values.Look(ref crossLevelTemperature, "crossLevelTemperature", true);
            Scribe_Values.Look(ref threatBasementInfest, "threatBasementInfest", false);
            Scribe_Values.Look(ref threatSkyDrops, "threatSkyDrops", false);
            Scribe_Values.Look(ref threatDivertChance, "threatDivertChance", 0.25f);
            Scribe_Values.Look(ref columnWealth, "columnWealth", true);
            Scribe_Values.Look(ref worldIntegration, "worldIntegration", true);
            Scribe_Values.Look(ref cavernBasements, "cavernBasements", true);
            Scribe_Values.Look(ref cavernBiome, "cavernBiome", BiomesCavernsCompat.RandomChoice);
            Scribe_Values.Look(ref cavernOpenness, "cavernOpenness", 0.35f);
            Scribe_Values.Look(ref naturalPeaks, "naturalPeaks", true);
            Scribe_Values.Look(ref skyBiomeInherit, "skyBiomeInherit", true);
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakVegetation, "peakVegetation", 1f);
            Scribe_Values.Look(ref peakMeadowCutoff, "peakMeadowCutoff", 0.60f);
            Scribe_Values.Look(ref peakMeadowScale, "peakMeadowScale", 0.024f);
            Scribe_Values.Look(ref peakTerraceMax, "peakTerraceMax", 4);
            Scribe_Values.Look(ref peakOutcropDensity, "peakOutcropDensity", 1f);
            Scribe_Values.Look(ref peakTarns, "peakTarns", 1f);
            Scribe_Values.Look(ref peakHiddenValleys, "peakHiddenValleys", 1f);
            Scribe_Values.Look(ref skyOreDensity, "skyOreDensity", 6f);
            Scribe_Values.Look(ref basementOreDensity, "basementOreDensity", 6f);
            Scribe_Values.Look(ref cavernChamberFreq, "cavernChamberFreq", 0.02f);
            Scribe_Values.Look(ref cavernFormations, "cavernFormations", 1f);
        }
    }
}
