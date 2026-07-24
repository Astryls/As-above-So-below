using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Auto-generates the sky level at map creation when the map carries a
    /// mountain-source river (user directive 2026-07-24: "if we need to make
    /// sky levels generate on map selection to make this happen, that is
    /// okay") - so the lifted watercourse and its real waterfalls exist from
    /// the moment the player lands, no dev tool or manual level creation
    /// needed.
    ///
    /// Scope guards: player home maps only (raid sites, quest maps, and
    /// caravan encounters never auto-stack), the mountainRivers setting, the
    /// LevelGen kill switch, and the map must not already have a sky level.
    /// The qualification check is the SAME classifier the sky river genstep
    /// uses, run against the thick-rock roof footprint (the sky level does
    /// not exist yet); generation is deferred with ExecuteWhenFinished so a
    /// pocket map is never created inside another map's generation pass.
    /// Fails open: any exception trips LevelGen and leaves the map unstacked.
    /// </summary>
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
    internal static class Patch_MapGenerator_AutoSkyRivers
    {
        private static void Postfix(Map __result)
        {
            Map map = __result;
            if (map == null || !ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.mountainRivers)
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                try
                {
                    if (map.Disposed || !map.IsPlayerHome)
                    {
                        return;
                    }
                    LevelComp comp = map.Levels();
                    if (comp == null || comp.level != 0 || comp.upperMap != null)
                    {
                        return;
                    }
                    if (!GenStep_ABSkyRivers.GroundQualifiesForAutoSky(map))
                    {
                        return;
                    }
                    LevelMapGen.GetOrGenerate(map, 1, ABDefOf.AB_Sky, out _);
                    ABLog.Dev("Auto-generated sky level for a mountain-source river on map "
                        + map.uniqueID + ".");
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.LevelGen, e, "auto sky generation");
                }
            });
        }
    }
}
