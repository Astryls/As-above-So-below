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
            public RegionLink link;
        }

        private static readonly Dictionary<int, List<Pair>> byMap = new Dictionary<int, List<Pair>>();

        /// <summary>Re-entrancy latch: rearming must never trigger a region rebuild
        /// (we only use NoRebuild accessors, but the latch makes that guarantee
        /// explicit and cheap to audit).</summary>
        private static bool rearming;

        public static int PairCount(Map map)
        {
            return map != null && byMap.TryGetValue(map.uniqueID, out List<Pair> l) ? l.Count : 0;
        }

        /// <summary>True when the cell is within <paramref name="radius"/> of either end of any
        /// wormhole on this map. Used to scope the stuck watchdog to stairwell traffic instead
        /// of every pawn on the map.</summary>
        public static bool NearAnyAnchor(Map map, IntVec3 cell, int radius)
        {
            if (map == null || !byMap.TryGetValue(map.uniqueID, out List<Pair> list))
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
            if (!byMap.TryGetValue(map.uniqueID, out List<Pair> list) || list.Count == 0)
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
                bool armed = p.link != null && ra != null && rb != null
                    && ra.links.Contains(p.link) && rb.links.Contains(p.link);
                sb.AppendLine("      link armed = " + armed);
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
                if (list[i].a == a || list[i].b == a || list[i].a == b || list[i].b == b)
                {
                    return; // already paired
                }
            }
            list.Add(new Pair { a = a, b = b });
            RearmAll(a.Map);
        }

        public static void Unlink(Building_Door anchor, Map map)
        {
            if (anchor == null || map == null || !byMap.TryGetValue(map.uniqueID, out List<Pair> list))
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
            if (!byMap.TryGetValue(map.uniqueID, out List<Pair> list))
            {
                list = new List<Pair>();
                byMap[map.uniqueID] = list;
            }
            return list;
        }

        private static void TearDown(Pair p)
        {
            if (p.link == null)
            {
                return;
            }
            p.link.RegionA?.links.Remove(p.link);
            p.link.RegionB?.links.Remove(p.link);
            p.link = null;
        }

        /// <summary>Re-create every synthetic link on this map whose regions have been
        /// rebuilt out from under it. Cheap and idempotent: a still-armed pair costs
        /// two grid lookups and two list scans.</summary>
        public static void RearmAll(Map map)
        {
            if (map == null || rearming || !byMap.TryGetValue(map.uniqueID, out List<Pair> list) || list.Count == 0)
            {
                return;
            }
            rearming = true;
            try
            {
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

        private static bool Rearm(Map map, Pair p)
        {
            if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
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
            if (p.link != null
                && p.link.RegionA != null && p.link.RegionA.valid
                && p.link.RegionB != null && p.link.RegionB.valid
                && ra.links.Contains(p.link) && rb.links.Contains(p.link))
            {
                return false; // still armed
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

            RegionLink link = new RegionLink();
            // Synthetic span. Never handed to RegionLinkDatabase, so the hash cannot
            // collide with a real edge link; it exists only so RegionCostCalculator and
            // debug drawing have something non-degenerate to read.
            link.span = new EdgeSpan(p.a.Position, SpanDirection.North, 1);
            link.RegionA = ra;
            link.RegionB = rb;
            ra.links.Add(link);
            rb.links.Add(link);
            p.link = link;
            return true;
        }

        /// <summary>Best transit pair for travelling from one band to another. Minimises
        /// (walk to near anchor) + (walk from far anchor to destination), which is the
        /// same whole-trip metric V1's StairRouter had to hand-roll - except here it is
        /// only an optimisation, because reachability is already correct.</summary>
        public static bool TryGetTransit(Map map, IntVec3 from, IntVec3 to,
            out Building_Door near, out Building_Door far)
        {
            near = null;
            far = null;
            if (map == null || !byMap.TryGetValue(map.uniqueID, out List<Pair> list) || list.Count == 0)
            {
                return false;
            }
            int bandFrom = ABBands.BandOf(map, from);
            int bandTo = ABBands.BandOf(map, to);
            if (bandFrom == bandTo)
            {
                return false;
            }
            float best = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                Pair p = list[i];
                if (p.a == null || p.b == null || !p.a.Spawned || !p.b.Spawned)
                {
                    continue;
                }
                Consider(map, from, to, bandFrom, bandTo, p.a, p.b, ref best, ref near, ref far);
                Consider(map, from, to, bandFrom, bandTo, p.b, p.a, ref best, ref near, ref far);
            }
            return near != null;
        }

        private static void Consider(Map map, IntVec3 from, IntVec3 to, int bandFrom, int bandTo,
            Building_Door candNear, Building_Door candFar,
            ref float best, ref Building_Door near, ref Building_Door far)
        {
            if (ABBands.BandOf(map, candNear.Position) != bandFrom
                || ABBands.BandOf(map, candFar.Position) != bandTo)
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
                try
                {
                    ABWormhole.RearmAll(map);
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: wormhole re-arm threw: " + e);
                }
            };
        }
    }
}
