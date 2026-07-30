using System;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// CROSS-LEVEL WEATHER.
    ///
    /// THE CONSTRAINT THAT SHAPES EVERYTHING HERE: <c>WeatherManager</c> is per-Map and V2
    /// has exactly ONE Map. There is one <c>curWeather</c>, one RainRate/SnowRate, one wind
    /// speed and one sky for all seven levels, and that is not negotiable without shadow
    /// WeatherManagers and intercepting every <c>map.weatherManager</c> read in the game.
    ///
    /// So weather is not simulated per band. It is one weather, INTERPRETED per band:
    ///
    ///   Tier 1 - PRESENTATION. The overlays do not draw on a level that is underground.
    ///   Tier 2 - EFFECT. Precipitation falls as snow wherever the band is below freezing,
    ///            and snow melts at the band's own temperature rather than the map's.
    ///   Wind   - turbines earn more at altitude and nothing underground.
    ///
    /// That is enough to deliver the whole fantasy - dry rock below, rain on the surface,
    /// blizzard on the peak, all from a single storm - without inventing a second weather
    /// system.
    /// </summary>
    public static class ABBandWeather
    {
        /// <summary>Vanilla's <c>SteadyEnvironmentEffects.MeltAmountAt</c>, reproduced
        /// because it is private. Note the first branch: BELOW ZERO, NOTHING MELTS. That is
        /// the entire snow-line mechanism - once the melt is asked about the band's own
        /// temperature, a sub-zero level simply never loses snow, with no bespoke
        /// "permanent snow" flag anywhere.</summary>
        internal static float MeltAmountAt(float temperature)
        {
            if (temperature < 0f)
            {
                return 0f;
            }
            if (temperature < 10f)
            {
                return temperature * temperature * 0.0058f * 0.1f;
            }
            return temperature * 0.0058f;
        }

        /// <summary>Outdoor temperature as this band experiences it.</summary>
        internal static float BandOutdoorTemp(Map map, int level)
        {
            return map.mapTemperature.OutdoorTemp + ABBandEnv.TempOffsetForLevel(level);
        }

        /// <summary>Vanilla's own outdoor test from DoCellSteadyEffects, so the rain-to-snow
        /// pass covers exactly the cells vanilla would have rained on.</summary>
        internal static bool OutdoorAt(Map map, IntVec3 c)
        {
            Room room = c.GetRoom(map);
            return room == null || room.UsesOutdoorTemperature;
        }
    }

    /// <summary>
    /// SNOW, PER BAND. Two defects, one interception.
    ///
    /// DEFECT 1 - "the snow melts immediately at +3". Outdoor melt is MAP-WIDE:
    /// <c>SteadyEnvironmentEffectsTick</c> computes
    /// <c>outdoorMeltAmount = MeltAmountAt(map.mapTemperature.OutdoorTemp)</c> exactly once
    /// per tick and every outdoor cell is then melted by that one number. It never touches
    /// <c>GenTemperature.TryGetTemperatureForCell</c>, which is the ONLY thing the per-level
    /// offset patches - so the -12 at +3 was invisible to snow and the alpine cap melted at
    /// the same rate as the meadow below it. The snow line has never worked; the note in
    /// ABBandEnv claiming vanilla melts per-cell was simply wrong.
    ///
    /// DEFECT 2 - a sub-zero level should not be rained on. Vanilla has one precipitation
    /// type for the whole map, so a rainstorm on the surface fell as rain on the peak too.
    ///
    /// THE PREFIX MUST WRITE THE FIELD ON EVERY CALL, INCLUDING THE SURFACE. The field is
    /// shared state that vanilla sets once per tick and then reuses across ~87 cells in that
    /// tick; leaving it alone for level 0 would hand a surface cell whatever value the last
    /// SKY cell installed, at random, depending on the order cells come out of
    /// cellsInRandomOrder. Recomputing unconditionally is both correct and self-healing, and
    /// costs one add and a compare per cell.
    ///
    /// The rain-to-snow half is a POSTFIX calling the public <c>AddFallenSnowAt</c> rather
    /// than more field rewriting, because vanilla's snowfall line reads the LIVE property
    /// (<c>0.046f * map.weatherManager.SnowRate</c>) for the amount and only uses the cached
    /// field for the <c>&gt; 0.001f</c> gate - so forcing the field would pass the gate and
    /// then add exactly zero snow.
    /// </summary>
    [HarmonyPatch(typeof(SteadyEnvironmentEffects), "DoCellSteadyEffects")]
    public static class Patch_SteadyEnvironmentEffects_ABBandSnow
    {
        private static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, Map> MapRef =
            AccessTools.FieldRefAccess<SteadyEnvironmentEffects, Map>("map");

        private static readonly AccessTools.FieldRef<SteadyEnvironmentEffects, float> MeltRef =
            AccessTools.FieldRefAccess<SteadyEnvironmentEffects, float>("outdoorMeltAmount");

        private static void Prefix(SteadyEnvironmentEffects __instance, IntVec3 c)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return;
                }
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                // Unconditional - see the class note. Level 0 recomputes to exactly
                // vanilla's value, so the surface is untouched in behaviour.
                MeltRef(__instance) = ABBandWeather.MeltAmountAt(
                    ABBandWeather.BandOutdoorTemp(map, bands.LevelOf(c)));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: band melt prefix threw: " + e, 118843301);
            }
        }

        private static void Postfix(SteadyEnvironmentEffects __instance, IntVec3 c)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return;
                }
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int level = bands.LevelOf(c);
                if (level <= 0)
                {
                    return; // the surface gets vanilla's precipitation; below is roofed rock
                }
                float rain = map.weatherManager.RainRate;
                if (rain <= 0.001f)
                {
                    return;
                }
                if (ABBandWeather.BandOutdoorTemp(map, level) >= 0f)
                {
                    return; // warm enough up here for it to stay rain
                }
                // Match vanilla's own gates exactly, or snow appears indoors and under roofs.
                if (map.roofGrid.Roofed(c) || !ABBandWeather.OutdoorAt(map, c))
                {
                    return;
                }
                __instance.AddFallenSnowAt(c, 0.046f * rain);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: rain-to-snow postfix threw: " + e, 118843302);
            }
        }
    }

    /// <summary>
    /// TIER 1 - no weather overlays on a level that is underground.
    ///
    /// <c>DrawAllWeather</c> is purely visual (the event handler's draw pass plus the two
    /// weather workers' full-screen overlays), so suppressing it changes nothing about
    /// simulation, sound or accumulation - the basement is roofed rock and vanilla's roof
    /// test already keeps rain and snow from settling there. What was wrong was only that
    /// the player watched rain fall through a hundred metres of stone.
    ///
    /// Judged on the VIEWED band rather than any particular cell, because the overlays are
    /// screen-space: there is no per-cell answer to give. Sky bands deliberately keep their
    /// weather - they are outdoors, and Tier 2 has already turned the rain up there into
    /// snow.
    /// </summary>
    [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.DrawAllWeather))]
    public static class Patch_WeatherManager_ABNoWeatherUnderground
    {
        private static bool Prefix()
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return true;
                }
                Map map = Find.CurrentMap;
                if (map == null || !ABBands.Banded(map))
                {
                    return true;
                }
                return ABBandView.CurrentLevel(map) >= 0;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// WIND SCALES WITH ALTITUDE - the one way the sky levels pay the player back.
    ///
    /// Patched on the <c>DesiredPowerOutput</c> GETTER, not on CompTick, and that choice is
    /// load-bearing: <c>CompPowerPlantWind.CompTick</c> only recomputes
    /// <c>cachedPowerOutput</c> every 250 ticks, so a postfix there would multiply the same
    /// stored field again on every one of the 249 ticks in between and the turbine's output
    /// would run away exponentially. The getter is a pure read of that field and
    /// <c>CompPowerPlant.UpdateDesiredPowerOutput</c> calls it fresh each tick, so scaling
    /// there is idempotent by construction.
    ///
    /// Applied AFTER vanilla's <c>Mathf.Min(WindSpeed, 1.5f)</c> cap, deliberately: the cap
    /// is a limit on how hard the weather can blow, and altitude is a separate multiplier on
    /// top of it. A ridge turbine at +3 therefore can exceed the sea-level ceiling, which is
    /// the entire point of hauling one up there.
    /// </summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), "DesiredPowerOutput", MethodType.Getter)]
    public static class Patch_CompPowerPlantWind_ABAltitudeWind
    {
        private static void Postfix(CompPowerPlantWind __instance, ref float __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather) || __result == 0f)
                {
                    return;
                }
                Thing parent = __instance.parent;
                if (parent == null || !parent.Spawned)
                {
                    return;
                }
                Map map = parent.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                __result *= ABBandEnv.WindFactorForLevel(bands.LevelOf(parent.Position));
            }
            catch
            {
                // Power output must never be the thing that throws.
            }
        }
    }

    /// <summary>Tells the player WHY the turbine on the ridge is out-performing the one in
    /// the valley. An unexplained multiplier reads as a bug.</summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), nameof(CompPowerPlantWind.CompInspectStringExtra))]
    public static class Patch_CompPowerPlantWind_ABInspectString
    {
        private static void Postfix(CompPowerPlantWind __instance, ref string __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return;
                }
                Thing parent = __instance.parent;
                if (parent == null || !parent.Spawned)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(parent.Map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int level = bands.LevelOf(parent.Position);
                float factor = ABBandEnv.WindFactorForLevel(level);
                if (Mathf.Approximately(factor, 1f))
                {
                    return;
                }
                string line = level < 0
                    ? "AB_WindNoneUnderground".Translate()
                    : "AB_WindAltitudeBonus".Translate(
                        Mathf.RoundToInt((factor - 1f) * 100f).ToString());
                __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
            }
            catch
            {
            }
        }
    }
}
