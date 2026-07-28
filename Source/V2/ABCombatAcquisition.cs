using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 cross-band target ACQUISITION.
    ///
    /// ABCombatV2 makes a cross-band shot legal - range and line of sight resolve correctly
    /// once something decides to fire. But nothing ever decided to: vanilla's target search
    /// pre-filters candidates by cell distance, and a pawn one band up is 256 cells away, so
    /// every cross-band hostile was discarded long before the verb was consulted.
    ///
    /// Pawns and turrets both funnel through
    /// AttackTargetFinder.BestShootTargetFromCurrentPosition, so one postfix serves both.
    /// It runs ONLY when vanilla found nothing, which keeps same-band behaviour completely
    /// untouched - a colonist shoots what is in front of it exactly as before, and only
    /// looks through the floor when there is nothing else to shoot.
    /// </summary>
    public static class ABCombatAcquisition
    {
        public static IAttackTarget FindCrossBandTarget(IAttackTargetSearcher searcher,
            Predicate<Thing> validator)
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
            List<IAttackTarget> candidates = map.attackTargetsCache.GetPotentialTargetsFor(searcher);
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }

            IAttackTarget best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                IAttackTarget target = candidates[i];
                Thing t = target?.Thing;
                if (t == null || !t.Spawned || t.Map != map)
                {
                    continue;
                }
                // Same band is vanilla's business; it already had its chance.
                if (bands.BandOf(t.Position) == bands.BandOf(root))
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
                if (!ABCombatV2.TryCrossBandShot(map, root, t.Position, verb.EffectiveRange,
                    out float dist))
                {
                    continue;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = target;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Fallback acquisition. Deliberately a POSTFIX gated on a null result: cross-band
    /// targets must never outrank something the shooter can already engage on its own band,
    /// or colonists would start ignoring the raider next to them to snipe through a hole.
    /// </summary>
    [HarmonyPatch(typeof(AttackTargetFinder),
        nameof(AttackTargetFinder.BestShootTargetFromCurrentPosition))]
    public static class Patch_AttackTargetFinder_ABCrossBand
    {
        private static void Postfix(IAttackTargetSearcher searcher, Predicate<Thing> validator,
            ref IAttackTarget __result)
        {
            if (__result != null)
            {
                return;
            }
            try
            {
                __result = ABCombatAcquisition.FindCrossBandTarget(searcher, validator);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band acquisition");
            }
        }
    }
}
