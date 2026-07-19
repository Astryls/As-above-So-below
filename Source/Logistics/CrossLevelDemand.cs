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
            public readonly Dictionary<ThingDef, int> available = new Dictionary<ThingDef, int>();
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

        /// <summary>Quantity-aware pin: exporting this stack is fine as long as the
        /// level keeps enough of the material to cover its remaining construction
        /// need. A level needing 20 steel out of 500 exports freely.</summary>
        public static bool ExportAllowed(Map map, Thing t)
        {
            if (map == null || map.Disposed || t?.def == null)
            {
                return true;
            }
            CacheEntry entry = GetEntry(map);
            if (!entry.need.TryGetValue(t.def, out int need) || need <= 0)
            {
                return true;
            }
            entry.available.TryGetValue(t.def, out int available);
            return available - t.stackCount >= need;
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
            foreach (KeyValuePair<ThingDef, int> kvp in entry.need)
            {
                List<Thing> things = map.listerThings.ThingsOfDef(kvp.Key);
                int sum = 0;
                for (int i = 0; i < things.Count; i++)
                {
                    sum += things[i].stackCount;
                }
                entry.available[kvp.Key] = sum;
            }
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
