using System;
using System.Reflection;
using HarmonyLib;
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

        private static readonly ABPawnCooldown routeCooldown = new ABPawnCooldown();

        private static FieldInfo rechargeStationField;

        private static bool active;

        static ABMiscRobotsCompat()
        {
            try
            {
                if (!ABDetect.Active("Haplo.Miscellaneous.Robots"))
                {
                    return;
                }
                Type robotType = AccessTools.TypeByName("AIRobot.X2_AIRobot");
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
    }
}
