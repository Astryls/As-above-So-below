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
            // source map, so reachability needs no virtual swap. The demand
            // query routes STRICTLY toward the demanding island (2026-07-24
            // ferry-loop fix): stairs that cannot reach the blueprints are
            // never chosen, and an island already holding enough loose
            // material registers no shortfall.
            Thing t = CrossLevelDemand.FindFetchableDemand(target, pawn.Map, pawn,
                requireReachable: true, constructionOnly: true,
                out Building_ABStairs stairs, out Building_ABStairs exit, out int wanted);
            int fixedCount = 0;
            Thing site = null;
            if (t == null)
            {
                // Install blueprints over there whose minified thing sits on
                // this level: ferry it across; the local install giver takes
                // it from the drop (run #71 "No path" fix, automatic flow).
                // Same strictness: route toward the install site itself.
                t = FindInstallMini(pawn, target, out site, out stairs, out exit);
                fixedCount = 1;
            }
            if (t == null || stairs == null || exit == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, t, stairs);
            job.targetC = exit;
            // Clamp to the residual construction shortfall (net of en-route
            // cargo) and claim the errand: several idle suppliers used to each
            // ferry a full stack for the same blueprint and strand the surplus
            // at the stairs (2026-07-25 report).
            job.count = fixedCount > 0
                ? fixedCount
                : Mathf.Min(wanted > 0 ? wanted : int.MaxValue,
                    Mathf.Min(t.stackCount, pawn.carryTracker.MaxStackSpaceEver(t.def)));
            if (fixedCount == 0 && wanted > 0)
            {
                CrossLevelDemand.NoteInFlight(pawn, target, t.def, job.count);
            }
            // DIRECT-TO-BLUEPRINT LEG (user request 2026-07-24): resolve the
            // needing site now and carry the load ALL THE WAY there, reusing
            // the manual bring-and-build order's arrival continuation (drop at
            // the site + forced deliver giver + idle retry). Previously the
            // load was dropped at the stairwell for the store-cargo fallback,
            // which walked it to a stockpile first and left the final leg to a
            // second haul - the indirection users reported. When no site
            // resolves (built or destroyed mid-scan) the old drop-at-stairs
            // behavior stands and the local scan takes over.
            if (site == null)
            {
                site = ABConstructSupply.FindSiteNeeding(target, t.def, exit.Position);
            }
            if (site != null)
            {
                Pawn carrier = pawn;
                Thing deliverTo = site;
                ABPendingOrders.Set(pawn, target,
                    delegate { ABConstructSupply.FinishOnSite(carrier, deliverTo, allowRetry: true); });
            }
            return job;
        }

        /// <summary>A loose minified thing on the pawn's level that an install
        /// blueprint on the target level is waiting for, plus the strict stair
        /// route toward that blueprint. Built buildings awaiting reinstall are
        /// skipped here: ReinstallAcrossLevels designates their uninstall the
        /// moment the cross-level blueprint spawns, vanilla uninstalls them on
        /// their own level (the designation migrates a constructor there), and
        /// the resulting mini flows through this ferry.</summary>
        private static Thing FindInstallMini(Pawn pawn, Map target, out Thing site,
            out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            site = null;
            stairs = null;
            exit = null;
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
                    && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, mini, forced: false)
                    && CrossLevelWork.TryResolveStairsStrict(pawn, target, install.Position,
                        out stairs, out exit))
                {
                    // The install blueprint IS the direct-delivery site.
                    site = install;
                    return mini;
                }
            }
            stairs = null;
            exit = null;
            return null;
        }
    }
}
