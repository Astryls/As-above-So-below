using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-map cache of the materials that player blueprints and frames still
    /// need (exact remaining counts via IConstructible.ThingCountNeeded). Used to
    /// pull materials toward construction on other levels and to stop storage
    /// hauling from carrying materials away from a level that needs them.
    /// </summary>
    public static class CrossLevelDemand
    {
        private const int CacheTtlTicks = 600;

        private static readonly Dictionary<int, CacheEntry> cache = new Dictionary<int, CacheEntry>();

        private class CacheEntry
        {
            public int tick;
            public readonly Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
        }

        public static bool Demands(Map map, ThingDef def)
        {
            if (map == null || map.Disposed || def == null)
            {
                return false;
            }
            CacheEntry entry = GetEntry(map);
            return entry.need.TryGetValue(def, out int n) && n > 0;
        }

        private static CacheEntry GetEntry(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(map.uniqueID, out CacheEntry entry) && now - entry.tick < CacheTtlTicks)
            {
                return entry;
            }
            if (cache.Count > 64)
            {
                cache.Clear();
            }
            entry = new CacheEntry { tick = now };
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint), entry.need);
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame), entry.need);
            cache[map.uniqueID] = entry;
            return entry;
        }

        private static void AddFrom(List<Thing> things, Dictionary<ThingDef, int> need)
        {
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is IConstructible constructible) || things[i].Faction != Faction.OfPlayer)
                {
                    continue;
                }
                List<ThingDefCountClass> cost = constructible.TotalMaterialCost();
                for (int j = 0; j < cost.Count; j++)
                {
                    ThingDef def = cost[j].thingDef;
                    if (def == null)
                    {
                        continue;
                    }
                    int remaining = constructible.ThingCountNeeded(def);
                    if (remaining > 0)
                    {
                        need.TryGetValue(def, out int cur);
                        need[def] = cur + remaining;
                    }
                }
            }
        }
    }
}
