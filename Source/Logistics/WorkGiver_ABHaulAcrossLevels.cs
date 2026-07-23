using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Runs after vanilla hauling (lower priorityInType): picks up items whose
    /// better storage lives on a linked level and carries them through the stairs.
    /// Vanilla's carried-thing handling stores the item after the transfer.
    ///
    /// This is the single-item FALLBACK. When Pick Up And Haul or Hauler's
    /// Dream is present and the pawn carries their inventory comp, the bulk
    /// giver (WorkGiver_ABBulkHaulAcrossLevels) takes over instead - so this one
    /// stands down for those pawns to avoid two givers racing the same stacks.
    /// </summary>
    public class WorkGiver_ABHaulAcrossLevels : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ABGuard.On(ABGuard.Logistics)
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !pawn.Map.ConnectedToOtherLevel()
                // Bulk inventory hauler takes this pawn instead.
                || (ABInventoryHaulBridge.AnyActive && ABInventoryHaulBridge.HasComp(pawn))
                // Battery-driven workers (Misc. Robots, mechs) stay near home
                // when low; their recharge AI will want them shortly.
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
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
            job.targetC = stairs.CounterpartTowards(target);
            job.count = Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
            return job;
        }
    }
}
