using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §95 TIER E2 - EVERY POSITION-BLIND MAP-SPACE DRAW, TRANSLATED AT THE ONE TRUE FUNNEL.
    ///
    /// Tier E v1 sat on Verse's Graphic.Draw, which fixed the fire family but left the raw
    /// Graphics.DrawMesh class invisible from above: windmill and watermill blades, the
    /// deathrest hose, the biosculpter cycle quad, interceptor domes, hacking progress bars,
    /// GenDraw lines - every legacy comp that computes its own matrix from parent.DrawPos.
    /// Per the DrawMesh census (§95.h) there are a dozen of these in vanilla alone, plus an
    /// unbounded modded set, and per-comp reimplementation rots (rule 62).
    ///
    /// THE FUNNEL (decompile-verified against THIS install's UnityEngine.CoreModule):
    /// every non-instanced Graphics.DrawMesh overload - including all (position, rotation)
    /// variants, which build Matrix4x4.TRS themselves - chains into ONE private managed
    /// method:
    ///     Graphics.Internal_DrawMesh(Mesh, int, Matrix4x4 matrix, Material, int, Camera,
    ///         MaterialPropertyBlock, ShadowCastingMode, bool, Transform, LightProbeUsage,
    ///         LightProbeProxyVolume)
    /// Its body is a 12-arg forward to the extern - ~30 bytes of IL, over Mono's inline
    /// limit, so the detour holds for every caller. One prefix therefore covers the whole
    /// class, Verse and modded alike, and Graphic.Draw's output arrives here too (its
    /// DrawWorkers submit through Graphics.DrawMesh), which is why the old Graphic.Draw
    /// patch is GONE - two seams on one flow would be rule 50's silent double-fire, except
    /// the tolerance guard below happens to make translation idempotent. One seam anyway.
    ///
    /// THE DISCRIMINATION (unchanged from v1): inside the armed window both kinds of call
    /// arrive - the thing's own graphic at our ALREADY-TRANSLATED loc, and legacy draws
    /// still at the RAW position. Bands sit >= a Slot (192+) apart in z; comp draws stay
    /// within a few cells of their parent (the widest honest case is a line's midpoint,
    /// covered up to a 16-cell z-span). |m23 - rawZ| <= 8 says "still at the source":
    /// translate. Anything further is either already translated or a cross-thing beam that
    /// the relays own. The guard also makes the patch IDEMPOTENT: a translated matrix is a
    /// Slot away from raw and can never be translated again.
    ///
    /// ⚠ THE WINDOW STAYS REALTIME-THINGS-ONLY - THE PAWN LOOP IS DELIBERATELY EXCLUDED.
    /// ABBelowRenderCache bakes atlas frames via a camera render INSIDE the pawn draw
    /// phases; bake-space geometry sits near z=0, and for a pawn in the bottom rows of the
    /// map that is within tolerance of its raw z - arming there would translate bake
    /// geometry out of the bake camera and ship blank pawn frames. Worn shield bubbles
    /// below therefore stay a known gap (§95.h) until the bake gets its own bracket.
    ///
    /// ⚠ KNOWN OUT: Graphics.DrawMeshInstanced* are TRUE externs (InternalCall, no managed
    /// body to detour). Nothing in the census draws per-thing content through them
    /// (designators/vacuum overlays are whole-map passes with their own stories). If a mod
    /// ever instances a comp overlay, that is a new census entry, not a bug here.
    ///
    /// COST (§36e): the game's every non-instanced DrawMesh - section submeshes, dynamic
    /// things, overlays, a few hundred to low thousands per frame - now crosses one
    /// trampoline, one static float read and a branch. That is the price of "all of them"
    /// and it is the row named ABBelowMeshTranslate if Circinus ever disagrees.
    ///
    /// INSTALL IS MANUAL, NEVER ATTRIBUTED: a guessed Unity signature in a [HarmonyPatch]
    /// attribute fails at PatchAll time and takes every other patch in the assembly down
    /// with it (PipeSystemCompat's lesson). Failure here degrades to the v1 Graphic.Draw
    /// seam - fire family keeps working, the DrawMesh class stays a gap, and a WarningOnce
    /// says so (rule 33).
    /// </summary>
    public static class ABBelowMeshTranslate
    {
        private const float Tolerance = 8f;

        /// <summary>The one true seam. No try/catch: float field reads and compares on
        /// structs cannot throw, and this is the hottest patch this mod owns.</summary>
        internal static void PrefixInternalDrawMesh(ref Matrix4x4 matrix)
        {
            float drop = ABBelowDynamicDraw.RealtimeDropZ;
            if (drop == 0f)
            {
                return; // the permanent common case
            }
            float dz = matrix.m23 - ABBelowDynamicDraw.RealtimeRawZ;
            if (dz > Tolerance || dz < -Tolerance)
            {
                return; // already translated, or not this thing's neighbourhood
            }
            matrix.m23 += drop;
        }

        /// <summary>Rule-33 fallback: the Tier E v1 seam, installed only when
        /// Internal_DrawMesh could not be patched. Covers Graphic.Draw-routed overlays
        /// (the fire family); the raw DrawMesh class stays dark under it.</summary>
        internal static void PrefixGraphicDraw(ref Vector3 loc)
        {
            float drop = ABBelowDynamicDraw.RealtimeDropZ;
            if (drop == 0f)
            {
                return;
            }
            float dz = loc.z - ABBelowDynamicDraw.RealtimeRawZ;
            if (dz > Tolerance || dz < -Tolerance)
            {
                return;
            }
            loc.z += drop;
        }
    }

    /// <summary>Manual installation with graceful degradation. [StaticConstructorOnStartup]
    /// for uniformity with the other late installers; nothing here needs the DefDatabase,
    /// but running late costs nothing and keeps one boot pattern.</summary>
    [StaticConstructorOnStartup]
    public static class ABBelowMeshTranslateBoot
    {
        static ABBelowMeshTranslateBoot()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(Graphics), "Internal_DrawMesh");
                if (target != null)
                {
                    HarmonyBoot.Harmony.Patch(target, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(ABBelowMeshTranslate),
                            nameof(ABBelowMeshTranslate.PrefixInternalDrawMesh))));
                    ABLog.Dev("below mesh translate seated on Graphics.Internal_DrawMesh.");
                    return;
                }
                Log.WarningOnce(ABLog.Tag + " Graphics.Internal_DrawMesh not found in this"
                    + " Unity build; falling back to the Graphic.Draw seam (raw DrawMesh"
                    + " overlays will not show across levels).", 0x2B10E6);
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " could not patch Graphics.Internal_DrawMesh ("
                    + e.Message + "); falling back to the Graphic.Draw seam.", 0x2B10E6);
            }
            try
            {
                HarmonyBoot.Harmony.Patch(
                    AccessTools.Method(typeof(Graphic), nameof(Graphic.Draw)),
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(ABBelowMeshTranslate),
                        nameof(ABBelowMeshTranslate.PrefixGraphicDraw))));
                ABLog.Dev("below mesh translate FALLBACK seated on Graphic.Draw.");
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " below mesh translate failed to seat on either"
                    + " seam: " + e, 0x2B10E7);
            }
        }
    }
}
