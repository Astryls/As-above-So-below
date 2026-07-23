using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Column-wide orbital trading (parity audit P1): vanilla only sells what
    /// sits under powered beacons on the CURRENT map, so goods stored on
    /// another level were untradeable and selling silently shorted. Two
    /// patches make the column one market:
    ///  - AllLaunchableThingsForTrade appends the linked levels' beacon
    ///    inventories (reentrancy-guarded; the inner calls are the vanilla
    ///    method against each linked map).
    ///  - LaunchThingsOfType fulfills the debt column-wide, walking the same
    ///    beacon search per level and only erroring when the WHOLE column
    ///    cannot cover it.
    /// Comms consoles stay surface-side; caravan (physical) trading is
    /// unchanged by design - a walking trader can only see goods brought to
    /// them.
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.AllLaunchableThingsForTrade))]
    internal static class Patch_Trade_AllLaunchableAcrossLevels
    {
        /// <summary>True while enumerating a linked map's things through the
        /// vanilla method - its postfix must not append again.</summary>
        private static bool appending;

        private static void Postfix(Map map, ITrader trader, ref IEnumerable<Thing> __result)
        {
            if (appending || !ABGuard.On(ABGuard.Logistics)
                || !(ABMod.Settings?.crossLevelSupply ?? true))
            {
                return;
            }
            if (map == null || !map.TryLinkedLevels(out LevelComp comp))
            {
                return;
            }
            __result = AppendLinked(__result, comp, trader);
        }

        private static IEnumerable<Thing> AppendLinked(IEnumerable<Thing> local, LevelComp comp, ITrader trader)
        {
            foreach (Thing t in local)
            {
                yield return t;
            }
            appending = true;
            IEnumerator<Thing> linked = null;
            try
            {
                linked = LinkedThings(comp, trader).GetEnumerator();
                while (true)
                {
                    Thing t;
                    try
                    {
                        if (!linked.MoveNext())
                        {
                            break;
                        }
                        t = linked.Current;
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Logistics, e, "cross level trade enumeration");
                        break;
                    }
                    yield return t;
                }
            }
            finally
            {
                linked?.Dispose();
                appending = false;
            }
        }

        private static IEnumerable<Thing> LinkedThings(LevelComp comp, ITrader trader)
        {
            if (comp.upperMap != null && !comp.upperMap.Disposed)
            {
                foreach (Thing t in TradeUtility.AllLaunchableThingsForTrade(comp.upperMap, trader))
                {
                    yield return t;
                }
            }
            if (comp.lowerMap != null && !comp.lowerMap.Disposed)
            {
                foreach (Thing t in TradeUtility.AllLaunchableThingsForTrade(comp.lowerMap, trader))
                {
                    yield return t;
                }
            }
        }
    }

    [HarmonyPatch(typeof(TradeUtility), nameof(TradeUtility.LaunchThingsOfType))]
    internal static class Patch_Trade_LaunchAcrossLevels
    {
        private static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (!ABGuard.On(ABGuard.Logistics) || !(ABMod.Settings?.crossLevelSupply ?? true))
            {
                return true;
            }
            if (map == null || !map.TryLinkedLevels(out LevelComp comp))
            {
                return true;
            }
            // Column-wide from here on: never fall through to vanilla after
            // launching anything, or stacks would be taken twice.
            try
            {
                debt = LaunchFrom(map, resDef, debt, trader);
                if (debt > 0 && comp.upperMap != null && !comp.upperMap.Disposed)
                {
                    debt = LaunchFrom(comp.upperMap, resDef, debt, trader);
                }
                if (debt > 0 && comp.lowerMap != null && !comp.lowerMap.Disposed)
                {
                    debt = LaunchFrom(comp.lowerMap, resDef, debt, trader);
                }
                if (debt > 0)
                {
                    Log.Error("Could not find any " + resDef + " to transfer to trader (column-wide search).");
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level trade launch");
            }
            return false;
        }

        /// <summary>Vanilla's per-map beacon launch loop, minus the final
        /// error (the caller decides after the whole column is searched).</summary>
        private static int LaunchFrom(Map m, ThingDef resDef, int debt, TradeShip trader)
        {
            while (debt > 0)
            {
                Thing found = null;
                foreach (Building_OrbitalTradeBeacon beacon in Building_OrbitalTradeBeacon.AllPowered(m))
                {
                    foreach (IntVec3 cell in beacon.TradeableCells)
                    {
                        foreach (Thing t in m.thingGrid.ThingsAt(cell))
                        {
                            if (t.def == resDef)
                            {
                                found = t;
                                break;
                            }
                        }
                        if (found != null)
                        {
                            break;
                        }
                    }
                    if (found != null)
                    {
                        break;
                    }
                }
                if (found == null)
                {
                    return debt;
                }
                int num = Math.Min(debt, found.stackCount);
                if (trader != null)
                {
                    trader.GiveSoldThingToTrader(found, num, TradeSession.playerNegotiator);
                }
                else
                {
                    found.SplitOff(num).Destroy();
                }
                debt -= num;
            }
            return debt;
        }
    }
}
