using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 multi-select through the floor: drag-boxes and double-click-select-all.
    ///
    /// Both funnel through ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct,
    /// which converts the screen rect to a MAP rect and walks its cells. On a banded map
    /// that rect only ever covers the band being viewed, so items and pawns plainly visible
    /// through open air were never candidates - drag-select found nothing and double-click
    /// selected only the one thing under the cursor.
    ///
    /// Patching that single utility covers both gestures, since the selector reaches them
    /// through the same call.
    /// </summary>
    [HarmonyPatch(typeof(ThingSelectionUtility),
        nameof(ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct))]
    public static class Patch_ThingSelectionUtility_ABBelow
    {
        /// <summary>Dev A/B switch: when off, the selection scan is exactly vanilla's.</summary>
        internal static bool Enabled = true;

        private static void Postfix(Rect rect, ref IEnumerable<Thing> __result)
        {
            try
            {
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                int viewBand = ABBandView.CurrentBand(map);
                // No early-out on the bottom band any more: the below-scan has nothing to
                // add there, but the IN-BAND FILTER below must still run - the drag rect
                // can overhang the band edge (panMargin, wide zoom), where vanilla's own
                // scan happily gathers the neighbouring band's things out of the curtain.
                __result = WithBelow(__result, map, bands, viewBand, rect);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below multi-select");
            }
        }

        /// <summary>Lazy so the extra scan only costs anything if the caller actually
        /// enumerates, and so an exception mid-scan cannot break the selector.</summary>
        private static IEnumerable<Thing> WithBelow(IEnumerable<Thing> original, Map map,
            ABBandMap bands, int viewBand, Rect screenRect)
        {
            HashSet<Thing> seen = new HashSet<Thing>();
            foreach (Thing t in original)
            {
                // SELECTABLE IS DRAWABLE, the same rule as the click patch: vanilla walks
                // the whole screen-derived map rect, and where that rect overhangs the band
                // edge its cells are the undrawn neighbouring band. A drag box must not
                // harvest what the player cannot see. (The see-below additions are exempt
                // by construction - they are appended after this loop.)
                if (t != null && bands.BandOf(t.PositionHeld) != viewBand)
                {
                    continue;
                }
                seen.Add(t);
                yield return t;
            }
            // Dev A/B switch ("AB2: bisect - toggle below multi-select"). Gates only the
            // below-ADDITIONS: disabling it degrades to vanilla minus the void harvest.
            // The in-band filter above is a correctness clamp, not part of the feature
            // being bisected, so it stays on either way.
            if (!Enabled)
            {
                yield break;
            }
            // Nothing exists below the bottom band.
            if (viewBand <= 0)
            {
                yield break;
            }

            // Walk the drag rect in the VIEWING band and descend per cell, instead of
            // shifting the rect down a single Slot - which could only ever drag-select the
            // level immediately below, so from level 2 upward the box selected nothing.
            CellRect mapRect = ABScreenRect.GetMapRect(screenRect);
            mapRect.ClipInsideMap(map);
            FogGrid fog = map.fogGrid;

            foreach (IntVec3 above in mapRect)
            {
                if (!above.InBounds(map))
                {
                    continue;
                }
                // Same see-through rule as every other below interaction.
                if (!ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 c, out _)
                    || fog.IsFogged(c))
                {
                    continue;
                }
                List<Thing> things = map.thingGrid.ThingsListAt(c);
                if (things == null)
                {
                    continue;
                }
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || t.def.neverMultiSelect || !t.def.selectable || !t.Spawned)
                    {
                        continue;
                    }
                    if (seen.Add(t))
                    {
                        yield return t;
                    }
                }
            }
        }
    }

    /// <summary>Screen-rect to map-rect conversion, mirroring ThingSelectionUtility's own
    /// private helper (it is not accessible from here).</summary>
    internal static class ABScreenRect
    {
        internal static CellRect GetMapRect(Rect rect)
        {
            Vector2 min = new Vector2(rect.xMin, UI.screenHeight - rect.yMin);
            Vector2 max = new Vector2(rect.xMax, UI.screenHeight - rect.yMax);
            Vector3 a = UI.UIToMapPosition(min);
            Vector3 b = UI.UIToMapPosition(max);
            return CellRect.FromLimits(
                Mathf.FloorToInt(Mathf.Min(a.x, b.x)),
                Mathf.FloorToInt(Mathf.Min(a.z, b.z)),
                Mathf.FloorToInt(Mathf.Max(a.x, b.x)),
                Mathf.FloorToInt(Mathf.Max(a.z, b.z)));
        }
    }
}
