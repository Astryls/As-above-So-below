using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// T11 column trade. Orbital trade beacons on any level of the column sell
    /// their goods (the basement vault finally pays out), and comms consoles on
    /// pocket levels can hail the ships passing over the surface. Purchases
    /// still drop at the surface: pods cannot fall into a basement. Recursion
    /// is impossible by construction - the patches act only on ground maps and
    /// the appended calls run against pocket levels. Kill switch: world.
    /// </summary>
    internal static class ColumnTrade
    {
        internal static bool Active(Map ground, out LevelComp comp)
        {
            comp = null;
            if (!ABGuard.On(ABGuard.World))
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.worldIntegration)
            {
                return false;
            }
            comp = ground?.Levels();
            return comp != null && comp.level == 0
                && (comp.upperMap != null || comp.lowerMap != null);
        }
    }

    /// <summary>Trade listings walk the whole column's beacons.</summary>
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade))]
    internal static class Patch_AllLaunchableThings_Column
    {
        private static void Postfix(Map map, ITrader trader, ref IEnumerable<Thing> __result)
        {
            if (!ColumnTrade.Active(map, out LevelComp comp))
            {
                return;
            }
            IEnumerable<Thing> result = __result;
            if (comp.upperMap != null && !comp.upperMap.Disposed)
            {
                result = result.Concat(TradeUtility.AllLaunchableThingsForTrade(comp.upperMap, trader));
            }
            if (comp.lowerMap != null && !comp.lowerMap.Disposed)
            {
                result = result.Concat(TradeUtility.AllLaunchableThingsForTrade(comp.lowerMap, trader));
            }
            __result = result;
        }
    }

    /// <summary>Debt settlement (selling stack resources like silver) drains
    /// beacons level by level: surface first, then the linked levels.</summary>
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.LaunchThingsOfType))]
    internal static class Patch_LaunchThingsOfType_Column
    {
        private static bool inColumnLaunch;

        private static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (inColumnLaunch || !ColumnTrade.Active(map, out LevelComp comp))
            {
                return true;
            }
            try
            {
                inColumnLaunch = true;
                foreach (Map level in new[] { map, comp.upperMap, comp.lowerMap })
                {
                    if (level == null || level.Disposed || debt <= 0)
                    {
                        continue;
                    }
                    int available = CountAtBeacons(level, resDef);
                    if (available <= 0)
                    {
                        continue;
                    }
                    int take = Math.Min(debt, available);
                    TradeUtility.LaunchThingsOfType(resDef, take, level, trader);
                    debt -= take;
                }
                if (debt > 0)
                {
                    Log.Error("Could not find any " + resDef + " to transfer to trader (column-wide).");
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.World, e, "column trade launch");
                return true;
            }
            finally
            {
                inColumnLaunch = false;
            }
            return false;
        }

        private static int CountAtBeacons(Map map, ThingDef resDef)
        {
            int count = 0;
            foreach (Building_OrbitalTradeBeacon beacon in Building_OrbitalTradeBeacon.AllPowered(map))
            {
                foreach (IntVec3 cell in beacon.TradeableCells)
                {
                    List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
                    for (int i = 0; i < things.Count; i++)
                    {
                        if (things[i].def == resDef)
                        {
                            count += things[i].stackCount;
                        }
                    }
                }
            }
            return count;
        }
    }

    /// <summary>Comms consoles on pocket levels see the ships passing over the
    /// surface. The trade session itself runs against the ship's own (surface)
    /// map, whose listings are column-wide via the beacon patch.</summary>
    [HarmonyPatch(typeof(Building_CommsConsole), nameof(Building_CommsConsole.GetCommTargets))]
    internal static class Patch_CommTargets_Column
    {
        private static void Postfix(Pawn myPawn, ref IEnumerable<ICommunicable> __result)
        {
            if (!ABGuard.On(ABGuard.World))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.worldIntegration)
            {
                return;
            }
            Map map = myPawn?.Map;
            if (map == null || !ColumnWorld.TryGetColumnGround(map, out Map ground))
            {
                return;
            }
            if (ground.passingShipManager == null || ground.passingShipManager.passingShips.Count == 0)
            {
                return;
            }
            __result = __result.Concat(ground.passingShipManager.passingShips.Cast<ICommunicable>());
        }
    }
}
