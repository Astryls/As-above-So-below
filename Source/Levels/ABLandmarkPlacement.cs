using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Placement fence for landmark generation on sky levels (2026-07-22,
    /// user report: ancient vents spawned floating on open-air cells over the
    /// below view, with megastructure pads painted across the air).
    ///
    /// Vanilla tile-mutator workers scatter their structures map-wide - the
    /// vent worker picks 5-9 cells via CellFinder validated only by
    /// GenSpawn.CanSpawnAt (which knows nothing about AB_OpenAir), then
    /// paints radius-8.9 pads via TerrainGrid.SetTerrain. Everything funnels
    /// through three chokepoints, so the fence is three scoped prefixes,
    /// armed by ABSkyLandmarks between our gen step (order 200) and the end
    /// of GenStep_MutatorFinal (order 1600) - a window in which ONLY the
    /// mutator gen steps run on the sky map:
    ///  1. GenSpawn.CanSpawnAt -> false off-plateau: workers' own searches
    ///     are STEERED onto the plateau, so structures place whole and their
    ///     spacing rules naturally reduce counts on small plateaus.
    ///  2. GenSpawn.Spawn -> veto off-plateau spawns (backstop for workers
    ///     that place without asking CanSpawnAt).
    ///  3. TerrainGrid.SetTerrain -> veto off-plateau terrain writes (pads
    ///     clip at the plateau edge instead of floating on air).
    /// The mask is plateau ground minus tarn water. All checks require the
    /// scoped map, fail open on the LevelGen kill switch, and the scope is
    /// cleared by a finalizer on GenStep_MutatorFinal.
    /// </summary>
    internal static class ABLandmarkPlacement
    {
        private static Map scopeMap;
        private static bool[] mask;
        private static int maskW;
        private static int maskH;

        internal static void BeginScope(Map map, bool[] plateauMask)
        {
            scopeMap = map;
            mask = plateauMask;
            maskW = map.Size.x;
            maskH = map.Size.z;
        }

        internal static void EndScope(Map map)
        {
            if (scopeMap == map)
            {
                scopeMap = null;
                mask = null;
            }
        }

        internal static bool Fenced(Map map)
        {
            return scopeMap != null && map == scopeMap && mask != null && ABGuard.On(ABGuard.LevelGen);
        }

        internal static bool CellOk(IntVec3 c)
        {
            if (c.x < 0 || c.z < 0 || c.x >= maskW || c.z >= maskH)
            {
                return false;
            }
            return mask[c.z * maskW + c.x];
        }

        internal static bool RectOk(IntVec3 root, Rot4 rot, IntVec2 size)
        {
            foreach (IntVec3 c in GenAdj.OccupiedRect(root, rot, size))
            {
                if (!CellOk(c))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Steer worker cell searches onto the plateau.</summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.CanSpawnAt))]
    internal static class Patch_ABFence_CanSpawnAt
    {
        // Parameter names MUST match the vanilla signature (thingDef, c, rot):
        // Harmony binds injected arguments by name, and the old def/loc names
        // threw a patching exception at startup that silently disabled this
        // patch (run #70).
        private static bool Prefix(ThingDef thingDef, IntVec3 c, Map map, Rot4? rot, ref bool __result)
        {
            if (!ABLandmarkPlacement.Fenced(map))
            {
                return true;
            }
            Rot4 useRot = rot ?? thingDef?.defaultPlacingRot ?? Rot4.North;
            if (!ABLandmarkPlacement.RectOk(c, useRot, thingDef?.size ?? IntVec2.One))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Backstop: veto off-plateau spawns outright.</summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn),
        typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool))]
    internal static class Patch_ABFence_Spawn
    {
        private static bool Prefix(Thing newThing, IntVec3 loc, Map map, Rot4 rot, ref Thing __result)
        {
            if (!ABLandmarkPlacement.Fenced(map))
            {
                return true;
            }
            IntVec2 size = newThing?.def?.size ?? IntVec2.One;
            if (!ABLandmarkPlacement.RectOk(loc, rot, size))
            {
                ABLog.Dev("Landmark fence: vetoed off-plateau spawn of "
                    + (newThing?.def?.defName ?? "?") + " at " + loc + ".");
                __result = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>Pads and terrain washes clip at the plateau edge.</summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    internal static class Patch_ABFence_SetTerrain
    {
        private static bool Prefix(IntVec3 c, Map ___map)
        {
            if (!ABLandmarkPlacement.Fenced(___map))
            {
                return true;
            }
            return ABLandmarkPlacement.CellOk(c);
        }
    }

    /// <summary>The last mutator gen step in the AB_Sky generator closes the
    /// fence window; finalizer so an exception in a worker cannot leave the
    /// fence armed.</summary>
    [HarmonyPatch(typeof(GenStep_MutatorFinal), nameof(GenStep_MutatorFinal.Generate))]
    internal static class Patch_ABFence_MutatorFinal
    {
        private static void Finalizer(Map map)
        {
            ABLandmarkPlacement.EndScope(map);
        }
    }
}
