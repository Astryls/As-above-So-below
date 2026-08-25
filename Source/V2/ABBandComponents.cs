using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
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
    ///
    /// ⚠⚠ §59 THE FLOOD IS PATHFINDER-ACCURATE BUT WAS PAWN-BLIND, AND THAT IS A SECOND,
    /// DIFFERENT WAY TO BE MORE OPTIMISTIC THAN THE PATHFINDER. `WalkableFast` is a property
    /// of the CELL; a forbidden door is perfectly walkable in the path grid and is refused
    /// much later and PER PAWN, in `PathUtility.GetDoorCost` (`TraverseMode.ByPawn` ->
    /// `IsForbiddenToPass` -> ushort.MaxValue). So two rooms joined only by a forbidden door
    /// flooded as ONE island, `TrySegment` early-outed on "same band, same island", and the
    /// pawn was handed to a band-scoped pathfinder that correctly refused the only door on
    /// the route. Reported as "a drafted colonist will not route via the basement when the
    /// ground-floor doors are forbidden".
    ///
    /// ⚠ THE PREDICATE COLLAPSES TO ONE BIT, WHICH IS WHY THIS IS A SECOND PARTITION AND NOT
    /// A PER-PAWN GRAPH. `ForbidUtility.IsForbiddenToPass` is
    /// `CaresAboutForbidden(pawn, cellTarget:false, bypassDraftedCheck:true)` AND
    /// `door.IsForbidden(pawn.Faction)`, and the latter returns false for every faction that
    /// is not the player's. So the whole map has exactly TWO answers - the optimistic flood
    /// (raiders, wild animals, anyone in a mental state) and one forbid-aware flood - and a
    /// band that holds no forbidden door does not build the second one at all.
    ///
    /// ⚠ `bypassDraftedCheck: true` IS NOT A DETAIL: drafting does NOT excuse a pawn from a
    /// forbidden door, which is why the bug was reported with drafted colonists.
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

        /// <summary>
        /// Forbid-aware ids live in the upper half of a band's id space.
        ///
        /// ⚠ THE OFFSET IS THE SAFETY RAIL, NOT A LAYOUT CHOICE. The two partitions answer
        /// DIFFERENT questions about the same cell, and an id from one is meaningless against
        /// an id from the other. A routing call that leaked its `forbidAware` flag on one
        /// lookup and not the next would, without this, silently compare two unrelated ints
        /// and could read EQUAL - i.e. "same island, nothing to segment", the exact wrong
        /// answer. Offsetting makes any such mix-up read as "different island", which is the
        /// safe direction: it costs a wasted route query, never a stalled pawn.
        ///
        /// A band's local ids are bounded by its cell count, so this is only sound while a
        /// band is smaller than the offset. Asserted unconditionally in Build.
        /// </summary>
        private const int ForbidIdOffset = 500000;

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

            /// <summary>The forbid-aware partition: the same flood with every currently
            /// forbidden door treated as a wall. NULL-EQUIVALENT unless `hasForbid`.</summary>
            public int[] compForbid;

            public List<int> forbidSizes = new List<int>();

            public bool forbidFragmented;

            /// <summary>False when this band holds no forbidden door, in which case the two
            /// partitions would be identical and the second flood is skipped entirely. This
            /// is what makes the whole feature cost nothing on a map where the player has
            /// forbidden nothing - which is most maps, most of the time.</summary>
            public bool hasForbid;

            /// <summary>local cell index -> "a forbidden door stands here". Reused across
            /// rebuilds; only meaningful for the build that filled it.</summary>
            public bool[] forbidBlocked;
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
            return FragmentedBandAt(map, cell, false);
        }

        /// <summary>As above, for a pawn that may or may not obey forbidden doors. A band can
        /// be one island for a raider and three for a colonist.</summary>
        public static bool FragmentedBandAt(Map map, IntVec3 cell, Pawn pawn)
        {
            return FragmentedBandAt(map, cell, RespectsForbiddenDoors(pawn));
        }

        public static bool FragmentedBandAt(Map map, IntVec3 cell, bool forbidAware)
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
            return bd != null && (bd.fragmented || (forbidAware && bd.forbidFragmented));
        }

        /// <summary>
        /// Does this pawn obey forbidden doors - the ONE bit that selects a partition.
        ///
        /// A verbatim restatement of `ForbidUtility.IsForbiddenToPass` minus the door: the
        /// faction test (`Thing.IsForbidden(Faction)` is false for anyone but the player) and
        /// `CaresAboutForbidden` with vanilla's own arguments. If this ever drifts from
        /// vanilla, pawns route through doors they will then refuse to open - so it is
        /// written as a quotation, not as a paraphrase.
        /// </summary>
        public static bool RespectsForbiddenDoors(Pawn pawn)
        {
            if (pawn == null || pawn.Faction == null || pawn.Faction != Faction.OfPlayerSilentFail)
            {
                return false;
            }
            try
            {
                return ForbidUtility.CaresAboutForbidden(pawn, cellTarget: false,
                    bypassDraftedCheck: true);
            }
            catch
            {
                // Routing must never be broken by a predicate. Optimistic is the vanilla-
                // equivalent fallback: it is what every pawn got before §59.
                return false;
            }
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
            return ComponentOf(map, cell, false);
        }

        /// <summary>Island id as THIS PAWN would experience it.</summary>
        public static int ComponentOf(Map map, IntVec3 cell, Pawn pawn)
        {
            return ComponentOf(map, cell, RespectsForbiddenDoors(pawn));
        }

        public static int ComponentOf(Map map, IntVec3 cell, bool forbidAware)
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
            int idx = LocalIndex(bd, cell);
            if (forbidAware && bd.hasForbid)
            {
                int restricted = bd.compForbid[idx];
                // A cell holding a forbidden door resolves to -1 here, exactly as a wall
                // does. That is the honest answer for a pawn that will not open it, and
                // "unknown means leave it alone" downstream keeps it harmless.
                return restricted < 0 ? -1 : band * BandStride + ForbidIdOffset + restricted;
            }
            int local = bd.comp[idx];
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
            return KnownDifferentComponents(map, a, b, false);
        }

        /// <summary>As above, asked on behalf of a pawn. THIS is the overload every movement
        /// decision must use: the optimistic one answers for a pawn that ignores forbidden
        /// doors, and answering that question for a colonist is §59's bug.</summary>
        public static bool KnownDifferentComponents(Map map, IntVec3 a, IntVec3 b, Pawn pawn)
        {
            return KnownDifferentComponents(map, a, b, RespectsForbiddenDoors(pawn));
        }

        public static bool KnownDifferentComponents(Map map, IntVec3 a, IntVec3 b,
            bool forbidAware)
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
                // ⚠ BOTH FLAGS, OR THE FAST PATH SWALLOWS THE FIX. A band with one forbidden
                // door across a corridor is NOT fragmented optimistically and IS fragmented
                // for a colonist; checking only `fragmented` would return false here and the
                // same-band half of §59 would never reach the slow branch at all.
                if (bd == null || !(bd.fragmented || (forbidAware && bd.forbidFragmented)))
                {
                    return false; // the fast path, and the common one
                }
            }
            int ca = ComponentOf(map, a, forbidAware);
            if (ca < 0)
            {
                return false;
            }
            int cb = ComponentOf(map, b, forbidAware);
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
            bd.hasForbid = false;
            bd.forbidFragmented = false;

            PathingContext pc = map.pathing?.Normal;
            if (pc?.pathGrid == null)
            {
                for (int i = 0; i < cells; i++)
                {
                    bd.comp[i] = -1;
                }
                bd.sizes.Clear();
                bd.fragmented = false;
                return;
            }

            // ⚠ UNCONDITIONAL ASSERTION (§57d). The two partitions share one id space, split
            // at ForbidIdOffset; a band bigger than the offset would let a forbid-aware id
            // land on top of an optimistic one from the SAME band and read as "same island".
            // Cannot fire at any legal map size (ABMapSizeLimit caps the footprint long
            // before 500k cells per band) - it is the precondition the split rests on.
            if (cells >= ForbidIdOffset)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: band " + band + " has " + cells
                    + " cells, at or past the forbid-aware id offset (" + ForbidIdOffset
                    + "). Island ids from the two partitions can collide.", 762195936);
            }

            Flood(pc, bd, bd.comp, null, bd.sizes, out bd.fragmented);

            // --- the forbid-aware partition ---------------------------------------
            // Skipped outright when the band holds no forbidden door, which is the common
            // case: the two floods would be identical and every consumer falls back to the
            // optimistic one. The scan below is O(band rect) on top of a flood that is
            // already O(band rect), so it is a constant factor on a rebuild, not a new cost
            // class - and rebuilds are driven by RegionsRoomsChanged, not by queries.
            if (CollectForbiddenDoors(map, bd) > 0)
            {
                if (bd.compForbid == null || bd.compForbid.Length != cells)
                {
                    bd.compForbid = new int[cells];
                }
                Flood(pc, bd, bd.compForbid, bd.forbidBlocked, bd.forbidSizes,
                    out bd.forbidFragmented);
                bd.hasForbid = true;
            }
        }

        /// <summary>
        /// Mark every cell of every currently forbidden door in this band. Returns how many.
        ///
        /// ⚠ EVERY CELL, NOT THE POSITION. `edificeGrid` holds the building at each occupied
        /// cell, so a 2x1 stairwell blocks both of its cells - which matters, because our own
        /// anchors are `Building_Door` subclasses and a player CAN forbid a staircase. That
        /// they then stop conducting traffic is the correct reading of the order, and it is
        /// the one thing the old optimistic-only graph could not express.
        /// </summary>
        private static int CollectForbiddenDoors(Map map, BandData bd)
        {
            // `Thing.IsForbidden(Faction)` is false for every faction but the player's, so
            // with no player faction there is nothing to be forbidden to.
            Faction player = Faction.OfPlayerSilentFail;
            if (player == null)
            {
                return 0;
            }
            EdificeGrid edifices = map.edificeGrid;
            if (edifices == null)
            {
                return 0;
            }
            int found = 0;
            int minX = bd.rect.minX;
            int minZ = bd.rect.minZ;
            int maxX = bd.rect.maxX;
            int maxZ = bd.rect.maxZ;
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!(edifices[new IntVec3(x, 0, z)] is Building_Door door)
                        || !door.IsForbidden(player))
                    {
                        continue;
                    }
                    if (found == 0)
                    {
                        // Allocated and cleared only once a band actually has one. The array
                        // survives rebuilds, so it MUST be cleared here - a stale bit from a
                        // previous build would wall off a door the player has since released.
                        int cells = bd.rect.Width * bd.rect.Height;
                        if (bd.forbidBlocked == null || bd.forbidBlocked.Length != cells)
                        {
                            bd.forbidBlocked = new bool[cells];
                        }
                        else
                        {
                            Array.Clear(bd.forbidBlocked, 0, cells);
                        }
                    }
                    bd.forbidBlocked[(z - minZ) * bd.width + (x - minX)] = true;
                    found++;
                }
            }
            return found;
        }

        /// <summary>
        /// One flood. `blocked` is an optional extra impassability mask in LOCAL indices, on
        /// top of the pathfinder's own two rules - null for the optimistic partition.
        /// </summary>
        private static void Flood(PathingContext pc, BandData bd, int[] comp, bool[] blocked,
            List<int> sizes, out bool fragmented)
        {
            PathGrid grid = pc.pathGrid;
            for (int i = 0; i < comp.Length; i++)
            {
                comp[i] = -1;
            }
            sizes.Clear();
            fragmented = false;

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
                    if (comp[seedLocal] >= 0 || !grid.WalkableFast(seed)
                        || (blocked != null && blocked[seedLocal]))
                    {
                        continue;
                    }
                    int id = sizes.Count;
                    int count = 0;
                    stack.Clear();
                    stack.Add(seedLocal);
                    comp[seedLocal] = id;
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
                            if (comp[nLocal] >= 0)
                            {
                                continue;
                            }
                            var n = new IntVec3(nx, 0, nz);
                            if (!grid.WalkableFast(n) || (blocked != null && blocked[nLocal]))
                            {
                                continue;
                            }
                            // ⚠ THE LINE THAT MAKES THIS DIFFERENT FROM THE REGION GRAPH.
                            // A diagonal step is refused when either flanking cell blocks it -
                            // exactly what Pawn_PathFollower does. Without this, two areas
                            // touching only at a corner past an open-air hole flood as one
                            // island, the pathfinder disagrees, and the pawn stalls.
                            // ⚠ THE BLOCKED MASK IS APPLIED TO THE FLANKS TOO. Vanilla's
                            // BlocksDiagonalMovement reads the REAL path grid, where a
                            // forbidden door is still walkable - so on the forbid-aware pass
                            // it would permit a diagonal slip past a cell this partition
                            // treats as a wall. A forbidden door standing free of a wall is
                            // the case that finds it. Same rule as the cell test: the mask is
                            // an extension of impassability, so it must reach everywhere
                            // impassability does.
                            if (d >= 4
                                && (DiagBlocked(pc, bd, blocked, cx, nz)
                                    || DiagBlocked(pc, bd, blocked, nx, cz)))
                            {
                                continue;
                            }
                            comp[nLocal] = id;
                            stack.Add(nLocal);
                        }
                    }
                    stack.Clear();
                    sizes.Add(count);
                }
            }

            int worthNaming = 0;
            for (int i = 0; i < sizes.Count; i++)
            {
                if (sizes[i] >= MinIslandCells)
                {
                    worthNaming++;
                }
            }
            fragmented = worthNaming >= 2;
        }

        /// <summary>Flanking-cell test for a diagonal step, mask-aware. The caller guarantees
        /// (x, z) is inside the band rect - both flanks of a legal diagonal always are.</summary>
        private static bool DiagBlocked(PathingContext pc, BandData bd, bool[] blocked,
            int x, int z)
        {
            if (blocked != null
                && blocked[(z - bd.rect.minZ) * bd.width + (x - bd.rect.minX)])
            {
                return true;
            }
            return PathUtility.BlocksDiagonalMovement(x, z, pc, false);
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
                // §59. Printed only where it exists, so a map with nothing forbidden reads
                // exactly as it did before and the extra line always means something.
                if (bd.hasForbid)
                {
                    int bigF = 0;
                    for (int i = 0; i < bd.forbidSizes.Count; i++)
                    {
                        if (bd.forbidSizes[i] >= MinIslandCells)
                        {
                            bigF++;
                        }
                    }
                    sb.AppendLine("      forbid-aware: islands=" + bd.forbidSizes.Count
                        + " (>=" + MinIslandCells + " cells: " + bigF + ")"
                        + (bd.forbidFragmented
                            ? "  <-- FRAGMENTED FOR COLONISTS (forbidden doors split it)"
                            : ""));
                }
            }

            if (selected != null && selected.Spawned && selected.Map == map)
            {
                // ⚠ REPORT THE PARTITION THE PAWN ACTUALLY ROUTES ON, or this diagnostic
                // lies about exactly the case §59 exists for. The optimistic ids are printed
                // alongside so the two can be compared at a glance - when they disagree, a
                // forbidden door is on the route.
                bool aware = RespectsForbiddenDoors(selected);
                sb.AppendLine("  " + selected.LabelShortCap + " at " + selected.Position
                    + " band " + ABBands.BandOf(map, selected.Position)
                    + " island " + ComponentOf(map, selected.Position, aware)
                    + "  (respectsForbiddenDoors=" + aware
                    + ", optimistic island " + ComponentOf(map, selected.Position) + ")");
                IntVec3 dest = selected.pather != null && selected.pather.Destination.IsValid
                    ? selected.pather.Destination.Cell
                    : IntVec3.Invalid;
                if (dest.IsValid)
                {
                    sb.AppendLine("    destination " + dest
                        + " band " + ABBands.BandOf(map, dest)
                        + " island " + ComponentOf(map, dest, aware)
                        + "  sameBand=" + ABBands.SameBand(map, selected.Position, dest)
                        + "  knownDifferentIslands="
                        + KnownDifferentComponents(map, selected.Position, dest, aware));
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// ⚠⚠ §59 FORBIDDING A DOOR RAISES NO REGION EVENT, SO NOTHING WOULD EVER REBUILD THE
    /// FORBID-AWARE PARTITION.
    ///
    /// `ABBandComponents.version` is driven by `MapEvents.RegionsRoomsChanged`, which fires
    /// when regions are rebuilt - and toggling `CompForbiddable.Forbidden` does NOT rebuild
    /// regions. It calls `map.reachability.ClearCache()` and nothing else, because vanilla
    /// applies forbidden-ness per query (`Region.Allows`, `PathUtility.GetDoorCost`) and has
    /// no derived grid to invalidate. We DO have one, so we need this hook: without it the
    /// player forbids a door, the island data stays as it was, and the fix appears to work
    /// only after the next unrelated wall goes up.
    ///
    /// ⚠ THE PREFIX EXISTS TO AVOID AN INVALIDATION STORM. The setter early-outs when the
    /// value is unchanged, and bulk paths ("forbid all", zone rewrites, load) hit it many
    /// times with the value it already holds. Comparing before/after means a no-op set stays
    /// a no-op rather than costing every band on the map a re-flood.
    ///
    /// Doors that appear or vanish are already covered - spawning or despawning a building
    /// dirties regions, which is the event `version` listens to.
    /// </summary>
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden),
        MethodType.Setter)]
    public static class Patch_CompForbiddable_ABDoorForbidChanged
    {
        private static void Prefix(CompForbiddable __instance, out bool __state)
        {
            __state = __instance != null && __instance.Forbidden;
        }

        private static void Postfix(CompForbiddable __instance, bool __state)
        {
            try
            {
                if (__instance == null || __state == __instance.Forbidden)
                {
                    return;
                }
                if (!(__instance.parent is Building_Door door) || !door.Spawned
                    || !ABBands.Banded(door.Map))
                {
                    return;
                }
                ABBandComponents.Invalidate();
            }
            catch (Exception e)
            {
                // Keyed, because this sits on a setter the player can spam.
                Log.WarningOnce(ABLog.Tag + " V2: door forbid invalidation threw: "
                    + e.Message, 762195937);
            }
        }
    }
}
