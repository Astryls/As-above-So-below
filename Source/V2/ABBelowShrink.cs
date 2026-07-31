using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Makes the depth falloff stick on LIVE PAWNS drawn by the see-below pass.
    ///
    /// Everything else below the viewing band is printed into the map mesh, so
    /// SectionLayer_ABBelowV2 can shrink it by editing the vertices it just emitted. Pawns
    /// are not in that mesh - they are redrawn every frame - and DynamicDrawPhaseAt takes a
    /// POSITION and nothing else, so there is no per-call size to pass in.
    ///
    /// PawnRenderer does funnel every piece of a pawn (body, head, apparel, carried thing,
    /// held weapon) through one transform: PawnDrawParms.matrix. Right-multiplying a scale
    /// there shrinks the whole pawn about its own draw root in one place, which is why this
    /// is two tiny patches rather than a per-piece hunt. Ported from V1, where it was found
    /// the hard way.
    ///
    /// Both patches are armed ONLY inside a single below-pawn draw call, by the same
    /// arm/disarm-in-a-finally discipline as the body-position patch next door: outside the
    /// pass the guard field is 1 and both patches return on their first line.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "GetDrawParms")]
    public static class Patch_PawnRenderer_ABBelowShrink
    {
        private static void Postfix(ref PawnDrawParms __result)
        {
            float s = ABBelowDynamicDraw.BelowDrawScale;
            if (s > 0.999f || s <= 0f)
            {
                return; // not inside the see-below pass, or falloff disabled
            }
            try
            {
                // Right-multiplied: scaling happens in the pawn's LOCAL space, so the pawn
                // shrinks about its own draw position and stays on its cell. A left
                // multiply would scale the world position too and slide every below pawn
                // towards the map origin.
                __result.matrix *= Matrix4x4.Scale(new Vector3(s, 1f, s));
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: below pawn shrink failed: " + e.Message,
                    762195891);
            }
        }
    }

    /// <summary>
    /// ⚠ THE PART THAT LOOKS LIKE THE SHRINK "RANDOMLY STOPS WORKING".
    ///
    /// Beyond ZoomRootSize 18 a humanlike pawn renders from a cached atlas blit:
    /// ParallelGetPreRenderResults sets `useCached` and the draw positions a premade mesh
    /// directly at bodyPos, never reading PawnDrawParms.matrix at all. So the scale above
    /// silently evaporates at exactly the zoom levels a player uses to look down a column -
    /// colonists two levels down would shrink while zoomed in and snap back to full size
    /// when zoomed out, which reads as a flicker bug rather than a missing patch.
    ///
    /// Disabling the cache is scoped to below pawns only - a handful visible through open
    /// air - so the atlas keeps serving every pawn on the viewing band, where the vast
    /// majority are.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    public static class Patch_PawnRenderer_ABBelowDisableCache
    {
        private static void Prefix(ref bool disableCache)
        {
            float s = ABBelowDynamicDraw.BelowDrawScale;
            if (s < 0.999f && s > 0f)
            {
                disableCache = true;
            }
        }
    }
}
