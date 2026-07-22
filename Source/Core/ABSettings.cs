using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
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
        public float belowDim = 0.12f;
        public float belowDepthShift = 0.25f;
        public bool drawSlabEdge = true;
        public bool drawWallReveal = true;
        public float wallRevealWidth = 0.5f;
        public bool drawWallFacade = true;
        public float belowThingScale = 0.85f;
        public bool belowParallax;
        public float belowParallaxStrength = 0.35f;
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
        public float peakSoilFraction = 0.15f;
        public float peakVegetation = 1f;

        private Vector2 settingsScroll;

        public void DoWindowContents(Rect inRect)
        {
            // The option list outgrew the window: scroll it. Height is a
            // generous static estimate; excess just scrolls empty.
            Rect view = new Rect(0f, 0f, inRect.width - 20f, 2000f);
            Widgets.BeginScrollView(inRect, ref settingsScroll, view);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(view);
            listing.CheckboxLabeled("AB_CrossLevelWork".Translate(), ref crossLevelWork, "AB_CrossLevelWorkTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelOrders".Translate(), ref crossLevelOrders, "AB_CrossLevelOrdersTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelCombat".Translate(), ref crossLevelCombat, "AB_CrossLevelCombatTip".Translate());
            if (crossLevelCombat)
            {
                listing.CheckboxLabeled("AB_CrossLevelAutoEngage".Translate(), ref crossLevelAutoEngage, "AB_CrossLevelAutoEngageTip".Translate());
            }
            listing.CheckboxLabeled("AB_IdleReturnHome".Translate(), ref idleReturnHome, "AB_IdleReturnHomeTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelHauling".Translate(), ref crossLevelHauling, "AB_CrossLevelHaulingTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelSupply".Translate(), ref crossLevelSupply, "AB_CrossLevelSupplyTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelNeeds".Translate(), ref crossLevelNeeds, "AB_CrossLevelNeedsTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPrisoners".Translate(), ref crossLevelPrisoners, "AB_CrossLevelPrisonersTip".Translate());
            listing.CheckboxLabeled("AB_AnimalWander".Translate(), ref crossLevelAnimalWander, "AB_AnimalWanderTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelRituals".Translate(), ref crossLevelRituals, "AB_CrossLevelRitualsTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelSocial".Translate(), ref crossLevelSocial, "AB_CrossLevelSocialTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPipes".Translate(), ref crossLevelPipes, "AB_CrossLevelPipesTip".Translate());
            listing.CheckboxLabeled("AB_PodTransit".Translate(), ref podTransit, "AB_PodTransitTip".Translate());
            // crossLevelTemperature checkbox removed with the stairwell heat
            // exchange (user directive); the field stays scribed for old configs.
            listing.CheckboxLabeled("AB_ShowLiveBelow".Translate(), ref showLiveBelow, "AB_ShowLiveBelowTip".Translate());
            listing.CheckboxLabeled("AB_SelectBelowInPlace".Translate(), ref selectBelowInPlace, "AB_SelectBelowInPlaceTip".Translate());
            listing.CheckboxLabeled("AB_ShowCeilingHint".Translate(), ref showCeilingHint, "AB_ShowCeilingHintTip".Translate());
            listing.CheckboxLabeled("AB_ShowLevelWidget".Translate(), ref showLevelWidget, "AB_ShowLevelWidgetTip".Translate());
            listing.CheckboxLabeled("AB_OneColonistBar".Translate(), ref oneColonistBar, "AB_OneColonistBarTip".Translate());
            listing.CheckboxLabeled("AB_CameraFollowStairs".Translate(), ref cameraFollowStairs, "AB_CameraFollowStairsTip".Translate());
            listing.CheckboxLabeled("AB_CameraLockKeybind".Translate(), ref cameraLockKeybind, "AB_CameraLockKeybindTip".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("AB_ThreatBasementInfest".Translate(), ref threatBasementInfest, "AB_ThreatBasementInfestTip".Translate());
            listing.CheckboxLabeled("AB_ThreatSkyDrops".Translate(), ref threatSkyDrops, "AB_ThreatSkyDropsTip".Translate());
            if (threatBasementInfest || threatSkyDrops)
            {
                listing.Label("AB_ThreatDivertChance".Translate() + ": " + threatDivertChance.ToStringPercent(), tooltip: "AB_ThreatDivertChanceTip".Translate());
                threatDivertChance = listing.Slider(threatDivertChance, 0.05f, 1f);
            }
            listing.GapLine();
            listing.CheckboxLabeled("AB_NaturalPeaks".Translate(), ref naturalPeaks, "AB_NaturalPeaksTip".Translate());
            if (naturalPeaks)
            {
                listing.Label("AB_PeakSoil".Translate() + ": " + peakSoilFraction.ToStringPercent(), tooltip: "AB_PeakSoilTip".Translate());
                peakSoilFraction = listing.Slider(peakSoilFraction, 0f, 0.5f);
                listing.Label("AB_PeakVegetation".Translate() + ": " + peakVegetation.ToStringPercent(), tooltip: "AB_PeakVegetationTip".Translate());
                peakVegetation = listing.Slider(peakVegetation, 0f, 2f);
            }
            if (BiomesCavernsCompat.Active)
            {
                listing.CheckboxLabeled("AB_CavernBasements".Translate(), ref cavernBasements, "AB_CavernBasementsTip".Translate());
                if (cavernBasements)
                {
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
                }
            }
            listing.GapLine();
            listing.CheckboxLabeled("AB_ColumnWealth".Translate(), ref columnWealth, "AB_ColumnWealthTip".Translate());
            listing.CheckboxLabeled("AB_WorldIntegration".Translate(), ref worldIntegration, "AB_WorldIntegrationTip".Translate());
            listing.GapLine();
            listing.Label("AB_BelowDim".Translate() + ": " + belowDim.ToStringPercent(), tooltip: "AB_BelowDimTip".Translate());
            belowDim = listing.Slider(belowDim, 0f, 0.8f);
            listing.Label("AB_BelowDepthShift".Translate() + ": " + belowDepthShift.ToString("0.00"), tooltip: "AB_BelowDepthShiftTip".Translate());
            float newDepthShift = listing.Slider(belowDepthShift, 0f, 0.6f);
            if (Mathf.Abs(newDepthShift - belowDepthShift) > 0.0005f)
            {
                // The wall facade bakes the south shift into its clipped
                // verts; reprint so the slider applies live (amortized by
                // MapDrawer like the other reprint sliders).
                DirtyBelowThingsLayers();
            }
            belowDepthShift = newDepthShift;
            listing.CheckboxLabeled("AB_SlabEdge".Translate(), ref drawSlabEdge, "AB_SlabEdgeTip".Translate());
            listing.CheckboxLabeled("AB_WallReveal".Translate(), ref drawWallReveal, "AB_WallRevealTip".Translate());
            if (drawWallReveal)
            {
                listing.Label("AB_WallRevealWidth".Translate() + ": " + wallRevealWidth.ToString("0.00"), tooltip: "AB_WallRevealWidthTip".Translate());
                float newRevealWidth = listing.Slider(wallRevealWidth, 0.25f, 0.6f);
                if (Mathf.Abs(newRevealWidth - wallRevealWidth) > 0.0005f)
                {
                    // Strip geometry bakes the width into clipped verts;
                    // reprint so the slider applies live (same amortized
                    // path the below scale slider uses).
                    DirtyBelowThingsLayers();
                }
                wallRevealWidth = newRevealWidth;
            }
            listing.CheckboxLabeled("AB_WallFacade".Translate(), ref drawWallFacade, "AB_WallFacadeTip".Translate());
            listing.Label("AB_BelowScale".Translate() + ": " + belowThingScale.ToStringPercent(), tooltip: "AB_BelowScaleTip".Translate());
            float newBelowScale = listing.Slider(belowThingScale, 0.7f, 1f);
            if (Mathf.Abs(newBelowScale - belowThingScale) > 0.0005f)
            {
                // Printed below-things bake the scale into their vertices;
                // reprint the layers so the slider applies live. MapDrawer
                // amortizes actual regeneration, so drag spam is self-limiting.
                DirtyBelowThingsLayers();
            }
            belowThingScale = newBelowScale;
            listing.CheckboxLabeled("AB_BelowParallax".Translate(), ref belowParallax, "AB_BelowParallaxTip".Translate());
            if (belowParallax)
            {
                listing.Label("AB_BelowParallaxStrength".Translate() + ": " + belowParallaxStrength.ToString("0.00"), tooltip: "AB_BelowParallaxStrengthTip".Translate());
                belowParallaxStrength = listing.Slider(belowParallaxStrength, 0.1f, 0.8f);
            }
            listing.Label("AB_ClimbTime".Translate() + ": " + climbTimeMultiplier.ToStringPercent(), tooltip: "AB_ClimbTimeTip".Translate());
            climbTimeMultiplier = listing.Slider(climbTimeMultiplier, 0.25f, 3f);
            listing.GapLine();
            listing.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging, "AB_VerboseLoggingTip".Translate());
            listing.End();
            Widgets.EndScrollView();
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
            // Key renamed so the old heavier default is not carried over from
            // earlier test sessions; real surface lighting now does most of the work.
            Scribe_Values.Look(ref belowDim, "belowDimLight", 0.12f);
            Scribe_Values.Look(ref belowDepthShift, "belowDepthShift", 0.25f);
            Scribe_Values.Look(ref drawSlabEdge, "drawSlabEdge", true);
            Scribe_Values.Look(ref drawWallReveal, "drawWallReveal", true);
            Scribe_Values.Look(ref wallRevealWidth, "wallRevealWidth", 0.5f);
            Scribe_Values.Look(ref drawWallFacade, "drawWallFacade", true);
            Scribe_Values.Look(ref belowThingScale, "belowThingScale", 0.85f);
            Scribe_Values.Look(ref belowParallax, "belowParallax", false);
            Scribe_Values.Look(ref belowParallaxStrength, "belowParallaxStrength", 0.35f);
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
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakVegetation, "peakVegetation", 1f);
        }
    }
}
