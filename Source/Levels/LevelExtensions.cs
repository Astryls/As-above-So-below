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
            if (c.level == 0)
            {
                return map;
            }
            // groundMap field unset (old save, out-of-gen-context creation, or an
            // unrestored scribe reference): derive the ground by walking the
            // vertical links instead of failing to null. A null ground silently
            // breaks every column check keyed off it - SameColumn, the camera
            // level lock's ShouldSuppressJump, 2-hop cross-level RMB - so the
            // link-walk is the robust source of truth. Bounded by the 3-level cap.
            Map m = map;
            for (int i = 0; i < 4 && m != null; i++)
            {
                LevelComp mc = m.Levels();
                if (mc == null)
                {
                    return m;
                }
                if (mc.groundMap != null)
                {
                    return mc.groundMap;
                }
                if (mc.level == 0)
                {
                    return m;
                }
                m = mc.level > 0 ? mc.lowerMap : mc.upperMap;
            }
            return null;
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

        /// <summary>One-call form of the standard scan guard: true when the map
        /// has a level comp with at least one linked level, handing the comp
        /// back. Replaces the comp-null-or-no-links boilerplate that was
        /// duplicated at eleven call sites.</summary>
        public static bool TryLinkedLevels(this Map map, out LevelComp comp)
        {
            comp = map.Levels();
            return comp != null && (comp.upperMap != null || comp.lowerMap != null);
        }

        /// <summary>True when both maps belong to the same vertical column
        /// (either may be the ground map itself). Distinct unlinked maps each
        /// resolve to themselves as ground, so they never match.</summary>
        public static bool SameColumn(this Map a, Map b)
        {
            if (a == null || b == null)
            {
                return false;
            }
            if (a == b)
            {
                return true;
            }
            Map groundA = a.GroundMap();
            return groundA != null && groundA == b.GroundMap();
        }

        public static bool IsLevelMap(this Map map)
        {
            LevelComp c = map.Levels();
            return c != null && c.level != 0;
        }
    }
}
