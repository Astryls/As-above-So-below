using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    public class ABSettings : ModSettings
    {
        public bool verboseLogging;

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging, "AB_VerboseLoggingTip".Translate());
            listing.End();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
        }
    }
}
