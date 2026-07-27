using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

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
        private sealed class Pair
        {
            public Building_ABAnchor a;
            public Building_ABAnchor b;
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

        public static void Link(Building_ABAnchor a, Building_ABAnchor b)
        {
            if (a == null || b == null || a.Map == null || a.Map != b.Map)
            {
                Log.Warning(ABLog.Tag + " V2 spike: refusing to link anchors on different maps.");
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
            a.partner = b;
            b.partner = a;
            RearmAll(a.Map);
        }

        public static void Unlink(Building_ABAnchor anchor, Map map)
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
                if (p.a != null) { p.a.partner = null; }
                if (p.b != null) { p.b.partner = null; }
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
            TearDown(p);

            // Diagnostic, not a hard failure: if the anchors aren't Portal regions the
            // rooms WILL merge and assertion 2 fails. Surface it loudly at arm time.
            if (ra.type != RegionType.Portal || rb.type != RegionType.Portal)
            {
                Log.Warning(ABLog.Tag + " V2 spike: anchor region is " + ra.type + "/" + rb.type
                    + ", expected Portal/Portal. Rooms will merge across the wormhole.");
            }

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
            out Building_ABAnchor near, out Building_ABAnchor far)
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
            Building_ABAnchor candNear, Building_ABAnchor candFar,
            ref float best, ref Building_ABAnchor near, ref Building_ABAnchor far)
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
    /// Synthetic links are wiped by RegionDirtyer on every region rebuild, so they are
    /// re-armed immediately after the rebuild that destroyed them. This postfix is the
    /// single point that keeps the wormhole alive.
    /// </summary>
    [HarmonyPatch(typeof(RegionAndRoomUpdater), nameof(RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms))]
    public static class Patch_RegionAndRoomUpdater_ABRearmWormholes
    {
        private static readonly AccessTools.FieldRef<RegionAndRoomUpdater, Map> MapRef =
            AccessTools.FieldRefAccess<RegionAndRoomUpdater, Map>("map");

        private static void Postfix(RegionAndRoomUpdater __instance)
        {
            try
            {
                ABWormhole.RearmAll(MapRef(__instance));
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2 spike: rearm postfix threw: " + e);
            }
        }
    }
}
