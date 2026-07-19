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
        public float climbTimeMultiplier = 1f;
        public bool crossLevelWork = true;
        public bool idleReturnHome = true;
        public bool crossLevelHauling = true;
        public bool crossLevelNeeds = true;
        public bool crossLevelPipes = true;
        public bool crossLevelTemperature = true;

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("AB_CrossLevelWork".Translate(), ref crossLevelWork, "AB_CrossLevelWorkTip".Translate());
            listing.CheckboxLabeled("AB_IdleReturnHome".Translate(), ref idleReturnHome, "AB_IdleReturnHomeTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelHauling".Translate(), ref crossLevelHauling, "AB_CrossLevelHaulingTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelNeeds".Translate(), ref crossLevelNeeds, "AB_CrossLevelNeedsTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelPipes".Translate(), ref crossLevelPipes, "AB_CrossLevelPipesTip".Translate());
            listing.CheckboxLabeled("AB_CrossLevelTemperature".Translate(), ref crossLevelTemperature, "AB_CrossLevelTemperatureTip".Translate());
            listing.CheckboxLabeled("AB_ShowLiveBelow".Translate(), ref showLiveBelow, "AB_ShowLiveBelowTip".Translate());
            listing.CheckboxLabeled("AB_ShowCeilingHint".Translate(), ref showCeilingHint, "AB_ShowCeilingHintTip".Translate());
            listing.CheckboxLabeled("AB_ShowLevelWidget".Translate(), ref showLevelWidget, "AB_ShowLevelWidgetTip".Translate());
            listing.Label("AB_BelowDim".Translate() + ": " + belowDim.ToStringPercent(), tooltip: "AB_BelowDimTip".Translate());
            belowDim = listing.Slider(belowDim, 0f, 0.8f);
            listing.Label("AB_ClimbTime".Translate() + ": " + climbTimeMultiplier.ToStringPercent(), tooltip: "AB_ClimbTimeTip".Translate());
            climbTimeMultiplier = listing.Slider(climbTimeMultiplier, 0.25f, 3f);
            listing.GapLine();
            listing.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging, "AB_VerboseLoggingTip".Translate());
            listing.End();
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
            Scribe_Values.Look(ref crossLevelWork, "crossLevelWork", true);
            Scribe_Values.Look(ref idleReturnHome, "idleReturnHome", true);
            Scribe_Values.Look(ref crossLevelHauling, "crossLevelHauling", true);
            Scribe_Values.Look(ref crossLevelNeeds, "crossLevelNeeds", true);
            Scribe_Values.Look(ref crossLevelPipes, "crossLevelPipes", true);
            Scribe_Values.Look(ref crossLevelTemperature, "crossLevelTemperature", true);
        }
    }
}
