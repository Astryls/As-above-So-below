using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The fast path for autonomous fetching (T7 #4): a hauler with nothing to
    /// haul locally checks directly linked levels for real haul work and takes
    /// the stairs immediately, instead of waiting for the idle-migration cadence.
    /// Covers both directions: items needing storage on the linked level itself,
    /// and items there whose better storage is back on another level (via the
    /// nested cross-level verdict, which rides its 600-tick cache). Runs last
    /// within the Hauling work type (priorityInType) so local hauls always win.
    /// Existence checks are bounded per scan; empty scans charge a per-pawn
    /// cooldown so idle haulers stay cheap. Any job found during the virtual
    /// scan is discarded; the real haul resolves after the transfer.
    /// </summary>
    public class WorkGiver_ABFetchFromOtherLevels : WorkGiver
    {
        private const int EmptyScanCooldownTicks = 450;

        private const int MaxItemsPerScan = 25;

        private static readonly ABPawnCooldown emptyScanCooldown = new ABPawnCooldown();

        public override Job NonScanJob(Pawn pawn)
        {
            // Never run inside another cross-level virtual scan: nested map
            // swaps would hand out stairs jobs referencing the virtual map.
            if (!ABGuard.On(ABGuard.Logistics) || CrossLevelWork.VirtualScanActive)
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelHauling)
            {
                return null;
            }
            if (pawn == null || !pawn.Spawned || pawn.Drafted || pawn.GetLord() != null
                || pawn.carryTracker?.CarriedThing != null)
            {
                return null;
            }
            if (CrossLevelWork.LowPowerWorker(pawn))
            {
                // Battery-driven workers (Misc. Robots, mechs) stay near home
                // when low; their recharge AI will want them shortly.
                return null;
            }
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!emptyScanCooldown.Ready(pawn, now))
            {
                return null;
            }
            try
            {
                Job job = TryFetchTowards(pawn, comp.upperMap) ?? TryFetchTowards(pawn, comp.lowerMap);
                if (job != null)
                {
                    return job;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level fetch");
                return null;
            }
            emptyScanCooldown.ChargeUntil(pawn, now + EmptyScanCooldownTicks);
            return null;
        }

        private static Job TryFetchTowards(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            Map demandMap = pawn.Map;
            ICollection<Thing> haulables = target.listerHaulables.ThingsPotentiallyNeedingHauling();
            bool anyHaulables = haulables.Count > 0;
            // Materials this pawn's OWN level still needs (blueprints, frames, bill
            // ingredients, patient/prisoner food) that sit in a stockpile on the
            // linked level. Those never enter the haulables lister, so the pure
            // push side leaves a level of builders starved when no idle hauler is
            // standing on the source level. Cheap pre-check keeps idle scans free.
            bool wantDemand = ABMod.Settings.crossLevelSupply
                && CrossLevelDemand.HasFetchableDemand(demandMap, target, pawn);
            if (!anyHaulables && !wantDemand)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return null;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return null;
            }
            bool found = false;
            bool demandFetch = false;
            IntVec3 fetchDest = IntVec3.Invalid;
            CrossLevelWork.VirtualScanActive = true;
            try
            {
                if (anyHaulables)
                {
                    int examined = 0;
                    foreach (Thing t in haulables)
                    {
                        if (++examined > MaxItemsPerScan)
                        {
                            break;
                        }
                        if (t == null || !t.Spawned || t.Map != target || t.IsForbidden(pawn)
                            || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced: false))
                        {
                            continue;
                        }
                        // Better storage on the linked level itself?
                        if (StoreUtility.TryFindBestBetterStorageFor(t, pawn, target,
                            StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction,
                            out IntVec3 _, out IHaulDestination _, needAccurateResult: false))
                        {
                            found = true;
                            fetchDest = t.PositionHeld;
                            break;
                        }
                        // Or does it want to travel to yet another level (for example
                        // back down to this pawn's own fridge)? Cached verdict.
                        if (CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _) != null)
                        {
                            found = true;
                            fetchDest = t.PositionHeld;
                            break;
                        }
                    }
                }
                if (!found && wantDemand)
                {
                    // Pawn is virtually on `target` now, so reachability is measured
                    // there. A demanded stack that can actually be picked up means
                    // the trip is worthwhile.
                    Thing demanded = CrossLevelDemand.FindFetchableDemand(demandMap, target, pawn,
                        requireReachable: true);
                    demandFetch = demanded != null;
                    found = demandFetch;
                    if (demanded != null)
                    {
                        fetchDest = StairRouter.DestHint(demanded, target);
                    }
                }
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
                CrossLevelWork.VirtualScanActive = false;
            }
            if (!found)
            {
                return null;
            }
            // Positions are restored: route through the stairwell nearest the goods.
            StairRouter.Reroute(pawn, target, fetchDest, ref stairs, ref exit);
            if (demandFetch)
            {
                // Job queues do not survive the transfer, so stash the return trip:
                // on arrival, pick up a demanded stack and haul it back down.
                ABPendingOrders.Set(pawn, target, delegate
                {
                    IssueDemandHaulBack(pawn, target, demandMap);
                });
            }
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }

        /// <summary>Runs on arrival on the source level: grab a stack the origin
        /// level still needs and issue the cross-level haul back toward it. Fails
        /// open - if nothing is fetchable anymore the pawn just re-scans normally.</summary>
        private static void IssueDemandHaulBack(Pawn pawn, Map sourceMap, Map demandMap)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map != sourceMap
                    || pawn.Drafted || pawn.GetLord() != null
                    || pawn.carryTracker?.CarriedThing != null)
                {
                    return;
                }
                Thing t = CrossLevelDemand.FindFetchableDemand(demandMap, sourceMap, pawn,
                    requireReachable: true);
                if (t == null)
                {
                    return;
                }
                if (!CrossLevelWork.TryResolveStairs(pawn, demandMap, out Building_ABStairs stairs,
                    out Building_ABStairs exit))
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
                job.targetC = exit;
                job.count = Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
                pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "demand haul back");
            }
        }
    }
}
