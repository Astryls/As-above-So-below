using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 cross-band target ACQUISITION - what pawns and turrets decide to shoot at.
    ///
    /// ABShaft makes a cross-band shot legal, and ABCombatV2 makes the verb agree. But
    /// nothing ever DECIDED to fire: vanilla's target search pre-filters candidates by cell
    /// distance, and a pawn one band up is a whole Slot away, so every cross-band hostile was
    /// discarded long before a verb was consulted.
    ///
    /// ⚠⚠ IT HOOKS <c>BestAttackTarget</c>, NOT <c>BestShootTargetFromCurrentPosition</c>,
    /// AND THAT ONE-WORD DIFFERENCE WAS AN ENTIRE CLASS OF BUG.
    ///
    /// Reported as: "neutrals / allies aren't attacking hostile factions when they aren't
    /// colonists; they walk up the stairs and then engage, but not across levels." The cause
    /// is that the two methods serve DISJOINT populations, and the first version of this file
    /// patched the smaller one:
    ///
    ///   BestShootTargetFromCurrentPosition - turrets (Building_TurretGun, CompTurretGun),
    ///     CompFleshmassSpitter, and JobDriver_Wait. That last one is why it LOOKED like it
    ///     worked: a drafted colonist standing still auto-attacks through JobDriver_Wait.
    ///   BestAttackTarget - JobGiver_AIFightEnemy (every raider, ally and hostile visitor),
    ///     JobGiver_ConfigurableHostilityResponse (UNDRAFTED colonists reacting to a threat),
    ///     Berserk, Manhunter, mutants, metalhorrors, sightstealers, holding-platform escapes.
    ///
    /// So cross-level combat worked for exactly two cases - a turret, and a drafted colonist
    /// told to hold position - and silently did nothing for every AI-driven combatant in the
    /// game. The user's report names allies and neutrals because those are always AI-driven;
    /// it was never faction-specific. **A BUG REPORT THAT NAMES ONE PAWN TYPE IS NOT
    /// NECESSARILY TYPE-SPECIFIC** (§14), for the second time in this project.
    ///
    /// ⚠ AND ONE PATCH HERE COVERS BOTH, because BestShootTargetFromCurrentPosition is a
    /// four-line wrapper that clamps min/max range and calls BestAttackTarget. Hooking the
    /// inner method means we see the ALREADY-CLAMPED limits plus locus, travel radius and
    /// canTakeTargetsCloserThanEffectiveMinRange, all of which the outer signature hides.
    /// Patching both would double every scan.
    ///
    /// It is also the real answer to the shipped note that "turrets do not target across
    /// levels": they always went through this path; what they lacked was the validation below,
    /// because a turret passes scan FLAGS and a range pair that the first version ignored.
    ///
    /// ⚠ IT RUNS ONLY WHEN VANILLA FOUND NOTHING, and that is a balance decision, not an
    /// optimisation. A cross-band target must never outrank something the shooter can already
    /// engage on its own level, or colonists start ignoring the raider next to them to snipe
    /// through a hole in the floor.
    ///
    /// ⚠⚠ AND THE VALIDATION HERE MUST MIRROR VANILLA'S innerValidator, NOT APPROXIMATE IT.
    /// BestAttackTarget builds a closure with ten checks in it; skipping them does not make
    /// the mod permissive, it makes it WRONG in specific, hard-to-attribute ways - a mortar
    /// that lobs shells at a target under thick roof, a turret that wakes on a dormant
    /// mechanoid cluster, a pawn under a Lord that attacks something its lord forbade. Each
    /// check below names the vanilla one it stands in for. The line-of-sight flags are the
    /// only ones deliberately dropped: our shaft solve IS the line-of-sight test, and a
    /// vanilla CanSee across a Slot of gutter can only ever answer false.
    /// </summary>
    public static class ABCombatAcquisition
    {
        // Observe-only counters for `AB2: combat report`.
        public static int scans;

        public static int found;

        public static int rejectedByFlags;

        public static void ResetCounters()
        {
            scans = 0;
            found = 0;
            rejectedByFlags = 0;
        }

        public static string CounterReport()
        {
            return "acquisition: scans=" + scans + " found=" + found
                + " rejectedByFlags=" + rejectedByFlags;
        }

        public static IAttackTarget FindCrossBandTarget(IAttackTargetSearcher searcher,
            TargetScanFlags flags, Predicate<Thing> validator, float minDistance,
            float maxDistance, IntVec3 locus, float maxTravelRadiusFromLocus,
            bool canTakeTargetsCloserThanEffectiveMinRange, bool onlyRanged)
        {
            Thing searcherThing = searcher?.Thing;
            if (searcherThing == null || !searcherThing.Spawned || !ABCombatV2.Enabled)
            {
                return null;
            }
            Map map = searcherThing.Map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return null;
            }
            Verb verb = searcher.CurrentEffectiveVerb;
            if (verb == null || verb.verbProps == null || verb.verbProps.IsMeleeAttack)
            {
                return null;
            }
            IntVec3 root = searcherThing.Position;
            int rootBand = bands.BandOf(root);
            List<IAttackTarget> candidates = map.attackTargetsCache.GetPotentialTargetsFor(searcher);
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            scans++;
            // ⚠ THESE ARRIVE ALREADY CLAMPED now that the hook is on BestAttackTarget: its
            // caller folded verbProps.minRange and EffectiveRange in. Re-applying the clamp
            // is harmless (max/min are idempotent) and keeps the method honest for the direct
            // callers - JobGiver_AIFightEnemy passes 0f and a raw acquire radius.
            float minRange = Mathf.Max(minDistance, verb.verbProps.minRange);
            float maxRange = Mathf.Min(maxDistance, verb.EffectiveRange);
            bool overhead = ABShaft.IsOverheadFire(verb);
            Pawn searcherPawn = searcher as Pawn;
            Lord lord = searcherPawn?.GetLord();

            IAttackTarget best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                IAttackTarget target = candidates[i];
                Thing t = target?.Thing;
                if (t == null || !t.Spawned || t.Map != map || t == searcherThing)
                {
                    continue;
                }
                // Same band is vanilla's business; it already had its chance and lost.
                if (bands.BandOf(t.Position) == rootBand)
                {
                    continue;
                }
                if (target.ThreatDisabled(searcher) || !searcherThing.HostileTo(t))
                {
                    continue;
                }
                if (validator != null && !validator(t))
                {
                    continue;
                }
                if (!PassesFlags(searcher, searcherThing, lord, searcherPawn, t, target, flags))
                {
                    rejectedByFlags++;
                    continue;
                }
                // Siege and defend-point lords cap how far a member may stray from its flag.
                // Measured band-locally, like every other distance here: the raw version would
                // put every cross-band target a Slot outside the radius and quietly restore
                // the bug this file exists to fix.
                if (maxTravelRadiusFromLocus < 9999f)
                {
                    float allowed = maxTravelRadiusFromLocus + verb.EffectiveRange;
                    IntVec3 targetHere = bands.Translate(t.Position, bands.BandOf(locus));
                    if ((targetHere - locus).LengthHorizontalSquared > allowed * allowed)
                    {
                        rejectedByFlags++;
                        continue;
                    }
                }
                // The verb's own minimum range, per target, exactly as vanilla's
                // canTakeTargetsCloserThanEffectiveMinRange:false branch does it - except
                // measured on the SOLVED distance rather than the raw one, which is the whole
                // point. BestShootTargetFromCurrentPosition always passes false.
                float perTargetMin = canTakeTargetsCloserThanEffectiveMinRange
                    ? minRange
                    : Mathf.Max(minRange, verb.verbProps.EffectiveMinRange(t, searcherThing));
                if (!ABShaft.TrySolve(map, root, t.Position, maxRange, perTargetMin, overhead,
                        out ABShotSolution sol))
                {
                    continue;
                }
                if (sol.distance < bestDist)
                {
                    bestDist = sol.distance;
                    best = target;
                }
            }
            if (best != null)
            {
                found++;
                ABV2Debug.Combat("acquired cross-band " + best.Thing.LabelShortCap + " at "
                    + best.Thing.Position + " for " + searcherThing.LabelShortCap
                    + " dist " + bestDist.ToString("0.0"));
            }
            return best;
        }

        /// <summary>
        /// The subset of vanilla's innerValidator that is not about line of sight. Named
        /// against the original so the two can be diffed when Ludeon changes it.
        /// </summary>
        private static bool PassesFlags(IAttackTargetSearcher searcher, Thing searcherThing,
            Lord lord, Pawn searcherPawn, Thing t, IAttackTarget target, TargetScanFlags flags)
        {
            // Lord veto. A raid's lord decides what its members may engage, and ignoring it
            // is how a besieger abandons the siege to shoot through a floor.
            if (searcherPawn != null && lord != null
                && !lord.LordJob.ValidateAttackTarget(searcherPawn, t))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedNotUnderThickRoof) != TargetScanFlags.None)
            {
                // ⚠ THIS IS THE ONE THAT MATTERS FOR MORTARS, and on a banded map it is not
                // decoration: every level below the surface is under thick rock roof by
                // construction, so this single check is what stops an AI mortar crew trying
                // to shell your basement. Player-forced mortar targets bypass acquisition
                // entirely, which is the intended asymmetry - vanilla behaves the same way.
                RoofDef roof = t.Position.GetRoof(t.Map);
                if (roof != null && roof.isThickRoof)
                {
                    return false;
                }
            }
            if ((flags & TargetScanFlags.NeedThreat) != TargetScanFlags.None
                && target.ThreatDisabled(searcher))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedAutoTargetable) != TargetScanFlags.None
                && !AttackTargetFinder.IsAutoTargetable(target))
            {
                return false;
            }
            if ((flags & TargetScanFlags.NeedNonBurning) != TargetScanFlags.None && t.IsBurning())
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Fallback acquisition, at the method EVERY combat think-node reaches. Deliberately a
    /// POSTFIX gated on a null result - see the banner for why cross-band must never outrank
    /// a same-band target, and why this is the inner method rather than the wrapper.
    /// </summary>
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
    public static class Patch_AttackTargetFinder_ABCrossBand
    {
        private static void Postfix(IAttackTargetSearcher searcher, TargetScanFlags flags,
            Predicate<Thing> validator, float minDist, float maxDist, IntVec3 locus,
            float maxTravelRadiusFromLocus, bool canTakeTargetsCloserThanEffectiveMinRange,
            bool onlyRanged, ref IAttackTarget __result)
        {
            if (__result != null)
            {
                return;
            }
            try
            {
                __result = ABCombatAcquisition.FindCrossBandTarget(searcher, flags, validator,
                    minDist, maxDist, locus, maxTravelRadiusFromLocus,
                    canTakeTargetsCloserThanEffectiveMinRange, onlyRanged);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band acquisition");
            }
        }
    }
}
