using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Select-in-place for the level below. When looking down from the sky level
    /// through open air, a left click on a pawn visible on the surface selects it
    /// WITHOUT switching maps or moving the camera - you keep the top-down view and
    /// the pawn becomes the live selection (inspect pane, gizmos, orders all follow).
    ///
    /// Two hooks:
    ///  - Selector.SelectUnderMouse (prefix): when nothing on the sky level is under
    ///    the cursor but a below pawn is, we take over and add it to the selection
    ///    directly (vanilla Select would force a map switch + camera jump, defeating
    ///    the whole point). Sky clicks always fall through to vanilla untouched.
    ///  - SelectionDrawer.DrawSelectionBracketFor (prefix/postfix): a below-map
    ///    selected thing's bracket is drawn through the same see-below transform the
    ///    pawn itself renders through, so the highlight lands on what you see. Hidden
    ///    (roofed / fogged) below selections draw no bracket.
    ///
    /// Everything is gated on the selectBelowInPlace + showLiveBelow settings and the
    /// ABGuard.Ui kill switch, and fails open to vanilla behavior.
    /// </summary>
    internal static class BelowSelection
    {
        /// <summary>How close (world cells) the cursor must be to a below pawn's
        /// on-screen center to hit it. A touch more generous than vanilla's tight
        /// 0.4 because below pawns render shrunk (belowThingScale).</summary>
        private const float ClickRadius = 0.75f;

        private static readonly AccessTools.FieldRef<Selector, List<object>> SelectedRef =
            AccessTools.FieldRefAccess<Selector, List<object>>("selected");

        private static readonly MethodInfo PlaySelectionSound =
            AccessTools.Method(typeof(Selector), "PlaySelectionSoundFor");

        /// <summary>Reused between hit-test and sort; cleared each query.</summary>
        private static readonly List<Thing> hitBuffer = new List<Thing>();
        private static readonly Dictionary<Thing, float> hitDist = new Dictionary<Thing, float>();

        /// <summary>True and hands back the sky/lower maps when the current view is
        /// a sky level rendering the surface live below it, and both toggles are on.</summary>
        internal static bool TryGetBelowView(out Map sky, out Map lower)
        {
            sky = null;
            lower = null;
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.selectBelowInPlace || !settings.showLiveBelow)
            {
                return false;
            }
            Map cur = Find.CurrentMap;
            if (cur == null)
            {
                return false;
            }
            LevelComp comp = cur.Levels();
            if (comp == null || comp.level <= 0)
            {
                return false;
            }
            Map below = comp.lowerMap;
            if (below == null || below.Disposed)
            {
                return false;
            }
            sky = cur;
            lower = below;
            return true;
        }

        /// <summary>Lighter gate for read-only overlays (item counts): needs only
        /// the below RENDER toggle, not the selection toggle. Hands back the
        /// sky/lower maps whenever the surface is drawn live under the sky
        /// level.</summary>
        internal static bool TryGetLiveBelowView(out Map sky, out Map lower)
        {
            sky = null;
            lower = null;
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.showLiveBelow)
            {
                return false;
            }
            Map cur = Find.CurrentMap;
            if (cur == null)
            {
                return false;
            }
            LevelComp comp = cur.Levels();
            if (comp == null || comp.level <= 0)
            {
                return false;
            }
            Map below = comp.lowerMap;
            if (below == null || below.Disposed)
            {
                return false;
            }
            sky = cur;
            lower = below;
            return true;
        }

        /// <summary>The one-way-mirror visibility rule, mirroring the renderer's
        /// TryDrawFilteredDynamic: a below thing is visible from above only where its
        /// cell is unroofed, under open air on the sky level, and unfogged.</summary>
        internal static bool VisibleFromAbove(Thing t, Map sky, Map lower)
        {
            if (t == null || !t.Spawned || t.MapHeld != lower)
            {
                return false;
            }
            return CellVisibleFromAbove(t.Position, sky, lower);
        }

        /// <summary>Cell-level one-way-mirror rule: a surface cell is visible from the sky
        /// only when it is unroofed, under open air on the sky level, and unfogged.</summary>
        internal static bool CellVisibleFromAbove(IntVec3 pos, Map sky, Map lower)
        {
            if (!pos.InBounds(lower) || lower.roofGrid.Roofed(pos))
            {
                return false;
            }
            if (!pos.InBounds(sky) || sky.terrainGrid.TerrainAt(pos) != ABDefOf.AB_OpenAir)
            {
                return false;
            }
            return !lower.fogGrid.IsFogged(pos);
        }

        /// <summary>True when the given thing is currently part of the selection and
        /// lives on the surface directly below the viewed sky level.</summary>
        internal static bool IsBelowSelected(Thing t, out Map sky, out Map lower)
        {
            sky = Find.CurrentMap;
            lower = null;
            if (sky == null || t == null)
            {
                return false;
            }
            LevelComp comp = sky.Levels();
            if (comp == null || comp.level <= 0)
            {
                return false;
            }
            lower = comp.lowerMap;
            return lower != null && !lower.Disposed && t.MapHeld == lower;
        }

        /// <summary>Below pawns whose on-screen center sits under the cursor,
        /// nearest first. Only pawns for now (feature 2); items come later.</summary>
        internal static List<Thing> SelectablesUnderMouse(Map sky, Map lower, Vector3 clickPos)
        {
            hitBuffer.Clear();
            hitDist.Clear();
            IReadOnlyList<Pawn> pawns = lower.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (pawn == null || !pawn.def.selectable || pawn.IsHiddenFromPlayer())
                {
                    continue;
                }
                if (!VisibleFromAbove(pawn, sky, lower))
                {
                    continue;
                }
                Vector3 screen = LevelRenderer.ShiftedBelowDrawPos(pawn.DrawPos);
                float dist = (screen - clickPos).MagnitudeHorizontal();
                if (dist < ClickRadius)
                {
                    hitBuffer.Add(pawn);
                    hitDist[pawn] = dist;
                }
            }
            hitBuffer.Sort((a, b) => hitDist[a].CompareTo(hitDist[b]));
            // Items and buildings: cell-based, so map the cursor back to the surface cell
            // it is drawn over (inverse of the see-below transform) and take what is there.
            // Appended after the sorted pawns so a pawn on the same cell still wins first,
            // matching vanilla's pawn-priority selection order.
            AddBelowThingsAtCursor(sky, lower, clickPos);
            return hitBuffer;
        }

        private static void AddBelowThingsAtCursor(Map sky, Map lower, Vector3 clickPos)
        {
            IntVec3 cell = LevelRenderer.ScreenToBelowPos(clickPos).ToIntVec3();
            if (!cell.InBounds(lower) || !CellVisibleFromAbove(cell, sky, lower))
            {
                return;
            }
            List<Thing> things = lower.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (IsBelowSelectableThing(t) && !hitBuffer.Contains(t))
                {
                    hitBuffer.Add(t);
                }
            }
        }

        /// <summary>Non-pawn things selectable from above: loose items and buildings.
        /// Pawns are handled separately (draw-pos proximity); filth, plants, and other
        /// non-selectable defs are excluded to keep clicks through holes clean.</summary>
        private static bool IsBelowSelectableThing(Thing t)
        {
            if (t == null || !t.Spawned || !t.def.selectable)
            {
                return false;
            }
            ThingCategory cat = t.def.category;
            return cat == ThingCategory.Item || cat == ThingCategory.Building;
        }

        /// <summary>Whether the sky level itself has something selectable under the
        /// cursor; if so we leave the click entirely to vanilla.</summary>
        internal static bool SkyBlocksSelection(Map sky, Vector3 clickPos)
        {
            TargetingParameters clickParams = new TargetingParameters
            {
                mustBeSelectable = true,
                canTargetPawns = true,
                canTargetBuildings = true,
                canTargetItems = true,
                mapObjectTargetsMustBeAutoAttackable = false
            };
            // ThingsUnderMouse reads Find.CurrentMap == sky.
            if (GenUI.ThingsUnderMouse(clickPos, 1f, clickParams).Count > 0)
            {
                return true;
            }
            IntVec3 cell = UI.MouseCell();
            if (sky.zoneManager.ZoneAt(cell) != null)
            {
                return true;
            }
            return sky.planManager.PlanAt(cell) != null;
        }

        private static void AddInPlace(Selector selector, Thing thing)
        {
            Find.DesignatorManager?.Deselect();
            List<object> selected = SelectedRef(selector);
            if (selected.Count >= 200 || selected.Contains(thing))
            {
                return;
            }
            try
            {
                PlaySelectionSound?.Invoke(selector, new object[] { thing });
            }
            catch
            {
                // Sound is cosmetic; never let it break selection.
            }
            selected.Add(thing);
            thing.Notify_ThingSelected();
            SelectionDrawer.Notify_Selected(thing);
        }

        /// <summary>Deselect anything not on the target's map so the selection stays
        /// single-map (matches vanilla's cross-map deselect in SelectInternal).</summary>
        private static void DropOtherMapSelection(Selector selector, Map map)
        {
            List<object> selected = SelectedRef(selector);
            for (int i = selected.Count - 1; i >= 0; i--)
            {
                Map m = null;
                if (selected[i] is Thing th)
                {
                    m = th.MapHeld;
                }
                else if (selected[i] is Zone z)
                {
                    m = z.Map;
                }
                else if (selected[i] is Plan p)
                {
                    m = p.Map;
                }
                if (m != map)
                {
                    selector.Deselect(selected[i]);
                }
            }
        }

        internal static void HandleBelowClick(Selector selector, List<Thing> hits)
        {
            bool shift = Selector.ShiftIsHeld;
            // Cycle through stacked below pawns on repeat clicks (no-shift only),
            // matching vanilla's overlapping-selection behavior.
            Thing target = hits[0];
            if (!shift)
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    if (selector.IsSelected(hits[i]))
                    {
                        target = hits[(i + 1) % hits.Count];
                        break;
                    }
                }
            }
            if (shift)
            {
                if (selector.IsSelected(target))
                {
                    selector.Deselect(target);
                    return;
                }
                DropOtherMapSelection(selector, target.MapHeld);
                AddInPlace(selector, target);
            }
            else
            {
                selector.ClearSelection();
                AddInPlace(selector, target);
            }
        }

        /// <summary>Double-click parity: from a below thing under the cursor, pick
        /// the match "type" the way vanilla does - a player pawn first, then any
        /// pawn, then any thing that is not neverMultiSelect. Null when nothing
        /// under the cursor can seed a multi-select.</summary>
        internal static Thing PickMultiSelectSeed(List<Thing> hits)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i] is Pawn p && p.Faction == Faction.OfPlayer && !p.IsPrisoner)
                {
                    return p;
                }
            }
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i] is Pawn && hits[i].Spawned)
                {
                    return hits[i];
                }
            }
            for (int i = 0; i < hits.Count; i++)
            {
                Thing t = hits[i];
                if (t != null && !t.GetInnerIfMinified().def.neverMultiSelect)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>Adds every below thing of the seed's type that is visible from
        /// above and on screen, in place (no map switch). Mirrors vanilla's
        /// SelectAllMatchingObjectUnderMouseOnScreen validator: same faction and
        /// def (race-equivalence for pawns), skipping neverMultiSelect defs. The
        /// seed is already selected from the preceding single click; adding it
        /// again is a no-op.</summary>
        internal static void SelectAllMatchingBelow(Selector selector, Map sky, Map lower, Thing seed)
        {
            DropOtherMapSelection(selector, lower);
            if (!selector.IsSelected(seed))
            {
                AddInPlace(selector, seed);
            }
            CellRect view = Find.CameraDriver.CurrentViewRect;
            view = view.ClipInsideMap(lower);
            foreach (IntVec3 c in view)
            {
                if (!CellVisibleFromAbove(c, sky, lower))
                {
                    continue;
                }
                List<Thing> things = lower.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == seed || selector.IsSelected(t) || !MatchesForMultiSelect(t, seed))
                    {
                        continue;
                    }
                    AddInPlace(selector, t);
                }
            }
        }

        /// <summary>Vanilla's multi-select validator, applied to a below thing:
        /// same faction and def, skipping neverMultiSelect; for pawns also the
        /// host-faction, mutant and equivalent-race checks. Hidden pawns and
        /// non-selectable things never match.</summary>
        private static bool MatchesForMultiSelect(Thing t, Thing seed)
        {
            if (t == null || !t.Spawned || !t.def.selectable)
            {
                return false;
            }
            if (t is Pawn hp && hp.IsHiddenFromPlayer())
            {
                return false;
            }
            Thing ti = t.GetInnerIfMinified();
            Thing si = seed.GetInnerIfMinified();
            if (ti.def.neverMultiSelect || ti.Faction != si.Faction)
            {
                return false;
            }
            if (si is Pawn sp && ti is Pawn tp)
            {
                if (tp.HostFaction != sp.HostFaction || tp.mutant?.Def != sp.mutant?.Def)
                {
                    return false;
                }
                return SelectorUtility.IsEquivalentRace(tp, sp);
            }
            return ti.def == si.def;
        }
    }

    /// <summary>
    /// Left click that lands over open air with a below pawn under it selects that
    /// pawn in place. Sky clicks (a thing / zone / plan under the cursor on the sky
    /// level) fall straight through to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(Selector), "SelectUnderMouse")]
    internal static class Patch_Selector_SelectUnderMouse
    {
        private static bool Prefix(Selector __instance)
        {
            if (!ABGuard.On(ABGuard.Ui))
            {
                return true;
            }
            try
            {
                if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower))
                {
                    return true;
                }
                if (!UI.MouseCell().InBounds(sky))
                {
                    return true;
                }
                Vector3 clickPos = UI.MouseMapPosition();
                if (BelowSelection.SkyBlocksSelection(sky, clickPos))
                {
                    return true;
                }
                List<Thing> hits = BelowSelection.SelectablesUnderMouse(sky, lower, clickPos);
                if (hits.Count == 0)
                {
                    return true;
                }
                BelowSelection.HandleBelowClick(__instance, hits);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "below select under mouse");
                return true;
            }
        }
    }

    /// <summary>
    /// Double-click parity for the see-below view: vanilla's clickCount == 2 path
    /// (SelectAllMatchingObjectUnderMouseOnScreen) only scans the current (sky)
    /// map, so double-clicking a surface item below never selected the rest. When
    /// the cursor is over open air with a below thing under it, we select every
    /// matching below thing on screen in place. Sky double-clicks fall through.
    /// </summary>
    [HarmonyPatch(typeof(Selector), "SelectAllMatchingObjectUnderMouseOnScreen")]
    internal static class Patch_Selector_SelectAllMatchingBelow
    {
        private static bool Prefix(Selector __instance)
        {
            if (!ABGuard.On(ABGuard.Ui))
            {
                return true;
            }
            try
            {
                if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower))
                {
                    return true;
                }
                if (!UI.MouseCell().InBounds(sky))
                {
                    return true;
                }
                Vector3 clickPos = UI.MouseMapPosition();
                if (BelowSelection.SkyBlocksSelection(sky, clickPos))
                {
                    return true;
                }
                List<Thing> hits = BelowSelection.SelectablesUnderMouse(sky, lower, clickPos);
                if (hits.Count == 0)
                {
                    return true;
                }
                Thing seed = BelowSelection.PickMultiSelectSeed(hits);
                if (seed == null)
                {
                    return true;
                }
                BelowSelection.SelectAllMatchingBelow(__instance, sky, lower, seed);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "below select all matching");
                return true;
            }
        }
    }

    /// <summary>
    /// Draws a below-map selected thing's bracket through the see-below transform so
    /// the highlight sits on the pawn as it appears from above. Reuses the renderer's
    /// OffsetActive machinery: with it set, the already-patched DrawPos getters return
    /// the shifted position, and the vanilla bracket math follows for free. Hidden
    /// below selections (roofed / fogged / off open air) draw nothing.
    /// </summary>
    [HarmonyPatch(typeof(SelectionDrawer), nameof(SelectionDrawer.DrawSelectionBracketFor))]
    internal static class Patch_SelectionDrawer_DrawSelectionBracketFor
    {
        private static bool Prefix(object obj, out bool __state)
        {
            __state = false;
            if (!ABGuard.On(ABGuard.Ui) || !(obj is Thing thing))
            {
                return true;
            }
            if (!BelowSelection.IsBelowSelected(thing, out Map sky, out Map lower))
            {
                return true;
            }
            if (!BelowSelection.VisibleFromAbove(thing, sky, lower))
            {
                // Under a roof or otherwise not visible from above: no bracket.
                return false;
            }
            LevelRenderer.EnsureBelowTransform();
            LevelRenderer.OffsetActive = true;
            __state = true;
            return true;
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                LevelRenderer.OffsetActive = false;
            }
        }
    }
}
