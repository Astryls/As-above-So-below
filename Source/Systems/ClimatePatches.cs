using System;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The sky level's outdoor and seasonal temperatures mirror the ground map's
    /// live values (seasons, cold snaps, heat waves) instead of the pocket map's
    /// fixed constant. The basement keeps its stable underground constant by
    /// design. No recursion: the ground map is not a pocket map, so its own
    /// getters take the vanilla tile path.
    /// </summary>
    [HarmonyPatch(typeof(MapTemperature), "OutdoorTemp", MethodType.Getter)]
    internal static class Patch_MapTemperature_OutdoorTemp
    {
        private static void Postfix(Map ___map, ref float __result)
        {
            Map ground = ClimateSync.SkyGroundOrNull(___map);
            if (ground != null)
            {
                __result = ground.mapTemperature.OutdoorTemp;
            }
        }
    }

    [HarmonyPatch(typeof(MapTemperature), "SeasonalTemp", MethodType.Getter)]
    internal static class Patch_MapTemperature_SeasonalTemp
    {
        private static void Postfix(Map ___map, ref float __result)
        {
            Map ground = ClimateSync.SkyGroundOrNull(___map);
            if (ground != null)
            {
                __result = ground.mapTemperature.SeasonalTemp;
            }
        }
    }

    internal static class ClimateSync
    {
        /// <summary>Returns the ground map when the given map is a sky level with a
        /// live ground link; null otherwise. Cheap: comp cache lookup + two fields.</summary>
        public static Map SkyGroundOrNull(Map map)
        {
            // These postfixes ride two of the hottest vanilla getters; when the
            // game has no sky level at all, bail on static reads before touching
            // the per-map comp cache.
            if (!LevelCensus.AnySkyLevels)
            {
                return null;
            }
            if (!ABGuard.On(ABGuard.Climate) || map == null)
            {
                return null;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level != 1)
            {
                return null;
            }
            Map ground = comp.lowerMap ?? comp.groundMap;
            if (ground == null || ground.Disposed)
            {
                return null;
            }
            return ground;
        }
    }
}
