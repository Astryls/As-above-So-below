using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE - the load-bearing primitive.
    ///
    /// Joins two spatially distant areas of ONE map into a single connectivity
    /// graph by handing both ends' regions a SHARED synthetic RegionLink.
    ///
    /// Why this works (verified against 1.6 source):
    ///  - Region.Neighbors / NeighborsOfSameType iterate region.links and return
    ///    link.regions[i] with ZERO spatial validation. RegionLink.span is only used
    ///    for hash-dedup in RegionLinkDatabase and for RegionCostCalculator distance
    ///    estimates. Two regions sharing a link ARE adjacent as far as RimWorld cares.
    ///  - Reachability.CanReach is managed region-BFS, so it starts returning true
    ///    across the wormhole with no further patching - and that transitively fixes
    ///    ClosestThingReachable, RegionTraverser, storage search and every
    ///    WorkGiver_Scanner. That transitive win is the whole thesis of V2.
    ///  - Both ends are RegionType.Portal (the anchor is a Building_Door subclass, and
    ///    GetExpectedRegionType returns Portal for any door cell). Portal fails
    ///    RegionAndRoomUpdater.ShouldBeInTheSameRoom and AllowsMultipleRegionsPerDistrict,
    ///    so the link conducts connectivity WITHOUT merging rooms, temperature or vacuum.
    ///
    /// Three traps this class exists to handle:
    ///  1. RegionDirtyer sets reg.valid=false, deregisters every link and CLEARS
    ///     reg.links on any region rebuild - synthetic links are silently wiped, so
    ///     they must be re-armed after every rebuild (see the postfix below).
    ///  2. Do NOT mint the link via map.regionLinkDatabase.LinkFrom(span): that dedups
    ///     by span.UniqueHashCode() and would collide with a genuine edge link. We
    ///     construct RegionLink directly and never register it in the database.
    ///     (RegionLink.Deregister -> Notify_LinkHasNoRegions -> links.Remove(hash) is a
    ///     safe no-op for a key that was never added.)
    ///  3. Reachability saying "yes" does NOT produce a path - 1.6's pathfinder is
    ///     jobified and cannot traverse the link. ABWormholePather handles that.
    /// </summary>
    public static class ABWormhole
    {
        /// <summary>Ends are typed as Building_Door because that is what makes the cell a
        /// RegionType.Portal region - the property the whole mechanism depends on. Both
        /// the spike anchor and the V2 stairwell derive from it.</summary>
        private sealed class Pair
        {
            public Building_Door a;
            public Building_Door b;

            /// <summary>ONE LINK PER CELL PAIR, not one per building.
            ///
            /// RegionTypeUtility.IsOneCellRegion(Portal) is true: every door cell becomes its
            /// OWN one-cell Portal region. A 2x1 stairwell is therefore two Portal regions at
            /// each end, and linking only the building's Position cell leaves the other cell
            /// conducting nothing - a pawn that happens to walk into that half finds no
            /// connection, while the identical-looking cell beside it works. That is exactly
            /// the "sometimes the stairs work" symptom, and it gets worse the larger the
            /// footprint, so it had to be fixed before any multi-cell variant could exist.</summary>
            public readonly List<RegionLink> links = new List<RegionLink>();
        }

        /// <summary>Pairs per map, keyed by the Map OBJECT, not map.uniqueID.
        ///
        /// This was a Dictionary&lt;int, List&lt;Pair&gt;&gt; keyed by uniqueID, and that broke
        /// every reload: loading a save neither clears statics nor fires MapRemoved for the
        /// torn-down session, and the loaded map carries the SAME uniqueID as its dead
        /// predecessor - so ListFor handed the fresh map a list still full of stale Pairs
        /// whose buildings belong to unspawned objects on a dead Map. uniqueIDs also restart
        /// at 0 for every NEW game, so a different colony could inherit them too. Identical
        /// to the viewBand lesson: per-map state must be keyed by the Map object. A CWT also
        /// lets dead maps' lists be collected instead of pinned forever.</summary>
        private static readonly ConditionalWeakTable<Map, List<Pair>> byMap =
            new ConditionalWeakTable<Map, List<Pair>>();

        /// <summary>Re-entrancy latch: rearming must never trigger a region rebuild
        /// (we only use NoRebuild accessors, but the latch makes that guarantee
        /// explicit and cheap to audit).</summary>
        private static bool rearming;

        public static int PairCount(Map map)
        {
            return map != null && byMap.TryGetValue(map, out List<Pair> l) ? l.Count : 0;
        }

        /// <summary>True when the cell is within <paramref name="radius"/> of either end of any
        /// wormhole on this map. Used to scope the stuck watchdog to stairwell traffic instead
        /// of every pawn on the map.</summary>
        public static bool NearAnyAnchor(Map map, IntVec3 cell, int radius)
        {
            if (map == null || !byMap.TryGetValue(map, out List<Pair> list))
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                if (p.a != null && p.a.Spawned && cell.InHorDistOf(p.a.Position, radius))
                {
                    return true;
                }
                if (p.b != null && p.b.Spawned && cell.InHorDistOf(p.b.Position, radius))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Full state of every wormhole on this map: whether each end resolved to a
        /// Portal region, whether the synthetic link is actually present in BOTH regions'
        /// link lists, and whether vanilla reachability agrees the ends connect.
        ///
        /// The last line is the one that matters: if CanReach is false the link is not
        /// armed, and every cross-band order will be rejected before it starts - a pawn told
        /// to use the stairs simply stands there.</summary>
        public static string DebugDump(Map map)
        {
            if (map == null)
            {
                return "no map";
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (!byMap.TryGetValue(map, out List<Pair> list) || list.Count == 0)
            {
                sb.AppendLine("NO WORMHOLE PAIRS REGISTERED on this map.");
                return sb.ToString();
            }
            sb.AppendLine("wormhole pairs: " + list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                sb.AppendLine("  [" + i + "] a=" + (p.a != null ? p.a.Position.ToString() : "null")
                    + " spawned=" + (p.a != null && p.a.Spawned)
                    + "  b=" + (p.b != null ? p.b.Position.ToString() : "null")
                    + " spawned=" + (p.b != null && p.b.Spawned));
                if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                {
                    continue;
                }
                Region ra = map.regionGrid.GetValidRegionAt_NoRebuild(p.a.Position);
                Region rb = map.regionGrid.GetValidRegionAt_NoRebuild(p.b.Position);
                sb.AppendLine("      regionA=" + (ra != null ? ra.type.ToString() : "NULL")
                    + "  regionB=" + (rb != null ? rb.type.ToString() : "NULL")
                    + "   (both MUST be Portal)");
                // Reports links armed vs cells occupied. A multi-cell stairwell must have
                // ONE link per cell pair; "3/4" means a quarter of the footprint conducts
                // nothing and pawns entering that cell will find no connection.
                int cells = p.a.OccupiedRect().Area;
                int live = 0;
                for (int li = 0; li < p.links.Count; li++)
                {
                    RegionLink l = p.links[li];
                    if (l != null && l.RegionA != null && l.RegionB != null
                        && l.RegionA.links.Contains(l) && l.RegionB.links.Contains(l))
                    {
                        live++;
                    }
                }
                sb.AppendLine("      links armed = " + live + "/" + cells + " cells"
                    + (live == cells ? "" : "   <-- PARTIAL, some cells conduct nothing"));
                bool reach = map.reachability.CanReach(p.a.Position, p.b.Position,
                    PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly));
                sb.AppendLine("      CanReach across = " + reach + "   (MUST be true)");
                sb.AppendLine("      isDoorA=" + (p.a.Position.GetDoor(map) != null)
                    + " isDoorB=" + (p.b.Position.GetDoor(map) != null));
            }
            return sb.ToString();
        }

        public static void Link(Building_Door a, Building_Door b)
        {
            if (a == null || b == null || a.Map == null || a.Map != b.Map)
            {
                Log.Warning(ABLog.Tag + " V2: refusing to link wormhole ends on different maps.");
                return;
            }
            List<Pair> list = ListFor(a.Map);
            for (int i = 0; i < list.Count; i++)
            {
                // Dedupe the PAIR AS A SET - not "either member appears anywhere".
                //
                // The original check returned when either building was in ANY existing
                // pair, which encoded the single-counterpart assumption: one building, one
                // pair. The elevator broke it silently and precisely: it links bottom-up,
                // so surface<->basement was added first, and then Link(surface, sky) found
                // the surface car in that pair and bailed - as did the full-mesh
                // Link(basement, sky). Diagnosed from `AB2: band info` reading
                // "wormhole pairs: 1" on a three-car shaft whose bands were all open:
                // every car established, one edge in the graph. CanReach to the sky was
                // false with nothing visibly wrong - "the elevator only works going down".
                if ((list[i].a == a && list[i].b == b) || (list[i].a == b && list[i].b == a))
                {
                    return; // this exact pair already exists
                }
            }
            list.Add(new Pair { a = a, b = b });
            RearmAll(a.Map);
        }

        public static void Unlink(Building_Door anchor, Map map)
        {
            if (anchor == null || map == null || !byMap.TryGetValue(map, out List<Pair> list))
            {
                return;
            }
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Pair p = list[i];
                if (p.a != anchor && p.b != anchor)
                {
                    continue;
                }
                TearDown(p);
                list.RemoveAt(i);
            }
            map.reachability.ClearCache();
        }

        private static List<Pair> ListFor(Map map)
        {
            if (!byMap.TryGetValue(map, out List<Pair> list))
            {
                list = new List<Pair>();
                byMap.Add(map, list);
            }
            return list;
        }

        private static void TearDown(Pair p)
        {
            for (int i = 0; i < p.links.Count; i++)
            {
                RegionLink l = p.links[i];
                if (l == null)
                {
                    continue;
                }
                l.RegionA?.links.Remove(l);
                l.RegionB?.links.Remove(l);
            }
            p.links.Clear();
        }

        /// <summary>Re-create every synthetic link on this map whose regions have been
        /// rebuilt out from under it. Cheap and idempotent: a still-armed pair costs
        /// two grid lookups and two list scans.</summary>
        public static void RearmAll(Map map)
        {
            if (map == null || rearming || !byMap.TryGetValue(map, out List<Pair> list) || list.Count == 0)
            {
                return;
            }
            rearming = true;
            try
            {
                // Prune pairs whose ends are gone. Normal teardown goes through DeSpawn ->
                // Unlink, but map teardown and load-ordering edge cases can strand a pair,
                // and a stranded pair silently fails every rearm from then on.
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Pair p = list[i];
                    if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned
                        || p.a.Map != map || p.b.Map != map)
                    {
                        TearDown(p);
                        list.RemoveAt(i);
                    }
                }
                bool changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (Rearm(map, list[i]))
                    {
                        changed = true;
                    }
                }
                if (changed)
                {
                    // Reachability memoizes region-to-region answers; a new edge in the
                    // graph invalidates them.
                    map.reachability.ClearCache();
                }
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2 spike: wormhole rearm threw: " + e);
            }
            finally
            {
                rearming = false;
            }
        }

        /// <summary>The cell pairs joining two anchors, matched by position WITHIN each
        /// building's own rect so rotation and multi-cell footprints line up. Returns false
        /// when the two ends disagree on shape, which would otherwise silently link the wrong
        /// cells together.</summary>
        private static bool TryCellPairs(Pair p, List<KeyValuePair<IntVec3, IntVec3>> into)
        {
            into.Clear();
            CellRect ra = p.a.OccupiedRect();
            CellRect rb = p.b.OccupiedRect();
            // Compare WIDTH and HEIGHT, not just Area. Index-order pairing below is only a
            // valid positional correspondence when the two rects have the same shape - and a
            // rotated non-square end has identical Area with its dimensions swapped, so an
            // Area-only check would wave it through and silently pair the wrong cells. Every
            // link def is square today; this guard is what stops a future non-square one
            // failing invisibly.
            if (ra.Width != rb.Width || ra.Height != rb.Height)
            {
                Log.WarningOnce(ABLog.Tag + " V2: wormhole ends differ in shape ("
                    + ra.Width + "x" + ra.Height + " vs " + rb.Width + "x" + rb.Height
                    + "); linking the first cell only.",
                    762195901);
                into.Add(new KeyValuePair<IntVec3, IntVec3>(p.a.Position, p.b.Position));
                return true;
            }
            // Both rects walked in the same order, so cell i of one end maps to cell i of the
            // other. Counterparts are spawned translated with matching rotation, so this is a
            // straight positional correspondence.
            List<IntVec3> ca = new List<IntVec3>(ra.Area);
            foreach (IntVec3 c in ra) ca.Add(c);
            int i = 0;
            foreach (IntVec3 c in rb)
            {
                into.Add(new KeyValuePair<IntVec3, IntVec3>(ca[i], c));
                i++;
            }
            return into.Count > 0;
        }

        private static readonly List<KeyValuePair<IntVec3, IntVec3>> tmpPairs =
            new List<KeyValuePair<IntVec3, IntVec3>>();

        private static bool Rearm(Map map, Pair p)
        {
            if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
            {
                return false;
            }
            if (!TryCellPairs(p, tmpPairs))
            {
                return false;
            }

            // Still armed? Every cell pair must still hold a valid link in BOTH regions.
            // Checked wholesale: a partially armed anchor is the failure mode this exists to
            // prevent, so anything less than fully armed is torn down and rebuilt.
            bool allArmed = p.links.Count == tmpPairs.Count;
            if (allArmed)
            {
                for (int i = 0; i < p.links.Count; i++)
                {
                    RegionLink l = p.links[i];
                    if (l == null || l.RegionA == null || !l.RegionA.valid
                        || l.RegionB == null || !l.RegionB.valid
                        || !l.RegionA.links.Contains(l) || !l.RegionB.links.Contains(l))
                    {
                        allArmed = false;
                        break;
                    }
                }
            }
            if (allArmed)
            {
                return false;
            }

            // NoRebuild: this runs INSIDE the region rebuild postfix, so asking for a
            // rebuild here would recurse.
            Region ra = map.regionGrid.GetValidRegionAt_NoRebuild(p.a.Position);
            Region rb = map.regionGrid.GetValidRegionAt_NoRebuild(p.b.Position);
            if (ra == null || rb == null || ra == rb)
            {
                return false;
            }
            // REFUSE to arm unless both ends are Portal regions.
            //
            // This is nearly always a STALE READ rather than a broken def: Link() is
            // called from SpawnSetup, and the region containing the brand-new door has
            // only been marked dirty at that point - GetValidRegionAt_NoRebuild (which
            // must not trigger a rebuild, since we run inside the rebuild postfix) still
            // returns the pre-door Normal region.
            //
            // Arming anyway would be actively harmful: a link between two NORMAL regions
            // merges their districts into one room, so the basement would share a room -
            // and a temperature - with the surface. Deferring costs nothing; the rebuild
            // postfix re-runs this a moment later with the Portal regions in place.
            if (ra.type != RegionType.Portal || rb.type != RegionType.Portal)
            {
                ABLog.Dev("Wormhole ends not Portal yet (" + ra.type + "/" + rb.type
                    + "); deferring to the next region rebuild.");
                return false;
            }

            TearDown(p);

            for (int i = 0; i < tmpPairs.Count; i++)
            {
                IntVec3 ca = tmpPairs[i].Key;
                IntVec3 cb = tmpPairs[i].Value;
                Region rca = map.regionGrid.GetValidRegionAt_NoRebuild(ca);
                Region rcb = map.regionGrid.GetValidRegionAt_NoRebuild(cb);
                if (rca == null || rcb == null || rca == rcb)
                {
                    continue;
                }
                // Each cell must be its own Portal region; anything else would merge rooms.
                if (rca.type != RegionType.Portal || rcb.type != RegionType.Portal)
                {
                    continue;
                }
                RegionLink link = new RegionLink();
                // Synthetic span. Never handed to RegionLinkDatabase, so the hash cannot
                // collide with a real edge link; it exists only so RegionCostCalculator and
                // debug drawing have something non-degenerate to read.
                link.span = new EdgeSpan(ca, SpanDirection.North, 1);
                link.RegionA = rca;
                link.RegionB = rcb;
                rca.links.Add(link);
                rcb.links.Add(link);
                p.links.Add(link);
            }
            return p.links.Count > 0;
        }

        /// <summary>
        /// The FIRST HOP of a route from one band to another, which may take several.
        ///
        /// It used to require a single pair whose two ends were exactly (fromBand, toBand).
        /// That silently capped the mod at journeys of one flight: on a 4-level column a
        /// pawn on the surface asking for level +2 matched nothing, TrySegment logged
        /// "NONE (pawn will try to walk it and fail)", and the only way up was to order each
        /// flight by hand. Reported as "pawns can't path through the first set of stairs to
        /// proceed to the second one".
        ///
        /// Now it BFSes the band graph (bands = nodes, wormhole pairs = edges) and returns
        /// the best pair that makes real progress along a shortest route. Chaining then
        /// comes for free from machinery that already exists: after each transit,
        /// ABWormholePather.Carry re-issues StartPath toward the true destination, which
        /// re-enters TrySegment and asks for the next hop. Nothing has to know the length of
        /// the journey.
        ///
        /// The x/z cost metric stays meaningful across a multi-hop route because bands are
        /// aligned 1:1 in x/z: "how far is this anchor from the destination column" is the
        /// right question on every level.
        /// </summary>
        public static bool TryGetTransit(Map map, IntVec3 from, IntVec3 to,
            out Building_Door near, out Building_Door far)
        {
            near = null;
            far = null;
            if (map == null || !byMap.TryGetValue(map, out List<Pair> list) || list.Count == 0)
            {
                return false;
            }
            // PHASE 2: NODES ARE (BAND, COMPONENT), NOT BANDS.
            //
            // The old version keyed everything on band and early-returned when
            // `bandFrom == bandTo`. That was unsound in two directions at once:
            //  - it REFUSED a legitimate trip between two islands of the SAME band (two
            //    plateaus on a sky level, two buildings' upper floors) - the §34 report case;
            //  - it could ACCEPT a hop whose near anchor sat in an island the pawn cannot
            //    walk to, because "same band" was treated as "reachable".
            // Both vanish once the node is the island rather than the level.
            int compFrom = ABBandComponents.ComponentOf(map, from);
            int compTo = ABBandComponents.ComponentOf(map, to);
            if (compFrom < 0 || compTo < 0 || compFrom == compTo)
            {
                // Same island (or an endpoint with no region at all): an ordinary intra-band
                // path, or genuinely nothing we can help with. Either way, not our business.
                return false;
            }
            Dictionary<int, int> hopsToDest = HopDistances(map, list, compTo);
            if (hopsToDest == null || !hopsToDest.TryGetValue(compFrom, out int hops)
                || hops <= 0)
            {
                return false; // no chain of wormholes joins these islands at all
            }
            float best = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                {
                    continue;
                }
                Consider(map, from, to, compFrom, hops, hopsToDest, p.a, p.b,
                    ref best, ref near, ref far);
                Consider(map, from, to, compFrom, hops, hopsToDest, p.b, p.a,
                    ref best, ref near, ref far);
            }
            return near != null;
        }

        /// <summary>
        /// Hops from every reachable component to <paramref name="target"/> over the wormhole
        /// graph. Absent key means unreachable.
        ///
        /// ⚠ A DICTIONARY, NOT AN ARRAY, AND THAT IS THE ONE REAL COST OF PHASE 2. Bands were
        /// a dense 0..bandCount-1 range so an int[] indexed directly; component ids are dense
        /// too but there are far more of them (12 on a first real map, and player building
        /// only adds islands), and more importantly the set CHANGES as walls go up. Sizing an
        /// array to the live component count every call would allocate just as much. The
        /// graph is still tiny - one entry per island that can reach the target.
        /// </summary>
        private static Dictionary<int, int> HopDistances(Map map, List<Pair> list, int target)
        {
            if (target < 0)
            {
                return null;
            }
            // Adjacency between components, built from the wormhole pairs. A pair joins the
            // component containing anchor A to the component containing anchor B.
            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                {
                    continue;
                }
                int ca = ABBandComponents.ComponentOf(map, p.a.Position);
                int cb = ABBandComponents.ComponentOf(map, p.b.Position);
                // ⚠ AN ANCHOR WITH NO COMPONENT IS AN ANCHOR NOBODY CAN STAND ON. Dropping it
                // is the whole point: the old band-keyed version would happily route a pawn
                // to a staircase sealed inside rock.
                if (ca < 0 || cb < 0 || ca == cb)
                {
                    continue;
                }
                if (!adj.TryGetValue(ca, out List<int> la))
                {
                    adj[ca] = la = new List<int>();
                }
                la.Add(cb);
                if (!adj.TryGetValue(cb, out List<int> lb))
                {
                    adj[cb] = lb = new List<int>();
                }
                lb.Add(ca);
            }

            var dist = new Dictionary<int, int> { [target] = 0 };
            var queue = new List<int> { target };
            for (int head = 0; head < queue.Count; head++)
            {
                int cur = queue[head];
                if (!adj.TryGetValue(cur, out List<int> ns))
                {
                    continue;
                }
                for (int n = 0; n < ns.Count; n++)
                {
                    if (!dist.ContainsKey(ns[n]))
                    {
                        dist[ns[n]] = dist[cur] + 1;
                        queue.Add(ns[n]);
                    }
                }
            }
            return dist;
        }

        private static void Consider(Map map, IntVec3 from, IntVec3 to, int compFrom,
            int hops, Dictionary<int, int> hopsToDest, Building_Door candNear,
            Building_Door candFar, ref float best, ref Building_Door near,
            ref Building_Door far)
        {
            // ⚠ THE NEAR ANCHOR MUST BE IN THE PAWN'S OWN ISLAND, NOT MERELY ITS OWN BAND.
            // This one line is the soundness fix: under the band-keyed version a pawn could be
            // dispatched to a staircase on the correct level that it had no way of walking to,
            // and would then stall at the edge of its island re-issuing the same order.
            if (ABBandComponents.ComponentOf(map, candNear.Position) != compFrom)
            {
                return;
            }
            int farComp = ABBandComponents.ComponentOf(map, candFar.Position);
            if (farComp < 0 || !hopsToDest.TryGetValue(farComp, out int farHops))
            {
                return;
            }
            // Only hops that get strictly CLOSER to the destination island. Without this a
            // pawn could be sent sideways or back the way it came and ping-pong forever,
            // because each hop re-plans from scratch with no memory of the last one.
            if (farHops != hops - 1)
            {
                return;
            }
            float cost = (candNear.Position - from).LengthHorizontal
                + (candFar.Position - to).LengthHorizontal;
            if (cost < best)
            {
                best = cost;
                near = candNear;
                far = candFar;
            }
        }
    }

    /// <summary>
    /// Synthetic links are wiped by RegionDirtyer on every region rebuild, so they must be
    /// re-armed immediately after the rebuild that destroyed them.
    ///
    /// NOT A HARMONY PATCH ANY MORE. This was a postfix on
    /// RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms, which is called ~4,500 times per
    /// frame and early-outs on !regionDirtyer.AnyDirty. Re-arming on every no-op call cost
    /// 0.34 ms/frame. Gating it with a prefix that sampled AnythingToRebuild only recovered
    /// 22% (0.339 -> 0.266 ms/frame), because the gate ADDED a second patch to an extremely
    /// hot method and the residue was Harmony dispatch, not work.
    ///
    /// vanilla already publishes exactly the signal we want: MapEvents.RegionsRoomsChanged
    /// is invoked on the LAST line of TryRebuildDirtyRegionsAndRooms, on the only path that
    /// actually rebuilt (after SetAllClean and initialized = true). Subscribing costs nothing
    /// on the millions of no-op calls and fires precisely when links have been wiped.
    /// </summary>
    public static class ABWormholeRearmHook
    {
        /// <summary>Maps we have already subscribed, so a re-entrant or repeated
        /// FinalizeInit cannot stack duplicate handlers on one map's event.</summary>
        private static readonly ConditionalWeakTable<Map, object> hooked =
            new ConditionalWeakTable<Map, object>();

        public static void Register(Map map)
        {
            if (map?.events == null)
            {
                return;
            }
            if (hooked.TryGetValue(map, out _))
            {
                return;
            }
            try
            {
                hooked.Add(map, new object());
            }
            catch (ArgumentException)
            {
                return; // benign race; already registered
            }
            // Captures the map, and MapEvents dies with the map, so there is nothing to
            // unsubscribe - the handler becomes unreachable together with its map.
            map.events.RegionsRoomsChanged += delegate
            {
                if (!ABGuard.On(ABGuard.Transit))
                {
                    return;
                }
                try
                {
                    ABWormhole.RearmAll(map);
                }
                catch (Exception e)
                {
                    // ⚠ THIS HANDLER FIRES ONCE PER REGION REBUILD. A bare Log.Error here was
                    // an unbounded error stream on a hot event - the same runaway shape as
                    // the per-frame camera clamp. Guard-switched: the stairs stop conducting
                    // (which is honest - a failed re-arm means they are not conducting
                    // anyway), the player is told once, and the settings panel can re-arm it.
                    ABGuard.Disable(ABGuard.Transit, e, "V2 wormhole re-arm");
                }
            };
        }
    }
}
