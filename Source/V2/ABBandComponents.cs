using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// INTRA-BAND WALKABLE ISLANDS: "where could this pawn get to WITHOUT using a staircase".
    ///
    /// Consumed by `ABWormholePather.TrySegment` (should this same-band trip be routed through
    /// a stairwell) and by `ABWormhole.TryGetTransit` (is this anchor one the pawn can
    /// actually reach). See §34.
    ///
    /// ⚠⚠ THIS IS FLOODED OVER CELLS, NOT OVER REGIONS, AND THE FIRST VERSION GOT THAT WRONG
    /// IN A WAY THAT MADE IT USELESS. Flooding `Region.Neighbors` produced components that
    /// exactly matched `CanReach` - which is the thing we are trying to second-guess. Measured
    /// on a real two-platform fixture: `component: pawn=3 dest=3`, `CanReach=True`,
    /// `FindPathNow=NOT FOUND`. The map cannot be more pessimistic than its own source, so
    /// sourcing it from the region graph guaranteed it could never see the failure.
    ///
    /// The gap is documented at the top of `ABDevTools.V2PathProbe` and it is REAL PATHFINDER
    /// BEHAVIOUR, not a bug: a region is a CELL SET, but path production additionally refuses
    /// to cut a diagonal corner when either flanking cell is unwalkable
    /// (`PathUtility.BlocksDiagonalMovement`, applied in `Pawn_PathFollower`). A sky band is
    /// full of single-cell open-air holes, so two areas joined only by a diagonal touch are
    /// ONE REGION and NOT WALKABLE. That is the `CanReach=True` + `path=NOT FOUND` re-issue
    /// loop, and it predates §34 entirely.
    ///
    /// ⚠ SO THE FLOOD USES THE PATHFINDER'S OWN TWO RULES AND NOTHING ELSE:
    /// `PathGrid.WalkableFast` for the cell, and `PathUtility.BlocksDiagonalMovement` on BOTH
    /// flanking cells for a diagonal step - the identical pair `Pawn_PathFollower` applies. If
    /// this ever disagrees with the pathfinder again, THAT is the bug; do not paper over it
    /// at the consumer.
    ///
    /// ⚠ NO PATHFINDING IS INVOLVED AND NONE MAY BE. Asking `FindPathNow` whether two cells
    /// connect is the expensive exhaustive failure §32 exists to avoid.
    /// </summary>
    public static class ABBandComponents
    {
        /// <summary>
        /// An island smaller than this does not set the `fragmented` flag.
        ///
        /// ⚠ THE FLAG IS A HOT-PATH GATE, NOT A FACT, AND A ONE-CELL POCKET DEFEATED IT.
        /// Run #307 measured band 1 as "FRAGMENTED" on the strength of a SINGLE isolated cell
        /// (16155 total, largest 16154), which forced every `StartPath` on the map down the
        /// slow branch for something no pawn will ever be ordered into. 12 cells is a small
        /// room: below that an island is scenery, not a destination.
        ///
        /// ⚠ THIS GATES THE FLAG ONLY. `ComponentOf` and `KnownDifferentComponents` still
        /// report tiny islands truthfully; a pawn standing in one is simply not worth slowing
        /// the whole map down to rescue.
        /// </summary>
        private const int MinIslandCells = 12;

        /// <summary>Component ids are `band * BandStride + localId` so ids from independently
        /// rebuilt bands can never collide.</summary>
        private const int BandStride = 1000000;

        private static int version;

        public static int rebuilds;

        private sealed class BandData
        {
            public int builtVersion = -1;
            public CellRect rect;
            public int width;
            public int[] comp;       // local cell index -> local component id, -1 unwalkable
            public List<int> sizes = new List<int>();
            public bool fragmented;
        }

        private sealed class MapData
        {
            public BandData[] bands;
        }

        private static readonly ConditionalWeakTable<Map, MapData> byMap =
            new ConditionalWeakTable<Map, MapData>();

        private static readonly ConditionalWeakTable<Map, object> hooked =
            new ConditionalWeakTable<Map, object>();

        private static readonly List<int> stack = new List<int>();

        public static void Invalidate()
        {
            version++;
        }

        /// <summary>Monotonic stamp of the island data. Consumers caching anything DERIVED
        /// from islands (the wormhole chain cache) key on this so region rebuilds invalidate
        /// them for free.</summary>
        public static int Version => version;

        /// <summary>Is the band containing this cell split into 2+ islands worth naming?
        /// The hot-path gate for every island-aware consumer: one cached bool on a settled
        /// band. Triggers the lazy per-band rebuild when stale, which is intended.</summary>
        public static bool FragmentedBandAt(Map map, IntVec3 cell)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return false;
            }
            int band = ABBands.BandOf(map, cell);
            if (band < 0)
            {
                return false;
            }
            BandData bd = BandFor(map, band);
            return bd != null && bd.fragmented;
        }

        /// <summary>
        /// Subscribe invalidation to the ONE vanilla signal meaning "regions actually changed".
        ///
        /// ⚠ NEVER PATCH `RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms` FOR THIS. It is
        /// called ~4,500 times per frame, and a gating prefix on `AnythingToRebuild` recovers
        /// only ~22% because the gate itself is Harmony dispatch on a hot method - measured
        /// and rejected in ABWormhole.cs, then rediscovered here the hard way (`version=759324`
        /// on a quiet map). `MapEvents.RegionsRoomsChanged` is raised on the LAST line of that
        /// method, on the only path that actually rebuilt. After the switch: `version=4`.
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
                return; // benign race
            }
            map.events.RegionsRoomsChanged += delegate { version++; };
        }

        // ---- queries ---------------------------------------------------------

        /// <summary>Island id for a cell, or -1 when the cell is unwalkable or off-band.</summary>
        public static int ComponentOf(Map map, IntVec3 cell)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map))
            {
                return -1;
            }
            int band = ABBands.BandOf(map, cell);
            if (band < 0)
            {
                return -1;
            }
            BandData bd = BandFor(map, band);
            if (bd == null || !bd.rect.Contains(cell))
            {
                return -1;
            }
            int local = bd.comp[LocalIndex(bd, cell)];
            return local < 0 ? -1 : band * BandStride + local;
        }

        /// <summary>
        /// True ONLY when both cells resolve to an island AND those islands differ.
        ///
        /// ⚠ THIS IS NOT `!SameComponent`, AND THE DIFFERENCE IS THE WHOLE SAFETY MARGIN. An
        /// unresolved cell (-1) is a wall, the gutter, or unfogged rock. Reading unknown as
        /// "different island" would route every ordinary intra-band order through a staircase.
        /// Unknown must mean LEAVE IT ALONE.
        ///
        /// ⚠ AND THE FRAGMENTED FLAG IS CHECKED FIRST, PER BAND. This runs on
        /// `Pawn_PathFollower.StartPath`. On a band with no island worth naming the answer is
        /// always false and costs one bool - and only THAT band is ever rebuilt, never the
        /// whole stack.
        /// </summary>
        public static bool KnownDifferentComponents(Map map, IntVec3 a, IntVec3 b)
        {
            if (map == null)
            {
                return false;
            }
            int ba = ABBands.BandOf(map, a);
            int bb = ABBands.BandOf(map, b);
            if (ba < 0 || bb < 0)
            {
                return false;
            }
            if (ba == bb)
            {
                BandData bd = BandFor(map, ba);
                if (bd == null || !bd.fragmented)
                {
                    return false; // the fast path, and the common one
                }
            }
            int ca = ComponentOf(map, a);
            if (ca < 0)
            {
                return false;
            }
            int cb = ComponentOf(map, b);
            return cb >= 0 && ca != cb;
        }

        public static bool SameComponent(Map map, IntVec3 a, IntVec3 b)
        {
            int ca = ComponentOf(map, a);
            return ca >= 0 && ca == ComponentOf(map, b);
        }

        // ---- build -----------------------------------------------------------

        private static int LocalIndex(BandData bd, IntVec3 c)
        {
            return (c.z - bd.rect.minZ) * bd.width + (c.x - bd.rect.minX);
        }

        /// <summary>
        /// Band data, rebuilt on demand.
        ///
        /// ⚠ PER BAND, NOT PER MAP - THAT IS ONE OF THE TWO SOFTENINGS. A cell flood costs
        /// ~16k cells for one 126 band against ~80k for a 5-band stack, and a query only ever
        /// needs the band it asked about. `RegionsRoomsChanged` does not say WHICH band
        /// changed, so all bands are marked stale and each re-floods lazily the first time
        /// something asks. The bound is one flood per band per region rebuild, not per query.
        /// </summary>
        private static BandData BandFor(Map map, int band)
        {
            if (!ABBands.Banded(map) || band < 0)
            {
                return null;
            }
            MapData md = byMap.GetValue(map, _ => new MapData());
            int bandCount = ABBands.BandCount(map);
            if (md.bands == null || md.bands.Length != bandCount)
            {
                md.bands = new BandData[bandCount];
            }
            if (band >= md.bands.Length)
            {
                return null;
            }
            BandData bd = md.bands[band] ?? (md.bands[band] = new BandData());
            if (bd.builtVersion != version)
            {
                // §34f: the open measurement. NoteFlood is two adds; the flood itself is
                // what we are finally putting a number on.
                long perfT0 = ABPerfStats.Now();
                Build(map, band, bd);
                ABPerfStats.NoteFlood(ABPerfStats.Now() - perfT0);
                bd.builtVersion = version;
                rebuilds++;
            }
            return bd;
        }

        private static void Build(Map map, int band, BandData bd)
        {
            bd.rect = ABBands.RectOfBand(map, band);
            bd.width = bd.rect.Width;
            int cells = bd.rect.Width * bd.rect.Height;
            if (bd.comp == null || bd.comp.Length != cells)
            {
                bd.comp = new int[cells];
            }
            for (int i = 0; i < cells; i++)
            {
                bd.comp[i] = -1;
            }
            bd.sizes.Clear();
            bd.fragmented = false;

            PathingContext pc = map.pathing?.Normal;
            if (pc?.pathGrid == null)
            {
                return;
            }
            PathGrid grid = pc.pathGrid;

            int minX = bd.rect.minX;
            int minZ = bd.rect.minZ;
            int maxX = bd.rect.maxX;
            int maxZ = bd.rect.maxZ;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var seed = new IntVec3(x, 0, z);
                    int seedLocal = (z - minZ) * bd.width + (x - minX);
                    if (bd.comp[seedLocal] >= 0 || !grid.WalkableFast(seed))
                    {
                        continue;
                    }
                    int id = bd.sizes.Count;
                    int count = 0;
                    stack.Clear();
                    stack.Add(seedLocal);
                    bd.comp[seedLocal] = id;
                    while (stack.Count > 0)
                    {
                        int curLocal = stack[stack.Count - 1];
                        stack.RemoveAt(stack.Count - 1);
                        count++;
                        int cx = minX + (curLocal % bd.width);
                        int cz = minZ + (curLocal / bd.width);
                        for (int d = 0; d < 8; d++)
                        {
                            int nx = cx + DX[d];
                            int nz = cz + DZ[d];
                            if (nx < minX || nx > maxX || nz < minZ || nz > maxZ)
                            {
                                continue;
                            }
                            int nLocal = (nz - minZ) * bd.width + (nx - minX);
                            if (bd.comp[nLocal] >= 0)
                            {
                                continue;
                            }
                            var n = new IntVec3(nx, 0, nz);
                            if (!grid.WalkableFast(n))
                            {
                                continue;
                            }
                            // ⚠ THE LINE THAT MAKES THIS DIFFERENT FROM THE REGION GRAPH.
                            // A diagonal step is refused when either flanking cell blocks it -
                            // exactly what Pawn_PathFollower does. Without this, two areas
                            // touching only at a corner past an open-air hole flood as one
                            // island, the pathfinder disagrees, and the pawn stalls.
                            if (d >= 4
                                && (PathUtility.BlocksDiagonalMovement(cx, nz, pc, false)
                                    || PathUtility.BlocksDiagonalMovement(nx, cz, pc, false)))
                            {
                                continue;
                            }
                            bd.comp[nLocal] = id;
                            stack.Add(nLocal);
                        }
                    }
                    stack.Clear();
                    bd.sizes.Add(count);
                }
            }

            int worthNaming = 0;
            for (int i = 0; i < bd.sizes.Count; i++)
            {
                if (bd.sizes[i] >= MinIslandCells)
                {
                    worthNaming++;
                }
            }
            bd.fragmented = worthNaming >= 2;
        }

        private static readonly int[] DX = { 0, 1, 0, -1, 1, 1, -1, -1 };

        private static readonly int[] DZ = { 1, 0, -1, 0, 1, -1, 1, -1 };

        // ---- diagnostic ------------------------------------------------------

        public static string Report(Map map, Pawn selected)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB2 BAND COMPONENT REPORT (cell flood, pathfinder rules)");
            if (map == null || !ABBands.Banded(map))
            {
                sb.AppendLine("  map is not banded");
                return sb.ToString();
            }
            sb.AppendLine("  version=" + version + " rebuilds=" + rebuilds
                + " minIsland=" + MinIslandCells);
            int bandCount = ABBands.BandCount(map);
            for (int b = 0; b < bandCount; b++)
            {
                BandData bd = BandFor(map, b);
                if (bd == null)
                {
                    continue;
                }
                int big = 0;
                int total = 0;
                int largest = 0;
                for (int i = 0; i < bd.sizes.Count; i++)
                {
                    total += bd.sizes[i];
                    if (bd.sizes[i] >= MinIslandCells)
                    {
                        big++;
                    }
                    if (bd.sizes[i] > largest)
                    {
                        largest = bd.sizes[i];
                    }
                }
                sb.AppendLine("  band " + b + ": islands=" + bd.sizes.Count
                    + " (>=" + MinIslandCells + " cells: " + big + ")"
                    + " walkable=" + total + " largest=" + largest
                    + (bd.fragmented ? "  <-- FRAGMENTED (phase 3 active here)" : ""));
            }

            if (selected != null && selected.Spawned && selected.Map == map)
            {
                int pc2 = ComponentOf(map, selected.Position);
                sb.AppendLine("  " + selected.LabelShortCap + " at " + selected.Position
                    + " band " + ABBands.BandOf(map, selected.Position) + " island " + pc2);
                IntVec3 dest = selected.pather != null && selected.pather.Destination.IsValid
                    ? selected.pather.Destination.Cell
                    : IntVec3.Invalid;
                if (dest.IsValid)
                {
                    sb.AppendLine("    destination " + dest
                        + " band " + ABBands.BandOf(map, dest)
                        + " island " + ComponentOf(map, dest)
                        + "  sameBand=" + ABBands.SameBand(map, selected.Position, dest)
                        + "  knownDifferentIslands="
                        + KnownDifferentComponents(map, selected.Position, dest));
                }
            }
            return sb.ToString();
        }
    }
}
