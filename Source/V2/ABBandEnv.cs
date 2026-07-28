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
        /// Underground is stable and cool; the sky is thinner and colder.</summary>
        private const float BasementTempOffset = -6f;

        private const float SkyTempOffset = -4f;

        private static BiomeDef undergroundBiome;

        private static BiomeDef skyBiome;

        public static BiomeDef BiomeOf(Map map, IntVec3 cell)
        {
            if (map == null)
            {
                return null;
            }
            int level = ABBands.LevelOf(map, cell);
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
                ABBandMap bands = ABBands.CompOf(map);
                if (bands?.basementBiome != null)
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

        public static float TempOffsetForLevel(int level)
        {
            if (level == 0)
            {
                return 0f;
            }
            return level < 0 ? BasementTempOffset : SkyTempOffset;
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
    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.TryGetTemperatureForCell))]
    public static class Patch_GenTemperature_ABBandOffset
    {
        private static void Postfix(IntVec3 c, Map map, ref float tempResult, bool __result)
        {
            try
            {
                if (!__result || map == null || !ABBands.Banded(map))
                {
                    return;
                }
                int level = ABBands.LevelOf(map, c);
                if (level == 0)
                {
                    return;
                }
                Room room = c.GetRoom(map);
                if (room != null && !room.UsesOutdoorTemperature)
                {
                    return; // a real interior; vanilla's room temperature is correct
                }
                tempResult += ABBandEnv.TempOffsetForLevel(level);
            }
            catch
            {
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

        /// <summary>Perimeter cells of the surface band, treated as the map edge.</summary>
        public static bool TryRandomSurfaceEdgeCell(Map map, Predicate<IntVec3> validator, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            CellRect surface = ABBands.RectOfBand(map, ABBands.SurfaceBand(map));
            for (int attempt = 0; attempt < 200; attempt++)
            {
                IntVec3 c = RandomEdgeOf(surface);
                if (!c.InBounds(map))
                {
                    continue;
                }
                if (validator == null || validator(c))
                {
                    result = c;
                    return true;
                }
            }
            // Deterministic sweep fallback so callers never silently fail.
            foreach (IntVec3 c in surface.EdgeCells)
            {
                if (c.InBounds(map) && (validator == null || validator(c)))
                {
                    result = c;
                    return true;
                }
            }
            return false;
        }

        private static IntVec3 RandomEdgeOf(CellRect r)
        {
            switch (Rand.RangeInclusive(0, 3))
            {
                case 0: return new IntVec3(r.minX, 0, Rand.RangeInclusive(r.minZ, r.maxZ));
                case 1: return new IntVec3(r.maxX, 0, Rand.RangeInclusive(r.minZ, r.maxZ));
                case 2: return new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, r.minZ);
                default: return new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, r.maxZ);
            }
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
