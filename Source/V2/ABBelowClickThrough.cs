using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 click-through: right-clicking something you can SEE through open air orders
    /// against that thing, not against the empty sky cell in front of it.
    ///
    /// The see-below renderer already draws the band underneath through every open-air
    /// cell, so the player is looking straight at the surface - but a click resolves to the
    /// SKY cell, which is empty air. Ordering anything down there was impossible, which is
    /// what made drafted commands feel unlike V1.
    ///
    /// The interception is a single one: FloatMenuMakerMap.GetOptions takes the raw click
    /// position, and everything downstream (move, attack, haul, every provider) reads the
    /// cell from the FloatMenuContext built out of it. Translating the click there means
    /// every order type follows automatically, with no per-order patching.
    ///
    /// Only cells that are genuinely see-through translate: the cell's own terrain must be
    /// AB_OpenAir and the band below unfogged. Rooftops, mountain caps and anything else
    /// opaque keep their normal click behaviour.
    /// </summary>
    public static class ABBelowClickThrough
    {
        /// <summary>Translate a click position down one band when the player is looking
        /// through open air. Returns false when the click should be left alone.</summary>
        public static bool TryTranslate(Map map, Vector3 clickPos, out Vector3 translated)
        {
            translated = clickPos;
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (!cell.InBounds(map) || bands.BandOf(cell) <= 0 || bands.InGutter(cell))
            {
                return false;
            }
            if (!ABBands.ShowsBelow(map.terrainGrid.TerrainAt(cell)))
            {
                return false; // opaque from here; the click belongs to this band
            }
            IntVec3 below = new IntVec3(cell.x, 0, cell.z - bands.Slot);
            if (!below.InBounds(map) || bands.InGutter(below) || map.fogGrid.IsFogged(below))
            {
                return false; // nothing legible down there to click
            }
            translated = new Vector3(clickPos.x, clickPos.y, clickPos.z - bands.Slot);
            return true;
        }
    }

    /// <summary>
    /// The single interception. Every right-click order for every selected pawn is built
    /// from this click position.
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    public static class Patch_FloatMenuMakerMap_ABClickThrough
    {
        private static void Prefix(ref Vector3 clickPos)
        {
            try
            {
                if (ABBelowClickThrough.TryTranslate(Find.CurrentMap, clickPos, out Vector3 t))
                {
                    clickPos = t;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 click-through");
            }
        }
    }

    /// <summary>
    /// Left-click selection through open air, so a pawn or building you can see below can
    /// actually be selected. Mirrors the right-click rule exactly.
    /// </summary>
    [HarmonyPatch(typeof(Selector), "SelectableObjectsUnderMouse")]
    public static class Patch_Selector_ABSelectThrough
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(Selector), "SelectableObjectsUnderMouse") != null;
        }

        private static void Postfix(ref System.Collections.Generic.IEnumerable<object> __result)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || __result == null)
                {
                    return;
                }
                Vector3 mouse = UI.MouseMapPosition();
                if (!ABBelowClickThrough.TryTranslate(map, mouse, out Vector3 t))
                {
                    return;
                }
                IntVec3 belowCell = IntVec3.FromVector3(t);
                if (!belowCell.InBounds(map))
                {
                    return;
                }
                System.Collections.Generic.List<object> extra =
                    new System.Collections.Generic.List<object>(__result);
                System.Collections.Generic.List<Thing> things = map.thingGrid.ThingsListAtFast(belowCell);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing th = things[i];
                    if (th != null && th.def.selectable && !extra.Contains(th))
                    {
                        extra.Add(th);
                    }
                }
                __result = extra;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 select-through");
            }
        }
    }
}
