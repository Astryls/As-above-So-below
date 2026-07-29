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
            int slot = bands.Slot;
            // The strip of the band BELOW that is currently under the camera.
            CellRect belowView = cam.CurrentViewRect.MovedBy(new IntVec3(0, 0, -slot));
            belowView.ClipInsideMap(map);
            TerrainDef air = ABDefOf.AB_OpenAir;
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
                if (!belowView.Contains(pos))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - outside translated view rect " + belowView);
                    continue;
                }
                if (fog.IsFogged(pos))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos + " - fogged");
                    continue;
                }
                IntVec3 above = new IntVec3(pos.x, 0, pos.z + slot);
                if (!above.InBounds(map) || bands.InGutter(above))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - cell above out of bounds / in gutter");
                    continue;
                }
                if (!ABBands.ShowsBelow(terrain.TerrainAt(above)))
                {
                    if (probing) report.AppendLine("  SKIP " + p.LabelShortCap + " " + pos
                        + " - covered from above by " + terrain.TerrainAt(above).defName);
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
                    loc.z += slot;

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
                    BelowDrawOffsetZ = slot;
                    try
                    {
                        p.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                    }
                    finally
                    {
                        // Cleared in a finally so a throw mid-draw cannot leave every pawn on
                        // the map rendering a band too high.
                        BelowDrawOffsetZ = 0f;
                    }
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 below pawn draw failed for "
                        + p.LabelShortCap + ": " + e.Message, p.thingIDNumber ^ 762195872);
                }
            }

            DrawBelowRealtimeThings(map, belowView, slot, terrain, fog, air);

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
        /// </summary>
        private static void DrawBelowRealtimeThings(Map map, CellRect belowView, int slot,
            TerrainGrid terrain, FogGrid fog, TerrainDef air)
        {
            IReadOnlyList<Thing> things = map.dynamicDrawManager?.DrawThings;
            if (things == null)
            {
                return;
            }
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || !t.Spawned || t is Pawn)
                {
                    continue; // pawns already handled above, with their bed-pose special case
                }
                IntVec3 pos = t.Position;
                if (!belowView.Contains(pos) || fog.IsFogged(pos))
                {
                    continue;
                }
                IntVec3 above = new IntVec3(pos.x, 0, pos.z + slot);
                if (!above.InBounds(map) || !ABBands.ShowsBelow(terrain.TerrainAt(above)))
                {
                    continue; // roofed or capped from above, exactly like the mesh layer
                }
                try
                {
                    Vector3 loc = t.DrawPos;
                    loc.z += slot;
                    t.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                    t.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                    t.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 below realtime draw failed for "
                        + t.LabelCap + ": " + e.Message, t.thingIDNumber ^ 762195874);
                }
            }
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
                ABBelowDynamicDraw.DrawBelowPawns(MapRef(__instance));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: below pawn pass threw: " + e, 762195873);
            }
        }
    }
}
