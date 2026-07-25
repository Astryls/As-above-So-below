using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Allow Tool priority-hauling parity across levels. Allow Tool's own
    /// WorkGiver_HaulUrgently stores same-map only, so an urgently-designated
    /// stack whose better storage is on a linked level would drop out of the
    /// urgent lane. This giver carries those stacks across FIRST - a high
    /// priorityInType puts it ahead of ordinary hauling within the Hauling work
    /// type, the closest match to Allow Tool's above-Hauling urgent work type
    /// without adding a work type that would break when Allow Tool is absent.
    /// When a bulk bridge (PUAH / Hauler's Dream) is active the trip carries a
    /// whole load of urgent cargo; otherwise it is a single carry. Inert unless
    /// Allow Tool is installed.
    ///
    /// 2026-07-24 rework (user report "haul urgently should work across
    /// levels"), three fixes:
    ///  - FETCH: urgent stacks on a LINKED level now pull pawns across (the
    ///    NonScanJob branch walks the stairs when a linked level has doable
    ///    urgent work; on arrival Allow Tool's own giver or this one's scanner
    ///    finishes the haul). Before, only stacks on the pawn's map were seen.
    ///  - PINS: the urgent designation is an explicit player order, so both
    ///    export pins (construction island, import) are bypassed via the
    ///    ignorePins verdict path - a pinned stack no longer ignores the
    ///    player's click.
    ///  - The ShouldSkip gate considers the whole column pair, not just the
    ///    pawn's map, so the fetch branch can fire at all.
    /// </summary>
    public class WorkGiver_ABHaulUrgentlyAcrossLevels : WorkGiver_Scanner
    {
        private static int EmptyScanCooldownTicks => ABMod.Settings?.jobEmptyScanCooldown ?? 450;

        /// <summary>Urgent things examined per island during a fetch probe.</summary>
        private const int MaxUrgentPerIsland = 25;

        private static readonly ABPawnCooldown fetchCooldown = new ABPawnCooldown();

        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ABGuard.On(ABGuard.Logistics)
                || CrossLevelWork.VirtualScanActive
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !ABAllowToolCompat.Active
                || !pawn.Map.ConnectedToOtherLevel()
                || CrossLevelWork.LowPowerWorker(pawn)
                || !AnyUrgentInColumn(pawn.Map);
        }

        /// <summary>Urgent work anywhere in the pawn's column pair: own map
        /// (scanner branch) or a directly linked level (fetch branch).</summary>
        private static bool AnyUrgentInColumn(Map map)
        {
            if (ABAllowToolCompat.AnyUrgent(map))
            {
                return true;
            }
            if (!map.TryLinkedLevels(out LevelComp comp))
            {
                return false;
            }
            return ABAllowToolCompat.AnyUrgent(comp.upperMap)
                || ABAllowToolCompat.AnyUrgent(comp.lowerMap);
        }

        // ---------------------------------------------------------- fetch leg

        /// <summary>Urgent stacks on a linked level: walk over and let the
        /// normal scans finish the job on arrival (Allow Tool's own giver for
        /// local storage, this giver's scanner for another cross trip). Runs
        /// before this giver's scan at the same high priority, so urgent fetch
        /// preempts ordinary hauling exactly like local urgent work does.</summary>
        public override Job NonScanJob(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Drafted || pawn.GetLord() != null
                || pawn.carryTracker?.CarriedThing != null)
            {
                return null;
            }
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!fetchCooldown.Ready(pawn, now))
            {
                return null;
            }
            try
            {
                Job job = TryFetchUrgentTowards(pawn, comp.upperMap)
                    ?? TryFetchUrgentTowards(pawn, comp.lowerMap);
                if (job != null)
                {
                    return job;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "urgent cross level fetch");
                return null;
            }
            fetchCooldown.ChargeUntil(pawn, now + EmptyScanCooldownTicks);
            return null;
        }

        private static Job TryFetchUrgentTowards(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed || !ABAllowToolCompat.AnyUrgent(target))
            {
                return null;
            }
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            if (pairs.Count == 0)
            {
                return null;
            }
            Building_ABStairs stairs = null;
            Building_ABStairs exit = null;
            IntVec3 fetchDest = IntVec3.Invalid;
            for (int p = 0; p < pairs.Count && stairs == null; p++)
            {
                if (!ABVirtualPosition.TrySwap(pawn, target, pairs[p].exit.Position, out ABVirtualPosition.Token token))
                {
                    return null;
                }
                CrossLevelWork.VirtualScanActive = true;
                try
                {
                    int examined = 0;
                    foreach (Thing t in ABAllowToolCompat.UrgentThings(target))
                    {
                        if (++examined > MaxUrgentPerIsland)
                        {
                            break;
                        }
                        if (t.Map != target || t.IsForbidden(pawn)
                            || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced: false))
                        {
                            continue;
                        }
                        // Doable = it has somewhere to go: better storage on
                        // its own level (Allow Tool finishes) or a cross-level
                        // verdict (this giver's scanner finishes). Pins never
                        // silence a player designation.
                        if (StoreUtility.TryFindBestBetterStorageFor(t, pawn, target,
                                StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction,
                                out IntVec3 _, out IHaulDestination _, needAccurateResult: false)
                            || CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _, ignorePins: true) != null)
                        {
                            fetchDest = t.PositionHeld;
                            stairs = pairs[p].stairs;
                            exit = pairs[p].exit;
                            break;
                        }
                    }
                }
                finally
                {
                    ABVirtualPosition.Restore(pawn, token);
                    CrossLevelWork.VirtualScanActive = false;
                }
            }
            if (stairs == null)
            {
                return null;
            }
            StairRouter.Reroute(pawn, target, fetchDest, ref stairs, ref exit);
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }

        // -------------------------------------------------------- scanner leg

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return ABAllowToolCompat.IsUrgent(t)
                && CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _, ignorePins: true) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!ABAllowToolCompat.IsUrgent(t))
            {
                return null;
            }
            Map target = CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs stairs, ignorePins: true,
                out int allowedCount, out bool demand);
            if (target == null || stairs == null)
            {
                return null;
            }
            if (demand && allowedCount > 0)
            {
                CrossLevelDemand.NoteInFlight(pawn, target, t.def, allowedCount);
            }
            // An urgent trip carries only urgent cargo, pin-exempt throughout;
            // the count still clamps to what the destination can take.
            return CrossLevelHaulJob.Build(pawn, t, target, stairs, ABAllowToolCompat.IsUrgent,
                ignorePins: true, allowedCount: allowedCount);
        }
    }
}
