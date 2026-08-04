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
            // ⚠ THE ONE CASE WHERE PULLING THE ORDER ONTO THE PAWNS' BAND IS WRONG: the
            // player is LOOKING at another level and clicking solid ground on it. That is an
            // explicit "come up here" (or down here), and rewriting it onto the pawns' own
            // band silently turned every upward force-move into a same-band shuffle. It read
            // as "ordering a drafted pawn upward does nothing at all", because there is no
            // upward direction anywhere else in the click model either: the see-through
            // fallback below only ever descends (clickPos.z - drop), so DOWN worked purely
            // because the cursor and the pawn shared a band and the descent rule took over.
            //
            // The discriminator is the VIEW band. Clicking a cell on the level you are
            // actually looking at means that level. Clicking a cell on some other level
            // (which happens when the cursor is over open air) still means "my own level",
            // which is what the translation below is for.
            //
            // CanReach is the gate, and it is the same one the descent branch uses: it is
            // transitive through the wormhole RegionLinks, so it answers "is there a
            // staircase joining these levels" for free. With no route we fall through and
            // translate as before, so a player with no stairs built keeps the old, useful
            // behaviour instead of issuing orders that quietly fail.
            //
            // Applies to ALL orders, not just drafted ones, by the user's call.
            int clickBand = bands.BandOf(cell);
            if (pawnBand >= 0 && clickBand != pawnBand
                && clickBand == ABBandView.CurrentBand(map)
                && !bands.InGutter(cell)
                && AnyCanReach(map, pawns, cell))
            {
                return false; // deliberate cross-band order; leave it for the wormhole router
            }

            if (pawnBand >= 0 && clickBand != pawnBand)
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
            // THE shared see-below gate - the same one the renderer uses, which is what makes
            // "you can click what you can see" true by construction rather than by two
            // predicates being kept in step by hand. Descends as far as the view does, not
            // one band: a single step worked only while there was exactly one level above the
            // surface, and with levels stacked the level directly below an open-air cell is
            // usually open air too, so clicking and selecting stopped working from level 2
            // upward even though the renderer was showing the ground perfectly well.
            if (!ABBands.TryResolveVisibleFrom(map, bands, cell, requireUnfogged: true,
                    out IntVec3 _, out int drop))
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
    /// Covers grouped moves, single drafted moves, crates and hackables for free.
    ///
    /// ⚠⚠ BUT NOT JUMPS, AND THE `reachable` FLAG IS HOW THEY ARE TOLD APART. JumpUtility
    /// calls this with `reachable: false` precisely because a jump does NOT need a walkable
    /// route - that is the entire purpose of a jump pack. This patch's rule ("if the pawn
    /// cannot WALK there, bring the destination onto its own band") is therefore exactly
    /// backwards for a leap: it would drag every cross-level jump back onto the level the
    /// pawn is already standing on, silently, and the jump would look like it simply did
    /// nothing. Reading the caller's own flag is better than sniffing the job or the verb,
    /// because `reachable: false` IS the caller stating that reachability is not the test.
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.BestOrderedGotoDestNear))]
    public static class Patch_RCellFinder_ABOrderedGotoBand
    {
        private static void Prefix(ref IntVec3 root, Pawn searcher, bool reachable)
        {
            try
            {
                if (searcher == null || !searcher.Spawned || !ABBelowClickThrough.Enabled)
                {
                    return;
                }
                if (!reachable)
                {
                    return; // a jump (or anything else that flies): not a walking question
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
    /// <remarks>
    /// <c>[StaticConstructorOnStartup]</c> is required by Verse's own static analysis, not by
    /// our logic: any type holding a <c>Material</c> field is flagged with "All assets must be
    /// loaded in the main thread". The two materials here are resolved LAZILY from vanilla
    /// statics inside Draw (already the main thread), so nothing was ever actually unsafe -
    /// but the attribute is free, it silences a warning that would otherwise be noise in
    /// every future test run, and the check exists because this pattern usually IS a
    /// cross-thread asset load. The static field initialisers it forces to run at startup are
    /// all AccessTools reflection, which is main-thread-safe.
    /// </remarks>
    [StaticConstructorOnStartup]
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

        /// <summary>
        /// Why the destination ghost was not drawn last frame, read by
        /// `AB2: goto ghost report`.
        ///
        /// ⚠ INSTRUMENTED RATHER THAN GUESSED, BY THE §14 RULE. The ghost is NOT missing
        /// because we deleted it: the RenderPawnAt call below is vanilla's own, at a lifted
        /// position. So "no ghost on a cross-level order" has at least four candidate causes
        /// that look identical from the outside - the controller never went active for this
        /// order, the per-pawn destination came back invalid, the destination cell is FOGGED
        /// (other bands start fogged, which is why `AB2: open all bands` exists), or vanilla's
        /// materials could not be resolved and we bailed to vanilla entirely. Guessing between
        /// them costs a test cycle each; this string separates them in one.
        /// </summary>
        internal static string lastGhostSkip = "never ran";

        internal static int ghostsDrawn;

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
                // ⚠ THESE TWO WERE THE UNINSTRUMENTED RETURNS AND THE ANSWER WAS BEHIND THEM.
                // Run #298 reported "never ran", which is the initial value of lastGhostSkip -
                // meaning we exited above every probe. Instrumenting three of five early
                // returns is the same mistake as instrumenting none, just harder to notice.
                if (!ABGuard.On(ABGuard.Rendering))
                {
                    lastGhostSkip = "ABGuard.Rendering is OFF (a bisect toggle - these persist "
                        + "across runs); vanilla is drawing, unlifted";
                    return true;
                }
                if (!ActiveRef(__instance))
                {
                    // ⚠ NOT A BUG IN THE COMMON CASE. The destination ghost is drawn ONLY by
                    // MultiPawnGotoController, and both paths that activate it
                    // (Selector.HandleMultiselectGoto and FloatMenuOptionProvider_DraftedMove)
                    // take a single-pawn branch that calls PawnGotoAction directly and never
                    // touches the controller. With ONE drafted pawn selected vanilla draws no
                    // ghost either. The feature is a 2+ pawn drag.
                    lastGhostSkip = "gotoController not active - no multi-pawn goto drag in "
                        + "progress. With a single drafted pawn vanilla draws no ghost either; "
                        + "select TWO OR MORE and drag to exercise this.";
                    return true;
                }
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    lastGhostSkip = "map is not banded";
                    return true;
                }
                if (!matsResolved)
                {
                    ResolveMats();
                }
                if (circleMat == null || lineMat == null)
                {
                    lastGhostSkip = "vanilla materials unresolved; fell through to vanilla";
                    return true; // could not reach vanilla's materials; let it draw normally
                }
                int viewBand = ABBandView.CurrentBand(map);
                System.Collections.Generic.List<Pawn> pawns = PawnsRef(__instance);
                System.Collections.Generic.List<IntVec3> dests = DestsRef(__instance);
                if (pawns == null || dests == null)
                {
                    lastGhostSkip = "pawns or dests list was null";
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
                        lastGhostSkip = pawn == null ? "pawn null"
                            : !c.IsValid ? (pawn.LabelShort + ": dest cell invalid")
                            : !pawn.Spawned ? (pawn.LabelShort + ": not spawned")
                            : pawn.LabelShort + ": dest " + c + " band "
                              + bands.BandOf(c) + " is FOGGED (pawn band "
                              + bands.BandOf(pawn.Position) + ", view band " + viewBand + ")";
                        continue;
                    }
                    lastGhostSkip = "drawn ok for " + pawn.LabelShort + " at " + c
                        + " band " + bands.BandOf(c);
                    ghostsDrawn++;
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

        /// <summary>A cell's draw position, lifted from its own band into the viewed one.
        /// Delegates to the canonical transform - this method used to be the third private
        /// copy of it. See ABUIGeometry.LiftToView.</summary>
        private static Vector3 Lift(ABBandMap bands, int viewBand, IntVec3 c, float altitude)
        {
            return ABUIGeometry.LiftToView(bands, viewBand, c, altitude);
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
    ///
    /// ⚠⚠ BUT IT MUST ONLY FIRE WHEN THE TWO ENDPOINTS DISAGREE WITH EACH OTHER, AND
    /// DOING IT UNCONDITIONALLY BROKE EVERY GROUP CROSS-LEVEL ORDER (runs #297-#300).
    /// Read the trigger above again: the interpolation is only unsafe when `start` and `end`
    /// are a Slot apart FROM ONE ANOTHER, because that is what puts the midpoint in the
    /// gutter. Two endpoints that both sit on the viewed band interpolate perfectly safely
    /// within it, whichever band that is - so pulling them onto the PAWNS' band in that case
    /// achieves nothing except destroying a deliberate cross-level order.
    ///
    /// The symptom was precise and took four runs to corner: a group ordered onto the level
    /// above walked to the stairwell's own cell on their CURRENT level and stopped, with no
    /// pending transit. `AB2: why is this pawn stuck` read destination (35, 0, 219) for a
    /// click at (35, 347) - exactly one Slot down, i.e. this method's arithmetic.
    ///
    /// ⚠ AND IT IS THE SAME DEFECT §33b FIXED FOR SINGLE PAWNS, IN THE PATH NOBODY AUDITED.
    /// A single pawn is ordered through `FloatMenuMakerMap.GetOptions` ->
    /// `TryTranslateForOrder`, which learned the view-band rule; a GROUP is ordered through
    /// `MultiPawnGotoController`, which did not. Two entry points for one user action, and
    /// fixing one of them looked like fixing the feature. **When a fix lands in a click
    /// handler, find every other handler for the same gesture.**
    ///
    /// Per-pawn resolution is already correct and needs nothing from us:
    /// `Patch_RCellFinder_ABOrderedGotoBand` checks `CanReach` first and leaves genuine
    /// cross-level destinations to the wormhole router.
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
                // ⚠ THE TRIGGER IS THE TWO ENDS DISAGREEING WITH EACH OTHER - that is the only
                // case that can put the interpolation midpoint (j/(count-1)) in the gutter,
                // and it is the only reason this patch exists. Agreeing ends are left exactly
                // where they are, including on a band that is not the pawns' own: that is a
                // deliberate cross-level group order and the wormhole router's job.
                IntVec3 s = Patch_MultiPawnGotoController_ABDrawInViewBand.StartRef(__instance);
                IntVec3 e2 = Patch_MultiPawnGotoController_ABDrawInViewBand.EndRef(__instance);
                if (!s.IsValid || !e2.IsValid)
                {
                    return;
                }
                int bandStart = bands.BandOf(s);
                if (bandStart < 0 || bandStart == bands.BandOf(e2))
                {
                    return;
                }

                // ⚠⚠ RESOLVE ONTO THE *START'S* BAND, NOT THE PAWNS'. THIS IS WHY DOWNWARD
                // GROUP ORDERS FAILED WHILE UPWARD ONES WORKED.
                //
                // `MultiPawnGotoController.ProcessInputEvents` assigns `end = UI.MouseCell()`
                // RAW, so the drag end never passes through our see-through translation -
                // only `start` does, via the float-menu click. Upward that is harmless,
                // because you are LOOKING at the destination band and both cells land on it
                // anyway. Downward you are looking THROUGH open air: `start` gets translated
                // down to the level you can see, `end` stays on the viewed band, the two
                // disagree, and normalising both onto the PAWNS' band threw the deliberate
                // descent away. Exactly the same class of bug as §33b and §33f, one layer
                // further in.
                //
                // `start` is the cell the player actually aimed at and it has been resolved
                // correctly; `end` is raw cursor noise. So the line is made coplanar by
                // bringing END to START, which preserves the destination band AND keeps the
                // formation inside one band, which is all the original fix ever needed.
                IntVec3 movedEnd = bands.Translate(e2, bandStart);
                if (movedEnd.InBounds(map) && !bands.InGutter(movedEnd))
                {
                    Patch_MultiPawnGotoController_ABDrawInViewBand.EndRef(__instance) = movedEnd;
                    return;
                }
                // END could not be brought onto START's band (out of bounds or gutter): fall
                // back to the original behaviour and put BOTH on the pawns' band, which is
                // never a cross-level order but is always a valid one.
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
