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
        private static int EmptyScanCooldownTicks => ABMod.Settings?.jobEmptyScanCooldown ?? 450;

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
            if (settings == null || !settings.crossLevelHauling || !settings.fetchFromOtherLevels)
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
            // Island-aware (2026-07-24): probe from one exit per distinct island
            // of the source level; goods behind a different staircase than the
            // nearest are no longer invisible. Scan budget is shared across
            // islands so the whole sweep stays bounded.
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            if (pairs.Count == 0)
            {
                return null;
            }
            bool found = false;
            bool demandFetch = false;
            IntVec3 fetchDest = IntVec3.Invalid;
            Building_ABStairs stairs = null;
            Building_ABStairs exit = null;
            for (int p = 0; p < pairs.Count && !found; p++)
            {
                // Per-island item budget: a shared budget let the first island
                // exhaust it on items only reachable from elsewhere and starve
                // the later islands forever (lister order is stable).
                int examined = 0;
                if (!ABVirtualPosition.TrySwap(pawn, target, pairs[p].exit.Position, out ABVirtualPosition.Token token))
                {
                    return null;
                }
                CrossLevelWork.VirtualScanActive = true;
                try
                {
                    if (anyHaulables)
                    {
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
                            // Or is its only accepting storage 2+ gaps away (multi-hop)?
                            // The pawn is virtually on `target`, so this reads the far
                            // destination from the item's real level. Bring the pawn
                            // here; on arrival the push chain carries it onward.
                            if (CrossLevelHaulChain.TryStartFarHaul(pawn, t,
                                    out Building_ABStairs _, out Building_ABStairs _))
                            {
                                found = true;
                                fetchDest = t.PositionHeld;
                                break;
                            }
                        }
                    }
                    // In-STORAGE items on `target` whose best storage is a STRICTLY
                    // better level elsewhere in the column - e.g. a new Critical
                    // basement stockpile pulling items out of a Normal sky stockpile
                    // two levels up. Vanilla considers such an item "in valid best
                    // storage" (best on ITS OWN map) and drops it from the haulables
                    // lister, so only a pawn already standing on `target` would ever
                    // push the upgrade. Bring an idle pawn here; on arrival the push
                    // giver / chain carries it to the better tier. Bounded by the
                    // shared per-island examine budget.
                    if (!found)
                    {
                        System.Collections.Generic.List<SlotGroup> groups =
                            target.haulDestinationManager?.AllGroupsListForReading;
                        if (groups != null)
                        {
                            for (int gi = 0; gi < groups.Count && !found && examined <= MaxItemsPerScan; gi++)
                            {
                                SlotGroup g = groups[gi];
                                if (g?.HeldThings == null)
                                {
                                    continue;
                                }
                                foreach (Thing st in g.HeldThings)
                                {
                                    if (++examined > MaxItemsPerScan)
                                    {
                                        break;
                                    }
                                    if (st == null || !st.Spawned || st.Map != target
                                        || st.IsForbidden(pawn)
                                        || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, st, forced: false))
                                    {
                                        continue;
                                    }
                                    if (CrossLevelHaul.TargetLevelFor(pawn, st, out Building_ABStairs _) != null
                                        || CrossLevelHaulChain.TryStartFarHaul(pawn, st,
                                            out Building_ABStairs _, out Building_ABStairs _))
                                    {
                                        found = true;
                                        fetchDest = st.PositionHeld;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (!found && wantDemand)
                    {
                        // Pawn is virtually on `target` now, so both the stack
                        // reachability and the strict return route toward the
                        // demanding island are measured there.
                        Thing demanded = CrossLevelDemand.FindFetchableDemand(demandMap, target, pawn,
                            requireReachable: true, constructionOnly: false,
                            out Building_ABStairs _, out Building_ABStairs _, out int wanted);
                        demandFetch = demanded != null;
                        found = demandFetch;
                        if (demanded != null)
                        {
                            fetchDest = StairRouter.DestHint(demanded, target);
                            // Claim the errand for the OUTBOUND leg already:
                            // other idle fetchers must see this shortfall as
                            // covered while this pawn rides up (2026-07-25
                            // duplicate-ferry report). The haul-back refreshes
                            // the claim with the real carried count.
                            CrossLevelDemand.NoteInFlight(pawn, demandMap, demanded.def,
                                Mathf.Min(wanted > 0 ? wanted : int.MaxValue,
                                    Mathf.Min(demanded.stackCount,
                                        pawn.carryTracker.MaxStackSpaceEver(demanded.def))));
                        }
                    }
                }
                finally
                {
                    ABVirtualPosition.Restore(pawn, token);
                    CrossLevelWork.VirtualScanActive = false;
                }
                if (found)
                {
                    stairs = pairs[p].stairs;
                    exit = pairs[p].exit;
                }
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
                // Strictly routed toward the demanding island (2026-07-24):
                // stairs that cannot reach the goal are never used, so the
                // return cargo cannot strand on the wrong island.
                Thing t = CrossLevelDemand.FindFetchableDemand(demandMap, sourceMap, pawn,
                    requireReachable: true, constructionOnly: false,
                    out Building_ABStairs stairs, out Building_ABStairs exit, out int wanted);
                if (t == null || stairs == null || exit == null)
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
                job.targetC = exit;
                // Residual-clamped: carry what the origin level still wants,
                // not the whole stack; refresh this pawn's claim to the real
                // carried count (the outbound leg claimed an estimate).
                job.count = Mathf.Min(wanted > 0 ? wanted : int.MaxValue,
                    Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def)));
                CrossLevelDemand.NoteInFlight(pawn, demandMap, t.def, job.count);
                pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "demand haul back");
            }
        }
    }
}
