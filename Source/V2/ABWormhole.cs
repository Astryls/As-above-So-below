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
            // Area-only check would wave it through and silently pair the wrong cells.
            //
            // ⚠ THIS GUARD IS NO LONGER HYPOTHETICAL. It was written when every link def was
            // square and read "stops a FUTURE non-square one failing invisibly"; §85 made the
            // plain staircases 1x2. Pairs stay safe only because both ends share a def SIZE
            // and a ROTATION (Building_ABStairs2 spawns the counterpart with this end's
            // Rotation, and that line is load-bearing for exactly this reason) - so if a
            // future link ever pairs two different defs, or something re-rotates one end,
            // this is the warning that will fire instead of a silent cell mismatch.
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
        /// §94: it now runs an EXACT planner - Dijkstra over the anchor graph, seeded at
        /// the destination - and returns the first crossing of the cheapest FULL chain
        /// (walks + flights), not the best-looking single hop. Chaining still comes for
        /// free from machinery that already exists: after each transit,
        /// ABWormholePather.Carry re-issues StartPath toward the true destination, which
        /// re-enters TrySegment and asks for the next hop - and because every suffix of a
        /// cheapest chain is itself a cheapest chain, the per-hop re-plans agree with each
        /// other instead of needing the old strict-progress hop filter.
        ///
        /// ⚠⚠ §94 WHY THE PREDECESSOR (min-hop BFS + greedy two-term proxy) HAD TO GO. Its
        /// proxy measured (far anchor -> FINAL destination) RAW across bands, and upper
        /// bands sit NORTH in map space - so for any multi-hop UP trip, "north of the
        /// destination" and "closer to it" were the same number, up to a full Slot of free
        /// discount. Field report: every ground -> 3rd-floor trip detoured through
        /// battlement ladders ~a hundred cells north while the pawn stood BESIDE the grand
        /// staircase; the reverse trip, where the same term PENALIZES northness, chose
        /// correctly; single-hop trips (same-band term) were always clean. The proxy also
        /// let min-hop dominate (a full-meshed elevator pair spanning N bands is ONE hop
        /// and would beat every staircase chain regardless of walking) and measured
        /// progress against the destination COLUMN rather than the NEXT FLIGHT
        /// (split-flight layouts mis-picked). Dijkstra answers all three with one piece of
        /// arithmetic - see TryPlanFirstHop.
        /// </summary>
        public static bool TryGetTransit(Map map, IntVec3 from, IntVec3 to,
            out Building_Door near, out Building_Door far)
        {
            return TryGetTransit(map, from, to, null, out near, out far);
        }

        /// <summary>
        /// As above, planned for a SPECIFIC PAWN.
        ///
        /// ⚠⚠ §59 THE ROUTE DEPENDS ON WHO IS WALKING IT, AND THE PAWNLESS OVERLOAD ANSWERS
        /// FOR SOMEONE WHO IGNORES FORBIDDEN DOORS. Every movement decision must come through
        /// here with the real pawn; the pawnless form is for diagnostics and fixtures that
        /// genuinely have nobody to ask.
        ///
        /// The pawn is consumed as ONE BIT (`RespectsForbiddenDoors`) resolved once, here,
        /// and passed down as `forbidAware`. Resolving it per lookup instead would put
        /// `CaresAboutForbidden` - which touches host faction, mental state, slave rebellion
        /// and mech state - on the inside of a loop over every wormhole pair.
        /// </summary>
        public static bool TryGetTransit(Map map, IntVec3 from, IntVec3 to, Pawn pawn,
            out Building_Door near, out Building_Door far)
        {
            near = null;
            far = null;
            if (map == null || !byMap.TryGetValue(map, out List<Pair> list) || list.Count == 0)
            {
                return false;
            }
            bool forbidAware = ABBandComponents.RespectsForbiddenDoors(pawn);
            // §94.g: wander commutes keep the cheap all-straight-line ranking. An idle pawn
            // does not need the optimal staircase, cross-band wander is frequent, and paying
            // real pathfinding for it would be rule-4 negligence. Everything else - player
            // orders, work jobs, the tick-side route preview - gets verified edges.
            bool exact = pawn?.CurJob?.def != JobDefOf.GotoWander;
            // PHASE 2: NODES ARE (BAND, COMPONENT), NOT BANDS.
            //
            // The old version keyed everything on band and early-returned when
            // `bandFrom == bandTo`. That was unsound in two directions at once:
            //  - it REFUSED a legitimate trip between two islands of the SAME band (two
            //    plateaus on a sky level, two buildings' upper floors) - the §34 report case;
            //  - it could ACCEPT a hop whose near anchor sat in an island the pawn cannot
            //    walk to, because "same band" was treated as "reachable".
            // Both vanish once the node is the island rather than the level.
            int compFrom = ABBandComponents.ComponentOf(map, from, forbidAware);
            if (compFrom < 0)
            {
                // The PAWN has no island: standing somewhere unwalkable, off-band, carried,
                // or mid-spawn. Nothing to route FROM.
                return false;
            }

            // ⚠⚠ A `Touch` TARGET IS USUALLY UNWALKABLE, AND THAT USED TO END THE ROUTE HERE.
            // `ComponentOf` returns -1 for an unwalkable cell, and the old guard folded that
            // in with "same island" as "not our business". But mining, deconstructing,
            // attacking a wall and every other PathEndMode.Touch job targets an IMPASSABLE
            // cell BY NATURE, so a cross-level mining trip could never be segmented onto a
            // staircase. The pawn kept vanilla's CanReach=True (the synthetic RegionLink does
            // connect the region graph) while the pathfinder returned NOT FOUND, and re-issued
            // the job forever - the "stands still and never arrives" field report.
            //
            // CONSTRUCTION WAS UNAFFECTED AND THAT IS THE TELL: a BLUEPRINT is passable, so it
            // HAS a component and routed fine. "Trying to go downstairs to mine and is stuck;
            // constructing across levels seems fine" falls straight out of this one guard.
            //
            // Resolve the target the way the pathfinder does: to the cells a Touch job can
            // actually stand on.
            int compTo = ABBandComponents.ComponentOf(map, to, forbidAware);
            if (compTo < 0)
            {
                return TryGetTransitToTouch(map, from, to, compFrom, forbidAware, list,
                    pawn, exact, out near, out far);
            }
            if (compFrom == compTo)
            {
                // Same island: an ordinary intra-band path. Not our business.
                return false;
            }
            // §94: the walkable-destination case seeds the planner with one island.
            var destComps = new HashSet<int> { compTo };
            return TryPlanFirstHop(map, list, from, to, compFrom, destComps, forbidAware,
                pawn, exact, out near, out far);
        }

        /// <summary>
        /// Route to a target that is ITSELF UNWALKABLE - rock being mined, a wall being
        /// deconstructed, anything attacked in place. A pawn never stands on such a cell; it
        /// stands on one of the 8 neighbours, exactly as `PathEndMode.Touch` does. So the
        /// destination island is the island of an ADJACENT WALKABLE cell, not of the target.
        ///
        /// ⚠ IF ANY ADJACENT ISLAND IS THE PAWN'S OWN, THERE IS NOTHING TO SEGMENT and we
        /// must say so. The pawn can already stand next to the target without a staircase,
        /// and routing it through one would send a miner on a tour of the colony to reach
        /// rock at its feet. This is the same soundness rule as `ConsiderHop`'s near-anchor
        /// check, applied to the far end.
        ///
        /// ⚠ NEIGHBOURS MUST BE IN THE TARGET'S OWN BAND. Bands are slices of one Map along
        /// +z, so the cell "one north" of a target on a band edge is a DIFFERENT LEVEL that
        /// merely shares an x/z edge. Standing there touches nothing.
        ///
        /// Cost: only ever runs when the target is unwalkable, which is precisely the case
        /// that previously returned false immediately. Walkable destinations are untouched.
        /// </summary>
        private static bool TryGetTransitToTouch(Map map, IntVec3 from, IntVec3 to,
            int compFrom, bool forbidAware, List<Pair> list, Pawn pawn, bool exact,
            out Building_Door near, out Building_Door far)
        {
            near = null;
            far = null;

            // §94: EVERY approach island seeds the planner at once. The old shape picked
            // the min-hop island first and ranked anchors only within its table, so a rock
            // face touchable from two rooms never let the genuinely cheaper approach
            // compete. The planner's seed edge measures each destination-island anchor
            // against the REAL target cell, so the chosen staircase is still the one
            // nearest the rock.
            HashSet<int> destComps = null;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 nb = to + GenAdj.AdjacentCells[i];
                if (!nb.InBounds(map) || !ABBands.SameBand(map, nb, to))
                {
                    continue;
                }
                int cn = ABBandComponents.ComponentOf(map, nb, forbidAware);
                if (cn < 0)
                {
                    continue;
                }
                if (cn == compFrom)
                {
                    // Already touchable from the pawn's own island: no transit wanted.
                    return false;
                }
                if (destComps == null)
                {
                    destComps = new HashSet<int>();
                }
                destComps.Add(cn);
            }
            if (destComps == null)
            {
                return false;
            }
            return TryPlanFirstHop(map, list, from, to, compFrom, destComps, forbidAware,
                pawn, exact, out near, out far);
        }

        /// <summary>
        /// §94: cost of one wormhole crossing, in walk-cell equivalents (a cell is ~13
        /// ticks at baseline speed; the §78 hold plus climb clip is on the order of 90
        /// ticks, so ~7 cells).
        ///
        /// Two duties beyond realism:
        ///  - it keeps chains SHORT on ties: with a zero crossing cost, a two-flight chain
        ///    with marginally less walking would beat one flight next door;
        ///  - it is the anti-regress margin for per-hop re-planning. A crossed pawn lands
        ///    within LandingRadius of the far anchor (diagonal ~2.8 cells), so a re-plan
        ///    starts at most that far from where the chain thought it stood. FlightCost
        ///    exceeds twice that displacement, so "cross straight back" can never price
        ///    below continuing an optimal chain - the weighted successor of the old
        ///    `farHops == hops - 1` strict-progress filter, which Dijkstra obsoletes.
        /// </summary>
        private const float FlightCost = 7f;

        /// <summary>
        /// §94 THE EXACT FIRST-HOP PLANNER. Multi-source Dijkstra over the anchor graph,
        /// seeded at the destination island(s); the crossing that begins the cheapest full
        /// chain from the pawn is returned.
        ///
        /// Nodes are anchor BUILDINGS (deduped - an elevator car appears once however many
        /// counterpart pairs it sits in). Edges are the wormhole pairs at FlightCost each,
        /// plus straight-line walks between anchors sharing an island. Seeds are the
        /// anchors standing in a destination island, at their walk distance to the target
        /// cell; the pawn enters the graph only through anchors of its own island, ranked
        /// in ConsiderHop.
        ///
        /// ⚠⚠ NO DISTANCE IS EVER MEASURED ACROSS BANDS, BY CONSTRUCTION. Every walk edge
        /// joins two members of ONE island, and an island never spans bands (component ids
        /// encode their band - see ABBandComponents.ComponentOf). Crossings are a CONSTANT
        /// because the map-space separation of a pair's ends is band scaffolding, not
        /// geometry. That is the structural guarantee that the §94 mis-route - "north" and
        /// "up" priced as the same number - cannot be reintroduced by any future layout.
        ///
        /// ⚠⚠ §94.g VERIFIED EDGES, NOT BLIND STRAIGHT LINES. Straight-line walk terms lie
        /// across walls - field-proven within an hour of §94 v1 shipping: the estimate said
        /// the grand staircase mouth was 18 cells away, the pather measured 105 around the
        /// compound wall, and the planner sent the pawn on the long march. Exact mode
        /// therefore (a) prices anchor-to-anchor mid-chain edges with REAL path lengths,
        /// cached per region-rebuild version (WalkEdge), and (b) verifies the WINNER's
        /// pawn-side and destination-side legs with real paths, correcting the lied term
        /// and re-ranking until the winner survives - candidates that never win are never
        /// paid for (rule 76). Path-avoid areas now weigh in exactly as far as they bend
        /// those real paths. Estimate mode (wander, §94.g) stays all-straight-line and
        /// touches neither the pathfinder nor the cache: FindPathNow is tick-side-only
        /// machinery, and a per-frame caller would be the trap ABTransitVisuals' banner
        /// documents.
        ///
        /// ⚠ CROSSINGS ARE UNDIRECTED. Every link type today conducts both ways; if a
        /// one-way link ever ships, the cross-edge relaxation below needs a direction
        /// check.
        ///
        /// Budget: O(nodes^2) with linear-scan extraction, nodes = 2 x live pairs, only on
        /// genuinely cross-island StartPaths - the same order of work as the per-call
        /// dictionaries the old BFS built (and the Touch case ran up to eight of those).
        /// </summary>
        private static bool TryPlanFirstHop(Map map, List<Pair> list, IntVec3 from,
            IntVec3 to, int compFrom, HashSet<int> destComps, bool forbidAware, Pawn pawn,
            bool exact, out Building_Door near, out Building_Door far)
        {
            near = null;
            far = null;

            // ---- nodes ------------------------------------------------------------
            var index = new Dictionary<Building_Door, int>();
            var nodes = new List<Building_Door>();
            var comps = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                {
                    continue;
                }
                AddNode(map, p.a, forbidAware, index, nodes, comps);
                AddNode(map, p.b, forbidAware, index, nodes, comps);
            }
            int n = nodes.Count;
            if (n == 0)
            {
                return false;
            }

            // ---- estimates that start as straight lines and get verified ----------
            // seed[i]: leg from a destination-island anchor to `to`. walkIn[i]: leg from
            // the pawn to a compFrom anchor. Both LIE across walls; the loop below
            // replaces exactly the estimates that DECIDE the answer with real path
            // lengths, one winner at a time (rule 76).
            float[] seed = new float[n];
            bool[] seedDone = new bool[n];
            float[] walkIn = new float[n];
            bool[] walkInDone = new bool[n];
            for (int i = 0; i < n; i++)
            {
                seed[i] = destComps.Contains(comps[i])
                    ? (nodes[i].Position - to).LengthHorizontal
                    : float.MaxValue;
                walkIn[i] = comps[i] == compFrom
                    ? (nodes[i].Position - from).LengthHorizontal
                    : float.MaxValue;
            }

            float[] dist = new float[n];
            bool[] done = new bool[n];
            for (int round = 0; ; round++)
            {
                // ---- Dijkstra from the destination, against current estimates -----
                // Re-run per correction round: n is tiny, and a corrected seed feeds
                // every dist[] downstream, so patching in place would be wrong.
                for (int i = 0; i < n; i++)
                {
                    dist[i] = seed[i];
                    done[i] = false;
                }
                for (int settle = 0; settle < n; settle++)
                {
                    int u = -1;
                    float best = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        if (!done[i] && dist[i] < best)
                        {
                            best = dist[i];
                            u = i;
                        }
                    }
                    if (u < 0)
                    {
                        break; // everything still open is unreachable
                    }
                    done[u] = true;
                    // Walk edges: every other anchor on the settled node's island.
                    // §94.g: real and version-cached in exact mode - in the reporting
                    // colony the battlement corridor between two ladders is several
                    // times its crow-flies length, and mid-chain legs never reach the
                    // winner-verification below, so they must be honest up front.
                    for (int i = 0; i < n; i++)
                    {
                        if (done[i] || comps[i] != comps[u])
                        {
                            continue;
                        }
                        float step = exact
                            ? WalkEdge(map, nodes[u], nodes[i], forbidAware)
                            : (nodes[i].Position - nodes[u].Position).LengthHorizontal;
                        float cand = dist[u] + step;
                        if (cand < dist[i])
                        {
                            dist[i] = cand;
                        }
                    }
                    // Crossing edges: every pair this building is an end of.
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pair p = list[i];
                        Building_Door other = p.a == nodes[u] ? p.b
                            : (p.b == nodes[u] ? p.a : null);
                        if (other == null || !index.TryGetValue(other, out int vi) || vi < 0
                            || done[vi])
                        {
                            continue;
                        }
                        float cand = dist[u] + FlightCost;
                        if (cand < dist[vi])
                        {
                            dist[vi] = cand;
                        }
                    }
                }

                // ---- rank the first hop against current estimates -----------------
                float bestTotal = float.MaxValue;
                int bestNi = -1;
                int bestFi = -1;
                for (int i = 0; i < list.Count; i++)
                {
                    Pair p = list[i];
                    if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                    {
                        continue;
                    }
                    RankHop(index, comps, compFrom, walkIn, dist, p.a, p.b,
                        ref bestTotal, ref bestNi, ref bestFi);
                    RankHop(index, comps, compFrom, walkIn, dist, p.b, p.a,
                        ref bestTotal, ref bestNi, ref bestFi);
                }
                if (bestNi < 0)
                {
                    return false;
                }
                near = nodes[bestNi];
                far = nodes[bestFi];
                if (!exact)
                {
                    return true;
                }

                // ---- verify the winner; correct and re-rank when a term lied ------
                // Each slot is verified at most once, so every dirty round marks fresh
                // ground and the loop terminates; the cap is a belt for the
                // pathological. A NOT-FOUND probe keeps the estimate (transient region
                // states mid-rebuild) rather than condemning the candidate.
                bool clean = true;
                if (!walkInDone[bestNi])
                {
                    walkInDone[bestNi] = true;
                    float real = RealPathLen(map, pawn, from,
                        new LocalTargetInfo(nodes[bestNi]));
                    if (real >= 0f && Math.Abs(real - walkIn[bestNi]) > VerifyEpsilon)
                    {
                        if (ABV2Debug.LogTransit)
                        {
                            ABV2Debug.Transit("PLAN: walk-in to " + nodes[bestNi].ThingID
                                + " " + nodes[bestNi].Position + " corrected "
                                + walkIn[bestNi].ToString("0.0") + " -> "
                                + real.ToString("0.0"));
                        }
                        walkIn[bestNi] = real;
                        clean = false;
                    }
                }
                if (destComps.Contains(comps[bestFi]) && !seedDone[bestFi])
                {
                    seedDone[bestFi] = true;
                    float real = RealPathLen(map, pawn, nodes[bestFi].Position,
                        new LocalTargetInfo(to));
                    if (real >= 0f && Math.Abs(real - seed[bestFi]) > VerifyEpsilon)
                    {
                        if (ABV2Debug.LogTransit)
                        {
                            ABV2Debug.Transit("PLAN: seed leg from " + nodes[bestFi].ThingID
                                + " " + nodes[bestFi].Position + " corrected "
                                + seed[bestFi].ToString("0.0") + " -> "
                                + real.ToString("0.0"));
                        }
                        seed[bestFi] = real;
                        clean = false;
                    }
                }
                if (clean)
                {
                    return true;
                }
                if (round >= MaxVerifyRounds)
                {
                    if (ABV2Debug.LogTransit)
                    {
                        ABV2Debug.Transit("PLAN: verify budget exhausted after "
                            + MaxVerifyRounds + " rounds; shipping current best");
                    }
                    return true;
                }
            }
        }

        /// <summary>
        /// Register one anchor building as a planner node, resolving its island ONCE.
        ///
        /// ⚠ AN ANCHOR WITH NO COMPONENT IS AN ANCHOR NOBODY CAN STAND ON, and it is not in
        /// the graph at all - not a waypoint, not a crossing end, not a seed. That keeps
        /// the old guarantees: a staircase sealed inside rock conducts nothing, and §59's
        /// widening still holds - anchors are Building_Door subclasses, so a FORBIDDEN
        /// staircase resolves to -1 on the forbid-aware partition and forbidding it
        /// actually closes it to colonists. Recorded as index -1 so each pair membership
        /// does not re-resolve the island of a dropped building.
        /// </summary>
        private static void AddNode(Map map, Building_Door d, bool forbidAware,
            Dictionary<Building_Door, int> index, List<Building_Door> nodes, List<int> comps)
        {
            if (index.ContainsKey(d))
            {
                return;
            }
            int c = ABBandComponents.ComponentOf(map, d.Position, forbidAware);
            if (c < 0)
            {
                index.Add(d, -1);
                return;
            }
            index.Add(d, nodes.Count);
            nodes.Add(d);
            comps.Add(c);
        }

        /// <summary>Rounds of winner-verification before the planner ships its current
        /// best. Each dirty round verifies at least one fresh term, so the loop is bounded
        /// by construction; this cap is a belt. Six rounds ≈ three candidates fully
        /// verified, beyond which the field is lying so uniformly that another probe will
        /// not change the story.</summary>
        private const int MaxVerifyRounds = 6;

        /// <summary>Correction threshold in cells. Re-ranking is free, so this only
        /// suppresses churn from Touch-vs-OnCell off-by-ones, not real divergence.</summary>
        private const float VerifyEpsilon = 0.5f;

        /// <summary>§94.g: real anchor-to-anchor walk lengths, cached until regions change.
        /// Keyed by the two anchors' thingIDs (order-normalized), complemented for the
        /// forbid-aware partition - the partitions can disagree about which doors exist.</summary>
        private sealed class WalkCache
        {
            public int version = -1;

            public readonly Dictionary<long, float> dist = new Dictionary<long, float>();
        }

        private static readonly ConditionalWeakTable<Map, WalkCache> walkCache =
            new ConditionalWeakTable<Map, WalkCache>();

        /// <summary>
        /// Real walk length between two same-island anchors, cached per region-rebuild
        /// version (ABBandComponents.Version - the exact signal that moves walls).
        ///
        /// ⚠ PAWNLESS BY DESIGN: the cache is shared across every pawn, so it paths with
        /// PassDoors - walls, the actual liars, are captured; per-pawn door nuance is not.
        /// Per-pawn exactness lives in the winner-verification legs, which DO thread the
        /// pawn. A NOT-FOUND probe (transient mid-rebuild state) falls back to the straight
        /// line rather than poisoning the cache with an infinity.
        /// </summary>
        private static float WalkEdge(Map map, Building_Door a, Building_Door b,
            bool forbidAware)
        {
            if (!walkCache.TryGetValue(map, out WalkCache c))
            {
                c = new WalkCache();
                try
                {
                    walkCache.Add(map, c);
                }
                catch (ArgumentException)
                {
                    walkCache.TryGetValue(map, out c);
                }
            }
            int v = ABBandComponents.Version;
            if (c.version != v)
            {
                c.version = v;
                c.dist.Clear();
            }
            long key = a.thingIDNumber < b.thingIDNumber
                ? ((long)a.thingIDNumber << 32) | (uint)b.thingIDNumber
                : ((long)b.thingIDNumber << 32) | (uint)a.thingIDNumber;
            if (forbidAware)
            {
                key = ~key;
            }
            if (c.dist.TryGetValue(key, out float d))
            {
                return d;
            }
            float real = RealPathLen(map, null, a.Position, new LocalTargetInfo(b));
            if (real < 0f)
            {
                real = (a.Position - b.Position).LengthHorizontal;
            }
            c.dist[key] = real;
            return real;
        }

        /// <summary>
        /// Length of the real path from <paramref name="start"/> to the target, in
        /// straight-line-comparable cells (octile-summed over the nodes: cardinal 1,
        /// diagonal √2 - PawnPath node COUNTS undercount diagonals against
        /// LengthHorizontal). Returns -1 when no path was found.
        ///
        /// ⚠ TICK-SIDE ONLY. FindPathNow opens with ForceCompleteScheduledJobs(); calling
        /// it from a draw callback is the documented ABTransitVisuals trap. Every caller
        /// here is reached from StartPath segmentation or the tick-built route preview.
        /// ⚠ PawnPath IS POOLED - measured and disposed inside one using, never held.
        /// ⚠ Same-band by construction (island-scoped legs), so
        /// Patch_PathFinder_ABRejectCrossBandSync waves every probe through; if a
        /// cross-band pair ever slipped in it would come back NotFound, which degrades to
        /// "keep the estimate" - safe.
        /// </summary>
        private static float RealPathLen(Map map, Pawn pawn, IntVec3 start,
            LocalTargetInfo target)
        {
            if (map?.pathFinder == null || !start.IsValid || !target.IsValid)
            {
                return -1f;
            }
            try
            {
                using (PawnPath path = pawn != null && pawn.Spawned && pawn.Map == map
                    ? map.pathFinder.FindPathNow(start, target, pawn, null,
                        PathEndMode.Touch)
                    : map.pathFinder.FindPathNow(start, target,
                        TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly), null,
                        PathEndMode.Touch))
                {
                    if (path == null || !path.Found)
                    {
                        return -1f;
                    }
                    List<IntVec3> cells = path.NodesReversed;
                    if (cells == null || cells.Count < 2)
                    {
                        return 0f;
                    }
                    float len = 0f;
                    for (int i = 1; i < cells.Count; i++)
                    {
                        IntVec3 d = cells[i] - cells[i - 1];
                        len += d.x != 0 && d.z != 0 ? 1.41421356f : 1f;
                    }
                    return len;
                }
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        /// <summary>Rank one pair orientation as the pawn's first hop: walk-in estimate to
        /// the near end, cross, then the far end's settled cost-to-destination. Returns the
        /// winner as NODE INDICES so the §94.g verification loop can name what it checked.</summary>
        private static void RankHop(Dictionary<Building_Door, int> index, List<int> comps,
            int compFrom, float[] walkIn, float[] dist, Building_Door candNear,
            Building_Door candFar, ref float bestTotal, ref int bestNi, ref int bestFi)
        {
            if (!index.TryGetValue(candNear, out int ni) || ni < 0
                || !index.TryGetValue(candFar, out int fi) || fi < 0)
            {
                return; // an end nobody can stand on never became a node (§59)
            }
            // ⚠ THE NEAR ANCHOR MUST BE IN THE PAWN'S OWN ISLAND, NOT MERELY ITS OWN BAND.
            // Still the soundness line it always was: a pawn dispatched to a staircase it
            // cannot walk to stalls at the edge of its island re-issuing the same order.
            if (comps[ni] != compFrom)
            {
                return;
            }
            if (comps[fi] == comps[ni])
            {
                return; // both ends in one island conduct nothing worth crossing
            }
            if (dist[fi] >= float.MaxValue || walkIn[ni] >= float.MaxValue)
            {
                return; // no chain from the far side reaches the destination
            }
            // The walk-in term is same-band by the compFrom check; the far side's cost is
            // built from same-island walks and constant flights - nothing here measures
            // across a band boundary (rule 75).
            float total = walkIn[ni] + FlightCost + dist[fi];
            if (total < bestTotal)
            {
                bestTotal = total;
                bestNi = ni;
                bestFi = fi;
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
            map.events.RegionsRoomsChanged += delegate { Rearm(map); };

            // ⚠⚠ INITIAL ARM - THE SUBSCRIPTION ALONE IS NOT ENOUGH ON THE LOAD PATH.
            //
            // The event fires on the LAST line of TryRebuildDirtyRegionsAndRooms, i.e.
            // only on the NEXT rebuild after this registration. On a NEW banded game that
            // is fine: carving and the post-FinalizeInit terrain repairs dirty regions
            // within frames, the event fires, the links arm. On a LOADED game the one big
            // region rebuild has ALREADY happened by the time we get here -
            // Map.FinalizeInit rebuilds all regions BEFORE calling MapComponents'
            // FinalizeInit, which is where Register is called from. So the map sat with
            // regions built, ZERO synthetic links, and no event due until the first
            // genuine region change of the session. On a settled colony that can be
            // hours. Symptom: after save+reload every cross-level bill, haul and
            // construction job is silently dead until the player builds or destroys
            // something. (The old Harmony postfix re-armed ~4,500 times per FRAME and hid
            // this hole by brute force; the event rework removed the accidental initial
            // arm together with the waste - the perf numbers in the banner above were
            // real, and so was the regression they smuggled in.)
            //
            // Deferred to the main thread: FinalizeInit runs on the LongEvent WORKER
            // thread during load - the same reason ABBandMap defers its camera move.
            LongEventHandler.ExecuteWhenFinished(delegate { Rearm(map); });
        }

        /// <summary>One body for both arm paths (the per-rebuild event and the initial
        /// post-load arm), so the guard gate and the error handling cannot drift apart.</summary>
        private static void Rearm(Map map)
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
                // ⚠ THIS FIRES ONCE PER REGION REBUILD. A bare Log.Error here was an
                // unbounded error stream on a hot event - the same runaway shape as the
                // per-frame camera clamp. Guard-switched: the stairs stop conducting
                // (which is honest - a failed re-arm means they are not conducting
                // anyway), the player is told once, and the settings panel can re-arm it.
                ABGuard.Disable(ABGuard.Transit, e, "V2 wormhole re-arm");
            }
        }
    }
}
