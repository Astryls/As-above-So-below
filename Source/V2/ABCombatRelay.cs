using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// CROSS-LEVEL COMBAT, RENDER SIDE. The disclosed beta gap
    /// (<c>asb2-combat-experimental</c>: "shots fired UPWARD are not rendered") lives here.
    ///
    /// ⚠ FIRST, THE FOUR CASES, BECAUSE THREE OF THEM ALREADY WORKED AND ONLY ONE IS THE
    /// BUG. The projectile's ORIGIN is remapped into the target's band (see
    /// Patch_Projectile_ABCrossBandOrigin), so a cross-band bullet lives ENTIRELY in the
    /// TARGET's band for its whole flight. Therefore:
    ///
    ///   A. viewing UPPER, shooting DOWN  - bullet is in the lower band, the see-below
    ///      realtime pass draws it. Worked, but ONE band only.
    ///   B. viewing UPPER, being shot at from BELOW - bullet is in the upper band, which is
    ///      the viewed band. Vanilla draws it natively. Always worked.
    ///   C. viewing LOWER, shooting UP - bullet is in the band ABOVE the view. Nothing draws
    ///      it, and nothing ever will, because the mod does not render upward. THIS WAS THE
    ///      DISCLOSED BUG.
    ///   D. viewing LOWER, being shot at from ABOVE - bullet is in the lower band, the
    ///      viewed band. Vanilla draws it. Always worked.
    ///
    /// So "upward combat does not render" was never a missing upward VIEW. It is one case:
    /// a round in flight on a band above the one being watched.
    ///
    /// ⚠⚠ CASE C'S RULE: YOU SEE IT THROUGH THE HOLES IN YOUR CEILING. A round on a band
    /// above the view is drawn, translated down, exactly while the COLUMN under its current
    /// cell is open air the whole way to the viewed band - ABShaft.ColumnOpen, the same
    /// predicate the ballistics use, one copy (§14). Over a wide gap that is the full flight
    /// in real time; where the ceiling is solid the round is hidden, which is true. Roofs
    /// come free: a roof on a lower level writes AB_RoofSurface into the band above it, so
    /// the open-air test already refuses to see through them.
    ///
    /// (The first version drew case C only within the opening's mouth, on the argument that
    /// relaying the whole flight lies about where the round is. The column rule replaced it
    /// at the user's request for real-time visibility - and it is BETTER geometry, not a
    /// compromise: it lies exactly nowhere, it just looks through every hole instead of the
    /// one the shot was solved through. The mouth blip survives as its degenerate case.)
    ///
    /// The OPENING MARKER stays the readable half: any opening that fire is passing through
    /// is flagged for a moment, tinted by whether it is your fire going out or someone
    /// else's coming in. From below that is "you are taking fire through that hole", which a
    /// tracer alone would not say.
    ///
    /// ⚠ THIS FILE ALSO OWNS EVERY CROSS-BAND PROJECTILE'S DRAW, INCLUDING CASE A. The
    /// see-below realtime pass is single-band by construction (§5's documented last corner),
    /// so a shot two levels down was invisible from the top anyway. Handing all four cases
    /// to one owner is what stops the two passes double-drawing - see the exclusion in
    /// ABBelowDynamicDraw.DrawBelowRealtimeThings.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABCombatRelay
    {
        /// <summary>Ticks an opening stays flagged after fire passes through it. Long enough
        /// to read during a firefight, short enough that it stops the moment the shooting
        /// does.</summary>
        private const int OpeningMarkerTicks = 40;

        private const int MaxMarkers = 32;

        private static readonly Material OutgoingMat = MaterialPool.MatFrom(
            "UI/Overlays/AB_ShotThrough", ShaderDatabase.MetaOverlay,
            new Color(0.85f, 0.95f, 1f, 0.85f));

        private static readonly Material IncomingMat = MaterialPool.MatFrom(
            "UI/Overlays/AB_ShotThrough", ShaderDatabase.MetaOverlay,
            new Color(1f, 0.35f, 0.3f, 0.9f));

        private struct Live
        {
            /// <summary>Thing, not Projectile: Combat Extended's ProjectileCE is a
            /// ThingWithComps that never touches Verse.Projectile, and the relay's draw
            /// path only needs DrawPos/Position/DynamicDrawPhaseAt - all Thing members.
            /// One relay serves both worlds (§14: three copies, two right).</summary>
            public Thing proj;

            /// <summary>The band the projectile physically flies in (the target's band).</summary>
            public int band;

            /// <summary>The band the shot was fired FROM.</summary>
            public int fromBand;

            /// <summary>The opening it came through, in the UPPER of the two bands. Invalid
            /// for overhead fire, which does not use one.</summary>
            public IntVec3 opening;
        }

        private struct Marker
        {
            public IntVec3 opening;

            public int upperBand;

            public int lowerBand;

            public bool incoming;

            public int tick;
        }

        /// <summary>Keyed by the Map OBJECT, never by uniqueID: ids restart at zero every new
        /// game, and a static keyed by one is exactly the leak that made wormholes and the
        /// viewed band bleed between colonies (§38).</summary>
        private static readonly ConditionalWeakTable<Map, List<Live>> live =
            new ConditionalWeakTable<Map, List<Live>>();

        private static readonly ConditionalWeakTable<Map, List<Marker>> markers =
            new ConditionalWeakTable<Map, List<Marker>>();

        // Observe-only counters for `AB2: combat report`.
        public static int relayedProjectiles;

        public static int relayDraws;

        public static int markerDraws;

        public static void ResetCounters()
        {
            relayedProjectiles = 0;
            relayDraws = 0;
            markerDraws = 0;
        }

        public static string CounterReport()
        {
            return "relay: registered=" + relayedProjectiles + " draws=" + relayDraws
                + " markerDraws=" + markerDraws;
        }

        /// <summary>
        /// THE SHOT SOLUTION HANDOFF.
        ///
        /// <c>Verb_LaunchProjectile.TryCastShot</c> calls TryFindShootLineFromTo and then
        /// Launch inside one call stack, but Launch is handed only an origin vector - the
        /// opening the solver chose is gone by then. Rather than solve twice, the shoot-line
        /// patch parks its answer here and Launch collects it.
        ///
        /// ⚠ [ThreadStatic] plus a target/tick/band-pair triple check, not a plain static. A
        /// stale solution applied to the wrong shot would put a muzzle flash in a wall, and
        /// the failure would be invisible in a log. If the triple does not match we simply
        /// have no opening and fall back to the shooter's own column, which is what shipped.
        ///
        /// ⚠⚠ THE CASTER IS DELIBERATELY *NOT* PART OF THE CHECK, AND THAT IS NOT LAZINESS.
        /// The verb's caster is the TURRET, but Verb_LaunchProjectile.TryCastShot launches
        /// with `manningPawn` as the launcher whenever a CompMannable has someone on it - so a
        /// manned mortar records under the turret and collects under the pawn, and an identity
        /// check would silently never match on exactly the weapon class the user asked about.
        /// The band pair plus the target cell plus the tick is a tight enough triple: the only
        /// collision it admits is two shooters firing at the same cell from the same band pair
        /// in the same tick, who would share an opening anyway.
        /// </summary>
        [ThreadStatic]
        private static Thing lastCaster;

        [ThreadStatic]
        private static IntVec3 lastTargetCell;

        [ThreadStatic]
        private static int lastTick;

        [ThreadStatic]
        private static ABShotSolution lastSolution;

        public static void RecordSolution(Thing caster, IntVec3 targetCell, ABShotSolution sol)
        {
            lastCaster = caster;
            lastTargetCell = targetCell;
            lastTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            lastSolution = sol;
        }

        /// <summary>
        /// §66c: re-key the pending solution to a wild miss's FINAL destination.
        ///
        /// TryCastShot records under the INTENDED target cell; a failed accuracy roll then
        /// rewrites the shoot line's Dest (a scatter PLUS a blocker walk that can drag the
        /// cell many cells toward the shooter's column, §41), and Launch is handed only the
        /// rewritten cell. The 20-cell near-match below was built for the scatter, and the
        /// field run §66c still caught a wild miss falling back to the shooter's-column
        /// origin. The wild-miss patch sits INSIDE the cast stack at the exact moment the
        /// final cell exists, so it re-keys the pending record instead of hoping a radius
        /// covers whatever the walk did.
        ///
        /// Guarded on tick freshness and the destination band, the same discriminators
        /// TryTakeSolution itself trusts, so a stale or foreign record can never be re-keyed
        /// onto someone else's shot.
        /// </summary>
        public static void RebindPendingDest(int destBand, IntVec3 newDest)
        {
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (lastSolution.valid && lastTick == now && lastSolution.targetBand == destBand)
            {
                lastTargetCell = newDest;
            }
        }

        public static bool TryTakeSolution(IntVec3 targetCell, int bandFrom, int bandTo,
            out ABShotSolution sol)
        {
            sol = lastSolution;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (!sol.valid || now != lastTick || sol.rootBand != bandFrom
                || sol.targetBand != bandTo)
            {
                // §66c NARRATION (rule 15). The field fallback said "no parked solution"
                // without saying WHICH guard declined, and the logs rotated before the
                // question could be asked. Name the condition, so the next occurrence is a
                // diagnosis instead of a mystery. Launch-frequency path: gated, cheap.
                if (ABV2Debug.LogCombat)
                {
                    ABV2Debug.Combat("parked solution declined: " + (!sol.valid
                        ? "no valid record"
                        : now != lastTick
                            ? "stale tick (recorded " + lastTick + ", now " + now + ")"
                            : sol.rootBand != bandFrom
                                ? "rootBand " + sol.rootBand + " vs launch " + bandFrom
                                : "targetBand " + sol.targetBand + " vs launch " + bandTo));
                }
                sol = default(ABShotSolution);
                return false;
            }
            // ⚠ NEAR-MATCH ON THE CELL, NOT EQUALITY. A wild or forced miss launches at a
            // cell SCATTERED around the solved target, so an exact-cell check silently
            // dropped the parked solution for every missed shot - and a rapid-fire burst is
            // mostly misses, so most pellets fell back to "emerge at the shooter's column"
            // instead of coming out of the opening the shot was solved through. Same tick,
            // same band pair, within scatter range of the solved cell = the same shot. The
            // 15-cell tolerance comfortably covers vanilla's wild scatter and mortar
            // forced-miss radii; a genuine second shooter colliding inside all four of those
            // conditions would share the opening anyway.
            // ⚠ 20 CELLS, RAISED FROM 15 ON MEASUREMENT (run #405): an LMG's wild scatter at
            // long range lands ~17 cells from the solved cell, and both logged fallbacks that
            // run were exactly this - pellets that lost their opening to a cap one size too
            // small. A QUANTISED PARAMETER HAS FEWER LEGAL VALUES THAN IT LOOKS: the only
            // meaningful settings here are "covers vanilla's worst scatter" and "does not",
            // and 15 was the second one.
            if (targetCell != lastTargetCell)
            {
                IntVec3 d = targetCell - lastTargetCell;
                if (d.LengthHorizontalSquared > 400f)
                {
                    if (ABV2Debug.LogCombat)
                    {
                        ABV2Debug.Combat("parked solution declined: launch cell " + targetCell
                            + " is " + d.LengthHorizontal.ToString("0.0")
                            + " cells from recorded " + lastTargetCell
                            + " (cap 20) - rebind missed?");
                    }
                    sol = default(ABShotSolution);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Peek the parked solution for a launch that knows only its ORIGIN band - Combat
        /// Extended's Launch signature carries no target cell to near-match on. Same tick +
        /// matching root band is the discriminator; CE casts record and launch inside one
        /// TryCastShot stack (pellets included), so the parked record is always this cast's.
        /// </summary>
        public static bool TryPeekSolutionFor(int rootBand, out ABShotSolution sol)
        {
            sol = lastSolution;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (!sol.valid || now != lastTick || sol.rootBand != rootBand
                || sol.rootBand == sol.targetBand)
            {
                sol = default(ABShotSolution);
                return false;
            }
            return true;
        }

        /// <summary>Is this projectile one the relay has taken responsibility for drawing?
        /// Read by the see-below realtime pass so the two never both draw it.</summary>
        public static bool Handles(Thing t)
        {
            if (t == null || t.Map == null)
            {
                return false;
            }
            if (!live.TryGetValue(t.Map, out List<Live> list) || list.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].proj == t)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Called from the projectile origin patch, which is the producer - the one
        /// place that knows a shot crossed bands.</summary>
        /// <summary>The launcher is passed in rather than read off the projectile:
        /// <c>Projectile.launcher</c> is protected, and the Launch prefix has it to hand
        /// anyway.</summary>
        public static void Register(Thing proj, Thing launcher, int fromBand, int toBand,
            ABShotSolution sol)
        {
            if (proj == null)
            {
                return;
            }
            Map map = proj.Map ?? launcher?.Map;
            if (map == null)
            {
                return;
            }
            List<Live> list = live.GetValue(map, _ => new List<Live>());
            list.Add(new Live
            {
                proj = proj,
                band = toBand,
                fromBand = fromBand,
                opening = sol.valid && !sol.overhead ? sol.opening : IntVec3.Invalid
            });
            relayedProjectiles++;

            if (sol.valid && !sol.overhead && sol.opening.IsValid)
            {
                bool incoming = launcher != null && Faction.OfPlayerSilentFail != null
                    && launcher.HostileTo(Faction.OfPlayer);
                MarkOpening(map, sol.opening, Mathf.Max(fromBand, toBand),
                    Mathf.Min(fromBand, toBand), incoming);
            }
        }

        private static void MarkOpening(Map map, IntVec3 opening, int upperBand, int lowerBand,
            bool incoming)
        {
            List<Marker> list = markers.GetValue(map, _ => new List<Marker>());
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opening == opening && list[i].upperBand == upperBand)
                {
                    Marker m = list[i];
                    m.tick = now;
                    m.incoming = incoming;
                    list[i] = m;
                    return;
                }
            }
            if (list.Count >= MaxMarkers)
            {
                list.RemoveAt(0);
            }
            list.Add(new Marker
            {
                opening = opening,
                upperBand = upperBand,
                lowerBand = lowerBand,
                incoming = incoming,
                tick = now
            });
        }

        public static void Draw(Map map)
        {
            if (map == null || !ABGuard.On(ABGuard.Rendering) || !ABCombatV2.Enabled)
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            DrawProjectiles(map, bands, viewBand);
            DrawMarkers(map, bands, viewBand);
        }

        private static void DrawProjectiles(Map map, ABBandMap bands, int viewBand)
        {
            if (!live.TryGetValue(map, out List<Live> list) || list.Count == 0)
            {
                return;
            }
            int slot = bands.Slot;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Live e = list[i];
                // Pruned lazily on the draw pass rather than hooked on destroy: a projectile
                // leaves by impact, by Destroy, or with the map, and one liveness test here
                // covers all three without three more patches.
                if (e.proj == null || e.proj.Destroyed || !e.proj.Spawned || e.proj.Map != map)
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (e.band == viewBand)
                {
                    continue; // it is in the viewed band; vanilla draws it natively (cases B, D)
                }
                try
                {
                    Vector3 loc = e.proj.DrawPos;
                    int dz = (viewBand - e.band) * slot;

                    if (e.band > viewBand)
                    {
                        // CASE C: the round is on a level ABOVE the one being watched. Drawn
                        // through the holes in the ceiling - visible exactly while the column
                        // under its CURRENT cell is open air all the way down to the view.
                        // Works for overhead fire too (a mortar shell arcing up through the
                        // gap), which the old mouth-only rule skipped for want of an opening.
                        IntVec3 cell = loc.ToIntVec3();
                        if (!cell.InBounds(map) || bands.BandOf(cell) != e.band
                            || bands.InGutter(cell))
                        {
                            continue; // over the seam or off its own band: nothing to see
                        }
                        if (!ABShaft.ColumnOpen(map, bands, cell, e.band, viewBand))
                        {
                            continue; // ceiling is solid here
                        }
                        DrawAt(e.proj, new Vector3(loc.x, loc.y, loc.z + dz));
                        continue;
                    }

                    // CASE A: the round is on a level below the one being watched. Draw it
                    // through the column, using the SHARED descent rule so it appears exactly
                    // where the floor below is actually visible - and for any depth, not just
                    // one band.
                    IntVec3 projCell = e.proj.Position;
                    IntVec3 above = bands.Translate(projCell, viewBand);
                    if (!above.InBounds(map) || bands.InGutter(above))
                    {
                        continue;
                    }
                    if (!ABBands.TryResolveVisibleBelow(map, bands, above, out IntVec3 seen,
                            out int drop))
                    {
                        continue;
                    }
                    if (bands.BandOf(seen) != e.band)
                    {
                        continue; // something opaque between us and it
                    }
                    DrawAt(e.proj, new Vector3(loc.x, loc.y, loc.z + drop));
                }
                catch (Exception ex)
                {
                    Log.WarningOnce(ABLog.Tag + " V2: combat relay draw threw: " + ex.Message,
                        762195931);
                    list.RemoveAt(i);
                }
            }
        }

        /// <summary>All three phases, for the reason spelled out in
        /// ABBelowDynamicDraw.DrawBelowRealtimeThings: a thing culled from the camera's view
        /// rect never gets EnsureInitialized or ParallelPreDraw from vanilla, yet its cached
        /// render results stay flagged valid, so skipping a phase is how it draws stale.</summary>
        private static void DrawAt(Thing t, Vector3 loc)
        {
            t.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
            t.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
            t.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
            relayDraws++;
        }

        /// <summary>
        /// The opening marker. Drawn in the VIEWED band whenever that band sits between the
        /// two ends of the shot, which is what makes one rule serve both directions: from
        /// above you see which hole your pawns are firing through, from below you see which
        /// hole is being fired through at you.
        /// </summary>
        private static void DrawMarkers(Map map, ABBandMap bands, int viewBand)
        {
            if (!markers.TryGetValue(map, out List<Marker> list) || list.Count == 0)
            {
                return;
            }
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Marker m = list[i];
                int age = now - m.tick;
                // Rewind-proof, by §38's rule: loading an earlier save moves TicksGame
                // BACKWARDS, so a plain `now - tick > life` test can never expire an entry
                // stamped in a future that no longer happens.
                if (age < 0 || age > OpeningMarkerTicks)
                {
                    list.RemoveAt(i);
                    continue;
                }
                if (viewBand > m.upperBand || viewBand < m.lowerBand)
                {
                    continue; // the shot never passed through the level being watched
                }
                IntVec3 here = bands.Translate(m.opening, viewBand);
                if (!here.InBounds(map) || bands.InGutter(here))
                {
                    continue;
                }
                float fade = 1f - age / (float)OpeningMarkerTicks;
                Vector3 pos = here.ToVector3ShiftedWithAltitude(AltitudeLayer.MetaOverlays);
                // Rotated a half turn for fire heading UP through the opening, so the glyph
                // reads as a direction rather than just a warning.
                Quaternion rot = Quaternion.identity;
                if (viewBand <= m.lowerBand && m.upperBand != m.lowerBand)
                {
                    rot = Quaternion.Euler(0f, 180f, 0f);
                }
                Material mat = m.incoming ? IncomingMat : OutgoingMat;
                Graphics.DrawMesh(MeshPool.plane10,
                    Matrix4x4.TRS(pos, rot, new Vector3(1f + 0.35f * fade, 1f, 1f + 0.35f * fade)),
                    FadedMaterialPool.FadedVersionOf(mat, 0.35f + 0.65f * fade), 0);
                markerDraws++;
            }
        }
    }

    /// <summary>
    /// Runs after the see-below dynamic pass, so a relayed round composes over the level
    /// below rather than under it.
    /// </summary>
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    public static class Patch_DynamicDrawManager_ABCombatRelay
    {
        private static readonly AccessTools.FieldRef<DynamicDrawManager, Map> MapRef =
            AccessTools.FieldRefAccess<DynamicDrawManager, Map>("map");

        /// <summary>Priority below the below-pawn postfix so the ordering above is real and
        /// not incidental. Harmony orders same-target postfixes by priority, not by file.</summary>
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(DynamicDrawManager __instance)
        {
            try
            {
                ABCombatRelay.Draw(MapRef(__instance));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: combat relay pass threw: " + e, 762195932);
            }
        }
    }
}
