using System;
using System.Collections.Generic;
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

        /// <summary>True only while the top-right temperature readout is being built.
        /// See <see cref="Patch_GlobalControls_ABBandTemperature"/>.</summary>
        internal static bool ReadoutScope;

        /// <summary>
        /// SNOW HAS TO EXIST BEFORE IT CAN PERSIST.
        ///
        /// Reported after the melt fix landed: "upper level does not have snow". The melt
        /// was correct - at -35 nothing up there melts - but NOTHING WAS CREATING SNOW
        /// either. Vanilla only ever adds snow from falling precipitation, the weather is
        /// chosen from the SURFACE temperature, and a surface at +12 picks rain or clear,
        /// so a permanently frozen peak sat bare until it happened to rain. A snow cap that
        /// only appears after the first storm is not a snow cap.
        ///
        /// So generation-time seeding is BACK - and the original objection to it no longer
        /// applies. It was removed because a pre-frozen top level "read as a different biome
        /// instead of higher ground", which it did: with melt still map-wide, seeded snow was
        /// a permanent painted-on layer that no amount of heat could shift. Now the melt is
        /// band-aware, so this seeds the EQUILIBRIUM the simulation would have reached on its
        /// own and then hands control straight back to it - a hot spell at +2 will strip it,
        /// and the seasons move the line. Painted at t=0, but no longer painted permanently.
        ///
        /// Depth is scaled by how far below freezing the band actually is, so a marginal
        /// level gets a dusting and +3 gets a full cap. Seeded from the CURRENT outdoor
        /// temperature rather than an annual mean on purpose: a colony started in midwinter
        /// should find snow further down the mountain than one started in high summer.
        /// </summary>
        public static void SeedAltitudeSnow(Map map, ABBandMap bands)
        {
            if (map == null || bands == null || !bands.Banded || !ABGuard.On(ABGuard.Weather))
            {
                return;
            }
            float outdoor;
            try
            {
                outdoor = map.mapTemperature.OutdoorTemp;
            }
            catch
            {
                outdoor = map.TileInfo != null ? map.TileInfo.temperature : 0f;
            }
            TerrainGrid terrain = map.terrainGrid;
            RoofGrid roofs = map.roofGrid;
            SnowGrid snow = map.snowGrid;
            int seeded = 0;
            for (int band = bands.surfaceBand + 1; band < bands.bandCount; band++)
            {
                float t = outdoor + ABBandEnv.TempOffsetForLevel(band - bands.surfaceBand);
                if (t >= 0f)
                {
                    continue; // this level is above freezing today; no snow line here
                }
                float depth = Mathf.Clamp01(-t / 20f);
                if (depth <= 0.01f)
                {
                    continue;
                }
                foreach (IntVec3 c in bands.RectOfBand(band))
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    // Cheap filters FIRST. On a sky band most cells are open air (which no
                    // longer holds snow) or roofed mountain mass, so this rejects the large
                    // majority before touching SnowGrid at all - SetDepth is not free, it
                    // re-checks the edifice grid and can dirty mesh and path cost per cell.
                    TerrainDef td = terrain.TerrainAt(c);
                    if (td == null || !td.holdSnowOrSand || roofs.Roofed(c))
                    {
                        continue;
                    }
                    snow.SetDepth(c, depth);
                    seeded++;
                }
            }
            if (seeded > 0)
            {
                ABLog.Dev("Altitude snow seeded on " + seeded + " cell(s) (surface "
                    + outdoor.ToString("0.0") + "C).");
            }
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

    /// <summary>
    /// THE TOP-RIGHT TEMPERATURE READOUT, PER BAND.
    ///
    /// Reported: "the temp overlay shows it getting cooler but the right side widget stays
    /// at 12C". Both observations were correct and they are different code paths. The
    /// overlay asks <c>GenTemperature.TryGetTemperatureForCell</c>, which this mod patches,
    /// so it was already band-aware. <c>GlobalControls.TemperatureString</c> does NOT: when
    /// the cell under the mouse is outdoors it reads <c>mapTemperature.OutdoorTemp</c>, a
    /// MAP-WIDE scalar (the §1 slicing shape again), so it reported the surface's weather
    /// from the top of the mountain.
    ///
    /// That single wrong number is also why weather "looked the same on every level": the
    /// readout is the only place a player can see the temperature they are standing in, so
    /// with it pinned to the surface there was no visible evidence that altitude did
    /// anything at all.
    ///
    /// FIXED BY SCOPE, NOT BY REWRITING THE WIDGET. Reimplementing TemperatureString would
    /// mean copying vanilla's Indoors / IndoorsUnroofed(N) / adjacent-room-probe logic and
    /// maintaining it forever, and patching <c>OutdoorTemp</c> globally is out of the
    /// question - it drives weather selection, comfort and our own melt, which would then
    /// double-count the offset. So the getter is offset ONLY while this one UI method is on
    /// the stack. Vanilla's ternary is lazy, so the getter is reached only in the outdoors
    /// case; a sealed room reports its own tracked temperature and is untouched.
    ///
    /// A Finalizer rather than a Postfix clears the flag: a Postfix does not run if the
    /// method throws, and a stuck flag would silently offset every OutdoorTemp read in the
    /// game.
    /// </summary>
    [HarmonyPatch(typeof(GlobalControls), "TemperatureString")]
    public static class Patch_GlobalControls_ABBandTemperature
    {
        private static void Prefix()
        {
            ABBandWeather.ReadoutScope = true;
        }

        private static void Finalizer()
        {
            ABBandWeather.ReadoutScope = false;
        }
    }

    /// <summary>The scoped half of the readout fix - inert unless the flag above is set, so
    /// every other consumer of OutdoorTemp (weather, melt, comfort) still sees vanilla's
    /// map-wide value, which is what they must see.</summary>
    [HarmonyPatch(typeof(MapTemperature), nameof(MapTemperature.OutdoorTemp), MethodType.Getter)]
    public static class Patch_MapTemperature_ABReadoutOffset
    {
        private static void Postfix(ref float __result)
        {
            if (!ABBandWeather.ReadoutScope)
            {
                return; // the overwhelming majority of calls, one bool read
            }
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                __result += ABBandEnv.TempOffsetForLevel(ABBandView.CurrentLevel(map));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// A TURBINE'S WIND PATH MUST NOT LEAVE ITS OWN LEVEL.
    ///
    /// Reported: turbines show the altitude note but read as roof-blocked even at +3.
    /// <c>WindTurbineUtility.CalculateWindCells</c> builds two rects reaching from
    /// <c>center.z - 10</c> to <c>center.z + 11</c> - **22 rows** - and
    /// <c>RecalculateBlockages</c> then tests <c>roofGrid.Roofed</c> on every one with no
    /// notion of bands. The gutter is only 2 rows, so any turbine within ~11 rows of a band
    /// edge reaches straight across the seam into the NEXT band, which for a sky level is
    /// solid RoofRockThick mountain mass. The turbine was being blocked by a mountain on a
    /// different level, hundreds of cells away in world space.
    ///
    /// Filtering the BLOCKED lists (rather than the wind-path list) keeps vanilla's own
    /// geometry untouched and stays correct if the path calculation ever changes. The two
    /// lists are appended in lockstep, so they are trimmed at the same index.
    ///
    /// NOTE FOR TRIAGE: a turbine genuinely adjacent to this level's own mountain mass is
    /// still blocked, and that is correct - it is exactly what vanilla does next to a
    /// mountain. Only cross-band blocking is a bug.
    /// </summary>
    [HarmonyPatch(typeof(CompPowerPlantWind), "RecalculateBlockages")]
    public static class Patch_CompPowerPlantWind_ABWindPathBand
    {
        private static readonly AccessTools.FieldRef<CompPowerPlantWind, List<IntVec3>> BlockedCells =
            AccessTools.FieldRefAccess<CompPowerPlantWind, List<IntVec3>>("windPathBlockedCells");

        private static readonly AccessTools.FieldRef<CompPowerPlantWind, List<Thing>> BlockedThings =
            AccessTools.FieldRefAccess<CompPowerPlantWind, List<Thing>>("windPathBlockedByThings");

        private static void Postfix(CompPowerPlantWind __instance)
        {
            try
            {
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
                List<IntVec3> cells = BlockedCells(__instance);
                List<Thing> things = BlockedThings(__instance);
                if (cells == null || cells.Count == 0)
                {
                    return;
                }
                int band = bands.BandOf(parent.Position);
                for (int i = cells.Count - 1; i >= 0; i--)
                {
                    IntVec3 c = cells[i];
                    if (c.InBounds(map) && !bands.InGutter(c) && bands.BandOf(c) == band)
                    {
                        continue; // a real obstacle on this level
                    }
                    cells.RemoveAt(i);
                    if (things != null && i < things.Count)
                    {
                        things.RemoveAt(i);
                    }
                }
            }
            catch
            {
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
