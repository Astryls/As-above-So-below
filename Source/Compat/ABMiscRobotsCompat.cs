using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Misc. Robots (Haplo.Miscellaneous.Robots) soft compat: cross-level
    /// return routing. Robots happily take our cross-level haul givers to
    /// other levels (their custom X2_JobGiver_Work enumerates WorkGiverDefs by
    /// work type, including ours), but every one of their return/recharge
    /// givers resolves the bound station through CanReserveAndReach - always
    /// false across maps - so TryGiveJob returns null forever and the robot
    /// strands until its battery bricks it.
    ///
    /// Fix: postfix each return/recharge giver (TryGiveJob for JobGiver-shaped
    /// ones, TryIssueJobPackage for ThinkNode-shaped ones). When it produced
    /// no job - or an unstartable job targeting a station on another map -
    /// and the robot's bound rechargeStation sits on a linked level,
    /// issue our destination-aware stairs job toward the station's level; on
    /// arrival their own giver finds the station locally and docks normally.
    /// Two-hop cases (sky robot, basement station) chain naturally - each
    /// arrival re-fires the giver, which routes the next hop.
    ///
    /// Everything is resolved by name at startup, foreign types never appear
    /// in signatures, and the whole module fails open. Covers Misc. Robots++
    /// too (same AIRobot assembly). The complementary guard - not shipping a
    /// low-battery robot out in the first place - lives in the haul givers via
    /// CrossLevelWork.LowPowerWorker.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABMiscRobotsCompat
    {
        private const int RouteCooldownTicks = 450;

        private const int WorkRouteCooldownTicks = 900;

        private static readonly ABPawnCooldown routeCooldown = new ABPawnCooldown();

        private static readonly ABPawnCooldown workRouteCooldown = new ABPawnCooldown();

        private static FieldInfo rechargeStationField;

        private static Type robotType;

        private static MethodInfo getWorkGiversMethod;

        private static readonly object[] WorkGiversArgs = { false };

        private static bool active;

        static ABMiscRobotsCompat()
        {
            try
            {
                if (!ABDetect.Active("Haplo.Miscellaneous.Robots"))
                {
                    return;
                }
                robotType = AccessTools.TypeByName("AIRobot.X2_AIRobot");
                rechargeStationField = robotType != null
                    ? AccessTools.Field(robotType, "rechargeStation")
                    : null;
                if (rechargeStationField == null)
                {
                    Log.Warning(ABLog.Tag + " Misc. Robots detected but its recharge station internals were not found; cross-level robot return routing is off.");
                    return;
                }
                string[] giverTypeNames =
                {
                    "AIRobot.X2_JobGiver_RechargeEnergy",
                    "AIRobot.X2_JobGiver_RechargeEnergyIdle",
                    "AIRobot.X2_JobGiver_Return2BaseAndWait",
                    "AIRobot.X2_JobGiver_Return2BaseDespawn",
                    "AIRobot.X2_JobGiver_Return2BaseRoom"
                };
                HarmonyMethod postfix = new HarmonyMethod(typeof(ABMiscRobotsCompat), nameof(ReturnJobPostfix));
                HarmonyMethod thinkPostfix = new HarmonyMethod(typeof(ABMiscRobotsCompat), nameof(ReturnThinkPostfix));
                int patched = 0;
                for (int i = 0; i < giverTypeNames.Length; i++)
                {
                    Type giver = AccessTools.TypeByName(giverTypeNames[i]);
                    if (giver == null)
                    {
                        continue;
                    }
                    // Declared only: subclasses inheriting TryGiveJob (e.g.
                    // Return2BaseAndWait overriding RechargeEnergy's) must not
                    // double-patch the base implementation.
                    MethodInfo method = AccessTools.DeclaredMethod(giver, "TryGiveJob");
                    if (method != null)
                    {
                        HarmonyBoot.Harmony.Patch(method, postfix: postfix);
                        patched++;
                        continue;
                    }
                    // Some of these "givers" are ThinkNodes overriding
                    // TryIssueJobPackage instead (RechargeEnergyIdle) - run #70
                    // showed DeclaredMethod("TryGiveJob") resolving null and the
                    // type going entirely unpatched.
                    MethodInfo think = AccessTools.DeclaredMethod(giver, "TryIssueJobPackage");
                    if (think != null)
                    {
                        HarmonyBoot.Harmony.Patch(think, postfix: thinkPostfix);
                        patched++;
                    }
                }
                active = patched > 0;
                if (active)
                {
                    ABLog.Dev("Misc. Robots detected, cross-level return routing active (" + patched + " givers patched).");
                }
                // Work migration (user report 2026-07-23, Robots++): their custom
                // X2_JobGiver_Work scans only the robot's own map and never runs
                // the vanilla JobGiver_Work our migration patch lives on - so
                // base HAUL bots crossed levels (they enumerate our haul
                // WorkGiverDefs) while Robots++ construction/mining/etc. bots
                // idled next to an empty scan forever.
                Type workGiverType = AccessTools.TypeByName("AIRobot.X2_JobGiver_Work");
                MethodInfo tryIssue = workGiverType != null
                    ? AccessTools.DeclaredMethod(workGiverType, "TryIssueJobPackage")
                    : null;
                getWorkGiversMethod = robotType != null
                    ? AccessTools.Method(robotType, "GetWorkGivers", new[] { typeof(bool) })
                    : null;
                if (tryIssue != null && getWorkGiversMethod != null)
                {
                    HarmonyBoot.Harmony.Patch(tryIssue,
                        postfix: new HarmonyMethod(typeof(ABMiscRobotsCompat), nameof(WorkJobPostfix)));
                    ABLog.Dev("Misc. Robots work think node patched: robots follow work across levels (covers Robots++).");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Misc. Robots compat setup failed: " + e.Message);
            }
        }

        /// <summary>Cold path: fires only when a robot's own return/recharge
        /// giver already came up empty, which on a single level is rare and
        /// cross-level is exactly the stranding we fix.</summary>
        private static void ReturnJobPostfix(Pawn pawn, ref Job __result)
        {
            if (!active || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            try
            {
                // A non-null local answer is only trustworthy when the bound
                // station is actually on this level (run #72 rule).
                if (__result != null && !StationElsewhere(pawn))
                {
                    return;
                }
                __result = RouteHome(pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "robot return routing");
            }
        }

        /// <summary>True when this pawn is a robot whose bound recharge
        /// station sits on a DIFFERENT map: any local answer from a
        /// return/recharge giver is then invalid - including cell-target Goto
        /// jobs (run #72: Return2BaseRoom computed a base-room cell on the
        /// station's map; a bare cell carries no map, so with plumb column
        /// coordinates the job "completed" instantly on the wrong level and
        /// looped into vanilla's 10-jobs-per-tick breaker). Thing targets,
        /// cell targets, and the empty case all collapse into this one rule.</summary>
        private static bool StationElsewhere(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.Map != null
                && robotType != null && robotType.IsInstanceOfType(pawn)
                && rechargeStationField.GetValue(pawn) is Thing station
                && !station.Destroyed && station.Spawned
                && station.Map != null && station.Map != pawn.Map;
        }

        /// <summary>ThinkNode-shaped return/recharge givers (RechargeEnergyIdle)
        /// override TryIssueJobPackage and emit dock jobs with NO map check -
        /// harmless in base Misc Robots where bots never leave their map, but an
        /// error loop once our stairs move them (run #70: cross-map reservation
        /// always fails, job dies, node re-runs). Empty result -> route home
        /// like the TryGiveJob path. A job targeting a thing on ANOTHER map is
        /// unstartable: replace it with the stairs trip home, or clean NoJob
        /// when no stairs are available.</summary>
        private static void ReturnThinkPostfix(Pawn pawn, ThinkNode __instance, ref ThinkResult __result)
        {
            if (!active || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            try
            {
                Job cur = __result.Job;
                if (cur != null && !StationElsewhere(pawn))
                {
                    // Station is local: their answer is fine whatever it is.
                    return;
                }
                Job route = RouteHome(pawn);
                if (route != null)
                {
                    __result = new ThinkResult(route, __instance, JobTag.Misc);
                }
                else if (cur != null)
                {
                    __result = ThinkResult.NoJob;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "robot return think routing");
            }
        }

        /// <summary>Shared routing core: the stairs job toward the robot's bound
        /// recharge station when it sits on a linked level, or null (wrong
        /// column, no stairs, cooldown, or nothing to do).</summary>
        private static Job RouteHome(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null
                || pawn.GetLord() != null)
            {
                return null;
            }
            if (!(rechargeStationField.GetValue(pawn) is Thing station)
                || station.Destroyed || !station.Spawned)
            {
                return null;
            }
            Map stationMap = station.Map;
            if (stationMap == null || stationMap == pawn.Map)
            {
                return null;
            }
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            // Next hop toward the station's level; two hops max (cap 3).
            Map next;
            if (comp.upperMap == stationMap || comp.lowerMap == stationMap)
            {
                next = stationMap;
            }
            else if (comp.upperMap != null && comp.upperMap.Levels()?.upperMap == stationMap)
            {
                next = comp.upperMap;
            }
            else if (comp.lowerMap != null && comp.lowerMap.Levels()?.lowerMap == stationMap)
            {
                next = comp.lowerMap;
            }
            else
            {
                // Different map stack entirely (another colony): not ours.
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!routeCooldown.Ready(pawn, now))
            {
                return null;
            }
            routeCooldown.ChargeUntil(pawn, now + RouteCooldownTicks);
            IntVec3 dest = next == stationMap ? station.Position : IntVec3.Invalid;
            if (!CrossLevelWork.TryStairsJobToward(pawn, next, dest, out Job job))
            {
                return null;
            }
            ABLog.Dev("Routing robot " + pawn.LabelShort + " home toward its recharge station on level "
                + stationMap.Level() + ".");
            return job;
        }

        /// <summary>Robots++ cross-level work: when a robot's own map scan
        /// comes up empty, check the linked levels' work summaries for the
        /// robot's OWN work types (from its GetWorkGivers list) and take the
        /// stairs on a plausible hit. Arrival re-runs its scan locally; when
        /// the work dries up the existing return routing brings it home to
        /// dock. Cold path: only fires on an empty scan, behind a 900-tick
        /// per-robot cooldown, never for low-battery robots.</summary>
        private static void WorkJobPostfix(Pawn pawn, ThinkNode __instance, ref ThinkResult __result)
        {
            if (!active || getWorkGiversMethod == null || __result.Job != null
                || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelWork)
            {
                return;
            }
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null
                    || pawn.GetLord() != null
                    || robotType == null || !robotType.IsInstanceOfType(pawn))
                {
                    return;
                }
                if (CrossLevelWork.LowPowerWorker(pawn))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!workRouteCooldown.Ready(pawn, now))
                {
                    return;
                }
                workRouteCooldown.ChargeUntil(pawn, now + WorkRouteCooldownTicks);
                if (!(getWorkGiversMethod.Invoke(pawn, WorkGiversArgs) is IList raw)
                    || raw.Count == 0)
                {
                    return;
                }
                List<WorkGiver> order = new List<WorkGiver>(raw.Count);
                for (int i = 0; i < raw.Count; i++)
                {
                    if (raw[i] is WorkGiver wg && wg.def?.workType != null
                        && !LevelWorkSummary.IsOwnCrossLevelGiver(wg.def))
                    {
                        order.Add(wg);
                    }
                }
                if (order.Count == 0)
                {
                    return;
                }
                Job job = TryMigrate(pawn, order, comp.upperMap) ?? TryMigrate(pawn, order, comp.lowerMap);
                if (job == null)
                {
                    return;
                }
                ABLog.Dev("Routing robot " + pawn.LabelShort + " toward probed work on another level.");
                __result = new ThinkResult(job, __instance, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "robot work routing");
            }
        }

        /// <summary>Probe-gated robot migration (run #71 bounce fix): summary
        /// bits pre-gate cheaply, then a REAL virtual-position probe of the
        /// robot's own giver list must find doable work before any stairs job
        /// is issued - exactly the colonist migration discipline. A basement
        /// full of blueprints with no local materials no longer lures builder
        /// bots into a down-and-straight-back-up bounce; the surface-side
        /// supply giver ferries the materials first, and only then does the
        /// probe light up.</summary>
        private static Job TryMigrate(Pawn pawn, List<WorkGiver> order, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            bool plausible = false;
            for (int i = 0; i < order.Count; i++)
            {
                if (LevelWorkSummary.Plausible(target, order[i].def.workType))
                {
                    plausible = true;
                    break;
                }
            }
            if (!plausible)
            {
                return null;
            }
            // Island-aware probe (2026-07-24): every distinct stair island of
            // the target level is tried, not just the exit nearest the robot.
            if (!CrossLevelWork.ProbeWorkAt(pawn, target, order, out IntVec3 workDest,
                out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return null;
            }
            StairRouter.Reroute(pawn, target, workDest, ref stairs, ref exit);
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }
    }
}
