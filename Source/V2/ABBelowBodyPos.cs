using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Make a sleeping colonist visible from the band above.
    ///
    /// PawnRenderer.GetBodyPos honours the caller's drawLoc for a STANDING pawn, but for a
    /// humanlike pawn in a bed it recomputes the position from pawn.Position outright:
    ///
    ///     if (posture == PawnPosture.Standing) { return drawLoc; }
    ///     if (bed != null &amp;&amp; pawn.RaceProps.Humanlike)
    ///         result = pawn.Position.ToVector3ShiftedWithAltitude(altLayer) - facing * offset;
    ///
    /// So the translated location the see-below pass hands in is DISCARDED, and the pawn is
    /// drawn at its real cell one band down - off screen. It is not occluded and not masked;
    /// it renders perfectly, somewhere nobody is looking. Lying ANIMALS were unaffected
    /// because the else-branch does use drawLoc, which is why the bug looked like it was
    /// specifically about sleeping colonists.
    ///
    /// This is corrected at the drawing site, in keeping with the rule that every geometry
    /// surface is fixed where it is computed rather than by moving DrawPos globally. The
    /// offset is armed ONLY for the duration of one below-pawn draw call and is a plain
    /// same-thread field read in between - no ambient "am I drawing below" state survives the
    /// call, so nothing else in the game can observe it.
    ///
    /// Deliberately gated on the SAME condition as the branch that ignores drawLoc. A blanket
    /// postfix would double-shift every standing pawn, whose result is already the translated
    /// location - precisely the double-shift trap that made V1's global DrawPos patching
    /// unworkable.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "GetBodyPos")]
    public static class Patch_PawnRenderer_ABBelowBodyPos
    {
        private static readonly AccessTools.FieldRef<PawnRenderer, Pawn> PawnRef =
            AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");

        private static void Postfix(PawnRenderer __instance, ref Vector3 __result)
        {
            float dz = ABBelowDynamicDraw.BelowDrawOffsetZ;
            if (dz == 0f)
            {
                return; // not inside the see-below pass: vanilla behaviour, untouched
            }
            try
            {
                Pawn p = PawnRef(__instance);
                if (p == null || !p.RaceProps.Humanlike)
                {
                    return;
                }
                if (p.CurrentBed() == null)
                {
                    return; // took the drawLoc branch; already translated
                }
                __result.z += dz;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: below body-pos postfix failed: " + e.Message,
                    762195889);
            }
        }
    }
}
