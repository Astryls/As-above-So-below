using System;
using System.Collections.Generic;
using HarmonyLib;
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
                if (!belowView.Contains(pos) || fog.IsFogged(pos))
                {
                    continue;
                }
                IntVec3 above = new IntVec3(pos.x, 0, pos.z + slot);
                if (!above.InBounds(map) || bands.InGutter(above))
                {
                    continue;
                }
                if (terrain.TerrainAt(above) != air)
                {
                    continue; // covered from up here
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
                    p.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                    p.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                    p.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 below pawn draw failed for "
                        + p.LabelShortCap + ": " + e.Message, p.thingIDNumber ^ 762195872);
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
