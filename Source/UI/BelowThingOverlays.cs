using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Item counts and GUI overlays for the see-below view. Vanilla's
    /// ThingOverlays.ThingOverlaysOnGUI draws stack-count numbers (and quality
    /// letters, and any modded thing overlay) only for the CURRENT map - the sky
    /// level, which is open air, so surface items shown through the plumb below
    /// view carried no count. This postfix runs the same pass over the surface
    /// map's HasGUIOverlay things that are visible from above.
    ///
    /// The below view is PLUMB: x/z pass through untouched, so a surface thing's
    /// DrawPos projects to exactly the screen cell it renders on and Thing
    /// .DrawGUIOverlay lands its label in the right place with no transform.
    /// Vanilla only draws these at the closest zoom (Thing.DrawGUIOverlay gates
    /// on CameraZoomRange.Closest), so the same behavior carries over: counts
    /// appear when you zoom in on the surface below.
    ///
    /// Gated on the showLiveBelow render toggle plus the belowItemOverlays
    /// setting and the Ui kill switch; fails open (no overlays) on any throw.
    /// </summary>
    [HarmonyPatch(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI))]
    internal static class Patch_ThingOverlays_BelowOverlays
    {
        private static void Postfix()
        {
            if (Event.current.type != EventType.Repaint || !LevelCensus.AnyLevelColumns
                || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.belowItemOverlays)
            {
                return;
            }
            try
            {
                if (!BelowSelection.TryGetLiveBelowView(out Map sky, out Map lower))
                {
                    return;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ClipInsideMap(lower);
                List<Thing> list = lower.listerThings.ThingsInGroup(ThingRequestGroup.HasGUIOverlay);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing t = list[i];
                    if (t == null)
                    {
                        continue;
                    }
                    IntVec3 pos = t.Position;
                    if (!view.Contains(pos) || !BelowSelection.CellVisibleFromAbove(pos, sky, lower))
                    {
                        continue;
                    }
                    try
                    {
                        t.DrawGUIOverlay();
                    }
                    catch (Exception ex)
                    {
                        Log.ErrorOnce(ABLog.Tag + " below overlay draw failed for "
                            + t.LabelCap + ": " + ex.Message, t.thingIDNumber ^ 0x0B12A7);
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "below thing overlays");
            }
        }
    }
}
