using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 see-below, dynamic half: pawns.
    ///
    /// Items, buildings, plants and terrain all live in the map mesh, so
    /// SectionLayer_ABBelowV2 already carries them. PAWNS do not - they are
    /// realtime-drawn every frame by DynamicDrawManager, which culls to the camera's
    /// view rect. Since the camera is clamped to the current band, a pawn one band down
    /// is simply off-screen and never drawn at all.
    ///
    /// This is the single place where V2's "same map" property pays off most bluntly.
    /// V1 could not do this without lying about positions - hence DrawPosOffsetPatcher,
    /// hundreds of DrawPos getters patched on ParallelPreDraw worker threads. Here the
    /// pawn is already on this map; it just needs drawing somewhere else. Thing exposes
    /// exactly that: DrawNowAt(loc). No patching of any getter, no position mutation, no
    /// worker-thread hazard - one call with an offset vector.
    ///
    /// Masking matches the mesh layer: a pawn shows only through a cell that is open air
    /// on this band and unfogged below, so roofs and mountain caps stay opaque.
    /// </summary>
    public static class ABBelowDynamicDraw
    {
        /// <summary>Drawn per frame, so keep the scan tight: only pawns whose cell is
        /// inside the translated view rect are considered.</summary>
        /// <summary>Set by the "AB2: below pawn report" dev action for ONE pass, so the
        /// per-pawn verdict is captured without spamming every frame.</summary>
        public static bool ReportNextPass;

        /// <summary>Band offset armed ONLY around a single below-pawn draw call, read by
        /// Patch_PawnRenderer_ABBelowBodyPos. Non-zero exclusively inside that call.</summary>
        public static float BelowDrawOffsetZ;

        /// <summary>Depth-falloff scale armed around a single below-pawn draw call, read by
        /// Patch_PawnRenderer_ABBelowShrink. Exactly the same arm/disarm discipline as
        /// BelowDrawOffsetZ: 1 outside the pass, so nothing else in the game can observe it.
        /// Kept separate from the offset because a pawn is always translated but is only
        /// shrunk when the setting is on, and the two must disarm independently.</summary>
        public static float BelowDrawScale = 1f;

        /// <summary>Armed ONLY around the three draw phases of ONE below realtime THING
        /// (never pawns). Read by Patch_Graphic_ABBelowLegacyCompDraw, which translates
        /// legacy position-blind comp overlay draws - CompFireOverlay's campfire flame
        /// above all - into the view band. Zero outside the window, same arm/disarm
        /// discipline as BelowDrawOffsetZ next door (§95 Tier E).</summary>
        public static float RealtimeDropZ;

        /// <summary>The raw source-band z of the thing currently being drawn, for the
        /// discrimination test in the Graphic.Draw patch: a call still NEAR this z is a
        /// legacy comp reading parent.DrawPos; a call already a Slot away is the thing's
        /// own graphic receiving our translated loc. Bands are >= a Slot apart, so the
        /// 8-cell tolerance can never confuse the two.</summary>
        public static float RealtimeRawZ;

        private static readonly System.Text.StringBuilder report = new System.Text.StringBuilder();

        public static void DrawBelowPawns(Map map)
        {
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            CameraDriver cam = Find.CameraDriver;
            if (cam == null)
            {
                return;
            }
            // Nothing below the bottom band. Without this the whole pass still ran on the
            // basement every frame - building a view rect, then walking every spawned pawn
            // AND every realtime drawable only for each one to fail the bounds test. Pure
            // waste, and it scaled with colony size on the one band that can never show
            // anything beneath it.
            if (!bands.BandExists(ABBandView.CurrentBand(map) - 1))
            {
                return;
            }
            int slot = bands.Slot;
            int viewBand = ABBandView.CurrentBand(map);
            // Fog gate: every rejection below includes a per-thing fog test, so when
            // every band under the view is fully fogged (the undug-basement common case)
            // this whole pass - and the realtime pass it ends with - provably draws
            // nothing. Skip before walking any list. Verdicts are event-driven
            // (MapEvents.CellFogChanged/MapFogged) and fail open.
            if (!bands.AnyUnfoggedBelow(viewBand))
            {
                return;
            }
            // Perf sampling starts AFTER the cheap early-outs above, so the counters
            // describe frames where a below view actually exists.
            long perfT0 = ABPerfStats.Now();
            int perfConsidered = 0;
            int perfDrawn = 0;
            // Refills the per-frame budget for first-touch atlas bakes. Must happen once
            // per pass, not once per pawn, or the budget stops bounding anything.
            ABBelowRenderCache.BeginFrame();
            CellRect camView = cam.CurrentViewRect;
            TerrainGrid terrain = map.terrainGrid;
            FogGrid fog = map.fogGrid;

            // IReadOnlyList, walked by index: no enumerator boxing on a per-frame path.
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || !p.Spawned)
                {
                    continue;
                }
                IntVec3 pos = p.Position;

                // Every rejection is recorded with its REASON when probing. A pawn that is
                // filtered out before the draw call and a pawn that draws but is then
                // occluded look IDENTICAL on screen, and they need opposite fixes - one is a
                // masking bug, the other a draw-order bug. Guessing between them has already
                // cost one wrong fix (running the pawn through all three DynamicDrawPhases,
                // which was a real staleness bug but not this one).
                bool probing = ReportNextPass;
                // §73: a pawn in its transit-ghost window is drawn by ABStairAnim's own
                // pass at the origin mouth. Drawing it here too - shrunk, through the
                // stair opening it just used - would be a second copy of the same pawn.
                if (ABStairAnim.IsGhosting(p))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - transit ghost (drawn by the clip pass)");
                    continue;
                }
                // ANY band below the view, not just the one directly beneath.
                //
                // The candidate rect used to be the view shifted down exactly one Slot, so a
                // pawn two levels down was never even considered - "pawns disappear on floor
                // 2 and stay hidden on floor 3". The column is now tested against the view
                // rect in the VIEWING band and visibility is resolved with the shared
                // descent rule, so the two agree by construction.
                int pawnBand = bands.BandOf(pos);
                if (pawnBand < 0 || pawnBand >= viewBand)
                {
                    continue; // same band or above: vanilla draws it
                }
                perfConsidered++;
                IntVec3 above = bands.Translate(pos, viewBand);
                if (!camView.Contains(above))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - column outside the view rect " + camView);
                    continue;
                }
                if (fog.IsFogged(pos))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos + " - fogged");
                    continue;
                }
                if (!above.InBounds(map) || bands.InGutter(above))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - cell above out of bounds / in gutter");
                    continue;
                }
                if (!ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 seen, out int drop)
                    || seen.x != pos.x || seen.z != pos.z)
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - not what this column shows (covered by "
                        + terrain.TerrainAt(above).defName + ")");
                    continue;
                }
                if (probing)
                {
                    report.AppendLine("  DRAW " + p.LabelShortCap + " " + pos
                        + " posture=" + p.GetPosture()
                        + " inBed=" + (p.CurrentBed() != null)
                        + " drawPos.y=" + p.DrawPos.y.ToString("0.000")
                        + " job=" + (p.CurJob?.def?.defName ?? "none"));
                }
                try
                {
                    Vector3 loc = p.DrawPos;
                    loc.z += drop;
                    // The same depth cue the printed pass applies: shrink by how many levels
                    // down this pawn is. Position is untouched - the pawn stays plumb over
                    // its own cell, which is what lets a click land where the sprite is.
                    float shrink = ABDepthView.ScaleForLevels(slot > 0 ? drop / slot : 1);

                    // Run the SAME three phases vanilla runs for a visible pawn, at our
                    // translated location - do not just call DrawNowAt.
                    //
                    // DrawNowAt only issues DrawPhase.Draw, and RenderPawnAt recomputes
                    // ONLY when `!results.valid`. A below pawn is culled from the camera's
                    // view rect, so DynamicDrawManager never gives it EnsureInitialized or
                    // ParallelPreDraw - yet its results stay flagged valid from whenever it
                    // was last genuinely on screen. Anything that changes its appearance
                    // while culled is therefore never picked up: the pawn keeps rendering in
                    // its old pose. Lying down to sleep is the visible case (a sleeping pawn
                    // simply never appeared from above), but the same staleness applies to
                    // rotation, apparel and carried things.
                    //
                    // Main thread, called serially: this is the safe way to invoke
                    // ParallelPreDraw - the thread hazard is in postfixing what the job
                    // workers call, not in calling it here.
                    // Armed across all three phases: the bed branch of GetBodyPos is reached
                    // from ParallelPreDraw as well as Draw, so arming only the draw call
                    // leaves the cached results holding the untranslated position.
                    BelowDrawOffsetZ = drop;
                    BelowDrawScale = shrink;
                    // Decides whether THIS pawn may render from the vanilla atlas blit
                    // instead of walking its whole render tree. Must be armed before the
                    // phases, because the decision is consumed inside ParallelPreDraw.
                    ABBelowRenderCache.BeginPawn(p, shrink);
                    try
                    {
                        p.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                    }
                    finally
                    {
                        // Cleared in a finally so a throw mid-draw cannot leave every pawn on
                        // the map rendering a band too high, or every pawn in the colony
                        // rendering at 85%, or the whole game's pawns pinned to one cache
                        // verdict.
                        BelowDrawOffsetZ = 0f;
                        BelowDrawScale = 1f;
                        ABBelowRenderCache.EndPawn();
                    }
                    perfDrawn++;
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 below pawn draw failed for "
                        + p.LabelShortCap + ": " + e.Message, p.thingIDNumber ^ 762195872);
                }
            }

            int perfRealtime = DrawBelowRealtimeThings(map, bands, camView, viewBand, fog);
            ABPerfStats.NoteBelowPass(perfConsidered, perfDrawn, perfRealtime,
                ABPerfStats.Now() - perfT0);

            if (ReportNextPass)
            {
                ReportNextPass = false;
                Log.Warning(ABLog.Tag + " V2 below pawn report (view band "
                    + ABBandView.CurrentBand(map) + ", slot " + slot + ", "
                    + pawns.Count + " pawns on map):\n"
                    + (report.Length == 0 ? "  (no pawns considered)" : report.ToString()));
                report.Length = 0;
            }
        }

        /// <summary>
        /// Non-pawn REALTIME things: construction frames above all.
        ///
        /// SectionLayer_ABBelowV2 carries the level below by printing things into the map
        /// mesh, and it deliberately skips anything realtime:
        ///     if (drawer != MapMeshOnly &amp;&amp; drawer != MapMeshAndRealTime) continue;
        /// because realtime things simply are not in that mesh. Until now the only realtime
        /// things drawn from above were pawns, so everything else in that category was
        /// invisible from the level above:
        ///   * FRAMES are DrawerType.RealtimeOnly (ThingDefGenerator_Buildings.BaseFrameDef),
        ///     so anything under construction vanished when viewed from the sky.
        ///   * DOOR blueprints are forced RealtimeOnly too, so those disappeared while every
        ///     other blueprint (which inherits its target's drawer type) showed fine - which
        ///     is what made the bug look arbitrary.
        ///   * Projectiles are in this category as well, so cross-band shots were never
        ///     drawn either. Same root cause, closed by the same loop.
        ///
        /// Source is DynamicDrawManager.DrawThings - vanilla's own registry of realtime
        /// drawables. That is exactly the complement of what the mesh layer carries, so the
        /// two passes tile the whole map with no overlap and no gaps. Sweeping cells with
        /// ThingsListAtFast instead would repeat the OverlayDrawer mistake that cost
        /// 1.4 ms/frame.
        ///
        /// ⚠ MULTI-BAND SINCE WINDOW 4d. This pass was the documented last single-band
        /// corner of the below view (a strip exactly one Slot down), so fire, motes and
        /// construction frames two or more levels down were invisible while the pawns and
        /// projectiles beside them drew. It now resolves each thing's column with the SAME
        /// shared descent rule as the pawn loop above - the two loops finally agree, and the
        /// "one `- Slot` step" class of bug loses its last host in this file.
        /// </summary>
        private static int DrawBelowRealtimeThings(Map map, ABBandMap bands, CellRect camView,
            int viewBand, FogGrid fog)
        {
            IReadOnlyList<Thing> things = map.dynamicDrawManager?.DrawThings;
            if (things == null)
            {
                return 0;
            }
            int drawn = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || !t.Spawned || t is Pawn)
                {
                    continue; // pawns already handled above, with their bed-pose special case
                }
                // ⚠ CROSS-BAND PROJECTILES HAVE EXACTLY ONE OWNER, AND IT IS NOT THIS PASS.
                // ABCombatRelay draws every registered cross-band round depth-correctly; this
                // pass would draw a second copy on top. No isinst gate any more: Combat
                // Extended's rounds are ThingWithComps, not Verse.Projectile, so a type test
                // here would re-open the double-draw exactly for CE. Handles() fast-exits on
                // an empty registry, which is the common case.
                if (ABCombatRelay.Handles(t))
                {
                    continue;
                }
                IntVec3 pos = t.Position;
                int band = bands.BandOf(pos);
                if (band >= viewBand)
                {
                    continue; // same band or above: vanilla's job (or nothing's)
                }
                IntVec3 above = bands.Translate(pos, viewBand);
                if (!camView.Contains(above))
                {
                    continue; // the cheap screen gate, before any terrain is touched
                }
                if (fog.IsFogged(pos) || bands.InGutter(above))
                {
                    continue;
                }
                // The SAME shared descent rule as pawns: is this thing's cell what the
                // column actually shows from the view band? Multi-level for free, and roofs
                // and caps refuse exactly like the mesh layer.
                if (!ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 seen,
                        out int dropRt)
                    || seen.x != pos.x || seen.z != pos.z)
                {
                    continue;
                }
                try
                {
                    Vector3 loc = t.DrawPos;
                    loc.z += dropRt;
                    // ⚠ NO SHRINK ON THIS PATH. A realtime thing is drawn by its own Graphic
                    // through DynamicDrawPhaseAt, which takes a position and nothing else -
                    // unlike a pawn, whose renderer funnels every draw through
                    // PawnDrawParms.matrix (that single funnel is the whole reason the pawn
                    // shrink is two small patches instead of a per-piece hunt). Construction
                    // frames and projectiles one level down therefore keep full size while
                    // the pawns beside them shrink. Accepted for now: this pass is already
                    // documented as the last single-band corner of the below view (§5), and
                    // both gaps want the same fix.
                    // All three phases, unconditionally. A phase-skipping optimisation was
                    // tried here and REVERTED: it caused a visible regression and was never
                    // worth it - it removed two virtual calls that immediately return and
                    // replaced them with a dictionary lookup, so the expected saving was
                    // approximately nothing. Below things are culled from the camera's view
                    // rect, so vanilla never gives them EnsureInitialized or ParallelPreDraw
                    // while their cached render results stay flagged valid; skipping either
                    // phase is how a below thing ends up drawn stale. Do not re-attempt
                    // without a measured reason.
                    // §95 Tier E: legacy comps (CompFireOverlay et al) draw their overlay
                    // at parent.DrawPos, ignoring our loc - the campfire's flame landed on
                    // the source band, off-camera. Arm the translation window for the
                    // Graphic.Draw patch; cleared in a finally so a throw cannot leave
                    // every later Graphic.Draw on the map translated a band down.
                    RealtimeDropZ = dropRt;
                    RealtimeRawZ = t.DrawPos.z;
                    try
                    {
                        t.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                        t.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                        t.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                    }
                    finally
                    {
                        RealtimeDropZ = 0f;
                        RealtimeRawZ = 0f;
                    }
                    drawn++;
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 below realtime draw failed for "
                        + t.LabelCap + ": " + e.Message, t.thingIDNumber ^ 762195874);
                }
            }
            return drawn;
        }
    }

    /// <summary>
    /// Runs straight after vanilla's own dynamic pass, so below pawns compose on top of
    /// the below mesh but under anything this band draws afterwards.
    /// </summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    public static class Patch_DynamicDrawManager_ABBelowPawns
    {
        private static readonly AccessTools.FieldRef<DynamicDrawManager, Map> MapRef =
            AccessTools.FieldRefAccess<DynamicDrawManager, Map>("map");

        private static void Postfix(DynamicDrawManager __instance)
        {
            try
            {
                Map map = MapRef(__instance);
                ABBelowDynamicDraw.DrawBelowPawns(map);
                // §73 transit ghosts: entry clips drawn at the origin mouth, after the
                // below pass so they compose above it like any same-band pawn would.
                ABStairAnim.DrawGhosts(map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: below pawn pass threw: " + e, 762195873);
            }
        }
    }
}
