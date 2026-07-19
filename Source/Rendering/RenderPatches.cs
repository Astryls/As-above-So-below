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
    /// Patches every declared DrawPos getter (vanilla and modded Thing subclasses)
    /// with a postfix that shifts the result down while the lower map's dynamic
    /// draw pass runs. Inactive cost is a single static bool read.
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

        private static void OffsetPostfix(ref Vector3 __result)
        {
            if (LevelRenderer.OffsetActive)
            {
                __result.y += LevelRenderer.BelowOffset;
            }
        }
    }
}
