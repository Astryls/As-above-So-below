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
            try
            {
                // ⚠ TWO EFFECTS, ONE MATRIX, ONE PATCH. The depth cue (how far below the
                // viewed band this pawn is) and the stair animation (is it stepping into a
                // stairwell right now) are both a scale about the pawn's own draw root, and
                // they must MULTIPLY. Adding a second [HarmonyPatch] on GetDrawParms to carry
                // the second one would appear to work and would silently depend on patch
                // order forever after.
                //
                // ⚠ AND THEIR GUARDS ARE NOT THE SAME. BelowDrawScale is armed only inside the
                // see-below pass, so an early return on it would mean a pawn taking the
                // stairs on the band you are LOOKING AT never animated at all - which is the
                // common case.
                float s = ABBelowDynamicDraw.BelowDrawScale;
                if (s <= 0f)
                {
                    return; // falloff disabled
                }
                float stair = ABStairAnim.ScaleFor(__result.pawn);
                float shimmy = ABStairAnim.ShimmyFor(__result.pawn);
                float total = s * stair;
                if (total > 0.999f && Mathf.Abs(shimmy) < 0.0005f)
                {
                    return; // nothing to do: the overwhelmingly common path
                }

                // Right-multiplied: the transform happens in the pawn's LOCAL space, so it
                // scales about its own draw position and stays on its cell. A left multiply
                // would scale the world position too and slide every below pawn towards the
                // map origin.
                __result.matrix *= Matrix4x4.TRS(
                    new Vector3(shimmy, 0f, 0f),
                    Quaternion.identity,
                    new Vector3(total, 1f, total));
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
        private static void Prefix(PawnRenderer __instance, ref bool disableCache)
        {
            float s = ABBelowDynamicDraw.BelowDrawScale;
            if (s < 0.999f && s > 0f)
            {
                disableCache = true;
                return;
            }
            // ⚠ THE STAIR ANIMATION FALLS INTO THE SAME TRAP AND FOR THE SAME REASON. A pawn
            // animating on the band you are looking at is NOT in the see-below pass, so the
            // check above misses it - and past ZoomRootSize 18 the cached atlas blit ignores
            // PawnDrawParms.matrix outright, so the shimmy and shrink would evaporate at
            // exactly the zoom a player uses to watch someone take the stairs.
            try
            {
                if (ABStairAnim.IsAnimating(PawnRef(__instance)))
                {
                    disableCache = true;
                }
            }
            catch
            {
                // Never break rendering over a cosmetic effect.
            }
        }

        private static readonly AccessTools.FieldRef<PawnRenderer, Pawn> PawnRef =
            AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");
    }
}
