using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    public class ABSettings : ModSettings
    {
        public bool verboseLogging;
        public bool showLiveBelow = true;
        public float belowDim = 0.30f;

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
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
            Scribe_Values.Look(ref belowDim, "belowDim", 0.30f);
        }
    }
}
