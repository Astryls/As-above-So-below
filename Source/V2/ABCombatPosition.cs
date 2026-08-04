using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WHERE AN AI PAWN STANDS TO SHOOT ACROSS A LEVEL.
    ///
    /// Reported as: "neutrals / allies aren't attacking hostile factions when they aren't
    /// colonists. They will walk up the stairs and then begin to engage, but not across
    /// levels." Two distinct defects behind one symptom, and BOTH had to be fixed for any of
    /// it to work - see ABCombatAcquisition for the first (which target gets chosen).
    ///
    /// THIS FILE IS THE SECOND: having chosen a cross-band target, the pawn could not find
    /// anywhere to shoot it from.
    ///
    /// <c>JobGiver_AIFightEnemy.TryGiveJob</c> stands and fires only when it already has a
    /// firing solution AND either cover or a target within five cells; otherwise it asks
    /// <c>TryFindShootingPosition</c> -> <c>CastPositionFinder.TryFindCastPosition</c> for
    /// somewhere better. That search is anchored on the TARGET:
    ///
    ///     int num2 = Mathf.CeilToInt(req.maxRangeFromTarget);
    ///     CellRect otherRect2 = new CellRect(targetLoc.x - num2, targetLoc.z - num2, ...);
    ///     cellRect.ClipInsideRect(otherRect2);
    ///
    /// so the candidate rect is a square around the target's REAL cell, one Slot away in the
    /// target's own band. Every cell the caster could actually stand on is outside it, and the
    /// per-cell filter repeats the same mistake
    /// (<c>(c - req.target.Position).LengthHorizontalSquared > maxRangeFromTargetSquared</c>).
    /// The search therefore returns false ALWAYS for a cross-band pair - not sometimes, not
    /// for awkward geometry, but by construction.
    ///
    /// ⚠⚠ AND THAT IS WHY THEY WALKED UP THE STAIRS. A failed TryFindShootingPosition makes
    /// TryGiveJob return null, the fight think-node declines, and a later node sends the pawn
    /// toward the enemy instead. The pawn was not "choosing" to close the distance; it was
    /// falling through to the only behaviour left. **The symptom named movement and the defect
    /// was in a range check** - the same shape as §41e, for the fifth time.
    ///
    /// ⚠ ONE PATCH, SIX CONSUMERS. Every TryFindShootingPosition override in the game
    /// (AIFightEnemies, AIDefendPawn, AIDefendPoint, AIAbilityFight, MetalhorrorFight,
    /// ShamblerFight) bottoms out here, so this is the choke point - the same reasoning that
    /// made GenUI.ThingsUnderMouse the single interception for player targeting.
    /// </summary>
    public static class ABCombatPosition
    {
        /// <summary>How far from its current cell a pawn will look for a firing position on a
        /// cross-band target. Deliberately small: standing still and firing is the correct
        /// answer almost always, because the balcony rule already lets it shoot from anywhere
        /// with sight of an opening. This is for the case where it is standing just off the
        /// sight line and a step or two fixes it.</summary>
        private const float SearchRadius = 12f;

        public static int solvedInPlace;

        public static int solvedByStep;

        public static int noPosition;

        public static void ResetCounters()
        {
            solvedInPlace = 0;
            solvedByStep = 0;
            noPosition = 0;
        }

        public static string CounterReport()
        {
            return "castPos: inPlace=" + solvedInPlace + " stepped=" + solvedByStep
                + " none=" + noPosition;
        }

        /// <summary>
        /// Band-local cast position search. Runs ONLY for a cross-band pair, where vanilla's
        /// answer is a guaranteed false.
        ///
        /// ⚠ NO COVER SCORING, AND THAT IS A DELIBERATE OMISSION RATHER THAN AN OVERSIGHT.
        /// Vanilla's evaluator weighs cover from the target, distance preference, avoid grids
        /// and reservations, all computed against the target's real cell. Reproducing that in
        /// band-local terms would be a second copy of a long private method - and §14 is
        /// explicit that reproducing a subsystem means ALL of it. What this needs is far
        /// smaller: the nearest cell this pawn can legally stand in that HAS a firing
        /// solution. Cover across a hole in the floor is close to meaningless anyway; the
        /// opening is the cover.
        /// </summary>
        public static bool TryFindBandLocalCastPosition(CastPositionRequest req, out IntVec3 dest)
        {
            dest = IntVec3.Invalid;
            Pawn caster = req.caster;
            Thing target = req.target;
            Verb verb = req.verb;
            if (caster == null || target == null || verb == null || !caster.Spawned
                || !target.Spawned)
            {
                return false;
            }
            Map map = caster.Map;
            LocalTargetInfo targ = target;

            // Standing still is the preferred answer: it costs no movement, keeps whatever
            // cover the pawn already has, and TryGiveJob turns `dest == pawn.Position` into
            // Wait_Combat, which is exactly "hold here and shoot".
            if (verb.CanHitTargetFrom(caster.Position, targ))
            {
                dest = caster.Position;
                solvedInPlace++;
                return true;
            }

            int band = ABBands.BandOf(map, caster.Position);
            int count = Mathf.Min(GenRadial.NumCellsInRadius(SearchRadius),
                GenRadial.RadialPattern.Length);
            ABBandMap bands = ABBands.CompOf(map);
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = caster.Position + GenRadial.RadialPattern[i];
                // ⚠ THE BAND TEST IS NOT REDUNDANT WITH InBounds. GenRadial walks a square
                // neighbourhood in raw cell space, and on a banded map a radius of 12 from a
                // pawn near a band edge reaches straight over the gutter into the next level
                // (§1's "radius search wider than the gutter"). Without this the pawn could be
                // sent to stand on another floor entirely.
                if (!c.InBounds(map) || bands == null || bands.BandOf(c) != band
                    || bands.InGutter(c))
                {
                    continue;
                }
                if (!c.Standable(map) || !c.WalkableBy(map, caster))
                {
                    continue;
                }
                if (req.validator != null && !req.validator(c))
                {
                    continue;
                }
                if (!map.pawnDestinationReservationManager.CanReserve(c, caster))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(c, targ))
                {
                    continue;
                }
                // GenRadial is ordered by increasing distance, so the first hit is nearest.
                dest = c;
                solvedByStep++;
                return true;
            }
            noPosition++;
            return false;
        }
    }

    /// <summary>
    /// The single interception, prefixed rather than postfixed because vanilla's failure is
    /// structural: by the time it returns, it has clipped its candidate rect into the target's
    /// band and there is nothing left to repair.
    ///
    /// Same-band pairs never enter this code and pay one band compare.
    /// </summary>
    [HarmonyPatch(typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition))]
    public static class Patch_CastPositionFinder_ABCrossBand
    {
        private static bool Prefix(CastPositionRequest newReq, ref IntVec3 dest,
            ref bool __result)
        {
            try
            {
                Pawn caster = newReq.caster;
                Thing target = newReq.target;
                if (caster == null || target == null || !caster.Spawned || !target.Spawned
                    || !ABCombatV2.Enabled)
                {
                    return true;
                }
                Map map = caster.Map;
                if (target.Map != map)
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                if (bands.BandOf(caster.Position) == bands.BandOf(target.Position))
                {
                    return true; // one level: vanilla's search is correct and much better
                }
                __result = ABCombatPosition.TryFindBandLocalCastPosition(newReq, out dest);
                ABV2Debug.Combat("cast position for " + caster.LabelShortCap + " vs "
                    + target.LabelShortCap + " across bands: "
                    + (__result ? dest.ToString() : "NONE"));
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band cast position");
            }
            return true;
        }
    }
}
