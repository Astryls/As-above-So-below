using System.Runtime.CompilerServices;
using Verse;

namespace AsAboveSoBelow
{
    public static class LevelExtensions
    {
        // MapComponent lookup is a list scan; cache it per map. CWT is thread safe
        // and drops entries automatically when a map is collected.
        private static readonly ConditionalWeakTable<Map, LevelComp> cache = new ConditionalWeakTable<Map, LevelComp>();

        public static LevelComp Levels(this Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (cache.TryGetValue(map, out LevelComp comp))
            {
                return comp;
            }
            comp = map.GetComponent<LevelComp>();
            if (comp != null)
            {
                try
                {
                    cache.Add(map, comp);
                }
                catch (System.ArgumentException)
                {
                    // Benign race: another thread cached it first.
                }
            }
            return comp;
        }

        public static int Level(this Map map)
        {
            LevelComp c = map.Levels();
            return c?.level ?? 0;
        }

        public static Map UpperMap(this Map map) => map.Levels()?.upperMap;

        public static Map LowerMap(this Map map) => map.Levels()?.lowerMap;

        public static Map GroundMap(this Map map)
        {
            LevelComp c = map.Levels();
            if (c == null)
            {
                return map;
            }
            if (c.groundMap != null)
            {
                return c.groundMap;
            }
            return c.level == 0 ? map : null;
        }

        /// <summary>The column controller: the ground map's comp.</summary>
        public static LevelComp Controller(this Map map)
        {
            Map ground = map.GroundMap();
            return ground != null ? ground.Levels() : map.Levels();
        }

        public static bool ConnectedToOtherLevel(this Map map)
        {
            LevelComp c = map.Levels();
            return c != null && (c.upperMap != null || c.lowerMap != null);
        }

        public static bool IsLevelMap(this Map map)
        {
            LevelComp c = map.Levels();
            return c != null && c.level != 0;
        }
    }
}
