using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    public class ABSettings : ModSettings
    {
        public bool verboseLogging;
        public bool showLiveBelow = true;
        public float belowDim = 0.12f;
        public bool crossLevelWork = true;

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("AB_CrossLevelWork".Translate(), ref crossLevelWork, "AB_CrossLevelWorkTip".Translate());
            listing.CheckboxLabeled("AB_ShowLiveBelow".Translate(), ref showLiveBelow, "AB_ShowLiveBelowTip".Translate());
            listing.Label("AB_BelowDim".Translate() + ": " + belowDim.ToStringPercent(), tooltip: "AB_BelowDimTip".Translate());
            belowDim = listing.Slider(belowDim, 0f, 0.8f);
            listing.GapLine();
            listing.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging, "AB_VerboseLoggingTip".Translate());
            listing.End();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref showLiveBelow, "showLiveBelow", true);
            // Key renamed so the old heavier default is not carried over from
            // earlier test sessions; real surface lighting now does most of the work.
            Scribe_Values.Look(ref belowDim, "belowDimLight", 0.12f);
            Scribe_Values.Look(ref crossLevelWork, "crossLevelWork", true);
        }
    }
}
