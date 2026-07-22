using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Pocket levels never run their own weather decider. The sky level's weather is
    /// mirrored from the surface by LevelComp on a fixed cadence; letting the sky
    /// map's decider pick and start its own transition in between reads as a brief
    /// weather flicker before the next sync corrects it. Suppressing the decider
    /// makes the surface the sole driver, so the sky only ever changes weather when
    /// the surface does. The basement is fully thick-roofed, so its decider is
    /// pointless work regardless.
    /// Fails open: if the weather kill switch is tripped, vanilla deciders run
    /// normally on every map.
    /// </summary>
    [HarmonyPatch(typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick))]
    internal static class Patch_WeatherDecider_Tick
    {
        private static readonly AccessTools.FieldRef<WeatherDecider, Map> MapRef =
            AccessTools.FieldRefAccess<WeatherDecider, Map>("map");

        private static bool Prefix(WeatherDecider __instance)
        {
            if (!ABGuard.On(ABGuard.Weather))
            {
                return true;
            }
            Map map = MapRef(__instance);
            // Run only on the surface (level 0) and on maps with no level comp;
            // every pocket level is driven by the surface sync instead.
            return map == null || map.Level() == 0;
        }
    }
}
