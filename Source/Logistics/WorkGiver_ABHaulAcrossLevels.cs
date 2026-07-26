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
            return CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _) != null
                || CrossLevelHaulChain.TryStartFarHaul(pawn, t, out Building_ABStairs _, out Building_ABStairs _);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Map target = CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs stairs,
                ignorePins: false, out int allowedCount, out bool demand);
            if (target == null || stairs == null)
            {
                // No adjacent accepting level - is there one 2+ gaps away? Carry
                // it there hop by hop, storing at the first accepting level.
                if (CrossLevelHaulChain.TryStartFarHaul(pawn, t,
                        out Building_ABStairs chainEntry, out Building_ABStairs chainExit))
                {
                    Job chain = JobMaker.MakeJob(ABDefOf.AB_HaulChainAcrossLevels, t, chainEntry);
                    chain.targetC = chainExit;
                    chain.count = Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
                    return chain;
                }
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
            job.targetC = stairs.CounterpartTowards(target);
            int count = Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
            if (allowedCount > 0)
            {
                // Both flavors clamp (2026-07-25 log-carousel fix): demand
                // pulls carry only the residual want net of other pawns'
                // en-route cargo; storage moves carry only what the
                // destination storage can absorb, so a full stack never
                // chases a sliver of space and strands at the stair mouth.
                count = Mathf.Min(count, allowedCount);
            }
            if (demand)
            {
                // Claim the errand so idle haulers stop ferrying duplicates
                // to the stair mouth (2026-07-25 "hauling TO stairs" report).
                CrossLevelDemand.NoteInFlight(pawn, target, t.def, count);
            }
            job.count = count;
            return job;
        }
    }
}
