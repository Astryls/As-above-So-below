using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// INTRA-BAND CONNECTED COMPONENTS. PHASE 1: DATA AND DIAGNOSTIC ONLY - NOTHING READS
    /// THIS YET, BY DESIGN.
    ///
    /// The problem it exists to solve. `ABWormholePather.TrySegment` opens with
    /// `if (ABBands.SameBand(pawn.Position, destCell)) return false;` - "same band, nothing to
    /// segment". That assumes SAME BAND implies REACHABLE WITHIN THE BAND, and on a fragmented
    /// band it does not: two plateaus on a sky band, or two sealed caverns in a basement, are
    /// the same band and separate islands. The pawn has to go down, across, and back up.
    ///
    /// Today we decline to segment, vanilla runs, and vanilla correctly finds no path - but
    /// `CanReach` has already said TRUE, because the region graph genuinely IS connected
    /// through our synthetic wormhole links (island A -> anchor -> band below -> anchor ->
    /// island B). So a job gets issued against a destination with no path. That is exactly the
    /// `CanReach=True` + `path=NOT FOUND` signature `AB2: why is this pawn stuck` was built to
    /// catch, and it is the "pawns upstairs in one building think they can walk to another
    /// building's upper floor" report.
    ///
    /// ⚠⚠ AND THE SAME WRONG ABSTRACTION IS ALREADY IN THE CROSS-BAND ROUTER.
    /// `ABWormhole.HopDistances` BFSes over BANDS as graph nodes. On a fragmented band that
    /// can hand back a wormhole whose near anchor sits in an island the pawn cannot reach, so
    /// the existing router is unsound on fragmented maps too - it just fails less visibly.
    /// The correct graph node is not a band. It is a (band, component) PAIR. Phase 2
    /// generalises `TryGetTransit` onto this; phase 3 relaxes `TrySegment`.
    ///
    /// ⚠ THE ENTIRE TRICK IS ONE FILTER: THE BFS REFUSES A NEIGHBOUR ON A DIFFERENT BAND.
    /// `Region.Neighbors` is topological, and the ONLY reason it ever spans bands is our own
    /// wormhole `RegionLink`s (§1). Refusing cross-band neighbours therefore yields exactly
    /// "what could I walk to without using a staircase", which is the question the pathfinder
    /// actually answers. No pathfinding is involved and none may be: asking the pathfinder
    /// first is the expensive failure §32 exists to avoid.
    ///
    /// ⚠ A REGION NEVER STRADDLES THE GUTTER, WHICH IS WHAT MAKES "THE BAND OF A REGION" WELL
    /// DEFINED. Regions are built only from walkable cells and the gutter is impassable across
    /// the full map width, so no region flood can cross it. A 12x12 region chunk that happens
    /// to span a band boundary simply yields two separate regions.
    ///
    /// ⚠ INVALIDATION IS DRIVEN BY THE REGION REBUILD, NOT BY A TIMER. A stale component map
    /// is the dangerous failure mode here - it would route a pawn to a staircase that no
    /// longer helps - and "recompute every N ticks" cannot be made correct, only less wrong.
    /// </summary>
    public static class ABBandComponents
    {
        /// <summary>Bumped by the region-rebuild postfix. Snapshots older than this rebuild.</summary>
        private static int version;

        /// <summary>How many times a snapshot has actually been rebuilt, as opposed to
        /// invalidated. ⚠ THE GAP BETWEEN `version` AND THIS IS THE WHOLE PERFORMANCE STORY -
        /// see the invalidation patch at the bottom of this file.</summary>
        public static int rebuilds;

        private sealed class Snapshot
        {
            public int builtVersion = -1;

            /// <summary>Region id -> component id.</summary>
            public readonly Dictionary<int, int> regionToComponent = new Dictionary<int, int>();

            public readonly List<int> componentBand = new List<int>();

            public readonly List<int> componentRegions = new List<int>();

            public readonly List<int> componentCells = new List<int>();

            public int skippedBandless;

            /// <summary>True when ANY band holds more than one component. ⚠ THIS IS THE HOT
            /// PATH'S EARLY-OUT: on a map where every band is one island the same-component
            /// question cannot possibly change an answer, so `TrySegment` must not pay for
            /// two region lookups per StartPath to discover that.</summary>
            public bool anyFragmented;
        }

        private static readonly ConditionalWeakTable<Map, Snapshot> byMap =
            new ConditionalWeakTable<Map, Snapshot>();

        private static readonly Queue<Region> queue = new Queue<Region>();

        public static void Invalidate()
        {
            version++;
        }

        /// <summary>Maps already subscribed, so a repeated FinalizeInit cannot stack duplicate
        /// handlers on one map's event.</summary>
        private static readonly ConditionalWeakTable<Map, object> hooked =
            new ConditionalWeakTable<Map, object>();

        /// <summary>
        /// Subscribe component invalidation to the ONE vanilla signal that means "regions
        /// actually changed". Called from ABBandMap alongside the wormhole re-arm hook.
        ///
        /// ⚠ `MapEvents.RegionsRoomsChanged` FIRES ON THE LAST LINE OF
        /// `TryRebuildDirtyRegionsAndRooms`, ON THE ONLY PATH THAT ACTUALLY REBUILT. That is
        /// what makes it free: the method itself is called ~4,500 times per frame and
        /// early-outs, and the event fires only for the handful of calls that did work.
        /// </summary>
        public static void Register(Map map)
        {
            if (map?.events == null || hooked.TryGetValue(map, out _))
            {
                return;
            }
            try
            {
                hooked.Add(map, new object());
            }
            catch (System.ArgumentException)
            {
                return; // benign race; already registered
            }
            // MapEvents dies with the map, so there is nothing to unsubscribe.
            map.events.RegionsRoomsChanged += delegate { version++; };
        }

        /// <summary>Component id for a cell, or -1 when the cell has no valid region (a wall,
        /// the gutter, unfogged rock). -1 never equals -1 for comparison purposes: see
        /// SameComponent.</summary>
        public static int ComponentOf(Map map, IntVec3 cell)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return -1;
            }
            Snapshot s = SnapshotFor(map);
            if (s == null)
            {
                return -1;
            }
            Region r = map.regionGrid.GetValidRegionAt_NoRebuild(cell);
            if (r == null || !r.valid)
            {
                return -1;
            }
            return s.regionToComponent.TryGetValue(r.id, out int c) ? c : -1;
        }

        /// <summary>
        /// True when both cells are in the same walk-without-stairs island.
        ///
        /// ⚠ AN UNKNOWN COMPONENT (-1) IS NEVER "SAME". A cell with no valid region is a wall
        /// or the void; treating two unknowns as equal would make every pair of unreachable
        /// cells look mutually reachable, which is the exact inversion of the bug this is for.
        /// </summary>
        public static bool SameComponent(Map map, IntVec3 a, IntVec3 b)
        {
            int ca = ComponentOf(map, a);
            if (ca < 0)
            {
                return false;
            }
            return ca == ComponentOf(map, b);
        }

        /// <summary>
        /// True ONLY when both cells resolve to a component AND those components differ.
        ///
        /// ⚠ THIS IS NOT `!SameComponent`, AND THE DIFFERENCE IS THE WHOLE SAFETY MARGIN.
        /// An unknown component (-1) means a wall, the gutter, or unfogged rock. `SameComponent`
        /// answers false for those, which is right for "can I definitely walk there" but would
        /// be catastrophic here: `TrySegment` would read unknown as "different island" and
        /// start routing every ordinary intra-band order through a staircase. Unknown must
        /// mean LEAVE IT ALONE.
        ///
        /// ⚠ AND IT EARLY-OUTS ON AN UNFRAGMENTED MAP BEFORE TOUCHING THE REGION GRID. This
        /// runs on `Pawn_PathFollower.StartPath`, which every pawn hits constantly; on a map
        /// with no fragmented band the answer is always false and must cost one bool read.
        /// </summary>
        public static bool KnownDifferentComponents(Map map, IntVec3 a, IntVec3 b)
        {
            Snapshot s = SnapshotFor(map);
            if (s == null || !s.anyFragmented)
            {
                return false;
            }
            int ca = ComponentOf(map, a);
            if (ca < 0)
            {
                return false;
            }
            int cb = ComponentOf(map, b);
            return cb >= 0 && ca != cb;
        }

        private static Snapshot SnapshotFor(Map map)
        {
            if (map == null || !ABBands.Banded(map))
            {
                return null;
            }
            Snapshot s = byMap.GetValue(map, _ => new Snapshot());
            if (s.builtVersion != version)
            {
                Build(map, s);
                rebuilds++;
                s.builtVersion = version;
            }
            return s;
        }

        private static void Build(Map map, Snapshot s)
        {
            s.regionToComponent.Clear();
            s.componentBand.Clear();
            s.componentRegions.Clear();
            s.componentCells.Clear();
            s.skippedBandless = 0;

            // NoRebuild: this can be reached from a diagnostic or (later) from StartPath, and
            // triggering a region rebuild from either would be a re-entrancy hazard. Invalid
            // regions are filtered explicitly rather than by asking for a clean list.
            foreach (Region seed in map.regionGrid.AllRegions_NoRebuild_InvalidAllowed)
            {
                if (seed == null || !seed.valid || s.regionToComponent.ContainsKey(seed.id))
                {
                    continue;
                }
                int band = BandOfRegion(map, seed);
                if (band < 0)
                {
                    s.skippedBandless++;
                    continue;
                }

                int comp = s.componentBand.Count;
                int regions = 0;
                int cells = 0;

                queue.Clear();
                queue.Enqueue(seed);
                s.regionToComponent[seed.id] = comp;
                while (queue.Count > 0)
                {
                    Region cur = queue.Dequeue();
                    regions++;
                    cells += cur.CellCount;
                    foreach (Region n in cur.Neighbors)
                    {
                        if (n == null || !n.valid || s.regionToComponent.ContainsKey(n.id))
                        {
                            continue;
                        }
                        // ⚠ THE ONE LINE THE WHOLE FILE IS ABOUT. A cross-band neighbour can
                        // only be one of our wormhole links, and walking it requires a
                        // staircase - which is precisely what a component must NOT include.
                        if (BandOfRegion(map, n) != band)
                        {
                            continue;
                        }
                        s.regionToComponent[n.id] = comp;
                        queue.Enqueue(n);
                    }
                }
                queue.Clear();

                s.componentBand.Add(band);
                s.componentRegions.Add(regions);
                s.componentCells.Add(cells);
            }

            s.anyFragmented = false;
            for (int i = 0; i < s.componentBand.Count && !s.anyFragmented; i++)
            {
                for (int j = i + 1; j < s.componentBand.Count; j++)
                {
                    if (s.componentBand[i] == s.componentBand[j])
                    {
                        s.anyFragmented = true;
                        break;
                    }
                }
            }
        }

        /// <summary>Band of a region, via a cell known to belong to it. ⚠ `AnyCell`, NOT
        /// `extentsClose.CenterCell` - extentsClose is a bounding box and its centre can sit
        /// outside an L-shaped region entirely.</summary>
        private static int BandOfRegion(Map map, Region r)
        {
            IntVec3 c = r.AnyCell;
            return c.IsValid && c.InBounds(map) ? ABBands.BandOf(map, c) : -1;
        }

        /// <summary>
        /// Per-band component census, plus the selected pawn's own component against its
        /// destination. This is the whole point of phase 1: prove the data is right on a real
        /// fragmented map before anything depends on it.
        /// </summary>
        public static string Report(Map map, Pawn selected)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB2 BAND COMPONENT REPORT");
            if (map == null || !ABBands.Banded(map))
            {
                sb.AppendLine("  map is not banded");
                return sb.ToString();
            }
            Snapshot s = SnapshotFor(map);
            if (s == null)
            {
                sb.AppendLine("  no snapshot");
                return sb.ToString();
            }

            int bandCount = ABBands.BandCount(map);
            sb.AppendLine("  version=" + version + " rebuilds=" + rebuilds
                + " components=" + s.componentBand.Count
                + " regions mapped=" + s.regionToComponent.Count
                + " bandless regions skipped=" + s.skippedBandless
                + " anyFragmented=" + s.anyFragmented);
            for (int b = 0; b < bandCount; b++)
            {
                int n = 0;
                int biggest = 0;
                int total = 0;
                for (int i = 0; i < s.componentBand.Count; i++)
                {
                    if (s.componentBand[i] != b)
                    {
                        continue;
                    }
                    n++;
                    total += s.componentCells[i];
                    if (s.componentCells[i] > biggest)
                    {
                        biggest = s.componentCells[i];
                    }
                }
                // ⚠ A BAND WITH ONE COMPONENT BEHAVES EXACTLY AS IT DOES TODAY. Bands with
                // MORE than one are the entire reason this exists, and a band whose largest
                // island is a small fraction of its cells is where the current SameBand
                // early-out is actively wrong.
                sb.AppendLine("  band " + b + ": components=" + n
                    + " cells=" + total
                    + (n > 1 ? ("  LARGEST=" + biggest
                        + " (" + (total > 0 ? (100f * biggest / total).ToString("0") : "0")
                        + "% of band)  <-- FRAGMENTED") : ""));
            }

            if (selected != null && selected.Spawned && selected.Map == map)
            {
                int pc = ComponentOf(map, selected.Position);
                sb.AppendLine("  " + selected.LabelShortCap + " at " + selected.Position
                    + " band " + ABBands.BandOf(map, selected.Position)
                    + " component " + pc);
                IntVec3 dest = selected.pather != null && selected.pather.Destination.IsValid
                    ? selected.pather.Destination.Cell
                    : IntVec3.Invalid;
                if (dest.IsValid)
                {
                    int dc = ComponentOf(map, dest);
                    sb.AppendLine("    destination " + dest
                        + " band " + ABBands.BandOf(map, dest)
                        + " component " + dc
                        + "  sameBand=" + ABBands.SameBand(map, selected.Position, dest)
                        + "  sameComponent=" + (pc >= 0 && pc == dc));
                    // ⚠ THE SIGNATURE PHASE 3 IS FOR. sameBand=True with sameComponent=False
                    // is a destination TrySegment currently refuses to route and the
                    // pathfinder cannot reach - the stall this whole design addresses.
                    if (ABBands.SameBand(map, selected.Position, dest) && pc >= 0 && pc != dc)
                    {
                        sb.AppendLine("    >> SAME BAND, DIFFERENT ISLAND. This is the case "
                            + "TrySegment currently declines to segment and vanilla cannot "
                            + "path. Phase 3 target.");
                    }
                }
            }
            return sb.ToString();
        }
    }

    // ⚠⚠ THE HARMONY PATCHES THAT LIVED HERE HAVE BEEN DELETED. INVALIDATION IS AN EVENT
    // SUBSCRIPTION NOW - see ABBandComponents.Register, called from ABBandMap.
    //
    // They were a postfix on `RebuildAllRegionsAndRooms` and a prefix+postfix on
    // `TryRebuildDirtyRegionsAndRooms` gated on `AnythingToRebuild`. Both were wrong, and the
    // reason was ALREADY WRITTEN DOWN in ABWormhole.cs by an earlier session that made the
    // identical mistake for the wormhole re-arm:
    //
    //   - `TryRebuildDirtyRegionsAndRooms` is called **~4,500 times per frame**. That is why
    //     the first component report read `version=759324` on a quiet map.
    //   - Gating it with a prefix that samples `AnythingToRebuild` recovers only ~22%,
    //     because the gate ADDS A SECOND PATCH to an extremely hot method and the residue is
    //     Harmony dispatch cost, not work. Measured there at 0.339 -> 0.266 ms/frame.
    //   - Vanilla already publishes the exact signal: `MapEvents.RegionsRoomsChanged` is
    //     invoked on the LAST line of `TryRebuildDirtyRegionsAndRooms`, on the only path that
    //     actually rebuilt (after `SetAllClean` and `initialized = true`). Subscribing costs
    //     nothing on the millions of no-op calls and fires exactly once per real rebuild.
    //
    // ⚠ SO: NEVER PATCH `TryRebuildDirtyRegionsAndRooms`. If you need to know that regions
    // changed, subscribe to `map.events.RegionsRoomsChanged`.
}
