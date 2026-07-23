using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Construction-work-type material supply (user report 2026-07-23): a
    /// fresh basement full of blueprints starves even with full stockpiles
    /// upstairs. The old flow only moved materials down via the Hauling work
    /// type (lowest priority, so any local haul preempts it forever) or via a
    /// hauler already idling ON the demanding level - and builders never
    /// descend on their own because the work probe rightly finds nothing
    /// doable on a level with no materials. Deadlock.
    ///
    /// This giver runs inside CONSTRUCTION, after every local construction
    /// giver: when a linked level's blueprints or frames need a material this
    /// level has and can spare, the builder picks up a stack and carries it
    /// through the stairs themselves - exactly what a player expects builders
    /// to do. Delivery lands loose at the stairwell (construct-deliver uses
    /// unstored resources); the builder is then standing on the construction
    /// level, where the local scan immediately finds deliver/build work.
    /// Construction-only demand: bill ingredients and meals stay with the
    /// hauling flows.
    /// </summary>
    public class WorkGiver_ABSupplyConstruction : WorkGiver
    {
        private const int EmptyScanCooldownTicks = 450;

        private static readonly ABPawnCooldown emptyScanCooldown = new ABPawnCooldown();

        public override Job NonScanJob(Pawn pawn)
        {
            if (!ABGuard.On(ABGuard.Logistics) || CrossLevelWork.VirtualScanActive)
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelSupply)
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
                Job job = TrySupplyTowards(pawn, comp.upperMap) ?? TrySupplyTowards(pawn, comp.lowerMap);
                if (job != null)
                {
                    return job;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level construction supply");
                return null;
            }
            emptyScanCooldown.ChargeUntil(pawn, now + EmptyScanCooldownTicks);
            return null;
        }

        private static Job TrySupplyTowards(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            // A stack on MY level that the linked level's blueprints/frames
            // still need and my level can spare. The pawn really is on the
            // source map, so reachability needs no virtual swap.
            Thing t = CrossLevelDemand.FindFetchableDemand(target, pawn.Map, pawn,
                requireReachable: true, constructionOnly: true);
            if (t == null)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs,
                out Building_ABStairs exit))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
            job.targetC = exit;
            job.count = Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
            return job;
        }
    }
}
