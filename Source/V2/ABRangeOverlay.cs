using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE RANGE AND TARGET OVERLAYS, MADE BAND-AWARE.
    ///
    /// §82 fixed what the player can POINT at. This is what the player can SEE about what
    /// they can point at, which had drifted out of step with it in three separate places:
    ///
    ///   1. the white "where can I jump" ring (<c>Verb_Jump.DrawHighlight</c>) is a raw
    ///      radial disc in the caster's own band, so it stops dead at the lip of a hole even
    ///      though the cells below it are legal destinations;
    ///   2. the generic weapon/psycast range ring
    ///      (<c>VerbProperties.DrawRadiusRing</c>) has the same shape and the same gap;
    ///   3. the target box and the area-of-effect circle are drawn at the target's REAL
    ///      coordinates, which for a below-band target is one Slot off the bottom of the
    ///      screen - so pointing a psycast through a hole showed no highlight at all.
    ///
    /// ⚠ THE OVERLAY MUST ASK THE SAME QUESTION THE CLICK WILL ASK. A second, parallel
    /// "is this reachable" predicate written for drawing is §14's two-resolvers bug with a
    /// delay fuse: it agrees with the real one on the day it is written and drifts forever
    /// after. So the jump ring calls <c>JumpUtility.CanHitTargetFrom</c> - the very method
    /// §ABBandLeap prefixes - and the verb ring calls <c>ABCombatV2.TrySolve</c>, which is
    /// the body of the shoot-line patch.
    ///
    /// ⚠⚠ BUT IT MUST NOT ASK A QUESTION THAT HAS SIDE EFFECTS, AND ONE OF THEM DOES.
    /// <c>Verb.CanHitTargetFrom</c> looks like the obvious predicate for the verb ring and is
    /// the WRONG call: it funnels into <c>Patch_Verb_ABCrossBandShootLine</c>, which ends with
    /// <c>ABCombatRelay.RecordSolution(...)</c> - it PARKS a solution for the next
    /// <c>Projectile.Launch</c> to consume. Painting a ring evaluates thousands of cells, so
    /// it would park thousands of solutions and the next real shot could pick up whichever one
    /// happened to land in the slot. The ring therefore calls the pure solver underneath the
    /// patch instead. **An overlay is an observer; observers do not write.**
    /// </summary>
    public static class ABRangeOverlay
    {
        /// <summary>Which predicate a paint is asking for. The two differ enough (a jump
        /// needs a landable cell, a verb needs a firing solution) that folding them into one
        /// predicate would mean a bool parameter inside the hot loop anyway.</summary>
        public enum Kind
        {
            Jump,
            Verb
        }

        /// <summary>
        /// Cross-band solves allowed in ONE paint. A solve is a terrain read, a column walk
        /// and up to two line-of-sight traces; a wide-open sky band under a long-ranged verb
        /// could otherwise ask for seventeen thousand of them in a single frame.
        ///
        /// ⚠ WHEN THIS BITES THE RING IS INCOMPLETE, NOT WRONG - the in-band half is always
        /// finished first, and `AB2: combat report` says how often it happened. A silent cap
        /// would be the §62k mistake (a filter that can reject everything must say so).
        /// </summary>
        private const int MaxCrossBandTests = 4000;

        /// <summary>Rebuild cadence. The set depends on the caster's cell, the view band and
        /// the map's geometry, none of which change at frame rate - and vanilla recomputes
        /// its ring EVERY frame, so even at this cadence we are cheaper than what we replace
        /// on any map with no openings in range.</summary>
        private const int RefreshTicks = 30;

        /// <summary>Frame-count backstop so the set still refreshes while the game is PAUSED,
        /// where TicksGame does not advance. Dev-mode terrain edits happen paused more often
        /// than not.</summary>
        private const int RefreshFrames = 120;

        // Observe-only counters for `AB2: combat report` (§36: never read by a decision).
        public static int paints;

        public static int reuses;

        public static int cellsTested;

        public static int belowCells;

        public static int truncated;

        private static readonly List<IntVec3> Cells = new List<IntVec3>();

        private static Map cachedMap;

        private static Kind cachedKind;

        private static IntVec3 cachedRoot;

        private static int cachedViewBand;

        private static float cachedRange;

        private static float cachedMinRange;

        private static Verb cachedVerb;

        private static Pawn cachedJumper;

        private static int cachedTick = int.MinValue;

        private static int cachedFrame = int.MinValue;

        public static void ResetCounters()
        {
            paints = 0;
            reuses = 0;
            cellsTested = 0;
            belowCells = 0;
            truncated = 0;
        }

        public static string CounterReport()
        {
            return "rangeOverlay: paints=" + paints + " reused=" + reuses + " cellsTested="
                + cellsTested + " belowCells=" + belowCells + " truncated=" + truncated;
        }

        public static void Invalidate()
        {
            cachedMap = null;
            cachedTick = int.MinValue;
            cachedFrame = int.MinValue;
        }

        /// <summary>
        /// The outline set, in VIEW-BAND coordinates, cached across frames.
        ///
        /// ⚠ THE RETURNED LIST IS THE CACHE ITSELF. Callers hand it straight to
        /// GenDraw.DrawFieldEdges, which copies into its own BoolGrid and keeps no reference,
        /// so there is nothing to defend against - but do not store or mutate it.
        /// </summary>
        public static List<IntVec3> ReachSet(Map map, ABBandMap bands, int viewBand, Verb verb,
            Pawn jumper, IntVec3 root, float range, float minRange, Kind kind)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int frame = Time.frameCount;
            if (cachedMap == map && cachedKind == kind && cachedRoot == root
                && cachedViewBand == viewBand && cachedRange == range
                && cachedMinRange == minRange && cachedVerb == verb && cachedJumper == jumper
                && tick - cachedTick < RefreshTicks && frame - cachedFrame < RefreshFrames)
            {
                reuses++;
                return Cells;
            }
            cachedMap = map;
            cachedKind = kind;
            cachedRoot = root;
            cachedViewBand = viewBand;
            cachedRange = range;
            cachedMinRange = minRange;
            cachedVerb = verb;
            cachedJumper = jumper;
            cachedTick = tick;
            cachedFrame = frame;
            Build(map, bands, viewBand, verb, jumper, root, range, minRange, kind);
            return Cells;
        }

        private static void Build(Map map, ABBandMap bands, int viewBand, Verb verb,
            Pawn jumper, IntVec3 root, float range, float minRange, Kind kind)
        {
            Cells.Clear();
            paints++;

            // ⚠ SNAPSHOT AND RESTORE ABSHAFT'S COUNTERS AROUND THE PAINT. A single ring can
            // run thousands of solves, which would swamp `AB2: combat report` and make the
            // shaft numbers useless for diagnosing actual combat - the thing they exist for.
            // Legal precisely because §36 forbids any decision from reading them; this
            // overlay reports its own work in its own counters instead.
            int sSolves = ABShaft.solves;
            int sCacheHits = ABShaft.cacheHits;
            int sFastHits = ABShaft.fastHits;
            int sWalks = ABShaft.walks;
            int sMisses = ABShaft.misses;
            int sOverhead = ABShaft.overheadSolves;
            try
            {
                int rootBand = bands.BandOf(root);
                // The caster's own column, expressed in the band we are drawing into. Every
                // candidate is a VIEW-BAND cell, which is what makes the outline land where
                // the player is looking rather than where the cells really are.
                IntVec3 anchor = bands.Translate(root, viewBand);
                int count = Mathf.Min(GenRadial.NumCellsInRadius(range),
                    GenRadial.RadialPattern.Length);
                int crossTests = 0;
                for (int i = 0; i < count; i++)
                {
                    IntVec3 v = anchor + GenRadial.RadialPattern[i];
                    if (!v.InBounds(map) || bands.BandOf(v) != viewBand || bands.InGutter(v))
                    {
                        // ⚠ THE BAND CLAMP IS ALSO A BUG FIX, NOT ONLY AN OPTIMISATION.
                        // Vanilla's disc is raw cell space, so a caster standing within one
                        // range of the band's top row has its ring reach over the gutter into
                        // the level above. The impassable gutter usually blocks the sight test
                        // and hides it - but on a map whose band height is an exact slot
                        // multiple the gutter is zero rows wide and the ring bleeds onto
                        // another floor. Selectable is drawable; so is highlightable.
                        continue;
                    }
                    cellsTested++;

                    // WHAT DOES THIS COLUMN ACTUALLY SHOW? The shared see-through resolver,
                    // the same one the renderer and the click paths use, so the ring cannot
                    // disagree with the picture underneath it.
                    IntVec3 shown = v;
                    if (ABBands.TryResolveVisibleFrom(map, bands, v, requireUnfogged: true,
                            out IntVec3 seen, out int drop) && drop > 0)
                    {
                        shown = seen;
                    }

                    bool ok;
                    if (bands.BandOf(shown) == rootBand)
                    {
                        ok = SameBand(map, verb, jumper, root, shown, range, kind);
                    }
                    else
                    {
                        if (crossTests >= MaxCrossBandTests)
                        {
                            truncated++;
                            continue;
                        }
                        crossTests++;
                        ok = CrossBand(map, verb, jumper, root, shown, range, minRange, kind);
                    }
                    if (!ok)
                    {
                        continue;
                    }
                    Cells.Add(v);
                    if (shown != v)
                    {
                        belowCells++;
                    }
                }
            }
            catch (Exception e)
            {
                Cells.Clear();
                ABGuard.Disable(ABGuard.Rendering, e, "V2 band range overlay");
            }
            finally
            {
                ABShaft.solves = sSolves;
                ABShaft.cacheHits = sCacheHits;
                ABShaft.fastHits = sFastHits;
                ABShaft.walks = sWalks;
                ABShaft.misses = sMisses;
                ABShaft.overheadSolves = sOverhead;
            }
        }

        /// <summary>Vanilla's own rule, reproduced for the cells vanilla would have judged
        /// itself. Reproduced rather than delegated because vanilla states it as a lambda
        /// inside the method we are replacing.</summary>
        private static bool SameBand(Map map, Verb verb, Pawn jumper, IntVec3 root,
            IntVec3 cell, float range, Kind kind)
        {
            if (kind == Kind.Jump)
            {
                return jumper != null
                    && JumpUtility.CanHitTargetFrom(jumper, root, cell, range)
                    && JumpUtility.ValidJumpTarget(jumper, map, cell);
            }
            VerbProperties props = verb?.verbProps;
            if (props != null && !props.drawHighlightWithLineOfSight)
            {
                return true; // vanilla passes no predicate at all in this case
            }
            return GenSight.LineOfSight(root, cell, map);
        }

        /// <summary>The cross-band verdict, taken from the same solver the click uses.</summary>
        private static bool CrossBand(Map map, Verb verb, Pawn jumper, IntVec3 root,
            IntVec3 cell, float range, float minRange, Kind kind)
        {
            if (kind == Kind.Jump)
            {
                // Routes through Patch_JumpUtility_ABCrossBandRange, i.e. exactly the answer
                // the targeter will give when this cell is clicked.
                return jumper != null
                    && JumpUtility.CanHitTargetFrom(jumper, root, cell, range)
                    && JumpUtility.ValidJumpTarget(jumper, map, cell);
            }
            if (verb != null)
            {
                // The body of the shoot-line patch WITHOUT the patch's side effect. See the
                // banner: CanHitTargetFrom would park a solution per cell.
                return ABCombatV2.TrySolve(verb, root, cell, out ABShotSolution _);
            }
            return ABShaft.TrySolve(map, root, cell, range, minRange, overheadFire: false,
                out ABShotSolution _);
        }

        /// <summary>
        /// A cell on another band, brought into the viewed one - but ONLY when the column
        /// genuinely shows it (rule 26). `shown == c` is the whole test: it asks the renderer's
        /// own resolver what that column displays and insists on the answer being this exact
        /// cell, so an overlay can never be drawn for something hidden under a solid floor.
        ///
        /// Bands ABOVE the view are refused outright: the mod's see-through rendering is
        /// downward-only, so there is no column that shows them and lifting would invent a
        /// highlight for something not on screen.
        /// </summary>
        public static bool TryLiftCell(Map map, ABBandMap bands, int viewBand, IntVec3 c,
            out IntVec3 lifted)
        {
            lifted = c;
            if (map == null || bands == null || !bands.Banded || !c.InBounds(map))
            {
                return false;
            }
            int band = bands.BandOf(c);
            if (band == viewBand || band > viewBand)
            {
                return false;
            }
            IntVec3 v = bands.Translate(c, viewBand);
            if (!v.InBounds(map) || bands.InGutter(v))
            {
                return false;
            }
            if (!ABBands.TryResolveVisibleFrom(map, bands, v, requireUnfogged: true,
                    out IntVec3 shown, out int _))
            {
                return false;
            }
            if (shown != c)
            {
                return false;
            }
            lifted = v;
            return true;
        }
    }

    /// <summary>
    /// PIECE 1 - THE JUMP RING. Replaces <c>Verb_Jump.DrawHighlight</c> and its ability twin,
    /// whose bodies are three public draw calls each, so re-emitting them band-aware costs
    /// nothing in upkeep and gains the below-level half of the ring.
    ///
    /// ⚠ ONE BEHAVIOURAL DIFFERENCE FROM VANILLA, DELIBERATE: vanilla's ring predicate is
    /// <c>GenSight.LineOfSight(...) &amp;&amp; ValidJumpTarget(...)</c> and omits the range
    /// test that <c>CanHitTargetFrom</c> applies, because the radial disc already bounds it.
    /// Ours calls <c>CanHitTargetFrom</c> outright, so the ring is bounded by the same
    /// arithmetic that will judge the click - including the vertical cost per level, which is
    /// the entire point.
    /// </summary>
    public static class ABJumpRing
    {
        public static bool Draw(Verb verb, LocalTargetInfo target)
        {
            try
            {
                Pawn caster = verb?.CasterPawn;
                if (caster == null || !caster.Spawned || !ABGuard.On(ABGuard.Rendering))
                {
                    return true;
                }
                Map map = caster.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true; // ordinary map: vanilla's ring is correct and cheaper
                }
                int viewBand = ABBandView.CurrentBand(map);
                if (target.IsValid
                    && JumpUtility.ValidJumpTarget(caster, map, target.Cell))
                {
                    GenDraw.DrawTargetHighlightWithLayer(
                        ABUIGeometry.LiftToView(bands, viewBand, target.CenterVector3),
                        AltitudeLayer.MetaOverlays);
                }
                float range = verb.EffectiveRange;
                if (range > 0f && range < GenRadial.MaxRadialPatternRadius)
                {
                    List<IntVec3> cells = ABRangeOverlay.ReachSet(map, bands, viewBand, verb,
                        caster, caster.Position, range, 0f, ABRangeOverlay.Kind.Jump);
                    GenDraw.DrawFieldEdges(cells, Color.white);
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 jump ring");
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Verb_Jump), nameof(Verb_Jump.DrawHighlight))]
    public static class Patch_VerbJump_ABBandRing
    {
        private static bool Prefix(Verb_Jump __instance, LocalTargetInfo target)
        {
            return ABJumpRing.Draw(__instance, target);
        }
    }

    [HarmonyPatch(typeof(Verb_CastAbilityJump), nameof(Verb_CastAbilityJump.DrawHighlight))]
    public static class Patch_VerbCastAbilityJump_ABBandRing
    {
        private static bool Prefix(Verb_CastAbilityJump __instance, LocalTargetInfo target)
        {
            return ABJumpRing.Draw(__instance, target);
        }
    }

    /// <summary>
    /// PIECE 2 - THE GENERIC RANGE RING, for every weapon, turret, psycast and placement
    /// preview. Same treatment as the jump ring, over vanilla's own body.
    ///
    /// ⚠ THE MINIMUM-RANGE RING IS DRAWN AROUND THE ANCHOR, NOT THE CASTER. Minimum range is
    /// measured band-locally by the solver (a mortar cannot shell its own feet, one level down
    /// or not), so the inner circle belongs over the caster's COLUMN in the viewed band. Left
    /// at the caster's real cell it would be drawn a Slot off screen whenever you look at
    /// another level, which is the same defect this whole file exists to fix.
    /// </summary>
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.DrawRadiusRing),
        new Type[] { typeof(IntVec3), typeof(Verb) })]
    public static class Patch_VerbProperties_ABBandRangeRing
    {
        private static bool Prefix(VerbProperties __instance, IntVec3 center, Verb verb)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABGuard.On(ABGuard.Rendering))
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                // Vanilla's own four bail-outs, kept verbatim so a banded map suppresses
                // exactly what an ordinary one does.
                if (Find.World.renderer.wantedMode == WorldRenderMode.Planet
                    || __instance.IsMeleeAttack || !__instance.targetable)
                {
                    return false;
                }
                int viewBand = ABBandView.CurrentBand(map);
                IntVec3 anchor = bands.Translate(center, viewBand);
                float min = __instance.EffectiveMinRange(allowAdjacentShot: true);
                float max = verb != null ? verb.EffectiveRange : __instance.range;
                if (min > 0f && min < GenRadial.MaxRadialPatternRadius)
                {
                    GenDraw.DrawRadiusRing(anchor, min);
                }
                if (!(max < (float)(map.Size.x + map.Size.z))
                    || !(max < GenRadial.MaxRadialPatternRadius))
                {
                    return false; // vanilla refuses to draw rings it has no radial pattern for
                }
                List<IntVec3> cells = ABRangeOverlay.ReachSet(map, bands, viewBand, verb, null,
                    center, max, min, ABRangeOverlay.Kind.Verb);
                GenDraw.DrawFieldEdges(cells, Color.white);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 band range ring");
            }
            return true;
        }
    }

    /// <summary>
    /// PIECE 3 - THE TARGET BOX, wherever it is drawn from.
    ///
    /// Patched at GenDraw rather than at each of the dozen verbs and abilities that call it,
    /// for the same reason GenUI.ThingsUnderMouse was the single interception for targeting:
    /// one choke point serves every caller and none of them has to know a band exists.
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawTargetHighlightWithLayer),
        new Type[] { typeof(Vector3), typeof(AltitudeLayer) })]
    public static class Patch_GenDraw_ABLiftTargetHighlightVec
    {
        private static void Prefix(ref Vector3 c)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                if (ABRangeOverlay.TryLiftCell(map, bands, ABBandView.CurrentBand(map),
                        c.ToIntVec3(), out IntVec3 lifted))
                {
                    // Keep the sub-cell fraction: an ability aimed at a pawn is highlighted on
                    // the pawn's true centre, not snapped to the cell corner.
                    c = new Vector3(c.x, c.y, c.z + (lifted.z - c.ToIntVec3().z));
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 target highlight lift");
            }
        }
    }

    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawTargetHighlightWithLayer),
        new Type[] { typeof(IntVec3), typeof(AltitudeLayer), typeof(Material) })]
    public static class Patch_GenDraw_ABLiftTargetHighlightCell
    {
        private static void Prefix(ref IntVec3 c)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                if (ABRangeOverlay.TryLiftCell(map, bands, ABBandView.CurrentBand(map), c,
                        out IntVec3 lifted))
                {
                    c = lifted;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 target highlight lift");
            }
        }
    }

    /// <summary>
    /// PIECE 3b - THE AREA-OF-EFFECT CIRCLE around a below-band target.
    ///
    /// ⚠ ONLY WHEN THE CALLER PASSED NO PREDICATE, AND THAT GUARD IS LOAD-BEARING. A
    /// predicated ring's lambda is closed over the ORIGINAL centre (vanilla's is
    /// `c => GenSight.LineOfSight(center, c, map)`), so moving the centre without moving the
    /// closure would evaluate the new cells against the old origin - a ring drawn in the right
    /// place from the wrong maths. Every predicated caller worth fixing is already handled
    /// above by replacing its whole method, so there is nothing here to lose.
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawRadiusRing),
        new Type[] { typeof(IntVec3), typeof(float), typeof(Color), typeof(Func<IntVec3, bool>) })]
    public static class Patch_GenDraw_ABLiftRadiusRing
    {
        private static void Prefix(ref IntVec3 center, Func<IntVec3, bool> predicate)
        {
            try
            {
                if (predicate != null)
                {
                    return;
                }
                Map map = Find.CurrentMap;
                if (map == null || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                if (ABRangeOverlay.TryLiftCell(map, bands, ABBandView.CurrentBand(map), center,
                        out IntVec3 lifted))
                {
                    center = lifted;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 radius ring lift");
            }
        }
    }
}
