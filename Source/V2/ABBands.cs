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

        /// <summary>How many spike layouts have ever been registered this session.
        ///
        /// Purely a fast-out, and it earns its keep on EVERY MAP IN THE GAME. Banded(),
        /// BandOf() and SameBand() all fall through to SpikeLayoutOf() when the real band
        /// component says no - which is the answer on every quest site, caravan map, pocket
        /// map and unbanded colony - so an ordinary map paid a SECOND ConditionalWeakTable
        /// probe on every one of those calls. Banded() alone is asked once per section
        /// regenerate from three separate patches. That is a permanent tax on everyone's game
        /// for a dev-only spike almost nobody will ever arm.</summary>
        private static int spikeCount;

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
                spikeCount++;
            }
        }

        public static void Clear(Map map)
        {
            if (map != null && spikeLayouts.Remove(map))
            {
                spikeCount = System.Math.Max(0, spikeCount - 1);
            }
        }

        private static ABBandLayout SpikeLayoutOf(Map map)
        {
            // One int compare replaces a hash probe for every map that has no spike layout,
            // which in practice is every map anyone plays. See spikeCount.
            if (spikeCount == 0 || map == null)
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

        /// <summary>
        /// THE one definition of "what do I actually see through this cell".
        ///
        /// Descends while each level is itself see-through and stops at the first opaque
        /// floor (or the bottom band), returning the accumulated <paramref name="drop"/> in
        /// cells. Everything that looks downward MUST use this - the renderer, click-through,
        /// selection, overlays - because a single `- Slot` step silently works on a 3-level
        /// map and breaks the moment two see-through levels stack: from level +3 the level
        /// directly below is usually open air too, so one step lands in the void.
        /// </summary>
        public static bool TryResolveVisibleBelow(Map map, ABBandMap bands, IntVec3 cell,
            out IntVec3 below, out int drop)
        {
            below = cell;
            drop = 0;
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            if (!ShowsBelow(map.terrainGrid.TerrainAt(cell)))
            {
                return false; // opaque from here
            }
            int slot = bands.Slot;
            IntVec3 cur = cell;
            for (int guard = 0; guard < bands.bandCount; guard++)
            {
                IntVec3 next = new IntVec3(cur.x, cur.y, cur.z - slot);
                if (!next.InBounds(map) || bands.InGutter(next))
                {
                    return false;
                }
                cur = next;
                drop += slot;
                if (!ShowsBelow(map.terrainGrid.TerrainAt(cur)))
                {
                    below = cur;
                    return true; // an opaque floor: this is what is seen
                }
                if (bands.BandOf(cur) <= 0)
                {
                    below = cur;
                    return true; // bottom level; nothing further down
                }
            }
            return false;
        }

        /// <summary>
        /// THE FULL SEE-BELOW GATE, as every mirrored pass actually needs it.
        ///
        /// <see cref="TryResolveVisibleBelow"/> answers only "how far down does this column
        /// see". Every consumer additionally has to ask the same four preliminary questions -
        /// is the cell on the map, is it on a band that HAS something under it, is it out of
        /// the gutter, and is its own terrain see-through - and then usually whether what it
        /// found is legible (unfogged). Seven call sites hand-rolled that preamble
        /// independently, and they had drifted: some checked fog, some did not, some tested
        /// the gutter at the destination and some at the source.
        ///
        /// ⚠ THAT DRIFT IS NOT COSMETIC - IT IS HOW THE DESCENT BUG KEEPS COMING BACK.
        /// <c>SectionLayer_ABBelowLighting.SourceIndex</c> wrote its own preamble and then a
        /// single <c>idx - slot * sizeX</c> step, which is the one-descent bug for the EIGHTH
        /// time. It survived the standing `grep '- Slot'` audit for one reason: it was
        /// written as INDEX arithmetic, so it did not contain the string the audit looks for.
        /// A rule enforced by grepping for a syntax only catches that syntax. Enforcing it by
        /// making the correct version the only convenient one is what actually holds.
        ///
        /// Returns the cell this column genuinely shows and the accumulated drop to it.
        /// Consumers that need to distinguish "nothing below" from "fogged below" (the below
        /// terrain layer, which draws an air mask and a fog fan for each) still call
        /// TryResolveVisibleBelow directly - that is the one legitimate reason to.
        /// </summary>
        public static bool TryResolveVisibleFrom(Map map, ABBandMap bands, IntVec3 cell,
            bool requireUnfogged, out IntVec3 below, out int drop)
        {
            below = cell;
            drop = 0;
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            if (!cell.InBounds(map) || bands.BandOf(cell) <= 0 || bands.InGutter(cell))
            {
                return false; // bottom band, off map, or a seam: nothing to look down at
            }
            if (!ShowsBelow(map.terrainGrid.TerrainAt(cell)))
            {
                return false; // opaque from here
            }
            if (!TryResolveVisibleBelow(map, bands, cell, out below, out drop))
            {
                return false;
            }
            return !requireUnfogged || !map.fogGrid.IsFogged(below);
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
