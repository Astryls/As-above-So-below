using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// MULTI-HOP (2+ level) cross-level haul EXECUTION. The DECISION of where an
    /// item should go lives in `ColumnStorage` / `CrossLevelHaul.TargetLevelFor`;
    /// when the best storage is 2+ levels away, `CrossLevelHaulJob.Build` issues an
    /// `AB_HaulChainAcrossLevels` hop that heads ONE gap toward it.
    ///
    /// Because each stair transfer despawns the pawn - which ends its job - a
    /// single job cannot span multiple gaps. So this is a one-hop-then-relay:
    /// StairTransfer's ABPendingOrders callback (OnArrive) runs on each landing and
    /// either STORES the item here (if this level accepts it) or SETS IT DOWN and
    /// re-stamps the Haul designation (non-alwaysHaulable only) so the ordinary
    /// single-hop / far givers carry it onward. Every remaining leg is then a
    /// normal, well-tested haul - no carried item threaded through a re-entrant
    /// follow-up job (that was the "stalls at the top of the first flight" bug).
    /// </summary>
    public static class CrossLevelHaulChain
    {
        /// <summary>Runs on each landing of a chain hop (via ABPendingOrders, after
        /// StairTransfer). Stores the item here if this level is its best home;
        /// otherwise sets it down and, if a strictly-better tier still lives
        /// elsewhere in the column, re-designates it so the ordinary givers relay
        /// it onward. If nowhere is better, it simply stays put (self-heals when
        /// storage frees up). The destination decision is `ColumnStorage`, which
        /// picks the best TIER in the column - so a downward relay continues DOWN
        /// to Critical instead of bouncing back up into the Normal storage it just
        /// left.</summary>
        public static void OnArrive(Pawn pawn, Thing item)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || item == null)
                {
                    return;
                }
                Thing carried = pawn.carryTracker?.CarriedThing;
                if (carried == null || carried != item)
                {
                    return;
                }
                // 1) This level is its best home? Store it (vanilla deposit; a
                //    pawn already holding it just walks it to the cell).
                if (TryStoreHere(pawn, carried))
                {
                    return;
                }
                // 2) Otherwise set it down and, if a strictly-better tier still
                //    lives elsewhere in the column, take the next hop toward it
                //    DIRECTLY. Issuing the hop ourselves (not via the storage
                //    giver) is deliberate: a committed relay continuation must NOT
                //    be gated by the DeliveredHere / import pin, which guards
                //    against a storage-race BOUNCE and would otherwise trap a
                //    just-transferred stack on this interchange level (root cause
                //    of the medicine/food relay stall - diagnostic exportAllowed=
                //    False [deliveredHere=True]). Drop first so a re-entrant job
                //    start never threads a carried item.
                if (!pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near,
                        out Thing dropped) || dropped == null || !dropped.Spawned)
                {
                    return;
                }
                if (ColumnStorage.TryFindBetter(pawn, dropped, out Map tm, out IntVec3 _,
                        out IHaulDestination _, out StoragePriority _)
                    && ColumnStorage.FirstHopToward(pawn, tm,
                        out Building_ABStairs entry, out Building_ABStairs exit))
                {
                    Job hop = JobMaker.MakeJob(ABDefOf.AB_HaulChainAcrossLevels, dropped, entry);
                    hop.targetC = exit;
                    hop.count = dropped.stackCount;
                    pawn.jobs?.TryTakeOrderedJob(hop, JobTag.Misc);
                }
                // else: nowhere strictly better - leave it here (self-heals when
                // storage frees up or the pin ages out).
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level haul chain arrive");
                try
                {
                    if (pawn?.carryTracker?.CarriedThing != null)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                }
                catch
                {
                    // last-resort: never rethrow out of the transfer callback.
                }
            }
        }

        /// <summary>Store the carried item on the pawn's CURRENT level if that
        /// level has accepting, reachable storage. Vanilla builds the deposit job;
        /// a pawn already carrying the thing just walks it to the cell.</summary>
        private static bool TryStoreHere(Pawn pawn, Thing item)
        {
            Job store = HaulAIUtility.HaulToStorageJob(pawn, item, forced: false);
            if (store == null)
            {
                return false;
            }
            pawn.jobs?.TryTakeOrderedJob(store, JobTag.Misc);
            return true;
        }
    }
}
