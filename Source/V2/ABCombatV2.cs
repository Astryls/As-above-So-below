using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 cross-level combat, verb side.
    ///
    /// This is the ONE system that does not come free from the banded design, and it is
    /// worth being clear about why. Hauling, needs, work, prisoners and trade are GRAPH
    /// problems: the wormhole RegionLink makes connectivity correct and vanilla's
    /// reachability does the rest. Combat is a GEOMETRY problem - GenSight and weapon range
    /// are computed in flat 2D cell space, and the band layout fakes vertical adjacency
    /// using DISTANCE. To vanilla, a pawn one level up is literally one Slot north, behind
    /// an impassable gutter: out of range, no line of sight.
    ///
    /// The geometry itself now lives in <see cref="ABShaft"/> - one solver, one set of
    /// rules, one cache. This file is the VERB BRIDGE: it decides which rule a given verb
    /// plays by (balcony or map coordinates), feeds the solver a verb's real range and
    /// minimum range, and then repairs the four things vanilla derives from the target's
    /// real cell: the shot line, the accuracy roll, which way the pawn faces, and where the
    /// projectile starts.
    ///
    /// ⚠ EVERY ONE OF THOSE FOUR IS THE SAME BUG WEARING A DIFFERENT HAT. A cross-band
    /// target's real cell is a whole Slot away, so anything that measures or aims at it
    /// produces a number that is off by exactly one band. There is no fifth symptom class;
    /// if a new one appears, it is a fifth consumer of the raw cell that has not been found
    /// yet. Look for a raw subtraction, not for a new mechanism.
    /// </summary>
    public static class ABCombatV2
    {
        public static bool Enabled => ABGuard.On(ABGuard.Combat);

        /// <summary>Translate a cell from its own band into <paramref name="toBand"/>,
        /// preserving the in-band position. Bands are aligned 1:1, so this is what makes
        /// "directly above" meaningful.</summary>
        public static IntVec3 ToBand(ABBandMap bands, IntVec3 c, int toBand)
        {
            return bands.Translate(c, toBand);
        }

        /// <summary>
        /// Force a cell onto <paramref name="band"/>'s playable rows.
        ///
        /// ONE copy, used by both scatter paths, because they are the same transform and
        /// §14 has the receipt for what happens otherwise (THREE COPIES OF ONE TRANSFORM, TWO
        /// RIGHT). Translate first so the in-band offset is preserved when the scatter merely
        /// crossed the seam; clamp second for a scatter big enough to overshoot the band.
        /// </summary>
        public static IntVec3 ClampIntoBand(Map map, ABBandMap bands, IntVec3 cell, int band)
        {
            IntVec3 moved = bands.Translate(cell, band);
            CellRect rect = bands.RectOfBand(band);
            moved = new IntVec3(Mathf.Clamp(moved.x, rect.minX, rect.maxX), 0,
                Mathf.Clamp(moved.z, rect.minZ, rect.maxZ));
            return moved.InBounds(map) ? moved : cell;
        }

        /// <summary>
        /// THE verb-aware entry point. Everything that wants to know whether a verb can
        /// reach across bands calls this and nothing else - the patch below, target
        /// acquisition, the player's float menu, the targeter, and the renderer.
        ///
        /// Returning the whole <see cref="ABShotSolution"/> rather than a bool is what lets
        /// the renderer draw the tracer through the right opening instead of guessing.
        /// </summary>
        public static bool TrySolve(Verb verb, IntVec3 root, LocalTargetInfo targ,
            out ABShotSolution sol, bool ignoreRange = false)
        {
            sol = default(ABShotSolution);
            if (!OwnsPair(verb, root, targ))
            {
                return false;
            }
            Thing caster = verb.caster;
            // ⚠ ignoreRange IS NOT DECORATION. Some callers ask this method purely as a line
            // of sight test ("could I hit it from over there, range aside"), and answering
            // with a range verdict makes those callers wrong in a way that only shows up as
            // pawns refusing to reposition. Range is handled by passing the solver limits it
            // cannot fail rather than by a second code path.
            float range = ignoreRange ? float.MaxValue : verb.EffectiveRange;
            float minRange = ignoreRange ? 0f : verb.verbProps.EffectiveMinRange(targ, caster);
            return ABShaft.TrySolve(caster.Map, root, targ.Cell, range, minRange,
                ABShaft.IsOverheadFire(verb), out sol);
        }

        /// <summary>
        /// Is this (verb, root, target) triple a cross-band shot that THIS MOD is responsible
        /// for adjudicating? True means vanilla's answer must not be consulted at all -
        /// neither to permit nor to deny.
        ///
        /// ⚠⚠ THIS EXISTS BECAUSE "DECLINE TO HELP" IS NOT "DENY", AND THE DIFFERENCE WAS A
        /// SHIPPED BUG. §32d already records the rule for the pathing scope - EVERY EARLY
        /// RETURN IS "PERMIT" - and combat has exactly the same shape. When the solver said no
        /// the old prefix returned true and let vanilla decide, and vanilla decides on RAW
        /// distance: TryFindShootLineFromTo returns a shoot line for any verb with
        /// requireLineOfSight=false purely on range, and one Slot is comfortably inside mortar
        /// range. So a mortar could shell the basement through solid rock at 256 cells, with
        /// no opening, forever, and the log was clean because nothing threw.
        ///
        /// It cuts the other way too: two bands apart on a 254-cell layout is 512 cells, which
        /// is BEYOND mortar range, so vanilla would also have refused shots the map-coordinate
        /// rule should allow. One raw distance, two opposite errors - §1's anisotropy warning
        /// in its purest form.
        /// </summary>
        public static bool OwnsPair(Verb verb, IntVec3 root, LocalTargetInfo targ)
        {
            if (!Enabled || verb == null || verb.verbProps == null)
            {
                return false;
            }
            Thing caster = verb.caster;
            if (caster == null || !caster.Spawned)
            {
                return false;
            }
            // No reaching between levels with a knife. Also catches point-blank verbs, whose
            // shoot line vanilla resolves with CanReachImmediate - a question about walking,
            // which across bands is the router's business and not ours.
            if (verb.verbProps.IsMeleeAttack || verb.EffectiveRange <= 1.42f)
            {
                return false;
            }
            if (!targ.IsValid)
            {
                return false;
            }
            if (targ.HasThing && targ.Thing.Map != caster.Map)
            {
                return false; // genuinely another map; not our problem
            }
            ABBandMap bands = ABBands.CompOf(caster.Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            return bands.BandOf(root) != bands.BandOf(targ.Cell);
        }
    }

    /// <summary>
    /// Range and line of sight, at the single choke point both flow through.
    /// <c>Verb.CanHitTargetFrom</c> is four guard clauses and then a call to this, so one
    /// prefix covers targeting validation, AI target selection and the actual cast.
    ///
    /// ⚠ AND IT MUST BE A PREFIX THAT RETURNS TRUE, NOT A POSTFIX. Vanilla's body reaches
    /// <c>OutOfRange</c> before it ever considers line of sight, and one Slot is outside
    /// every weapon's range, so by postfix time the answer is already a hard false with no
    /// shoot line to repair.
    /// </summary>
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    public static class Patch_Verb_ABCrossBandShootLine
    {
        private static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref bool __result, bool ignoreRange)
        {
            try
            {
                if (!ABBands.Banded(__instance?.caster?.Map))
                {
                    return true;
                }
                if (ABCombatV2.TrySolve(__instance, root, targ, out ABShotSolution sol,
                        ignoreRange))
                {
                    ABV2Debug.Combat("shootline OK " + __instance.caster.LabelShortCap
                        + " " + root + " -> " + targ.Cell + " via "
                        + (sol.overhead ? "overhead fire" : "opening " + sol.opening)
                        + " dist " + sol.distance.ToString("0.0"));
                    // Park the answer for Projectile.Launch, which is handed only an origin
                    // vector and would otherwise have to solve the geometry a second time.
                    ABCombatRelay.RecordSolution(__instance.caster, targ.Cell, sol);
                    resultingLine = new ShootLine(root, targ.Cell);
                    __result = true;
                    return false;
                }
                // No solution, but the pair IS ours: DENY. See OwnsPair's banner - handing a
                // cross-band pair back to vanilla lets raw distance decide, which is both
                // more permissive and less permissive than the band rules, in different
                // places.
                if (ABCombatV2.OwnsPair(__instance, root, targ))
                {
                    ABV2Debug.Combat("shootline DENIED " + __instance.caster.LabelShortCap
                        + " " + root + " -> " + targ.Cell + " (no opening in range)");
                    resultingLine = new ShootLine(root, targ.Cell);
                    __result = false;
                    return false;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band shoot line");
            }
            return true;
        }
    }

    /// <summary>
    /// Where the shooter LOOKS.
    ///
    /// Facing is derived from the target's real cell, which for a cross-band target is a
    /// whole Slot away in +z or -z. The pawn therefore snaps to face straight north or
    /// south regardless of where the target actually is relative to the opening - the
    /// "shoots straight down" report. Facing the target TRANSLATED into the shooter's own
    /// band gives the true horizontal bearing, so the pawn turns toward the hole it is
    /// firing through.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.Face))]
    public static class Patch_RotationTracker_ABCrossBandFacing
    {
        /// <summary>Patches Face(Vector3), NOT FaceTarget.
        ///
        /// FaceTarget was the wrong hook: while a pawn is AIMING, UpdateRotation reads
        /// stance_Busy.focusTarg directly and calls Face(thing.DrawPos) - FaceTarget is
        /// never involved. That is why the body still snapped due south when shooting across
        /// bands. Face is the common bottleneck for every rotation path, so localizing here
        /// covers aiming, jobs and drafted orders alike.</summary>
        private static readonly AccessTools.FieldRef<Pawn_RotationTracker, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_RotationTracker, Pawn>("pawn");

        private static void Prefix(Pawn_RotationTracker __instance, ref Vector3 p)
        {
            try
            {
                Pawn pawn = PawnRef(__instance);
                if (pawn == null || !pawn.Spawned)
                {
                    return;
                }
                if (ABCombatGeometry.TryLocalize(pawn, p, out Vector3 local))
                {
                    p = local;
                }
            }
            catch
            {
                // Facing is cosmetic; never let it break the rotation tracker.
            }
        }
    }

    /// <summary>Cell-based facing takes the same treatment: UpdateRotation calls FaceCell
    /// when the focus target is a bare cell rather than a thing.</summary>
    [HarmonyPatch(typeof(Pawn_RotationTracker), nameof(Pawn_RotationTracker.FaceCell))]
    public static class Patch_RotationTracker_ABCrossBandFaceCell
    {
        private static readonly AccessTools.FieldRef<Pawn_RotationTracker, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_RotationTracker, Pawn>("pawn");

        private static void Prefix(Pawn_RotationTracker __instance, ref IntVec3 c)
        {
            try
            {
                Pawn pawn = PawnRef(__instance);
                if (pawn == null || !pawn.Spawned)
                {
                    return;
                }
                if (ABCombatGeometry.TryLocalize(pawn, c, out IntVec3 local))
                {
                    c = local;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Projectile origin. Without this the bullet is launched from the shooter's real
    /// position and has to physically cross a whole band - the gutter plus every
    /// intervening level - which both looks absurd and takes seconds to arrive.
    ///
    /// Because bands are aligned 1:1, translating the ORIGIN into the target's band puts
    /// the muzzle at the equivalent spot directly above or below, and the projectile then
    /// travels the short real horizontal distance. V1 needed a whole file
    /// (CrossGapProjectiles) to hand projectiles between two Maps; here it is one vector.
    ///
    /// ⚠ THE PROJECTILE THEREFORE LIVES ENTIRELY IN THE TARGET'S BAND, which is what makes
    /// the render relay necessary rather than optional: from the SHOOTER's band there is
    /// nothing to see. See ABCombatRelay.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new Type[]
    {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
    })]
    public static class Patch_Projectile_ABCrossBandOrigin
    {
        private static readonly AccessTools.FieldRef<Projectile, Vector3> OriginRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("origin");

        private static readonly AccessTools.FieldRef<Projectile, Vector3> DestinationRef =
            AccessTools.FieldRefAccess<Projectile, Vector3>("destination");

        private static readonly AccessTools.FieldRef<Projectile, int> TicksToImpactRef =
            AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");

        private static void Prefix(Projectile __instance, Thing launcher, ref Vector3 origin,
            LocalTargetInfo usedTarget, out string __state)
        {
            __state = "vanilla";
            try
            {
                if (launcher == null || !launcher.Spawned || !usedTarget.IsValid)
                {
                    return;
                }
                Map map = launcher.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABCombatV2.Enabled)
                {
                    return;
                }
                IntVec3 originCell = origin.ToIntVec3();
                int bandFrom = bands.BandOf(originCell);
                int bandTo = bands.BandOf(usedTarget.Cell);
                if (bandFrom == bandTo)
                {
                    __state = "same-band";
                    return;
                }
                Vector3 before = origin;
                // ⚠ THE ROUND COMES OUT OF THE OPENING, NOT OUT OF THE SHOOTER'S COLUMN.
                // Under the old shaft rule those were the same cell by definition, so
                // translating the shooter's own column was right by accident. Under the
                // balcony rule the chosen opening can be metres away, and the shooter's
                // column in the target's band is frequently solid rock or a wall - so the
                // muzzle flash appeared inside a wall and the round set off from there.
                ABCombatRelay.TryTakeSolution(usedTarget.Cell, bandFrom, bandTo,
                    out ABShotSolution sol);
                // ⚠⚠ y IS ZEROED, NOT PRESERVED, AND THE ZERO IS A MEASURED FIX (§41l).
                // StartingTicksToImpact divides the FULL 3D magnitude of (origin -
                // destination) by the def speed, and a caster's DrawPos.y is ~8.45 (its
                // altitude layer) while the destination's y is 0. Vanilla same-band shots
                // are 15-25 cells horizontal, so that vertical dead weight adds ~5% flight
                // time - invisible. Our opening-emergence shots fly only the DRIFT
                // horizontally (0.3-3 cells), so the 8.45 DOMINATED: run #404 measured a
                // charge rifle crossing 0.9 horizontal cells in 13 ticks - a round visibly
                // HOVERING at the mouth, reported as "projectiles move slower". The y
                // component is dead weight everywhere else (ExactPosition Yto0()s both ends
                // and re-adds def.Altitude; ExactRotation Yto0()s too), so zeroing it makes
                // flight time equal the horizontal distance the eye actually measures.
                if (sol.valid && !sol.overhead && sol.opening.IsValid)
                {
                    IntVec3 emerge = bands.Translate(sol.opening, bandTo);
                    origin = new Vector3(emerge.x + 0.5f, 0f, emerge.z + 0.5f);
                    __state = "opening " + sol.opening;
                }
                else
                {
                    float within = origin.z - bandFrom * bands.Slot;
                    origin = new Vector3(origin.x, 0f, bandTo * bands.Slot + within);
                    __state = "fallback(no parked solution; shooter's column)";
                }
                // Hand the round to the relay, which owns every cross-band projectile's draw.
                ABCombatRelay.Register(__instance, launcher, bandFrom, bandTo, sol);
                ABV2Debug.Combat("projectile origin " + before + " (band " + bandFrom + ") -> "
                    + origin + " (band " + bandTo + ") target " + usedTarget.Cell);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band projectile origin");
            }
        }

        /// <summary>
        /// THE SPANNING-FLIGHT DETECTOR. A projectile whose origin and destination sit in
        /// different bands is flying through the gutter and every level between - the exact
        /// thing this whole file exists to prevent, and the visible form of "fires straight
        /// down instead of at the target". By the time Launch returns, both private fields
        /// are final, so ANY mechanism that produces a spanning flight - a path we missed, a
        /// mod calling Launch directly, a future vanilla change - names itself here with the
        /// branch the prefix took, instead of costing another run of theorising. §10's rule:
        /// a diagnostic must print the intermediate values, not the verdict.
        /// </summary>
        private static void Postfix(Projectile __instance, string __state)
        {
            try
            {
                if (__instance == null || !__instance.Spawned)
                {
                    return;
                }
                Map map = __instance.Map;
                ABBandMap bands = map != null ? ABBands.CompOf(map) : null;
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                Vector3 o = OriginRef(__instance);
                Vector3 d = DestinationRef(__instance);
                int bandO = bands.BandOf(o.ToIntVec3());
                int bandD = bands.BandOf(d.ToIntVec3());
                // THE KINEMATICS PROBE, for "cross-band projectiles look slower". The flight
                // arithmetic is provably band-local and speed-preserving ON PAPER (Launch
                // computes ticksToImpact AFTER the origin remap, from the remapped pair), so
                // if rounds are genuinely slow in play, some branch is escaping the paper.
                // One line per cross-band launch, gated on the combat log toggle: the
                // measured tiles/tick against the def's own speed convicts or acquits in a
                // single reading. §10: print the intermediate values, not the verdict.
                if (ABV2Debug.LogCombat && __state != null && __state != "vanilla"
                    && __state != "same-band")
                {
                    // ⚠ HORIZONTAL distance, not 3D magnitude. The first version printed the
                    // 3D value and thereby HID the very bug it was built to find: the dead
                    // vertical component inflated dist and tti in the same proportion, so the
                    // ratio looked ~fine while the round crossed 0.9 cells in 13 ticks. The
                    // eye measures horizontal speed; the probe must too.
                    float dist = (o - d).Yto0().magnitude;
                    int tti = TicksToImpactRef(__instance);
                    float actual = tti > 0 ? dist / tti : dist;
                    ABV2Debug.Combat("kinematics " + __instance.def.defName
                        + " horiz " + dist.ToString("0.0") + " tti " + tti
                        + " => " + actual.ToString("0.000") + " tiles/tick vs def "
                        + __instance.def.projectile.SpeedTilesPerTick.ToString("0.000")
                        + " | " + __state);
                }
                if (bandO == bandD)
                {
                    return;
                }
                Log.WarningOnce(ABLog.Tag + " SPANNING PROJECTILE: " + __instance.def.defName
                    + " origin " + o + " (band " + bandO + ") -> destination " + d + " (band "
                    + bandD + "), originPath=" + (__state ?? "unknown")
                    + ". A projectile should never fly between bands; report this line.",
                    __instance.def.shortHash ^ 0x2AB41);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// A MISS MUST STAY ON ITS OWN LEVEL.
    ///
    /// <c>Verb_LaunchProjectile.TryCastShot</c> resolves a failed accuracy roll by calling
    /// <c>ShootLine.ChangeDestToMissWild</c>, which scatters the destination around the
    /// intended cell. On a banded map the scatter is applied to a cell that may be within a
    /// few rows of the band edge, so a wild miss can walk off the top of the level and land
    /// in the GUTTER - the impassable seam that by construction contains nothing and belongs
    /// to no level. The shell then detonates in a strip of dead air the player cannot see.
    ///
    /// Clamping the scattered destination back into the band it started in is the smaller
    /// half of the fix. Recorded band in a prefix rather than recomputed in the postfix
    /// because by then the original destination is gone.
    ///
    /// ⚠⚠ THE BIGGER HALF IS THE SOURCE TRANSLATION IN THE PREFIX, AND IT IS THE ANSWER TO
    /// "projectiles fire straight down/into the ground, especially on rapid-fire weapons".
    /// Vanilla's body does three things with the line: a scatter around Dest, a
    /// CellCanSeeCell(source, dest) check, and - when that fails - a walk along Points()
    /// FROM THE SOURCE that reassigns `dest = item` every step until the first Filled cell.
    /// Our cross-band shoot line spans bands, so:
    ///   * CellCanSeeCell always fails (the sight line crosses the gutter), so the walk
    ///     ALWAYS runs for a cross-band wild miss, and
    ///   * the walk marches through the SHOOTER'S OWN BAND first, so on any map with
    ///     terrain (a Mountainous tile above all) it parks the miss at the first rock or
    ///     wall a few cells from the shooter - which the launch then resolves at the
    ///     shooter's own column. Every wild pellet of a burst appeared to fire into the
    ///     ground at the shooter's feet; rapid-fire weapons wild-miss constantly, which is
    ///     exactly the reported shape.
    /// Translating the SOURCE into the destination's band makes all three steps band-local:
    /// the scatter's Dot rejection regains its intent (it was degenerate against a ±Slot
    /// vector), CellCanSeeCell answers within the target's band, and the blocker walk runs
    /// from the shooter's COLUMN toward the wild point through the terrain the pellet
    /// actually flies over. The Source is never read again after this method - TryCastShot
    /// spawned the projectile from it before the wild branch - so the mutation is contained.
    /// </summary>
    [HarmonyPatch(typeof(ShootLine), nameof(ShootLine.ChangeDestToMissWild))]
    public static class Patch_ShootLine_ABMissStaysInBand
    {
        private static void Prefix(ref ShootLine __instance, Map map, out int __state)
        {
            __state = -1;
            try
            {
                if (map == null)
                {
                    return; // debug-tool callers pass null; vanilla skips its walk too
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands != null && bands.Banded)
                {
                    __state = bands.BandOf(__instance.Dest);
                    IntVec3 src = __instance.Source;
                    if (bands.BandOf(src) != __state)
                    {
                        __instance = new ShootLine(bands.Translate(src, __state),
                            __instance.Dest);
                    }
                }
            }
            catch
            {
            }
        }

        private static void Postfix(ref ShootLine __instance, Map map, int __state)
        {
            try
            {
                if (__state < 0)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                IntVec3 dest = __instance.Dest;
                if (bands.BandOf(dest) == __state && !bands.InGutter(dest))
                {
                    return;
                }
                __instance = new ShootLine(__instance.Source,
                    ABCombatV2.ClampIntoBand(map, bands, dest, __state));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 wild-miss band clamp");
            }
        }
    }

    /// <summary>
    /// THE SECOND SCATTER PATH, and it is a different method from the first.
    ///
    /// A wild miss (accuracy roll failed) goes through ShootLine.ChangeDestToMissWild; a
    /// FORCED miss - the inherent inaccuracy of a mortar or a rocket - goes through
    /// Verb_LaunchProjectile.GetForcedMissTarget, which offsets currentTarget.Cell by a
    /// RadialPattern entry. Mortar forced-miss radii reach ten cells or more, so a shell aimed
    /// near the top of a band scatters straight over the seam and detonates in the gutter:
    /// impassable dead air that belongs to no level and that the player cannot see.
    ///
    /// ⚠ TWO METHODS, ONE RULE, ONE HELPER. Patching only the wild-miss path (which is what
    /// "a miss" means to a reader) would have left mortars - the weapon class this window is
    /// about - scattering into the seam, and the two paths do not share a line of code.
    /// COUNT THE EMITTERS.
    ///
    /// ⚠ A KNOWN, BOUNDED DEVIATION LIVES NEXT DOOR AND IS DELIBERATELY NOT FIXED.
    /// TryCastShot sizes the forced miss with
    /// <c>VerbUtility.CalculateAdjustedForcedMiss(radius, currentTarget.Cell - caster.Position)</c>,
    /// a RAW offset that measures a Slot across bands - §1's anisotropy trap again. It is left
    /// alone because the function only ever REDUCES the radius, and only below 7 cells: every
    /// mortar has a 29-cell minimum range, which the map-coordinate rule now enforces
    /// horizontally, so no accepted mortar shot can ever be in the reduced band and the patch
    /// would be a no-op. It would bite only a close cross-band shot from a non-overhead
    /// forced-miss weapon (a doomsday rocket through a hole), which scatters slightly wider
    /// than it should. Fixing it needs a latch keyed on nothing reliable, which is a worse
    /// trade than a documented rounding error. Recorded so the next window does not rediscover
    /// it as a bug.
    /// </summary>
    [HarmonyPatch(typeof(Verb_LaunchProjectile), "GetForcedMissTarget")]
    public static class Patch_VerbLaunchProjectile_ABForcedMissStaysInBand
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(Verb_LaunchProjectile), "GetForcedMissTarget")
                != null;
        }

        private static void Postfix(Verb_LaunchProjectile __instance, ref IntVec3 __result)
        {
            try
            {
                Thing caster = __instance?.caster;
                if (caster == null || !caster.Spawned || !__instance.CurrentTarget.IsValid)
                {
                    return;
                }
                Map map = caster.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABCombatV2.Enabled)
                {
                    return;
                }
                // The band the shell was AIMED at, which is the one it must land in - not the
                // caster's, because the whole point of the map-coordinate rule is that a
                // mortar can shell another level.
                int band = bands.BandOf(__instance.CurrentTarget.Cell);
                if (bands.BandOf(__result) == band && !bands.InGutter(__result))
                {
                    return;
                }
                IntVec3 before = __result;
                __result = ABCombatV2.ClampIntoBand(map, bands, __result, band);
                ABV2Debug.Combat("forced-miss target " + before + " left band " + band
                    + ", clamped to " + __result);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 forced-miss band clamp");
            }
        }
    }
}
