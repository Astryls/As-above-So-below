using System;
using System.Collections;
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
    /// Fix: postfix each return/recharge giver's TryGiveJob. When it produced
    /// no job and the robot's bound rechargeStation sits on a linked level,
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
                int patched = 0;
                for (int i = 0; i < giverTypeNames.Length; i++)
                {
                    Type giver = AccessTools.TypeByName(giverTypeNames[i]);
                    // Declared only: subclasses inheriting TryGiveJob (e.g.
                    // Return2BaseAndWait overriding RechargeEnergy's) must not
                    // double-patch the base implementation.
                    MethodInfo method = giver != null ? AccessTools.DeclaredMethod(giver, "TryGiveJob") : null;
                    if (method != null)
                    {
                        HarmonyBoot.Harmony.Patch(method, postfix: postfix);
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
            if (!active || __result != null || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null
                    || pawn.GetLord() != null)
                {
                    return;
                }
                if (!(rechargeStationField.GetValue(pawn) is Thing station)
                    || station.Destroyed || !station.Spawned)
                {
                    return;
                }
                Map stationMap = station.Map;
                if (stationMap == null || stationMap == pawn.Map)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
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
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!routeCooldown.Ready(pawn, now))
                {
                    return;
                }
                routeCooldown.ChargeUntil(pawn, now + RouteCooldownTicks);
                IntVec3 dest = next == stationMap ? station.Position : IntVec3.Invalid;
                if (!CrossLevelWork.TryStairsJobToward(pawn, next, dest, out Job job))
                {
                    return;
                }
                ABLog.Dev("Routing robot " + pawn.LabelShort + " home toward its recharge station on level "
                    + stationMap.Level() + ".");
                __result = job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "robot return routing");
            }
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
                if (!(getWorkGiversMethod.Invoke(pawn, WorkGiversArgs) is IList givers)
                    || givers.Count == 0)
                {
                    return;
                }
                Map target = FindWorkLevel(givers, comp.upperMap) ?? FindWorkLevel(givers, comp.lowerMap);
                if (target == null)
                {
                    return;
                }
                if (!CrossLevelWork.TryStairsJobToward(pawn, target, IntVec3.Invalid, out Job job))
                {
                    return;
                }
                ABLog.Dev("Routing robot " + pawn.LabelShort + " toward work on level " + target.Level() + ".");
                __result = new ThinkResult(job, __instance, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "robot work routing");
            }
        }

        /// <summary>The first linked level whose work summary says work of any
        /// of the robot's own work types is plausibly available. Our own
        /// cross-level givers are skipped (no recursion; they already run
        /// inside the robot's normal scan).</summary>
        private static Map FindWorkLevel(IList givers, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            for (int i = 0; i < givers.Count; i++)
            {
                WorkGiver giver = givers[i] as WorkGiver;
                WorkTypeDef workType = giver?.def?.workType;
                if (workType == null || LevelWorkSummary.IsOwnCrossLevelGiver(giver.def))
                {
                    continue;
                }
                if (LevelWorkSummary.Plausible(target, workType))
                {
                    return target;
                }
            }
            return null;
        }
    }
}
