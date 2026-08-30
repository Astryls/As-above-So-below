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
            // Ask the SHARED resolver what this column actually shows, rather than checking
            // only the view-band cell. The old test passed as soon as the cell directly
            // overhead was see-through, without caring whether the levels in between were -
            // and it translated by a fixed band delta, so with levels stacked the label of a
            // thing hidden behind an intervening floor could still be drawn, while a thing
            // genuinely visible two levels down resolved to the wrong place. Requiring the
            // descent to LAND on this very cell makes label placement agree with the
            // renderer by construction.
            IntVec3 above = bands.Translate(cell, viewBand);
            if (!above.InBounds(map)
                || !ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 seen, out int drop)
                || seen.x != cell.x || seen.z != cell.z)
            {
                return false;
            }
            localized = new Vector3(world.x, world.y, world.z + drop);
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
                // Fog gate: the loop below fog-rejects per thing, so a fully fogged
                // below stack makes this walk of the whole overlay lister group a no-op.
                if (!bands.AnyUnfoggedBelow(viewBand))
                {
                    return;
                }
                long perfT0 = ABPerfStats.Now();
                // Test the COLUMN, not one band's rect. Offsetting the view rect by a single
                // Slot only ever found things exactly one level down, so from level 2 upward
                // no below overlay was offered a draw at all - stack counts, forbidden
                // markers and pawn labels all silently vanished while the content itself
                // rendered fine.
                CellRect view = Find.CameraDriver.CurrentViewRect;
                FogGrid fog = map.fogGrid;
                // Toggleable Overlays swaps this very group out from under vanilla's own loop
                // at any zoom above Closest, so asking the bridge keeps the level below culled
                // in step with the level you are standing on instead of showing labels the
                // current level has already dropped. Plain HasGUIOverlay when TO is absent.
                List<Thing> list = map.listerThings.ThingsInGroup(
                    ToggleableOverlaysCompat.BelowOverlayGroup);
                // ⚠ EVERY Toggleable Overlays gate is "is the cursor on this thing's cell",
                // measured in VIEW-band coordinates - which a below thing's cell can never
                // equal, so without this its overlays are unreachable by hover. Aim their
                // mouse at the cell this column actually shows for the length of the pass.
                ToggleableOverlaysCompat.PushBelowMouse(map, bands, viewBand);
                try
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        Thing thing = list[i];
                        IntVec3 pos = thing.Position;
                        if (bands.BandOf(pos) >= viewBand || fog.IsFogged(pos))
                        {
                            continue;
                        }
                        IntVec3 above = bands.Translate(pos, viewBand);
                        if (!above.InBounds(map) || !view.Contains(above))
                        {
                            continue; // off screen
                        }
                        if (!ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 seen, out _)
                            || seen.x != pos.x || seen.z != pos.z)
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
                finally
                {
                    // Non-negotiable: a mouse left parked on a below cell would hide every
                    // overlay on the CURRENT level until the cursor moved.
                    ToggleableOverlaysCompat.PopMouse();
                }
                ABPerfStats.NoteOverlay(list.Count, ABPerfStats.Now() - perfT0);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below overlays");
            }
        }
    }
}
