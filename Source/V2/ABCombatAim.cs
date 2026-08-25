using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// TURRET AND MORTAR BARRELS, aimed across levels.
    ///
    /// The PAWN side of aiming was finished in the main combat pass: body facing goes
    /// through Pawn_RotationTracker.Face/FaceCell and the weapon sprite through
    /// PawnRenderUtility, all reading the target TRANSLATED into the shooter's band - and the
    /// solver's opening is collinear with the shooter-to-translated-target line by
    /// construction (the balcony walk runs along that very line), so facing the translated
    /// column IS facing through the hole. Nothing more to do there.
    ///
    /// TURRETS have their own third copy of the same maths. <c>TurretTop.TurretTopTick</c>:
    ///
    ///     float curRotation = (currentTarget.Cell.ToVector3Shifted()
    ///         - parentTurret.DrawPos).AngleFlat();
    ///
    /// - the raw cell again, §41e's list grown by one. A turret or mortar engaging across a
    /// band therefore renders its barrel pointing due north or south (straight at the other
    /// band), while its shells fly out at the true bearing. Pure cosmetics, but it is
    /// exactly the "aiming looks wrong" a player reports first, because the barrel is the
    /// only aim feedback a turret has.
    ///
    /// <c>ForceFaceTarget</c> repeats the line for the player's force-target gesture, so
    /// both get the same one-line correction: recompute from the localized cell. POSTFIXES,
    /// because vanilla's assignment also resets the idle-turn bookkeeping and none of that
    /// should be reimplemented for a cosmetic angle.
    /// </summary>
    public static class ABCombatAim
    {
        /// <summary>The barrel bearing toward a cross-band target, in the turret's own band.
        /// False when vanilla's angle is already right (same band, not banded, disabled).</summary>
        internal static bool TryLocalAngle(Building_Turret turret, LocalTargetInfo target,
            out float angle)
        {
            angle = 0f;
            if (turret == null || !turret.Spawned || !target.IsValid || !ABCombatV2.Enabled)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(turret.Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int myBand = bands.BandOf(turret.Position);
            if (bands.BandOf(target.Cell) == myBand)
            {
                return false;
            }
            IntVec3 local = bands.Translate(target.Cell, myBand);
            angle = (local.ToVector3Shifted() - turret.DrawPos).AngleFlat();
            return true;
        }
    }

    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.TurretTopTick))]
    public static class Patch_TurretTop_ABCrossBandAim
    {
        private static readonly AccessTools.FieldRef<TurretTop, Building_Turret> ParentRef =
            AccessTools.FieldRefAccess<TurretTop, Building_Turret>("parentTurret");

        private static bool Prepare()
        {
            return AccessTools.Field(typeof(TurretTop), "parentTurret") != null;
        }

        private static void Postfix(TurretTop __instance)
        {
            try
            {
                Building_Turret turret = ParentRef(__instance);
                if (turret != null
                    && ABCombatAim.TryLocalAngle(turret, turret.CurrentTarget, out float angle))
                {
                    __instance.CurRotation = angle;
                }
            }
            catch
            {
                // A wrong barrel angle must never take the turret down with it.
            }
        }
    }

    [HarmonyPatch(typeof(TurretTop), nameof(TurretTop.ForceFaceTarget))]
    public static class Patch_TurretTop_ABCrossBandForceFace
    {
        private static readonly AccessTools.FieldRef<TurretTop, Building_Turret> ParentRef =
            AccessTools.FieldRefAccess<TurretTop, Building_Turret>("parentTurret");

        private static bool Prepare()
        {
            return AccessTools.Field(typeof(TurretTop), "parentTurret") != null;
        }

        private static void Postfix(TurretTop __instance, LocalTargetInfo targ)
        {
            try
            {
                Building_Turret turret = ParentRef(__instance);
                if (turret != null
                    && ABCombatAim.TryLocalAngle(turret, targ, out float angle))
                {
                    __instance.CurRotation = angle;
                }
            }
            catch
            {
            }
        }
    }
}
