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

    /// <summary>Storage reconfiguration is the player's snappiest logistics
    /// order (bug report 2026-07-24: food storage moved to a sky bridge and
    /// haulers kept idling on stale "no better storage" verdicts for up to a
    /// cache TTL). TryNotifyChanged fires on every filter or priority change of
    /// any storage; clear the cross-level verdict and demand caches and bump
    /// the work summary for the owner's map so cooled-down haulers re-probe
    /// immediately.</summary>
    [HarmonyPatch(typeof(StorageSettings), "TryNotifyChanged")]
    internal static class Patch_StorageChanged_CrossLevel
    {
        private static void Postfix(StorageSettings __instance)
        {
            NotifyStorageChanged(__instance);
        }

        /// <summary>Shared invalidation for any storage reconfiguration.</summary>
        internal static void NotifyStorageChanged(StorageSettings settings)
        {
            if (settings == null || !ABGuard.On(ABGuard.Logistics)
                || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                CrossLevelHaul.ClearVerdicts();
                CrossLevelDemand.InvalidateAll();
                Map map = null;
                IStoreSettingsParent owner = settings.owner;
                if (owner is Zone zone)
                {
                    map = zone.Map;
                }
                else if (owner is Thing thing)
                {
                    map = thing.MapHeld;
                }
                if (map != null && map.ConnectedToOtherLevel())
                {
                    // Re-arms the versioned better-work cooldowns so idle
                    // haulers on other levels react now, not in 20 seconds.
                    LevelWorkSummary.Notify_WorkChanged(map);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "storage change invalidation");
            }
        }
    }

    /// <summary>The storage PRIORITY setter is a plain field assignment - it does
    /// NOT call TryNotifyChanged the way the filter does - so bumping a stockpile
    /// to Critical never invalidated our cross-level verdict cache, and haulers
    /// kept using a stale "no better storage" answer until the cache TTL lapsed
    /// (user report 2026-07-26: "sometimes they don't react to changes in storage
    /// priority"). Fire the same invalidation on a priority change.</summary>
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.Priority), MethodType.Setter)]
    internal static class Patch_StoragePriorityChanged_CrossLevel
    {
        private static void Postfix(StorageSettings __instance)
        {
            Patch_StorageChanged_CrossLevel.NotifyStorageChanged(__instance);
        }
    }
}
