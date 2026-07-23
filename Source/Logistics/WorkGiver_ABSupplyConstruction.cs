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
        private static int EmptyScanCooldownTicks => ABMod.Settings?.jobEmptyScanCooldown ?? 450;

        private static readonly ABPawnCooldown emptyScanCooldown = new ABPawnCooldown();

        public override Job NonScanJob(Pawn pawn)
        {
            if (!ABGuard.On(ABGuard.Logistics) || CrossLevelWork.VirtualScanActive)
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelSupply || !settings.supplyConstruction)
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
            int fixedCount = 0;
            if (t == null)
            {
                // Install blueprints over there whose minified thing sits on
                // this level: ferry it across; the local install giver takes
                // it from the drop (run #71 "No path" fix, automatic flow).
                t = FindInstallMini(pawn, target);
                fixedCount = 1;
            }
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
            job.count = fixedCount > 0
                ? fixedCount
                : Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def));
            return job;
        }

        /// <summary>A loose minified thing on the pawn's level that an install
        /// blueprint on the target level is waiting for. Built buildings
        /// awaiting reinstall are skipped (the uninstall must happen on their
        /// own level first; that designation migrates workers by itself).</summary>
        private static Thing FindInstallMini(Pawn pawn, Map target)
        {
            List<Thing> blueprints = target.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            for (int i = 0; i < blueprints.Count; i++)
            {
                if (!(blueprints[i] is Blueprint_Install install)
                    || install.Faction != Faction.OfPlayer || !install.Spawned)
                {
                    continue;
                }
                Thing mini = install.MiniToInstallOrBuildingToReinstall;
                if (mini is MinifiedThing && mini.Spawned && mini.Map == pawn.Map
                    && !mini.IsForbidden(pawn)
                    && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, mini, forced: false))
                {
                    return mini;
                }
            }
            return null;
        }
    }
}
