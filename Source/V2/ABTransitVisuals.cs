using System;
using System.Collections.Generic;
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
            public IntVec3 anchor = IntVec3.Invalid;
            public IntVec3 dest = IntVec3.Invalid;
            public int computedTick = -99999;
        }

        private static readonly Dictionary<int, Route> routes = new Dictionary<int, Route>();

        private static readonly List<IntVec3> tmpNodes = new List<IntVec3>();

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                routes.Remove(p.thingIDNumber);
            }
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
            if (!ABWormholePather.TryGetPending(pawn, out IntVec3 nearCell, out IntVec3 farCell,
                    out LocalTargetInfo realDest))
            {
                Clear(pawn);
                return;
            }
            Route r = Resolve(pawn, map, nearCell, farCell, realDest);
            if (r == null || r.nodes.Count < 2)
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
                IntVec3 a = r.nodes[i];
                IntVec3 b = r.nodes[i + 1];
                GenDraw.DrawLineBetween(
                    ABUIGeometry.LiftToView(bands, viewBand, a, alt),
                    ABUIGeometry.LiftToView(bands, viewBand, b, alt));
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
            try
            {
                Build(pawn, map, nearCell, farCell, realDest, r.nodes);
            }
            catch (Exception e)
            {
                r.nodes.Clear();
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
            LocalTargetInfo realDest, List<IntVec3> into)
        {
            into.Add(nearCell);
            IntVec3 cur = farCell;
            into.Add(cur);

            IntVec3 destCell = realDest.Cell;
            int hops = 0;
            while (destCell.IsValid && !ABBands.SameBand(map, cur, destCell) && hops++ < MaxHops)
            {
                if (!ABWormhole.TryGetTransit(map, cur, destCell,
                        out Building_Door nextNear, out Building_Door nextFar)
                    || nextNear == null || nextFar == null)
                {
                    return; // no further route known; stop where we are
                }
                AppendLeg(pawn, map, cur, nextNear.Position, PathEndMode.OnCell, into);
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
                tmpNodes.Clear();
                for (int i = path.NodesLeftCount - 1; i >= 0; i--)
                {
                    tmpNodes.Add(path.Peek(i));
                }
                for (int i = 0; i < tmpNodes.Count; i++)
                {
                    into.Add(tmpNodes[i]);
                }
                tmpNodes.Clear();
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
