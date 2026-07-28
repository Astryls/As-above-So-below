using System;
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
    /// distance is measured to the real cell, so every shot was resolved as if fired 256
    /// cells - far past any weapon's accuracy falloff, giving a near-zero hit chance.
    /// </summary>
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class Patch_ShotReport_ABCrossBandDistance
    {
        private static void Prefix(Thing caster, ref LocalTargetInfo target)
        {
            try
            {
                if (!target.IsValid || target.HasThing)
                {
                    // A Thing target keeps resolving its own position; rewrite to a CELL so
                    // the localized distance survives.
                    if (target.HasThing && ABCombatGeometry.TryLocalize(caster, target.Cell,
                        out IntVec3 localThing))
                    {
                        target = new LocalTargetInfo(localThing);
                    }
                    return;
                }
                if (ABCombatGeometry.TryLocalize(caster, target.Cell, out IntVec3 local))
                {
                    target = new LocalTargetInfo(local);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band shot report");
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

        private static void Prefix(Pawn pawn)
        {
            patched = null;
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
                patched = null;
            }
        }

        private static void Postfix()
        {
            if (patched != null)
            {
                patched.focusTarg = saved;
                patched = null;
            }
        }
    }
}
