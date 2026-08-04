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
    /// V2 combat GEOMETRY.
    ///
    /// ABCombatV2 answers "may this shot happen"; this file answers "where is the target,
    /// for the purposes of maths and rendering". They are separate because everything here
    /// shares one failure mode: vanilla measures from the target's REAL cell, which for a
    /// cross-band target is a full Slot (256 cells) away. That single fact produced every
    /// symptom at once - the pawn aiming due south, the gun sprite pointing at the floor,
    /// and shots that connect on paper but always miss because accuracy is computed over
    /// 256 cells.
    ///
    /// The fix is uniform: translate the target into the shooter's own band before any
    /// distance or angle is taken. Bands are aligned 1:1, so the translated position is the
    /// honest horizontal bearing and separation.
    /// </summary>
    public static class ABCombatGeometry
    {
        /// <summary>Target position as the shooter should perceive it: same band, true
        /// horizontal offset. Returns false when no translation applies.</summary>
        public static bool TryLocalize(Thing shooter, IntVec3 targetCell, out IntVec3 localized)
        {
            localized = targetCell;
            if (shooter == null || !shooter.Spawned || !ABCombatV2.Enabled)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(shooter.Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int bandShooter = bands.BandOf(shooter.Position);
            if (bands.BandOf(targetCell) == bandShooter)
            {
                return false;
            }
            localized = bands.Translate(targetCell, bandShooter);
            return true;
        }

        /// <summary>Vector3 form, for render maths.</summary>
        public static bool TryLocalize(Thing shooter, Vector3 worldPos, out Vector3 localized)
        {
            localized = worldPos;
            if (shooter == null || !shooter.Spawned || !ABCombatV2.Enabled)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(shooter.Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int bandShooter = bands.BandOf(shooter.Position);
            int bandTarget = bands.BandOf(worldPos.ToIntVec3());
            if (bandTarget == bandShooter)
            {
                return false;
            }
            localized = new Vector3(worldPos.x, worldPos.y,
                worldPos.z + (bandShooter - bandTarget) * bands.Slot);
            return true;
        }
    }

    /// <summary>
    /// ACCURACY. This is why cross-band shots connected in the log and never in practice:
    /// distance is measured to the real cell, so every shot was resolved as if fired a whole
    /// Slot away - far past any weapon's accuracy falloff, giving a near-zero hit chance.
    /// The prefix rewrites the target to a CELL translated into the shooter's band, so every
    /// distance-derived factor inside HitReportFor comes out band-local.
    ///
    /// ⚠⚠ BUT THE CELL REWRITE POISONS THREE OTHER THINGS THE REPORT DERIVES FROM THE
    /// TARGET, AND THE POSTFIX REPAIRS ALL THREE. HitReportFor also reads the target for:
    ///   1. COVER - CalculateCoverGiverSet(target, caster.Position): with the localized cell
    ///      that finds covers around a GHOST COLUMN ON THE SHOOTER'S OWN BAND. Worse than
    ///      wrong odds: TryCastShot's cover-miss branch launches the round INTO the sampled
    ///      cover thing, so pellets visibly dived into the shooter's own sandbags and rocks -
    ///      part of the "fires into the ground" report. Cross-band shots now carry NO cover:
    ///      the opening's drift cone IS the cover model, and fabricating band-1 furniture
    ///      into a band-2 firefight was the alternative.
    ///   2. TARGET SIZE - a cell has no BodySize, so factorFromTargetSize collapsed to 1.0
    ///      and shooting a scyther through a hole was harder than shooting a rat.
    ///   3. POSTURE - FactorFromPosture/FactorFromExecution read target.Thing off the stored
    ///      TargetInfo; a cell target erased the downed-pawn modifiers.
    /// The postfix writes the ORIGINAL thing back into the report's target field (posture and
    /// size recover their inputs) while KEEPING the localized distance, then recomputes the
    /// size factor exactly as vanilla does and empties the covers. Private struct fields via
    /// one box/unbox - ugly, but reimplementing HitReportFor to avoid it is the §14
    /// "reproducing a subsystem means all of it" trap.
    /// </summary>
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class Patch_ShotReport_ABCrossBandDistance
    {
        private static readonly FieldInfo TargetField =
            AccessTools.Field(typeof(ShotReport), "target");

        private static readonly FieldInfo CoversField =
            AccessTools.Field(typeof(ShotReport), "covers");

        private static readonly FieldInfo CoversChanceField =
            AccessTools.Field(typeof(ShotReport), "coversOverallBlockChance");

        private static readonly FieldInfo SizeFactorField =
            AccessTools.Field(typeof(ShotReport), "factorFromTargetSize");

        private static bool Prepare()
        {
            return TargetField != null && CoversField != null && CoversChanceField != null
                && SizeFactorField != null;
        }

        private static void Prefix(Thing caster, ref LocalTargetInfo target,
            out LocalTargetInfo __state)
        {
            __state = LocalTargetInfo.Invalid;
            try
            {
                if (!target.IsValid)
                {
                    return;
                }
                if (ABCombatGeometry.TryLocalize(caster, target.Cell, out IntVec3 local))
                {
                    __state = target; // the ORIGINAL, thing-ness intact, for the postfix
                    target = new LocalTargetInfo(local);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band shot report");
            }
        }

        private static void Postfix(Thing caster, ref ShotReport __result,
            LocalTargetInfo __state)
        {
            if (!__state.IsValid)
            {
                return; // not a cross-band report; vanilla's answer is untouched
            }
            try
            {
                object boxed = __result;
                CoversField.SetValue(boxed, new List<CoverInfo>());
                CoversChanceField.SetValue(boxed, 0f);
                if (__state.HasThing)
                {
                    TargetField.SetValue(boxed,
                        new TargetInfo(__state.Thing));
                    float size;
                    if (__state.Thing is Pawn p)
                    {
                        size = p.BodySize;
                    }
                    else
                    {
                        size = __state.Thing.def.fillPercent
                            * __state.Thing.def.size.x * __state.Thing.def.size.z * 2.5f;
                    }
                    SizeFactorField.SetValue(boxed, Mathf.Clamp(size, 0.5f, 2f));
                }
                __result = (ShotReport)boxed;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band shot report repair");
            }
        }
    }

    /// <summary>
    /// THE GUN SPRITE. PawnRenderUtility takes the aim angle straight from
    /// stance_Busy.focusTarg's draw position, which for a cross-band target sits a whole
    /// band away - so the weapon renders pointing straight down the map regardless of where
    /// the shaft actually is. Pawn_RotationTracker was patched separately (that is which way
    /// the BODY faces); this is the weapon.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    public static class Patch_PawnRenderUtility_ABAimAngle
    {
        private static Stance_Busy patched;

        private static LocalTargetInfo saved;

        /// <summary>Put the stance back. Idempotent, and safe to call when the swap never
        /// happened: writing the saved value over itself is a no-op.</summary>
        private static void Restore()
        {
            if (patched != null)
            {
                patched.focusTarg = saved;
                patched = null;
            }
        }

        private static void Prefix(Pawn pawn)
        {
            // Restore, do NOT just null the field. The old code opened with `patched = null`,
            // which DISCARDS a pending restore rather than deferring it - so if the previous
            // pawn's draw never got its restore, its focusTarg stayed permanently rewritten.
            Restore();
            try
            {
                Stance_Busy stance = pawn?.stances?.curStance as Stance_Busy;
                if (stance == null || !stance.focusTarg.IsValid)
                {
                    return;
                }
                if (!ABCombatGeometry.TryLocalize(pawn, stance.focusTarg.Cell, out IntVec3 local))
                {
                    return;
                }
                // Swap for the duration of the draw only, and restore in the postfix - the
                // stance is live game state, not render state.
                patched = stance;
                saved = stance.focusTarg;
                stance.focusTarg = new LocalTargetInfo(local);
            }
            catch
            {
                Restore();
            }
        }

        /// <summary>
        /// ⚠ A FINALIZER, NOT A POSTFIX, AND THE DIFFERENCE IS NOT COSMETIC.
        ///
        /// Harmony does not run postfixes when the original method THROWS - it runs
        /// finalizers. This prefix mutates <c>stance.focusTarg</c>, which is LIVE COMBAT
        /// STATE and not render state, so a single exception anywhere inside
        /// DrawEquipmentAndApparelExtras (a bad graphic, another mod's postfix, a null
        /// apparel) used to leave that pawn permanently aiming at a band-translated cell -
        /// silently, with no error attributable to us, and surviving until the stance ended.
        ///
        /// The general rule this is an instance of: if a patch takes a lock on game state in
        /// a prefix, the release belongs in a finalizer. A postfix is a happy-path hook.
        /// </summary>
        private static void Finalizer()
        {
            Restore();
        }
    }
}
