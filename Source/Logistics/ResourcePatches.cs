using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Every level's resource counter also counts stored resources on the other
    /// levels of its column, so the readout (and per-map alerts like low food)
    /// treat the column as one colony.
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), nameof(ResourceCounter.UpdateResourceCounts))]
    internal static class Patch_ResourceCounter_AllLevels
    {
        private static readonly AccessTools.FieldRef<ResourceCounter, Map> MapRef =
            AccessTools.FieldRefAccess<ResourceCounter, Map>("map");

        private static readonly AccessTools.FieldRef<ResourceCounter, Dictionary<ThingDef, int>> CountsRef =
            AccessTools.FieldRefAccess<ResourceCounter, Dictionary<ThingDef, int>>("countedAmounts");

        private static readonly Func<ResourceCounter, Thing, bool> ShouldCountDel =
            AccessTools.MethodDelegate<Func<ResourceCounter, Thing, bool>>(
                AccessTools.Method(typeof(ResourceCounter), "ShouldCount"));

        private static void Postfix(ResourceCounter __instance)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            try
            {
                Map map = MapRef(__instance);
                LevelComp controller = map?.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                Dictionary<ThingDef, int> counts = CountsRef(__instance);
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map other = kvp.Value;
                    if (other == null || other == map || other.Disposed)
                    {
                        continue;
                    }
                    List<SlotGroup> groups = other.haulDestinationManager.AllGroupsListForReading;
                    for (int i = 0; i < groups.Count; i++)
                    {
                        foreach (Thing held in groups[i].HeldThings)
                        {
                            Thing inner = held.GetInnerIfMinified();
                            if (inner.def.CountAsResource && ShouldCountDel(__instance, inner))
                            {
                                counts[inner.def] += inner.stackCount;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "colony wide resource counts");
            }
        }
    }
}
