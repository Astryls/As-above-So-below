using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The bulk variant of cross-level hauling, active only when Pick Up And
    /// Haul or Hauler's Dream is present and the pawn carries their inventory
    /// comp. Same verdict as the single-item giver (an item whose better
    /// storage is on a linked level), but the job scoops a whole load into
    /// inventory for one trip. Runs after vanilla hauling (same priorityInType
    /// as the single-item giver, which stands down for these pawns).
    /// </summary>
    public class WorkGiver_ABBulkHaulAcrossLevels : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ABGuard.On(ABGuard.Logistics)
                || CrossLevelWork.VirtualScanActive
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !ABInventoryHaulBridge.AnyActive
                || !ABInventoryHaulBridge.HasComp(pawn)
                || !pawn.Map.ConnectedToOtherLevel()
                || CrossLevelWork.LowPowerWorker(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Map target = CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs stairs);
            if (target == null || stairs == null)
            {
                return null;
            }
            return CrossLevelHaulJob.Build(pawn, t, target, stairs);
        }
    }
}
