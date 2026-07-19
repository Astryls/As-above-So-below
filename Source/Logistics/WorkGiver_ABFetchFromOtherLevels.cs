using System;
using System.Collections.Generic;
using RimWorld;
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

        private static readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

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
            LevelComp comp = pawn.Map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (nextAllowedTick.TryGetValue(pawn.thingIDNumber, out int next) && now < next)
            {
                return null;
            }
            if (nextAllowedTick.Count > 512)
            {
                nextAllowedTick.Clear();
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
            nextAllowedTick[pawn.thingIDNumber] = now + EmptyScanCooldownTicks;
            return null;
        }

        private static Job TryFetchTowards(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            ICollection<Thing> haulables = target.listerHaulables.ThingsPotentiallyNeedingHauling();
            if (haulables.Count == 0)
            {
                return null;
            }
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            Building_ABStairs exit = stairs?.CounterpartTowards(target);
            if (exit == null)
            {
                return null;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return null;
            }
            bool found = false;
            CrossLevelWork.VirtualScanActive = true;
            try
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
                        break;
                    }
                    // Or does it want to travel to yet another level (for example
                    // back down to this pawn's own fridge)? Cached verdict.
                    if (CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _) != null)
                    {
                        found = true;
                        break;
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
            Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            job.targetC = exit;
            return job;
        }
    }
}
