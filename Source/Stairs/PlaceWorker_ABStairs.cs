using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Enforces the level cap and, for now, restricts stair placement to the
    /// surface level. Placement from sky or basement levels arrives with T2.
    /// </summary>
    public class PlaceWorker_ABStairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            ABStairsExtension ext = (checkingDef as ThingDef)?.GetModExtension<ABStairsExtension>();
            if (ext == null)
            {
                return true;
            }
            if (map.Level() != 0)
            {
                return new AcceptanceReport("AB_OnlySurfaceForNow".Translate());
            }
            int target = map.Level() + ext.deltaLevel;
            if (target < -1 || target > 1)
            {
                return new AcceptanceReport("AB_LevelCap".Translate());
            }
            return true;
        }
    }
}
