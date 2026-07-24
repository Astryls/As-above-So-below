using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    internal static class Patch_MapDrawer_DrawMapMesh
    {
        private static void Prefix(Map ___map)
        {
            LevelRenderer.DrawBelowStatic(___map);
        }
    }

    /// <summary>
    /// Mirrors lower-map mesh dirtiness upward so the sky map's below-things
    /// layer reprints exactly the cells that changed (thing spawned or
    /// despawned, building built or destroyed, plant growth reprint). Vanilla
    /// marks adjacent-section flags internally without recursing, so the
    /// mirror passes regenAdjacentCells for linked-graphic edges. Costs one
    /// static bool and a flag mask when no sky level exists.
    /// </summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty),
        new Type[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    internal static class Patch_MapDrawer_MapMeshDirty
    {
        private static void Postfix(Map ___map, IntVec3 loc, ulong dirtyFlags)
        {
            LevelSync.OnLowerMeshDirty(___map, loc, dirtyFlags);
        }
    }

    // NOTE (run-25): a Graphic_Linked.ShouldLinkWith postfix lived here for
    // one build, force-linking sky-level natural rock toward open mass cells
    // so walls would not print edge lips facing the cap fill. Reverted: it
    // switched the whole unfogged wall band to the atlas' mask-15 interior
    // tile, which vanilla (almost) never displays - a fully-linked rock cell
    // is always fogged in vanilla play - and which reads as a near-flat
    // "square gray" slab, erasing the rock-group look. Native wall lips
    // facing the fill are the vanilla language for standing rock above
    // walkable floor and stay.
    [HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]
    internal static class Patch_DynamicDrawManager_DrawDynamicThings
    {
        private static void Postfix(Map ___map)
        {
            // Fires again for our own nested lower-map call, but that map is not
            // Find.CurrentMap, so DrawBelowDynamic early-outs. No recursion.
            LevelRenderer.DrawBelowDynamic(___map);
        }
    }

    /// <summary>
    /// Below-view pawn silhouettes (the far-zoom highlight outlines) are drawn at
    /// AltitudeLayer.Silhouettes, a fixed altitude that ignores our DrawPos offset,
    /// so a lower-map pawn's silhouette would float over the sky terrain. Skip the
    /// whole silhouette pass while the lower map's dynamic draw runs; the sky map's
    /// own silhouettes (OffsetActive false) are untouched.
    /// </summary>
    [HarmonyPatch(typeof(DynamicDrawManager), "DrawSilhouettes")]
    internal static class Patch_DynamicDrawManager_DrawSilhouettes
    {
        private static bool Prefix()
        {
            return !LevelRenderer.OffsetActive;
        }
    }

    /// <summary>
    /// Patches every declared DrawPos getter (vanilla and modded Thing subclasses)
    /// with a postfix that applies the below-view transform (altitude drop plus
    /// the faux-perspective depth shift and optional parallax) while the lower
    /// map's dynamic draw pass runs. Inactive cost is a single static bool read.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class DrawPosOffsetPatcher
    {
        static DrawPosOffsetPatcher()
        {
            HarmonyMethod postfix = new HarmonyMethod(typeof(DrawPosOffsetPatcher), nameof(OffsetPostfix))
            {
                priority = Priority.Last
            };
            int patched = 0;
            List<Type> types = new List<Type> { typeof(Thing) };
            types.AddRange(typeof(Thing).AllSubclasses());
            foreach (Type type in types)
            {
                MethodInfo getter = null;
                try
                {
                    PropertyInfo prop = type.GetProperty("DrawPos",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    getter = prop?.GetGetMethod(true);
                }
                catch (Exception)
                {
                    continue;
                }
                if (getter == null || getter.IsAbstract)
                {
                    continue;
                }
                try
                {
                    HarmonyBoot.Harmony.Patch(getter, postfix: postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    Log.Warning(ABLog.Tag + " Could not patch DrawPos on " + type.Name + ": " + e.Message);
                }
            }
            ABLog.Dev("Patched DrawPos on " + patched + " types for below-level rendering.");
        }

        // PERF-MOD COMPLIANCE (Performance Optimizer, verified 2026-07-24):
        // PO's Optimization_Pawn_DrawPos caches Pawn.DrawPos in a tick-stamped
        // per-pawn cache, but ONLY inside two scopes (Designation.Draw and
        // Dubs Mint minimap's DrawAllPawns) via an EnableCache/DisableCache
        // sandwich; outside them its prefix passes through and its
        // store-postfix gets a null state. Two invariants keep us compatible:
        //   1. OffsetActive is never true inside those scopes (we set it only
        //      around our nested lower-map dynamic draw and bracket draw), so
        //      PO can never cache a shifted position.
        //   2. This postfix stays priority Last and a strict no-op when
        //      OffsetActive is false.
        // If below-view designation drawing is ever added (OffsetActive around
        // a Designation.Draw call), PO would cache the SHIFTED DrawPos for its
        // refresh window - re-verify before widening the OffsetActive window.
        private static void OffsetPostfix(ref Vector3 __result)
        {
            if (LevelRenderer.OffsetActive)
            {
                LevelRenderer.ApplyDrawShift(ref __result);
            }
        }
    }

    /// <summary>
    /// Below-view "fake zoom out": pawns on the lower map draw shrunk about
    /// their own root position, so they read as one story further from the
    /// camera while staying glued to their cells. PawnDrawParms.matrix is the
    /// root every render tree node composes off (body, head, apparel, carried
    /// thing), so one right-multiplied scale shrinks the whole pawn in place.
    /// Gated on OffsetActive: portraits, statues, and same-level pawns are
    /// untouched.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "GetDrawParms")]
    internal static class Patch_PawnRenderer_GetDrawParms
    {
        private static void Postfix(ref PawnDrawParms __result)
        {
            if (LevelRenderer.OffsetActive)
            {
                float s = LevelRenderer.BelowThingScale;
                if (s < 0.999f)
                {
                    __result.matrix *= Matrix4x4.Scale(new Vector3(s, 1f, s));
                }
            }
        }
    }

    /// <summary>
    /// Humanlike pawns beyond ZoomRootSize 18 draw via the cached atlas blit,
    /// which positions a premade mesh directly at bodyPos and never reads
    /// PawnDrawParms.matrix - the scale above would silently drop out at far
    /// zoom. Disable the cache for below-view pawns only; the handful visible
    /// through open air render through the full tree instead.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    internal static class Patch_PawnRenderer_ParallelGetPreRenderResults
    {
        private static void Prefix(ref bool disableCache)
        {
            if (LevelRenderer.OffsetActive && LevelRenderer.BelowThingScale < 0.999f)
            {
                disableCache = true;
            }
        }
    }
}
