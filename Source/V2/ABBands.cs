using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE-ONLY layout: lets a wormhole be tested on an ordinary (unbanded) map by
    /// declaring one rect to be "band 1". Kept so the Stage 0 spike still runs after the
    /// real band model landed. Production banding comes from ABBandMap.
    /// </summary>
    public sealed class ABBandLayout
    {
        private readonly CellRect testRect;

        private ABBandLayout(CellRect testRect)
        {
            this.testRect = testRect;
        }

        public static ABBandLayout TestRect(CellRect rect) => new ABBandLayout(rect);

        public int BandOf(IntVec3 cell) => testRect.Contains(cell) ? 1 : 0;
    }

    /// <summary>
    /// The band API every other V2 system talks to. Reads the persisted ABBandMap first
    /// and falls back to a spike layout when one is registered.
    ///
    /// Everything here must stay allocation-free and cheap: BandOf sits on the movement
    /// hot path (every StartPath) and inside region/plant/temperature patches.
    /// </summary>
    public static class ABBands
    {
        private static readonly ConditionalWeakTable<Map, ABBandMap> cache = new ConditionalWeakTable<Map, ABBandMap>();

        /// <summary>Dev-spike layouts, keyed by the Map OBJECT. Was keyed by map.uniqueID,
        /// which leaks across loads (same id, dead session's layout) and across games (ids
        /// restart at 0) - the same defect that broke wormholes on reload.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, ABBandLayout> spikeLayouts =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Map, ABBandLayout>();

        /// <summary>
        /// One-entry front cache for CompOf, held as a single immutable object.
        ///
        /// WHY A CLASS AND NOT TWO STATIC FIELDS: CompOf is reached from PawnRenderer's
        /// ParallelPreDraw, which runs on Unity job worker threads. Two separate statics can
        /// be read TORN - the map from one entry and the component from another - handing a
        /// caller the wrong map's band layout, intermittently, only under load. Reference
        /// assignment is atomic, so publishing both fields together behind one reference and
        /// reading it once into a local makes the pair consistent by construction. No lock,
        /// no volatile, no cost.
        /// </summary>
        private sealed class CompMemo
        {
            public readonly Map map;

            public readonly ABBandMap comp;

            public CompMemo(Map map, ABBandMap comp)
            {
                this.map = map;
                this.comp = comp;
            }
        }

        private static CompMemo memo;

        /// <summary>Drop the front cache so a removed map is not pinned alive by it.</summary>
        public static void ForgetMemo()
        {
            memo = null;
        }

        /// <summary>Re-point every cache at the authoritative component instance.
        ///
        /// Called from ABBandMap.FinalizeInit, and it is load-bearing for LOAD. On loading
        /// a save, Map.ExposeData first runs ConstructComponents() - creating a fresh,
        /// EMPTY ABBandMap - and only later replaces it with the deserialized instance that
        /// carries the real band data. Loading from a running game keeps the main thread
        /// rendering during that window, so CameraDriver.Update -> band clamp -> CompOf
        /// could run against the half-loaded map, get the empty instance (non-null!), and
        /// cache it. Every subsequent lookup then returned the poisoned empty component:
        /// Banded == false on a fully banded map - the camera clamp died and every stair
        /// reported "this map has no bands". FinalizeInit runs on the REAL instance, so
        /// rebinding here heals any such poisoning no matter how it happened.</summary>
        public static void RebindAfterLoad(Map map, ABBandMap comp)
        {
            memo = null;
            if (map == null || comp == null)
            {
                return;
            }
            cache.Remove(map);
            try
            {
                cache.Add(map, comp);
            }
            catch (System.ArgumentException)
            {
                // Benign race.
            }
            memo = new CompMemo(map, comp);
        }

        public static ABBandMap CompOf(Map map)
        {
            if (map == null)
            {
                return null;
            }
            // NEVER cache while a save is loading or being written. During load the
            // components list transiently holds a freshly constructed EMPTY component (see
            // RebindAfterLoad) - caching it poisons every later lookup. Answer live and
            // uncached until the Scribe is quiet.
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                return map.GetComponent<ABBandMap>();
            }
            // Measured motivation: GenTemperature.TryGetTemperatureForCell drove 719,002
            // calls through here in 2,000 frames and MixedBiomeMapComponent.GetBiomeAt
            // another 843,350 - and the temperature patch alone probed the table TWICE per
            // call. A ConditionalWeakTable lookup is a hash probe; a reference compare is
            // not. In practice every call in a burst is for the same map.
            CompMemo m = memo;
            if (m != null && ReferenceEquals(m.map, map))
            {
                return m.comp;
            }
            if (cache.TryGetValue(map, out ABBandMap comp))
            {
                memo = new CompMemo(map, comp);
                return comp;
            }
            comp = map.GetComponent<ABBandMap>();
            if (comp != null)
            {
                try
                {
                    cache.Add(map, comp);
                }
                catch (System.ArgumentException)
                {
                    // Benign race.
                }
                memo = new CompMemo(map, comp);
            }
            return comp;
        }

        // ---- spike support -------------------------------------------------

        public static void Register(Map map, ABBandLayout layout)
        {
            if (map != null)
            {
                spikeLayouts.Remove(map);
                spikeLayouts.Add(map, layout);
            }
        }

        public static void Clear(Map map)
        {
            if (map != null)
            {
                spikeLayouts.Remove(map);
            }
        }

        private static ABBandLayout SpikeLayoutOf(Map map)
        {
            if (map == null)
            {
                return null;
            }
            return spikeLayouts.TryGetValue(map, out ABBandLayout l) ? l : null;
        }

        // ---- primary API ---------------------------------------------------

        public static bool Banded(Map map)
        {
            ABBandMap c = CompOf(map);
            return (c != null && c.Banded) || SpikeLayoutOf(map) != null;
        }

        public static int BandOf(Map map, IntVec3 cell)
        {
            ABBandMap c = CompOf(map);
            if (c != null && c.Banded)
            {
                return c.BandOf(cell);
            }
            ABBandLayout l = SpikeLayoutOf(map);
            return l != null ? l.BandOf(cell) : 0;
        }

        public static bool SameBand(Map map, IntVec3 a, IntVec3 b)
        {
            ABBandMap c = CompOf(map);
            if (c != null && c.Banded)
            {
                return c.BandOf(a) == c.BandOf(b);
            }
            ABBandLayout l = SpikeLayoutOf(map);
            return l == null || l.BandOf(a) == l.BandOf(b);
        }

        /// <summary>Level of a cell: 0 surface, +1 sky, -1 basement.</summary>
        public static int LevelOf(Map map, IntVec3 cell)
        {
            ABBandMap c = CompOf(map);
            return c != null && c.Banded ? c.LevelOf(cell) : 0;
        }

        /// <summary>
        /// Does this terrain let you SEE the band below through it?
        ///
        /// AB_OpenAir is the obvious case. AB_WallTop has to be included, and the reason is
        /// worth stating: the top of a wall, viewed from the level above, IS the wall. When
        /// wall tops were first added they read as flat grey squares, because turning the
        /// cell from AB_OpenAir into a solid terrain made every see-below renderer skip it -
        /// so the wooden wall underneath stopped being drawn and its own terrain was painted
        /// over the top instead.
        ///
        /// Visibility and solidity are separate questions here. AB_WallTop stays impassable
        /// and buildable; it is only DRAWN through. Deliberately NOT used by the combat
        /// hole test in ABCombatV2 - being able to see a wall top is not the same as being
        /// able to shoot through it.
        /// </summary>
        public static bool ShowsBelow(TerrainDef t)
        {
            return t != null && (t == ABDefOf.AB_OpenAir || t == ABDefOf.AB_WallTop);
        }

        public static bool InGutter(Map map, IntVec3 cell)
        {
            ABBandMap c = CompOf(map);
            return c != null && c.InGutter(cell);
        }

        public static CellRect RectOfBand(Map map, int band)
        {
            ABBandMap c = CompOf(map);
            return c != null && c.Banded ? c.RectOfBand(band) : CellRect.WholeMap(map);
        }

        public static int SurfaceBand(Map map)
        {
            ABBandMap c = CompOf(map);
            return c != null ? c.surfaceBand : 0;
        }

        public static int BandCount(Map map)
        {
            ABBandMap c = CompOf(map);
            return c != null && c.Banded ? c.bandCount : 1;
        }

        /// <summary>True when the cell is on the surface band (or the map isn't banded).
        /// The standard gate for "should vanilla map-wide behaviour apply here".</summary>
        public static bool IsSurface(Map map, IntVec3 cell)
        {
            ABBandMap c = CompOf(map);
            return c == null || !c.Banded || c.BandOf(cell) == c.surfaceBand;
        }
    }
}
