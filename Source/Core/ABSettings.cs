using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    public class ABSettings : ModSettings
    {
        public bool verboseLogging;
        public bool showLiveBelow = true;
        public bool showCeilingHint = true;
        public bool showLevelWidget = true;
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
        public bool idleReturnHome = true;
        public bool crossLevelHauling = true;
        public bool crossLevelSupply = true;
        public bool crossLevelNeeds = true;
        public bool crossLevelPrisoners = true;
        public bool crossLevelPipes = true;
        public bool crossLevelTemperature = true;
        public bool threatBasementInfest;
        public bool threatSkyDrops;
        public float threatDivertChance = 0.25f;
        public bool columnWealth = true;
        public bool worldIntegration = true;

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("AB_CrossLevelWork".Translate(), ref crossLevelWork, "AB_CrossLevelWorkTip".Translate());
            listing.CheckboxLabeled("AB_IdleReturnHome".Translate(), ref idleReturnHome, "AB_IdleReturnHomeTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelHauling".Translate(), ref crossLevelHauling, "AB_CrossLevelHaulingTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelSupply".Translate(), ref crossLevelSupply, "AB_CrossLevelSupplyTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelNeeds".Translate(), ref crossLevelNeeds, "AB_CrossLevelNeedsTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPrisoners".Translate(), ref crossLevelPrisoners, "AB_CrossLevelPrisonersTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPipes".Translate(), ref crossLevelPipes, "AB_CrossLevelPipesTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelTemperature".Translate(), ref crossLevelTemperature, "AB_CrossLevelTemperatureTip".Translate());
            listing.CheckboxLabeled("AB_ShowLiveBelow".Translate(), ref showLiveBelow, "AB_ShowLiveBelowTip".Translate());
            listing.CheckboxLabeled("AB_ShowCeilingHint".Translate(), ref showCeilingHint, "AB_ShowCeilingHintTip".Translate());
            listing.CheckboxLabeled("AB_ShowLevelWidget".Translate(), ref showLevelWidget, "AB_ShowLevelWidgetTip".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("AB_ThreatBasementInfest".Translate(), ref threatBasementInfest, "AB_ThreatBasementInfestTip".Translate());
            listing.CheckboxLabeled("AB_ThreatSkyDrops".Translate(), ref threatSkyDrops, "AB_ThreatSkyDropsTip".Translate());
            if (threatBasementInfest || threatSkyDrops)
            {
                listing.Label("AB_ThreatDivertChance".Translate() + ": " + threatDivertChance.ToStringPercent(), tooltip: "AB_ThreatDivertChanceTip".Translate());
                threatDivertChance = listing.Slider(threatDivertChance, 0.05f, 1f);
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
            Scribe_Values.Look(ref showCeilingHint, "showCeilingHint", true);
            Scribe_Values.Look(ref showLevelWidget, "showLevelWidget", true);
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
            Scribe_Values.Look(ref idleReturnHome, "idleReturnHome", true);
            Scribe_Values.Look(ref crossLevelHauling, "crossLevelHauling", true);
            Scribe_Values.Look(ref crossLevelSupply, "crossLevelSupply", true);
            Scribe_Values.Look(ref crossLevelNeeds, "crossLevelNeeds", true);
            Scribe_Values.Look(ref crossLevelPrisoners, "crossLevelPrisoners", true);
            Scribe_Values.Look(ref crossLevelPipes, "crossLevelPipes", true);
            Scribe_Values.Look(ref crossLevelTemperature, "crossLevelTemperature", true);
            Scribe_Values.Look(ref threatBasementInfest, "threatBasementInfest", false);
            Scribe_Values.Look(ref threatSkyDrops, "threatSkyDrops", false);
            Scribe_Values.Look(ref threatDivertChance, "threatDivertChance", 0.25f);
            Scribe_Values.Look(ref columnWealth, "columnWealth", true);
            Scribe_Values.Look(ref worldIntegration, "worldIntegration", true);
        }
    }
}
