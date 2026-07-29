using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 labels and overlays for the band below.
    ///
    /// The see-below renderer draws the surface through open air, and click-through lets it
    /// be ordered around - but it had no LABELS. Pawn names, item stack counts, forbidden
    /// markers and every other GUI overlay were missing, because vanilla only draws overlays
    /// for things inside the camera's view rect and a thing one band down is 256 cells
    /// outside it.
    ///
    /// Two halves:
    ///  - ThingOverlays only ITERATES things in view, so below things are never offered the
    ///    chance to draw. Extended here to also walk the translated rect.
    ///  - GenMapUI.LabelDrawPosFor computes the screen position from the thing's REAL
    ///    DrawPos, so even when asked to draw, a below thing's label lands hundreds of cells
    ///    off screen. Localized against the CURRENT VIEW BAND.
    ///
    /// The localization is a pure function of view state - no ambient "am I drawing below"
    /// latch - so it behaves identically no matter who calls it.
    /// </summary>
    public static class ABBelowOverlays
    {
        /// <summary>Shift a world position up into the current view band when it belongs to
        /// a band below and is visible through open air.</summary>
        public static bool TryLocalizeToView(Map map, Vector3 world, out Vector3 localized)
        {
            localized = world;
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int viewBand = ABBandView.CurrentBand(map);
            IntVec3 cell = world.ToIntVec3();
            if (!cell.InBounds(map))
            {
                return false;
            }
            int thingBand = bands.BandOf(cell);
            if (thingBand >= viewBand)
            {
                return false; // same band, or above us: not seen through the floor
            }
            // Only through a genuine hole, matching the renderer's own mask.
            IntVec3 above = bands.Translate(cell, viewBand);
            if (!above.InBounds(map) || !ABBands.ShowsBelow(map.terrainGrid.TerrainAt(above)))
            {
                return false;
            }
            localized = new Vector3(world.x, world.y, world.z + (viewBand - thingBand) * bands.Slot);
            return true;
        }
    }

    /// <summary>
    /// Label placement. Applies to every caller of LabelDrawPosFor - pawn names, stack
    /// counts, mod-added labels - so nothing needs per-overlay patching.
    /// </summary>
    [HarmonyPatch(typeof(GenMapUI), nameof(GenMapUI.LabelDrawPosFor), new Type[] { typeof(Thing), typeof(float) })]
    public static class Patch_GenMapUI_ABLabelPos
    {
        private static bool Prefix(Thing thing, float worldOffsetZ, ref Vector2 __result)
        {
            try
            {
                if (thing == null || !thing.Spawned)
                {
                    return true;
                }
                if (!ABBelowOverlays.TryLocalizeToView(thing.Map, thing.DrawPos, out Vector3 local))
                {
                    return true;
                }
                local.z += worldOffsetZ;
                Vector2 v = Find.Camera.WorldToScreenPoint(local) / Prefs.UIScale;
                v.y = UI.screenHeight - v.y;
                if (thing is Pawn pawn && !pawn.RaceProps.Humanlike)
                {
                    v.y -= 4f;
                }
                __result = v;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Offer below things the chance to draw at all. Vanilla iterates only the camera view
    /// rect; this adds the same pass over the translated rect one band down.
    /// </summary>
    [HarmonyPatch(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI))]
    public static class Patch_ThingOverlays_ABBelow
    {
        private static void Postfix()
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            try
            {
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                int viewBand = ABBandView.CurrentBand(map);
                if (viewBand <= 0)
                {
                    return;
                }
                CellRect below = Find.CameraDriver.CurrentViewRect
                    .MovedBy(new IntVec3(0, 0, -bands.Slot));
                below.ClipInsideMap(map);
                FogGrid fog = map.fogGrid;
                TerrainGrid terrain = map.terrainGrid;
                List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.HasGUIOverlay);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing thing = list[i];
                    IntVec3 pos = thing.Position;
                    if (!below.Contains(pos) || fog.IsFogged(pos))
                    {
                        continue;
                    }
                    IntVec3 above = bands.Translate(pos, viewBand);
                    if (!above.InBounds(map) || !ABBands.ShowsBelow(terrain.TerrainAt(above)))
                    {
                        continue; // not visible from up here
                    }
                    try
                    {
                        thing.DrawGUIOverlay();
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorOnce(ABLog.Tag + " V2 below overlay for " + thing.LabelCap
                            + ": " + ex.Message, thing.thingIDNumber ^ 762195882);
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below overlays");
            }
        }
    }
}
