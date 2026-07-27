using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mechanitor parity across levels (QOL/BUG pass 2026-07-21). Vanilla's
    /// only map-equality gate in the whole mechanitor stack is
    /// MechanitorUtility.InMechanitorCommandRange, which hard-fails when the
    /// mech and its overseer hold different maps - so a drafted mech one
    /// level away from its mechanitor refuses goto/attack orders ("out of
    /// mechanitor range") even though the levels share a coordinate space.
    /// Same-column maps are vertically aligned, so the vanilla distance rule
    /// (CanCommandTo: in-bounds + flat 24.9 radius around the overseer)
    /// extends naturally into a command CYLINDER through the column: we
    /// re-run exactly that check and only skip the map-equality gate.
    /// CompOverseerSubject.State itself is already map-agnostic (it reads
    /// ControlledPawns membership), so overseen state, bandwidth, and the
    /// feral timer need no help.
    /// </summary>
    [HarmonyPatch(typeof(MechanitorUtility), nameof(MechanitorUtility.InMechanitorCommandRange))]
    internal static class Patch_MechCommandRange_CrossLevel
    {
        private static void Postfix(Pawn mech, LocalTargetInfo target, ref bool __result)
        {
            if (__result || !LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                Pawn overseer = mech?.GetOverseer();
                if (overseer?.mechanitor == null)
                {
                    return;
                }
                Map mechMap = mech.MapHeld;
                Map overseerMap = overseer.MapHeld;
                if (mechMap == null || overseerMap == null || mechMap == overseerMap)
                {
                    // Same map: the vanilla verdict (distance fail) stands.
                    return;
                }
                if (!mechMap.SameColumn(overseerMap))
                {
                    return;
                }
                // Vanilla CanCommandTo carries no map check of its own: bounds
                // plus flat distance, both valid across the aligned column.
                __result = overseer.mechanitor.CanCommandTo(target);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross-level mech command range");
            }
        }
    }
}
