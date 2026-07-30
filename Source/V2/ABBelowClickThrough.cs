using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

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
        /// <summary>Dev A/B switch ("AB2: bisect - toggle click-through"). Gates BOTH the
        /// right-click translation and select-through, since they share this method.</summary>
        internal static bool Enabled = true;

        /// <summary>
        /// Click translation for an ORDER, which must respect the level of the pawns being
        /// commanded rather than only what is under the cursor.
        ///
        /// The plain see-through rule is right for "what am I pointing at" but wrong for
        /// "where should these pawns go": resolving the cursor to whatever column shows
        /// beneath it hands the group a destination on ANOTHER band, which then needs a
        /// staircase to reach. On a map with no stairs built, most of the selection simply
        /// refuses to move and only the pawn that happens to share the resolved band walks -
        /// exactly the "three drafted pawns, only one moves" report. Ordering onto the
        /// commanded pawns' own level first is what the player means by clicking the ground.
        /// </summary>
        public static bool TryTranslateForOrder(Map map, System.Collections.Generic.List<Pawn> pawns,
            Vector3 clickPos, out Vector3 translated)
        {
            translated = clickPos;
            if (map == null || !Enabled || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (!cell.InBounds(map))
            {
                return false;
            }
            // The band the selection lives on - only when they all agree. A mixed-level
            // selection has no single right answer, so it falls through to the cursor rule.
            int pawnBand = -1;
            if (pawns != null)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == null || !p.Spawned || p.Map != map)
                    {
                        continue;
                    }
                    int b = bands.BandOf(p.Position);
                    if (pawnBand < 0)
                    {
                        pawnBand = b;
                    }
                    else if (pawnBand != b)
                    {
                        pawnBand = -1;
                        break;
                    }
                }
            }
            if (pawnBand >= 0 && bands.BandOf(cell) != pawnBand)
            {
                IntVec3 onPawnBand = bands.Translate(cell, pawnBand);
                if (onPawnBand.InBounds(map) && !bands.InGutter(onPawnBand)
                    && onPawnBand.Walkable(map))
                {
                    // Keep the sub-cell fraction so the order lands where it was aimed.
                    translated = new Vector3(clickPos.x, clickPos.y,
                        onPawnBand.z + (clickPos.z - cell.z));
                    return true;
                }
                // Exact cell unusable (a wall, a rock, the lip of a cliff): SNAP to the
                // nearest ground on their own level rather than giving up and handing the
                // group a cross-band destination it needs stairs for. This is the
                // difference between "they walk to roughly where I pointed" and "nothing
                // happens", and vanilla already behaves this way within a level.
                if (TryNearestWalkableOnBand(map, bands, onPawnBand, pawnBand, out IntVec3 snapped))
                {
                    translated = new Vector3(snapped.x + 0.5f, clickPos.y, snapped.z + 0.5f);
                    return true;
                }
            }
            // Nothing usable on their own level (an open-air cell, say): the see-through
            // rule can still order onto visible content below - but ONLY if the group can
            // actually get there.
            //
            // This gate is the fix for "they don't move, and sometimes walk off toward the
            // map edge". An untranslated cross-band destination is 100+ cells away in z and
            // unreachable without stairs; vanilla's pathfinder cannot cross a synthetic link,
            // so the goto fails and a failed goto degenerates into wandering. CanReach is the
            // right question to ask and it works UNPATCHED through wormholes - that is the
            // whole premise of the mod - so it answers "are there stairs joining these
            // levels" for free. With no route we translate nothing, and vanilla resolves the
            // click on the pawns' own level, which is what the player pointed at.
            if (!TryTranslate(map, clickPos, out Vector3 seeThrough))
            {
                return false;
            }
            if (pawnBand >= 0 && !AnyCanReach(map, pawns, IntVec3.FromVector3(seeThrough)))
            {
                return false;
            }
            translated = seeThrough;
            return true;
        }

        /// <summary>True when at least one commanded pawn has a genuine route to the cell.
        /// Reachability is transitive through the wormhole RegionLinks, so this is also the
        /// cheapest possible "is there a staircase" test.</summary>
        private static bool AnyCanReach(Map map, System.Collections.Generic.List<Pawn> pawns,
            IntVec3 target)
        {
            if (pawns == null || !target.InBounds(map))
            {
                return false;
            }
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p != null && p.Spawned && p.Map == map
                    && p.CanReach(target, PathEndMode.OnCell, Danger.Deadly))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Nearest walkable cell on one specific band, searched outward from a
        /// starting cell. Radius is deliberately small: a snap should feel like the order
        /// landing next to where you pointed, not like the pawn choosing its own
        /// destination.</summary>
        internal static bool TryNearestWalkableOnBand(Map map, ABBandMap bands, IntVec3 origin,
            int band, out IntVec3 found)
        {
            found = origin;
            int count = Mathf.Min(GenRadial.NumCellsInRadius(8f), GenRadial.RadialPattern.Length);
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = origin + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || bands.InGutter(c) || bands.BandOf(c) != band)
                {
                    continue;
                }
                if (c.Walkable(map))
                {
                    found = c;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Translate a click position down one band when the player is looking
        /// through open air. Returns false when the click should be left alone.</summary>
        public static bool TryTranslate(Map map, Vector3 clickPos, out Vector3 translated)
        {
            translated = clickPos;
            if (map == null || !Enabled || !ABGuard.On(ABGuard.Rendering))
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
            // Descend as far as the view does, not one band. A single step worked only
            // while there was exactly one level above the surface; with levels stacked, the
            // level directly below an open-air cell is usually open air too, so clicking,
            // selecting and every other interaction stopped working from level 2 upward
            // even though the renderer was showing the ground perfectly well.
            if (!ABBands.TryResolveVisibleBelow(map, bands, cell, out IntVec3 below, out int drop)
                || map.fogGrid.IsFogged(below))
            {
                return false; // nothing legible down there to click
            }
            translated = new Vector3(clickPos.x, clickPos.y, clickPos.z - drop);
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
        private static void Prefix(System.Collections.Generic.List<Pawn> selectedPawns,
            ref Vector3 clickPos)
        {
            try
            {
                if (ABBelowClickThrough.TryTranslateForOrder(Find.CurrentMap, selectedPawns,
                        clickPos, out Vector3 t))
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
    /// THE per-pawn destination fix, at the one place every ordered goto resolves.
    ///
    /// `FloatMenuMakerMap.GetOptions` is the wrong layer for grouped movement: a group order
    /// runs through MultiPawnGotoController, which never consults it and instead calls
    /// RCellFinder.BestOrderedGotoDestNear ONCE PER PAWN. Patching there fixes what the
    /// clickPos interception could not, and it is per-pawn by construction - so a selection
    /// spanning several levels resolves correctly for each member instead of collapsing to
    /// one shared answer.
    ///
    /// The rule: if the destination is on another band and this pawn genuinely cannot reach
    /// it, bring the destination onto the PAWN'S OWN band at the same column. Reachability is
    /// checked first so that legitimate cross-level orders - ones with a staircase - are left
    /// completely alone for the wormhole router to segment.
    ///
    /// Covers every caller for free: grouped moves, single drafted moves, crates, hackables,
    /// and jump targeting.
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.BestOrderedGotoDestNear))]
    public static class Patch_RCellFinder_ABOrderedGotoBand
    {
        private static void Prefix(ref IntVec3 root, Pawn searcher)
        {
            try
            {
                if (searcher == null || !searcher.Spawned || !ABBelowClickThrough.Enabled)
                {
                    return;
                }
                Map map = searcher.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !root.InBounds(map))
                {
                    return;
                }
                int pawnBand = bands.BandOf(searcher.Position);
                if (pawnBand < 0 || bands.BandOf(root) == pawnBand)
                {
                    return; // already on this pawn's level
                }
                if (searcher.CanReach(root, PathEndMode.OnCell, Danger.Deadly))
                {
                    return; // a real route exists (stairs); leave it to the router
                }
                // NEVER hand back a destination this pawn cannot reach either. Without this
                // the patch could replace one unreachable cell with another and merely move
                // the failure - and because MultiPawnGotoController calls this ONCE PER PAWN
                // to lay out a formation, a per-pawn substitution that misses turns vanilla's
                // tight cluster into pawns resolving independently (observed: an evenly
                // spaced vertical column, one pawn heading the opposite way).
                IntVec3 onPawnBand = bands.Translate(root, pawnBand);
                if (onPawnBand.InBounds(map) && !bands.InGutter(onPawnBand)
                    && onPawnBand.Walkable(map)
                    && searcher.CanReach(onPawnBand, PathEndMode.OnCell, Danger.Deadly))
                {
                    root = onPawnBand;
                    return;
                }
                if (ABBelowClickThrough.TryNearestWalkableOnBand(map, bands, onPawnBand,
                        pawnBand, out IntVec3 snapped)
                    && searcher.CanReach(snapped, PathEndMode.OnCell, Danger.Deadly))
                {
                    root = snapped;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: ordered-goto band fix threw: " + e.Message,
                    762195914);
            }
        }
    }

    /// <summary>
    /// The group-goto PREVIEW, drawn into the level you are looking at.
    ///
    /// MultiPawnGotoController.Draw builds every position with
    /// `ToVector3ShiftedWithAltitude` from cells on the PAWNS' own band, so from an upper
    /// level the drag line and the destination ghosts render at that band's world z - which
    /// on screen is the bottom of the map. Nothing is wrong with the cells; they are simply
    /// drawn where they really are.
    ///
    /// Vanilla's body is short and uses public drawing APIs, so it is re-emitted here with
    /// each position lifted into the current view band. Replacing a vanilla draw carries
    /// upkeep if Ludeon changes it, so this is deliberately conservative: banded maps only,
    /// and ANY missing member or thrown exception falls straight through to vanilla rather
    /// than leaving the preview broken.
    /// </summary>
    [HarmonyPatch(typeof(MultiPawnGotoController), nameof(MultiPawnGotoController.Draw))]
    public static class Patch_MultiPawnGotoController_ABDrawInViewBand
    {
        private static readonly AccessTools.FieldRef<MultiPawnGotoController, bool> ActiveRef =
            AccessTools.FieldRefAccess<MultiPawnGotoController, bool>("active");

        internal static readonly AccessTools.FieldRef<MultiPawnGotoController,
            System.Collections.Generic.List<Pawn>> PawnsRef =
            AccessTools.FieldRefAccess<MultiPawnGotoController,
                System.Collections.Generic.List<Pawn>>("pawns");

        private static readonly AccessTools.FieldRef<MultiPawnGotoController,
            System.Collections.Generic.List<IntVec3>> DestsRef =
            AccessTools.FieldRefAccess<MultiPawnGotoController,
                System.Collections.Generic.List<IntVec3>>("dests");

        internal static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> StartRef =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("start");

        internal static readonly AccessTools.FieldRef<MultiPawnGotoController, IntVec3> EndRef =
            AccessTools.FieldRefAccess<MultiPawnGotoController, IntVec3>("end");

        private static bool matsResolved;

        private static Material circleMat;

        private static Material lineMat;

        private static void ResolveMats()
        {
            matsResolved = true;
            circleMat = AccessTools.Field(typeof(MultiPawnGotoController), "GotoCircleMaterial")
                ?.GetValue(null) as Material;
            lineMat = AccessTools.Field(typeof(MultiPawnGotoController), "GotoBetweenLineMaterial")
                ?.GetValue(null) as Material;
        }

        private static bool Prefix(MultiPawnGotoController __instance)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Rendering) || !ActiveRef(__instance))
                {
                    return true;
                }
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                if (!matsResolved)
                {
                    ResolveMats();
                }
                if (circleMat == null || lineMat == null)
                {
                    return true; // could not reach vanilla's materials; let it draw normally
                }
                int viewBand = ABBandView.CurrentBand(map);
                System.Collections.Generic.List<Pawn> pawns = PawnsRef(__instance);
                System.Collections.Generic.List<IntVec3> dests = DestsRef(__instance);
                if (pawns == null || dests == null)
                {
                    return true;
                }
                // Vanilla's own constants, kept verbatim so the preview looks identical.
                Vector3 size = new Vector3(1.7f, 1f, 1.7f);
                float alt = AltitudeLayer.MetaOverlays.AltitudeFor();
                float altCircle = alt + 0.03658537f;
                float altLine = alt - 0.03658537f;
                int count = Mathf.Min(pawns.Count, dests.Count);
                for (int i = 0; i < count; i++)
                {
                    Pawn pawn = pawns[i];
                    IntVec3 c = dests[i];
                    if (pawn == null || !c.IsValid || !pawn.Spawned || c.Fogged(pawn.Map))
                    {
                        continue;
                    }
                    pawn.Drawer.renderer.RenderPawnAt(Lift(bands, viewBand, c, alt), Rot4.South);
                    Graphics.DrawMesh(MeshPool.plane10,
                        Matrix4x4.TRS(Lift(bands, viewBand, c, altCircle), Quaternion.identity, size),
                        circleMat, 0);
                }
                GenDraw.DrawLineBetween(Lift(bands, viewBand, StartRef(__instance), altLine),
                    Lift(bands, viewBand, EndRef(__instance), altLine), lineMat, 0.9f);
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: goto preview redraw threw, using vanilla: "
                    + e.Message, 762195915);
                return true;
            }
        }

        /// <summary>A cell's draw position, lifted from its own band into the viewed one.</summary>
        private static Vector3 Lift(ABBandMap bands, int viewBand, IntVec3 c, float altitude)
        {
            Vector3 v = c.ToVector3ShiftedWithAltitude(altitude);
            int band = bands.BandOf(c);
            if (band >= 0 && band != viewBand)
            {
                v.z += (viewBand - band) * bands.Slot;
            }
            return v;
        }
    }

    /// <summary>
    /// THE group-order fix: keep the drag line inside ONE level.
    ///
    /// `RecomputeDestinations` spreads the selection ALONG the drag line -
    /// `root = start + (end - start) * (j / (count-1))` - and then finds each pawn a cell
    /// near its own interpolated point. That is a line formation, not a cluster, and it is
    /// only sane while both endpoints are on the same band: if they are a Slot apart, the
    /// line runs straight down through the GUTTER and the levels between, so pawns end up
    /// evenly spaced down the map instead of arranged around the cursor.
    ///
    /// It also explains the odd/even split exactly. The fractions are j/(count-1), so an ODD
    /// count samples precisely 0.5 - the midpoint - while 2 and 4 pawns never do. For a
    /// one-Slot drag that midpoint is the impassable, permanently fogged gutter, which is why
    /// 3 and 5 pawns misbehaved and 2 and 4 looked fine.
    ///
    /// Normalising the two endpoints into the commanded pawns' band fixes the cause and
    /// leaves vanilla's formation maths completely untouched - which is what makes this
    /// preferable to rewriting each pawn's destination individually.
    /// </summary>
    [HarmonyPatch(typeof(MultiPawnGotoController),
        nameof(MultiPawnGotoController.RecomputeDestinations))]
    public static class Patch_MultiPawnGoto_ABKeepLineInBand
    {
        private static void Prefix(MultiPawnGotoController __instance)
        {
            try
            {
                if (!ABBelowClickThrough.Enabled)
                {
                    return;
                }
                System.Collections.Generic.List<Pawn> pawns =
                    Patch_MultiPawnGotoController_ABDrawInViewBand.PawnsRef(__instance);
                if (pawns == null || pawns.Count == 0)
                {
                    return;
                }
                Map map = null;
                int band = -1;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == null || !p.Spawned)
                    {
                        continue;
                    }
                    map = p.Map;
                    ABBandMap probe = ABBands.CompOf(map);
                    if (probe == null || !probe.Banded)
                    {
                        return;
                    }
                    int b = probe.BandOf(p.Position);
                    if (band < 0)
                    {
                        band = b;
                    }
                    else if (band != b)
                    {
                        return; // selection spans levels: no single line can serve them
                    }
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (band < 0 || bands == null || !bands.Banded)
                {
                    return;
                }
                Normalise(map, bands, band,
                    Patch_MultiPawnGotoController_ABDrawInViewBand.StartRef, __instance);
                Normalise(map, bands, band,
                    Patch_MultiPawnGotoController_ABDrawInViewBand.EndRef, __instance);
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: goto line normalise threw: " + e.Message,
                    762195916);
            }
        }

        private static void Normalise(Map map, ABBandMap bands, int band,
            AccessTools.FieldRef<MultiPawnGotoController, IntVec3> field,
            MultiPawnGotoController inst)
        {
            IntVec3 c = field(inst);
            if (!c.IsValid || bands.BandOf(c) == band)
            {
                return;
            }
            IntVec3 moved = bands.Translate(c, band);
            if (moved.InBounds(map) && !bands.InGutter(moved))
            {
                field(inst) = moved;
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
