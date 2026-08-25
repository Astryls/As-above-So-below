using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// PSYCASTS AND ABILITIES ACROSS LEVELS.
    ///
    /// ⚠ MOST OF THIS FILE IS THE ABSENCE OF CODE, AND THAT IS THE INTERESTING PART.
    /// <c>Verb_CastAbility</c> is a Verb, and its ValidateTarget asks <c>CanHitTarget</c> -
    /// which bottoms out in <c>TryFindShootLineFromTo</c>, which is where ABShaft already sits.
    /// So every single-target offensive psycast, every ranged ability, and every ability with a
    /// range greater than zero inherits cross-level targeting from the shooting work with no
    /// ability-specific code at all. Range-zero abilities fall through to
    /// <c>pawn.CanReach</c>, which is a WALKING question and correctly answered by the wormhole
    /// router. Both halves were already right.
    ///
    /// ⚠ THE PLAYER-FACING HALF IS ALSO NOT HERE: being able to point a psycast at a pawn or a
    /// cell one level down is Patch_GenUI_ABThingsUnderMouseSeeThrough and its cell-substituting
    /// sibling. A psycast targeter is an ITargetingSource like any other.
    ///
    /// What remains is exactly one thing: the SECOND target of a two-target ability.
    /// </summary>
    public static class ABCombatAbilities
    {
        public static int destSolves;

        public static void ResetCounters()
        {
            destSolves = 0;
        }

        public static string CounterReport()
        {
            return "abilities: crossBandDestinations=" + destSolves;
        }
    }

    /// <summary>
    /// SKIP, and every other ability that asks for a target and then a DESTINATION.
    ///
    /// <c>CompAbilityEffect_WithDest.CanHitTarget</c> validates the destination against the
    /// already-chosen target with two raw lines - <c>target.Cell.DistanceTo(selectedTarget.Cell)</c>
    /// against <c>Props.range</c>, then a flat <c>GenSight.LineOfSight</c> between the two
    /// cells. Neither goes anywhere near a Verb, so the shoot-line patch never sees them, and
    /// on a banded map both are guaranteed to fail: the two cells are a Slot apart and the
    /// sight line runs through the gutter.
    ///
    /// The result was that Skip worked perfectly within a level and could not move anything
    /// between levels - which is the single most obviously useful thing a psycaster could do in
    /// this mod. Now the destination is solved as a shaft problem: to skip something through the
    /// floor there has to be an opening, sight to it from above, and sight from its mouth to the
    /// far end. Exactly the rule a bullet obeys.
    ///
    /// ⚠ SAME LESSON AS §37 AND AS THE JUMP VERB, THIRD INSTANCE: a subsystem that
    /// REIMPLEMENTS a range check instead of delegating to the verb is invisible to a patch on
    /// the verb. Three times now the giveaway has been the same - the feature works for
    /// shooting and not for its sibling - so when that shape appears again, go looking for a
    /// private DistanceTo before theorising about flags.
    /// </summary>
    [HarmonyPatch(typeof(CompAbilityEffect_WithDest), nameof(CompAbilityEffect_WithDest.CanHitTarget))]
    public static class Patch_CompAbilityEffectWithDest_ABCrossBandDest
    {
        private static readonly AccessTools.FieldRef<CompAbilityEffect_WithDest, LocalTargetInfo>
            SelectedTargetRef =
                AccessTools.FieldRefAccess<CompAbilityEffect_WithDest, LocalTargetInfo>(
                    "selectedTarget");

        private static bool Prepare()
        {
            return AccessTools.Field(typeof(CompAbilityEffect_WithDest), "selectedTarget") != null;
        }

        private static bool Prefix(CompAbilityEffect_WithDest __instance, LocalTargetInfo target,
            ref bool __result)
        {
            try
            {
                if (!target.IsValid || !ABCombatV2.Enabled)
                {
                    return true;
                }
                Pawn caster = __instance.parent?.pawn;
                if (caster == null || !caster.Spawned)
                {
                    return true;
                }
                Map map = caster.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                LocalTargetInfo selected = SelectedTargetRef(__instance);
                if (!selected.IsValid)
                {
                    return true;
                }
                if (bands.BandOf(selected.Cell) == bands.BandOf(target.Cell))
                {
                    return true; // one level: vanilla's maths is correct and cheaper
                }
                CompProperties_EffectWithDest props = __instance.Props;
                // A range of zero means "unlimited" to vanilla (the check is gated on
                // `Props.range > 0f`), so it must mean unlimited here too - otherwise an
                // ability with no declared range would suddenly acquire one.
                float range = props != null && props.range > 0f ? props.range : float.MaxValue;
                __result = ABShaft.TrySolve(map, selected.Cell, target.Cell, range, 0f,
                    overheadFire: false, out ABShotSolution sol);
                if (__result)
                {
                    ABCombatAbilities.destSolves++;
                    ABV2Debug.Combat("ability destination " + selected.Cell + " -> " + target.Cell
                        + " via opening " + sol.opening + " dist " + sol.distance.ToString("0.0"));
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band ability destination");
            }
            return true;
        }
    }
}
