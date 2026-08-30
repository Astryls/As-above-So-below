using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - per-band environment.
    ///
    /// This is the file that pays V2's ONE genuine regression. In V1 every level is a
    /// pocket map, so PocketMapProperties.biome gives each level a real BiomeDef for
    /// free. V2 has one Map, and Map.Biome is a get-only property derived from the world
    /// tile - so per-band biome has to be resolved explicitly and fed to the consumers
    /// that care.
    ///
    /// RESOLVED 2026-07-28: the "feed each consumer explicitly" plan is superseded. 1.6
    /// ships a per-CELL biome API (map.BiomeAt -> MixedBiomeMapComponent.GetBiomeAt) that
    /// vanilla itself routes plant spawning, animal spawning and generation terrain
    /// through. ABBandBiome patches that single cell-parameterized choke point and calls
    /// BiomeOf below, so those consumers are now correct by construction rather than one
    /// bespoke patch at a time. This function is the single source of truth for "what
    /// biome is this cell in".
    ///
    /// STILL DELIBERATELY NOT DONE: a contextual map.Biome getter override driven by an
    /// ambient "current cell" latch. It would additionally catch third-party code that
    /// reads map.Biome directly, and it is precisely the lying-to-vanilla-behind-a-global
    /// pattern that made V1 unmaintainable. A cell-parameterized query is not that.
    ///
    /// What V2 gets RIGHT that V1 had to fake: "the basement has no weather and no
    /// plants" is not a biome property here - the basement is roofed solid rock, so the
    /// roof grid stops rain, snow and sunlight exactly as it does for any vanilla indoor
    /// space. That is why V1's LevelClimate/ClimateSync mirrors have no counterpart here.
    /// </summary>
    public static class ABBandEnv
    {
        /// <summary>Degrees offset from the map's outdoor temperature, per level.
        /// Underground is stable and cool.</summary>
        private const float BasementTempOffset = -6f;

        /// <summary>
        /// ⚠ THE LAPSE RATE IS NOT LINEAR, AND THE OLD COMMENT HERE WAS WRONG.
        ///
        /// It used to be a single -4/level constant, justified by "vanilla melts snow using
        /// each CELL's own temperature, so a cold high level keeps its snow". That is FALSE.
        /// <c>SteadyEnvironmentEffects.SteadyEnvironmentEffectsTick</c> computes
        /// <c>outdoorMeltAmount = MeltAmountAt(map.mapTemperature.OutdoorTemp)</c> ONCE PER
        /// TICK FOR THE WHOLE MAP and applies it to every outdoor cell. It never consults
        /// <c>GenTemperature.TryGetTemperatureForCell</c>, which is the only thing this
        /// offset patches - so the sky levels melted at exactly the surface's rate and the
        /// snow line has never once worked. Fixed by
        /// <see cref="Patch_SteadyEnvironmentEffects_ABBandSnow"/>, which is what finally
        /// makes these numbers mean something.
        ///
        /// The curve ACCELERATES rather than stepping evenly, because the three levels are
        /// meant to read as three different places rather than three equal increments:
        ///   +1  -7   a cool upland. Seasons unchanged, just brisker.
        ///   +2 -16   THE SEASONAL SNOW LINE - white in winter, bare in high summer. This
        ///            band straddling 0 across the year is the whole point; it is what makes
        ///            altitude legible at a glance.
        ///   +3 -35   a permanent cap in any ordinary biome. Chosen so a temperate summer
        ///            (~30) still lands at -5, while a desert (~45) or a heat wave DOES thaw
        ///            it - melting the cap is meant to be possible, just extreme.
        /// Deliberately NO ceiling clamp: the melt has to stay a consequence of real
        /// temperature, or an extreme biome could never overcome it.
        ///
        /// Levels beyond the table extrapolate at the last step so a taller stack stays
        /// monotonic instead of falling off the end of the array.
        /// </summary>
        public static readonly float[] DefaultSkyTempOffsets = { -7f, -16f, -35f };

        /// <summary>Deep rock does NOT keep getting colder - it converges on a stable
        /// temperature, and real caves warm again with depth. Flat by default; a geothermal
        /// or a freezing gradient is now the player's call rather than ours.</summary>
        public static readonly float[] DefaultDeepTempOffsets = { -6f, -6f, -6f };

        /// <summary>Wind multiplier by level. Exposed ridges catch more wind, and this is
        /// the one place altitude pays the player back for the cold. Applied to wind TURBINE
        /// output only (see <see cref="Patch_CompPowerPlantWind_ABAltitudeWind"/>), not to
        /// the map's wind speed itself, which is a single per-map value driving plant sway
        /// and weather visuals.
        ///
        /// Below the surface it is ZERO: there is no wind in a sealed basement. Vanilla's
        /// own roof check in RecalculateBlockages very likely zeroes a buried turbine
        /// already, but stating it here means the rule does not depend on that.</summary>
        public static readonly float[] DefaultSkyWindFactors = { 1.15f, 1.3f, 1.5f };

        /// <summary>
        /// CLIMATE IS SNAPSHOTTED PER COLONY, NOT READ LIVE FROM SETTINGS.
        ///
        /// <c>ABBandMap.SnapshotClimate</c> copies the settings onto the map component at
        /// generation and scribes them, exactly as the band layout itself is. Reading the
        /// live settings instead would re-climate every EXISTING save the moment a slider
        /// moved - dragging a settings bar would trigger global warming in a colony that had
        /// been running for three years, silently melting its snow line and killing its
        /// crops. A colony's climate is part of the world it was generated into.
        ///
        /// Falls back to the settings, then to the defaults above, so a map generated before
        /// snapshotting existed (or one mid-generation, before Setup runs) still answers
        /// sensibly.
        /// </summary>
        private static float FromTable(List<float> snapshot, List<float> configured,
            float[] fallback, int index)
        {
            List<float> table = snapshot != null && snapshot.Count > 0 ? snapshot : configured;
            if (table == null || table.Count == 0)
            {
                table = null;
            }
            int count = table != null ? table.Count : fallback.Length;
            if (index < count)
            {
                return table != null ? table[index] : fallback[index];
            }
            // Past the table: continue at the last step rather than clamping, so an eighth
            // level is colder than the seventh instead of identical to it.
            float last = table != null ? table[count - 1] : fallback[count - 1];
            float prev = count >= 2
                ? (table != null ? table[count - 2] : fallback[count - 2])
                : last;
            return last + (last - prev) * (index - count + 1);
        }

        /// <summary>
        /// True when this banded map's EFFECTIVE temperature offsets contain any nonzero
        /// entry - the demand test for ABPatchLifecycle, sited HERE so it can only mirror
        /// <see cref="FromTable"/>'s tier rule (snapshot if present, else live settings,
        /// else compiled defaults) rather than re-derive it and drift (rule 62).
        ///
        /// A null settings object returns true: FromTable would fall through to the
        /// compiled default tables, which are nonzero, so the patch must be on. All-zero
        /// tables extrapolate to zero past the end, so zero tables genuinely mean "off".
        /// </summary>
        public static bool AnyOffsetConfigured(ABBandMap bands)
        {
            ABSettings s = ABMod.Settings;
            List<float> sky = bands?.climateSky != null && bands.climateSky.Count > 0
                ? bands.climateSky
                : s?.skyTempOffsets;
            List<float> deep = bands?.climateDeep != null && bands.climateDeep.Count > 0
                ? bands.climateDeep
                : s?.deepTempOffsets;
            if (sky == null || sky.Count == 0 || deep == null || deep.Count == 0)
            {
                return true; // a missing tier falls back to the nonzero compiled defaults
            }
            for (int i = 0; i < sky.Count; i++)
            {
                if (sky[i] != 0f)
                {
                    return true;
                }
            }
            for (int i = 0; i < deep.Count; i++)
            {
                if (deep[i] != 0f)
                {
                    return true;
                }
            }
            return false;
        }

        private static BiomeDef undergroundBiome;

        private static BiomeDef skyBiome;

        public static BiomeDef BiomeOf(Map map, IntVec3 cell)
        {
            return BiomeOf(map, ABBands.CompOf(map), cell);
        }

        /// <summary>
        /// Overload for callers that have already resolved the band component.
        ///
        /// This exists for one reason: MixedBiomeMapComponent.GetBiomeAt is called PER CELL
        /// inside WildPlantSpawner's scan loops, and the convenience overload above cost two
        /// ConditionalWeakTable probes on every one of those calls - one inside
        /// ABBands.LevelOf, another inside the basement branch - even though the caller had
        /// just resolved the same component. Threading it through removes both from the
        /// hottest path this file has.
        /// </summary>
        public static BiomeDef BiomeOf(Map map, ABBandMap bands, IntVec3 cell)
        {
            if (map == null)
            {
                return null;
            }
            if (bands == null || !bands.Banded)
            {
                return map.Biome;
            }
            int level = bands.LevelOf(cell);
            if (level == 0)
            {
                return map.Biome;
            }
            if (level < 0)
            {
                // A basement carved by ABCavernGen carries a real cave biome, scribed on
                // the band component so it survives save/load - this is the V2 stand-in
                // for V1's pocketTileInfo.PrimaryBiome assignment. Uncarved basements
                // fall through to plain solid rock.
                if (bands.basementBiome != null)
                {
                    return bands.basementBiome;
                }
                return undergroundBiome
                    ?? (undergroundBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("AB_Underground"))
                    ?? map.Biome;
            }
            // Sky inherits the surface biome by default (V1's skyBiomeInherit), because a
            // plateau above a boreal forest should still feel boreal.
            if (ABMod.Settings != null && ABMod.Settings.skyBiomeInherit)
            {
                return map.Biome;
            }
            return skyBiome
                ?? (skyBiome = DefDatabase<BiomeDef>.GetNamedSilentFail("AB_OpenSky"))
                ?? map.Biome;
        }

        /// <summary>Temperature offset for a level, preferring the colony's own snapshot.
        /// Pass the band component wherever it is already in hand - the temperature patch
        /// alone runs this hundreds of thousands of times per sample.</summary>
        public static float TempOffsetForLevel(ABBandMap bands, int level)
        {
            if (level == 0)
            {
                return 0f;
            }
            ABSettings s = ABMod.Settings;
            if (level > 0)
            {
                return FromTable(bands?.climateSky, s?.skyTempOffsets,
                    DefaultSkyTempOffsets, level - 1);
            }
            return FromTable(bands?.climateDeep, s?.deepTempOffsets,
                DefaultDeepTempOffsets, -level - 1);
        }

        /// <summary>Settings-only overload, for callers with no map in hand (the settings
        /// preview, principally).</summary>
        public static float TempOffsetForLevel(int level)
        {
            return TempOffsetForLevel(null, level);
        }

        /// <summary>Wind turbine output multiplier for a level. 0 underground.</summary>
        public static float WindFactorForLevel(ABBandMap bands, int level)
        {
            if (level == 0)
            {
                return 1f;
            }
            if (level < 0)
            {
                return 0f;
            }
            return FromTable(bands?.climateWind, ABMod.Settings?.skyWindFactors,
                DefaultSkyWindFactors, level - 1);
        }

        public static float WindFactorForLevel(int level)
        {
            return WindFactorForLevel(null, level);
        }
    }

    // REMOVED 2026-07-28: Patch_WildPlantSpawner_ABBandDensity.
    //
    // It postfixed GetBaseDesiredPlantsCountAt to scale by the band biome's plantDensity
    // and to hard-zero the basement. Both jobs are now done correctly upstream by
    // ABBandBiome, and keeping it would actively cause bugs:
    //
    //  - DOUBLE COUNTING. GetBaseDesiredPlantsCountAt is FERTILITY only (it returns
    //    fertilityGrid.FertilityAt). Vanilla applies biome density separately, in
    //    CalculateDesiredPlants, as `map.BiomeAt(forCell).plantDensity * ...`. Once
    //    BiomeAt is band-aware that multiply is already band-correct, so scaling here too
    //    squared the density factor.
    //  - THE BASEMENT HARD-ZERO blocked the whole point of cavern support: flora would be
    //    seeded at generation and could then never regrow. Plain rock still yields no
    //    plants without it, from two independent directions - rough stone terrain has zero
    //    fertility, and AB_Underground has plantDensity 0.
    //
    // Net: one fewer patch, and the sky band's vegetation now regrows on its own.

    /// <summary>
    /// Per-band outdoor temperature. Only affects cells that are actually outdoors on a
    /// non-surface band; roofed interiors already get correct room temperature from
    /// vanilla, which is the majority of the basement.
    /// </summary>
    // ⚠ NO [HarmonyPatch] ATTRIBUTE, ON PURPOSE: this postfix is owned by
    // ABPatchLifecycle, which applies it only while a banded map with nonzero effective
    // offsets exists (and the master toggle is on) and removes it when none does.
    // HarmonyBoot's attribute scan must not see it or it would be double-applied.
    // Target: GenTemperature.TryGetTemperatureForCell.
    public static class Patch_GenTemperature_ABBandOffset
    {
        private static void Postfix(IntVec3 c, Map map, ref float tempResult, bool __result)
        {
            try
            {
                // Resolve the component ONCE. This postfix ran 719,002 times in a 2,000
                // frame sample, and asking ABBands for Banded and then LevelOf hit the
                // component lookup twice on every one of them.
                if (!__result || map == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int level = bands.LevelOf(c);
                if (level == 0)
                {
                    return; // the surface, which is the overwhelming majority of calls
                }
                Room room = c.GetRoom(map);
                if (room != null && !room.UsesOutdoorTemperature)
                {
                    return; // a real interior; vanilla's room temperature is correct
                }
                tempResult += ABBandEnv.TempOffsetForLevel(bands, level);
            }
            catch (Exception e)
            {
                // Was a bare `catch {}` on a path measured at 719,002 calls per 2,000 frames.
                // A persistent fault here means every non-surface cell silently reports the
                // SURFACE temperature - crops living where they should freeze, no snow line,
                // and not one line in the log to connect it to. ErrorOnce is free on the
                // happy path and turns a permanently invisible failure into a reported one.
                Log.ErrorOnce(ABLog.Tag + " V2: band temperature offset threw: " + e, 118843305);
            }
        }
    }

    /// <summary>
    /// Map-edge semantics.
    ///
    /// RegionMaker sets touchesMapEdge only at z==0 and z==Size.z-1, which on a banded map
    /// are the BASEMENT's bottom row and the SKY's top row. Left alone, raids, caravan
    /// exits and wandering animals would try to enter through the basement floor, and the
    /// surface band would only be enterable from its x edges.
    ///
    /// These patches redirect edge-cell selection to the surface band's own perimeter,
    /// which is what "the edge of the world" means to the player.
    /// </summary>
    public static class ABBandEdges
    {
        public static bool NeedsRedirect(Map map)
        {
            return map != null && ABBands.Banded(map);
        }

        /// <summary>Perimeter cells of the surface band, treated as the map edge - ANY side.
        ///
        /// Forwards to the single implementation in ABBandSafety. This used to be a second
        /// copy of it: same 200-attempt loop, same deterministic perimeter sweep, same
        /// random-side pick spelled as a switch instead of a Rot4. The two lived in different
        /// files behind different helper classes purely because the dir and no-dir vanilla
        /// overloads were band-corrected at different times, and a fix to one would not have
        /// reached the other. A null dir preserves this version's distinguishing behaviour
        /// exactly - the side is re-rolled on every attempt, so a validator that rejects one
        /// whole side cannot trap the search.</summary>
        public static bool TryRandomSurfaceEdgeCell(Map map, Predicate<IntVec3> validator, out IntVec3 result)
        {
            return ABBandSafety.TryRandomSurfaceEdgeCell(map, null, validator, out result);
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomEdgeCell), new Type[] { typeof(Map) })]
    public static class Patch_CellFinder_ABRandomEdgeCell
    {
        private static bool Prefix(Map map, ref IntVec3 __result)
        {
            if (!ABBandEdges.NeedsRedirect(map))
            {
                return true;
            }
            if (ABBandEdges.TryRandomSurfaceEdgeCell(map, null, out IntVec3 c))
            {
                __result = c;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith),
        new Type[] { typeof(Predicate<IntVec3>), typeof(Map), typeof(float), typeof(IntVec3) },
        new ArgumentType[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class Patch_CellFinder_ABTryFindRandomEdgeCellWith
    {
        private static bool Prefix(Predicate<IntVec3> validator, Map map, ref IntVec3 result, ref bool __result)
        {
            if (!ABBandEdges.NeedsRedirect(map))
            {
                return true;
            }
            __result = ABBandEdges.TryRandomSurfaceEdgeCell(map, validator, out result);
            return false;
        }
    }
}
