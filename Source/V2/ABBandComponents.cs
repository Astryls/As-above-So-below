using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
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
        }

        private static readonly ConditionalWeakTable<Map, Snapshot> byMap =
            new ConditionalWeakTable<Map, Snapshot>();

        private static readonly Queue<Region> queue = new Queue<Region>();

        public static void Invalidate()
        {
            version++;
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
                + " bandless regions skipped=" + s.skippedBandless);
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

    /// <summary>
    /// Full-rebuild invalidation. Runs at map load and on any explicit rebuild, and always
    /// invalidates.
    ///
    /// ⚠ BOTH ENTRY POINTS MUST BE COVERED. Patching only the dirty-rebuild below would leave
    /// a freshly loaded map holding a component map built against the PREVIOUS game.
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater),
        nameof(RegionAndRoomUpdater.RebuildAllRegionsAndRooms))]
    public static class Patch_RegionUpdater_ABInvalidateAll
    {
        private static void Postfix()
        {
            ABBandComponents.Invalidate();
        }
    }

    /// <summary>
    /// Dirty-rebuild invalidation - ONLY WHEN THERE WAS ACTUALLY SOMETHING DIRTY.
    ///
    /// ⚠⚠ THE UNCONDITIONAL VERSION OF THIS WAS A LATENT DISASTER AND PHASE 1 CAUGHT IT.
    /// `TryRebuildDirtyRegionsAndRooms` is called every tick regardless of whether anything
    /// changed, so bumping the version in a bare postfix invalidated the component map
    /// CONSTANTLY: the first report on a quiet map read `version=759324`. As a diagnostic that
    /// is harmless - one rebuild when you press the button. The moment phase 3 puts
    /// `SameComponent` on `TrySegment`, which is on `StartPath`, it becomes a full 570-region
    /// BFS per query on the hottest path in the mod.
    ///
    /// `AnythingToRebuild` is public and cheap, so the prefix captures it and the postfix only
    /// invalidates when work was genuinely pending. Watch `version` vs `rebuilds` in the
    /// report: on a settled map both should now crawl.
    ///
    /// ⚠ THIS IS WHY PHASE 1 IS DATA-ONLY. The defect was invisible in the code and obvious in
    /// one line of output, and it would have shipped straight into the movement path.
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater),
        nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
    public static class Patch_RegionUpdater_ABInvalidateDirty
    {
        private static void Prefix(RegionAndRoomUpdater __instance, out bool __state)
        {
            __state = false;
            try
            {
                __state = __instance.AnythingToRebuild;
            }
            catch
            {
                __state = true; // cannot tell: assume dirty rather than serve stale data
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                ABBandComponents.Invalidate();
            }
        }
    }
}
