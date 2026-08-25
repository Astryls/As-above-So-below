using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WHAT A CROSS-BAND TRIP LOOKS LIKE WHILE IT IS HAPPENING.
    ///
    /// Two separate defects wore one costume here ("the path line stops at the stairs"), and
    /// they needed opposite fixes:
    ///
    ///  1. BEFORE THE HOP the line is not missing, it does not exist. `TrySegment` rewrites
    ///     the destination to the near anchor before the pather ever builds a path, so
    ///     `curPath` legitimately ends at the stairwell. The remainder lives only in
    ///     `ABWormholePather`'s transit record. To show it we have to compute it ourselves.
    ///  2. AFTER THE HOP the line exists and is drawn a full Slot off screen, because
    ///     `PawnPath.DrawPath` draws nodes at their true world coordinates while
    ///     `ABBelowDynamicDraw` draws the pawn itself lifted into the viewed band.
    ///
    /// \u26a0 EVERY OVERLAY MUST AGREE WITH WHERE THE RENDERER PUTS THE PAWN, NOT WITH WHERE THE
    /// PAWN ACTUALLY IS. That is the rule the old `LocalizeForPawn` broke by localizing onto
    /// the pawn's own band; see ABUIGeometry.LiftToView.
    ///
    /// \u26a0 THE FAR SIDE IS A REAL PATH, NOT A STRAIGHT LINE, AND IT IS CACHED FOR A REASON.
    /// It is produced by `FindPathNow`, which is a full synchronous A*. This code runs from a
    /// DRAW callback, so computing it per frame would be a hard freeze. It is therefore
    /// computed lazily on first draw (i.e. only for pawns the player has actually selected),
    /// cached against the transit's near anchor, and refreshed on a slow interval.
    ///
    /// \u26a0 THE CACHE IS KEYED ON THE NEAR ANCHOR, NOT ON THE PAWN. A pawn that re-issues its
    /// order picks a new near anchor, and that is exactly when a stale route would start
    /// lying. Keying on the anchor makes the invalidation automatic instead of remembered.
    /// </summary>
    public static class ABTransitVisuals
    {
        private const int RefreshTicks = 150;

        /// <summary>Hard ceiling on hops resolved for one preview. Bands cap at 7, so this
        /// only ever fires if the wormhole graph is malformed - in which case stopping is
        /// better than looping in a draw call.</summary>
        private const int MaxHops = 8;

        private sealed class Route
        {
            public readonly List<IntVec3> nodes = new List<IntVec3>();

            /// <summary>Indices i where segment i -> i+1 is a WORMHOLE HOP and must not be
            /// drawn: zero travel, arbitrary distance apart once both ends are lifted.</summary>
            public readonly List<int> hops = new List<int>();

            public IntVec3 anchor = IntVec3.Invalid;
            public IntVec3 dest = IntVec3.Invalid;
            public int computedTick = -99999;
        }

        private static readonly Dictionary<int, Route> routes = new Dictionary<int, Route>();

        /// <summary>Pawn-id keyed, so it must not cross a game load - see the banner on
        /// ABWormholePather.ResetForNewGame. A stale route here draws a line for a journey
        /// that belonged to a different session.</summary>
        [ABGameReset]
        public static void ResetForNewGame()
        {
            routes.Clear();
        }

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                routes.Remove(p.thingIDNumber);
            }
        }

        /// <summary>
        /// Dump the cached preview node-by-node with each node's band, for the "the line goes
        /// half way then straight down" report.
        ///
        /// ⚠ A ONE-SLOT VERTICAL SEGMENT IS AN UNLIFTED ENDPOINT, ALWAYS. Every node here is
        /// drawn through ABUIGeometry.LiftToView, so a vertical run of exactly Slot means one
        /// end of that segment came from somewhere else - or that the segment is a HOP whose
        /// skip-index went missing. Printing the bands next to the coordinates distinguishes
        /// those two in one reading instead of another round of theorising.
        /// </summary>
        public static string DescribeRoute(Pawn p)
        {
            if (p == null || p.Map == null)
            {
                return "  (no pawn)";
            }
            if (!routes.TryGetValue(p.thingIDNumber, out Route r))
            {
                return "  no cached route (pawn has no pending transit, or is not selected)";
            }
            var sb = new StringBuilder();
            sb.AppendLine("  route: " + r.nodes.Count + " nodes, anchor=" + r.anchor
                + " dest=" + r.dest + " builtTick=" + r.computedTick
                + " hopBreaks=[" + string.Join(",", r.hops.ConvertAll(i => i.ToString()).ToArray()) + "]");
            int viewBand = ABBandView.CurrentBand(p.Map);
            sb.AppendLine("  viewBand=" + viewBand);
            for (int i = 0; i < r.nodes.Count; i++)
            {
                IntVec3 c = r.nodes[i];
                string tag = r.hops.Contains(i) ? "  <-- HOP, segment to next is NOT drawn" : "";
                sb.AppendLine("    [" + i + "] " + c + " band " + ABBands.BandOf(p.Map, c) + tag);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Draw the part of the journey the pather does not know about: from the near anchor,
        /// across, and on to the real destination, however many bands that takes.
        /// </summary>
        public static void DrawRemainingRoute(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned)
            {
                return;
            }
            Map map = pawn.Map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            // ⚠⚠ READ ONLY. THIS METHOD MUST NEVER COMPUTE A PATH.
            //
            // It used to call Resolve() here, which calls FindPathNow, which is a SYNCHRONOUS
            // A* that begins with `ForceCompleteScheduledJobs()`. Running that from a draw
            // callback reaches into the pathfinder mid-frame, outside PathFinderTick, and
            // force-completes and recycles path jobs that the tick loop still has in flight.
            // It fires only while a pawn with a pending transit is SELECTED - which is exactly
            // the situation in which anyone tests cross-level movement, so the blast radius
            // and the test case are the same thing.
            //
            // Routes are now built on the game tick (TickRoutes) and this only draws what is
            // already cached. A route that has not been built yet simply draws nothing for one
            // tick, which is invisible.
            if (!routes.TryGetValue(pawn.thingIDNumber, out Route r) || r.nodes.Count < 2)
            {
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            float alt = AltitudeLayer.Item.AltitudeFor();
            for (int i = 0; i < r.nodes.Count - 1; i++)
            {
                // \u26a0 SKIP THE ANCHOR-TO-ANCHOR SEGMENT. The two ends of a wormhole are a Slot
                // apart in world space but zero cells apart in travel, so lifting both into
                // the viewed band collapses them onto (nearly) the same point and drawing
                // between them is a no-op. Drawing them UNLIFTED instead would put a line
                // straight through the gutter and every level between, which is the visual
                // this whole file exists to avoid.
                // The assumption above held only for RISERS, whose partner cell is
                // (x, z +/- Slot). STAIRS pairs are arbitrary, so a staircase with its two
                // ends in different corners drew a long diagonal streak for a journey that
                // takes zero travel. Skip the hop segments explicitly instead.
                if (r.hops.Contains(i))
                {
                    continue;
                }
                GenDraw.DrawLineBetween(
                    ABUIGeometry.LiftToView(bands, viewBand, r.nodes[i], alt),
                    ABUIGeometry.LiftToView(bands, viewBand, r.nodes[i + 1], alt));
            }
        }

        /// <summary>
        /// Build or refresh route previews, ON THE GAME TICK. Only for pawns the player has
        /// selected: the preview exists to be looked at, and this is the call that costs a
        /// synchronous pathfind.
        /// </summary>
        public static void TickRoutes()
        {
            if (routes.Count > 0 && Find.Selector == null)
            {
                routes.Clear();
                return;
            }
            if (Find.Selector == null)
            {
                return;
            }
            List<object> selected = Find.Selector.SelectedObjects;
            if (selected == null)
            {
                return;
            }
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn pawn) || !pawn.Spawned)
                {
                    continue;
                }
                if (!ABWormholePather.TryGetPending(pawn, out IntVec3 nearCell,
                        out IntVec3 farCell, out LocalTargetInfo realDest))
                {
                    Clear(pawn);
                    continue;
                }
                Map map = pawn.Map;
                if (map == null || !ABBands.Banded(map))
                {
                    continue;
                }
                Resolve(pawn, map, nearCell, farCell, realDest);
            }
        }

        private static Route Resolve(Pawn pawn, Map map, IntVec3 nearCell, IntVec3 farCell,
            LocalTargetInfo realDest)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (!routes.TryGetValue(pawn.thingIDNumber, out Route r))
            {
                r = new Route();
                routes[pawn.thingIDNumber] = r;
            }
            bool stale = r.anchor != nearCell
                || r.dest != realDest.Cell
                || now - r.computedTick > RefreshTicks;
            if (!stale)
            {
                return r;
            }
            r.anchor = nearCell;
            r.dest = realDest.Cell;
            r.computedTick = now;
            r.nodes.Clear();
            r.hops.Clear();
            try
            {
                Build(pawn, map, nearCell, farCell, realDest, r.nodes, r.hops);
            }
            catch (Exception e)
            {
                r.nodes.Clear();
                r.hops.Clear();
                Log.WarningOnce(ABLog.Tag + " V2: transit route preview failed: " + e.Message,
                    762195934);
            }
            return r;
        }

        /// <summary>
        /// Walk the hop chain, pathing each same-band leg for real.
        ///
        /// \u26a0 EVERY LEG HERE IS DELIBERATELY SAME-BAND, which is what makes the \u00a732 cross-band
        /// guard a safety net rather than an obstacle: if a leg ever comes out cross-band
        /// because the hop chain is wrong, the guard rejects it in O(1) instead of letting A*
        /// drain the whole band pocket inside a draw call.
        /// </summary>
        private static void Build(Pawn pawn, Map map, IntVec3 nearCell, IntVec3 farCell,
            LocalTargetInfo realDest, List<IntVec3> into, List<int> hopBreaks)
        {
            into.Add(nearCell);
            hopBreaks.Add(into.Count - 1); // nearCell -> farCell IS the hop
            IntVec3 cur = farCell;
            into.Add(cur);

            IntVec3 destCell = realDest.Cell;
            int hops = 0;
            while (destCell.IsValid && !ABBands.SameBand(map, cur, destCell) && hops++ < MaxHops)
            {
                // The pawn is threaded so the DRAWN route is the one that will actually be
                // walked - a preview planned pawnlessly would show a line straight through a
                // forbidden door while the pawn takes the stairs (§59).
                if (!ABWormhole.TryGetTransit(map, cur, destCell, pawn,
                        out Building_Door nextNear, out Building_Door nextFar)
                    || nextNear == null || nextFar == null)
                {
                    return; // no further route known; stop where we are
                }
                AppendLeg(pawn, map, cur, nextNear.Position, PathEndMode.OnCell, into);
                hopBreaks.Add(into.Count - 1);
                cur = nextFar.Position;
                into.Add(cur);
            }
            if (destCell.IsValid && !cur.Equals(destCell))
            {
                AppendLeg(pawn, map, cur, destCell, PathEndMode.OnCell, into);
            }
        }

        /// <summary>One same-band leg, as the pawn would actually walk it.</summary>
        private static void AppendLeg(Pawn pawn, Map map, IntVec3 from, IntVec3 to,
            PathEndMode peMode, List<IntVec3> into)
        {
            if (from == to || !from.InBounds(map) || !to.InBounds(map))
            {
                return;
            }
            // \u26a0 PawnPath IS POOLED. Copy the nodes out and dispose immediately - holding one
            // across frames starves map.pawnPathPool and the leak only shows up as pathing
            // stalls much later, nowhere near this file.
            using (PawnPath path = map.pathFinder.FindPathNow(from, to, pawn, null, peMode))
            {
                if (path == null || !path.Found)
                {
                    into.Add(to); // no route: a straight hint beats nothing
                    return;
                }
                // ⚠ ASCENDING, AND THIS WAS WRONG THE FIRST TIME. `Peek(n)` returns
                // `nodes[curNodeIndex - n]`, so Peek(0) is the NEXT cell and the index counts
                // FORWARD toward the destination - which is exactly why vanilla's own
                // DrawPath joins the pawn to Peek(0) and then walks i upward. Iterating
                // downward appended every leg destination-first, so the preview shot to the
                // end of each leg and doubled back: one reversed list per leg, drawn as a
                // zigzag. The bug is invisible on a straight corridor and obvious anywhere
                // the route turns.
                for (int i = 0; i < path.NodesLeftCount; i++)
                {
                    into.Add(path.Peek(i));
                }
            }
        }
    }

    /// <summary>
    /// THE ACTIVE PATH LINE, lifted into the viewed band.
    ///
    /// Reimplemented rather than postfixed for the same reason the job lines were: the
    /// geometry is a chain of segments and individual endpoints cannot be corrected after
    /// GenDraw has already issued them.
    ///
    /// \u26a0 THIS ALSO DRAWS THE CONTINUATION. Hanging it here rather than on
    /// `Pawn.DrawExtraSelectionOverlays` is deliberate: DrawPath is called from exactly one
    /// place, guarded by `IsPlayerControlled`, and already owns "this is the route" as a
    /// concept. Putting the far side anywhere else would give us two owners of one line.
    /// </summary>
    [HarmonyPatch(typeof(PawnPath), nameof(PawnPath.DrawPath))]
    public static class Patch_PawnPath_ABLiftPathLine
    {
        private static bool Prefix(PawnPath __instance, Pawn pathingPawn)
        {
            Map map;
            ABBandMap bands;
            try
            {
                if (pathingPawn == null || !pathingPawn.Spawned)
                {
                    return true;
                }
                map = pathingPawn.Map;
                bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true; // ordinary map: vanilla draws it
                }
            }
            catch
            {
                return true;
            }

            try
            {
                int viewBand = ABBandView.CurrentBand(map);
                float alt = AltitudeLayer.Item.AltitudeFor();
                if (__instance.Found && __instance.NodesLeftCount > 0)
                {
                    for (int i = 0; i < __instance.NodesLeftCount - 1; i++)
                    {
                        GenDraw.DrawLineBetween(
                            ABUIGeometry.LiftToView(bands, viewBand, __instance.Peek(i), alt),
                            ABUIGeometry.LiftToView(bands, viewBand, __instance.Peek(i + 1), alt));
                    }
                    Vector3 drawPos = ABUIGeometry.LiftToView(bands, viewBand, pathingPawn.DrawPos);
                    drawPos.y = alt;
                    Vector3 first = ABUIGeometry.LiftToView(bands, viewBand, __instance.Peek(0), alt);
                    if ((drawPos - first).sqrMagnitude > 0.01f)
                    {
                        GenDraw.DrawLineBetween(drawPos, first);
                    }
                }
                ABTransitVisuals.DrawRemainingRoute(pathingPawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: path line draw threw: " + e, 762195935);
            }
            return false;
        }
    }
}
