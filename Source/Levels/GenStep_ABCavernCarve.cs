using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Turns a freshly filled solid-rock basement into a Biomes! Caverns cave
    /// system: one connected worm-carved tunnel network with chambers, floored
    /// with the cavern biome's own terrain (patch makers + fertility bands, so
    /// mycelial soil, magma ash and crystal floors come out authentic), seeded
    /// with the biome's cave flora and a small starting fauna population, plus
    /// Biomes! Caverns' stalagmite/crystal scatterers. The pocket tile's
    /// PrimaryBiome is swapped to the cavern biome and is deep-scribed by
    /// vanilla, so plant regrowth, wildlife, ambience and biome extensions all
    /// follow it for the life of the save.
    ///
    /// Runs directly after AB_SolidRock and only when Biomes! Caverns is
    /// loaded and the setting is on; otherwise the basement stays vanilla
    /// solid rock. Everything is wrapped by the LevelGen kill switch.
    /// </summary>
    public class GenStep_ABCavernCarve : GenStep
    {
        public override int SeedPart => 762195843;

        /// <summary>Solid border kept around the map edge so the network never
        /// touches map bounds (no edge walk-ins, mirrors the sealed-box feel).</summary>
        private const int Margin = 7;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.cavernBasements || !BiomesCavernsCompat.Active)
            {
                return;
            }
            // Def wiring restricts this step to AB_Basement, but a modder can
            // reuse the generator: only ever carve a below-ground level.
            LevelMapGen.Context ctx = LevelMapGen.CurrentContext;
            if (ctx != null && ctx.levelToGenerate >= 0)
            {
                return;
            }
            BiomeDef biome = BiomesCavernsCompat.Resolve(settings.cavernBiome);
            if (biome == null)
            {
                return;
            }
            try
            {
                Carve(map, biome, Mathf.Clamp(settings.cavernOpenness, 0.1f, 0.6f));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "cavern basement carve");
            }
        }

        private static void Carve(Map map, BiomeDef biome, float openness)
        {
            // 1. The biome swap. The pocket tile is created by MapGenerator with
            // our AB_Underground default and deep-scribed with the map, so this
            // sticks across save/load and every biome-scoped system follows.
            if (map.pocketTileInfo == null)
            {
                ABLog.Dev("Cavern carve: no pocket tile, basement stays solid rock.");
                return;
            }
            map.pocketTileInfo.PrimaryBiome = biome;
            float chamberFreq = Mathf.Clamp(ABMod.Settings?.cavernChamberFreq ?? 0.02f, 0.01f, 0.05f);
            ABLog.Dev("Cavern basement: " + biome.defName + ", openness " + openness.ToString("0.00")
                + ", chambers " + chamberFreq.ToString("0.000") + ".");

            // 2. Worm-carve one connected network. Every worm after the first
            // starts on an already carved cell, so the whole system links up.
            CellRect inner = CellRect.WholeMap(map).ContractedBy(Margin);
            CellIndices indices = map.cellIndices;
            bool[] carved = new bool[indices.NumGridCells];
            List<IntVec3> carvedList = new List<IntVec3>();

            void CarveDisc(IntVec3 center, float radius)
            {
                int n = GenRadial.NumCellsInRadius(radius);
                for (int i = 0; i < n; i++)
                {
                    IntVec3 c = center + GenRadial.RadialPattern[i];
                    if (!inner.Contains(c))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(c);
                    if (!carved[idx])
                    {
                        carved[idx] = true;
                        carvedList.Add(c);
                    }
                }
            }

            int worms = Mathf.Max(3, Mathf.RoundToInt(inner.Area / 10000f * (4f + 14f * openness)));
            for (int w = 0; w < worms; w++)
            {
                IntVec3 start = w == 0 || carvedList.Count == 0
                    ? inner.RandomCell
                    : carvedList.RandomElement();
                Vector3 pos = start.ToVector3Shifted();
                float angle = Rand.Range(0f, 360f);
                int length = Rand.RangeInclusive(50, 130);
                for (int step = 0; step < length; step++)
                {
                    angle += Rand.Range(-22f, 22f);
                    Vector3 next = pos + Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                    IntVec3 c = next.ToIntVec3();
                    if (!inner.Contains(c))
                    {
                        // Bounce off the border band instead of leaving it.
                        angle += 160f + Rand.Range(0f, 40f);
                        continue;
                    }
                    pos = next;
                    CarveDisc(c, Rand.Value < 0.9f ? 1.4f : 2.1f);
                    if (Rand.Value < chamberFreq)
                    {
                        // Occasional chamber (frequency from settings; higher
                        // also gives BC's radius-5 scatterer validators more
                        // room, quieting the known-benign placement warning).
                        // Radius stays collapse-safe-ish; the pillar pass
                        // below is the hard guarantee.
                        CarveDisc(c, Rand.Range(3f, 4.8f));
                    }
                }
            }

            // 3. Open the carved cells: drop the rock fill (ore lumps in the
            // path included; the walls keep plenty) and floor them with the
            // biome's own terrain resolution - patch makers first (lakes, magma,
            // crystal fields), then the fertility bands.
            Perlin fertNoise = new Perlin(0.021, 2.0, 0.5, 6, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            TerrainGrid grid = map.terrainGrid;
            List<TerrainPatchMaker> patchMakers = biome.terrainPatchMakers;
            for (int i = 0; i < carvedList.Count; i++)
            {
                IntVec3 c = carvedList[i];
                Building edifice = c.GetEdifice(map);
                if (edifice != null)
                {
                    BuildingProperties bp = edifice.def.building;
                    if (bp == null || (!bp.isNaturalRock && !bp.isResourceRock))
                    {
                        continue; // never touch stairs or anything non-natural
                    }
                    edifice.Destroy(DestroyMode.Vanish);
                }
                float fert = (float)(fertNoise.GetValue(c.x, 0.0, c.z) + 1.0) * 0.6f;
                TerrainDef terrain = null;
                if (patchMakers != null)
                {
                    for (int j = 0; j < patchMakers.Count && terrain == null; j++)
                    {
                        terrain = patchMakers[j].TerrainAt(c, map, fert);
                    }
                }
                if (terrain == null)
                {
                    terrain = TerrainThreshold.TerrainAtValue(biome.terrainsByFertility, fert);
                }
                if (terrain != null)
                {
                    grid.SetTerrain(c, terrain);
                }
                // else: keep the rough stone the solid-rock fill already laid.
            }

            // 4. Thick rock roofs stay everywhere (underground), so any carved
            // cell out of roof-holder range gets a natural pillar planted at
            // it, exactly where support is missing. Scan order means later
            // checks already see earlier pillars.
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile).ToListSafe();
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }
            List<Perlin> rockNoises = ABRockGen.MakeNoises(rocks.Count);
            int pillars = 0;
            for (int i = 0; i < carvedList.Count; i++)
            {
                IntVec3 c = carvedList[i];
                if (RoofCollapseUtility.WithinRangeOfRoofHolder(c, map))
                {
                    continue;
                }
                ThingDef rock = rocks[ABRockGen.PickIndex(rockNoises, c)];
                GenSpawn.Spawn(rock, c, map, WipeMode.Vanish);
                grid.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                pillars++;
            }
            if (pillars > 0)
            {
                ABLog.Dev("Cavern carve: " + pillars + " support pillars added.");
            }

            // 5. Their own dressing: stalagmites everywhere, crystals in the
            // crystal biome. Both are self-contained scatterers with their own
            // placement validators.
            // Formation density (settings): run BC's scatterer 0..2 times,
            // fractional part as a chance for one more pass.
            float formations = Mathf.Clamp(ABMod.Settings?.cavernFormations ?? 1f, 0f, 2f);
            int formationRuns = Mathf.FloorToInt(formations);
            if (Rand.Chance(formations - formationRuns))
            {
                formationRuns++;
            }
            for (int fi = 0; fi < formationRuns; fi++)
            {
                BiomesCavernsCompat.RunForeignGenStep("BMT_ScatterStalagmiteGenerator", map);
            }
            if (biome.defName == "BMT_CrystalCaverns")
            {
                BiomesCavernsCompat.RunForeignGenStep("BMT_CrystalsGenerator", map);
            }

            // 6. Starting flora from the biome's own cave plant list, weighted
            // by commonality, gated by terrain fertility. Regrowth afterwards
            // is vanilla WildPlantSpawner business (the biome swap feeds it).
            List<ThingDef> plants = biome.AllWildPlants.ToListSafe();
            if (plants.Count > 0)
            {
                float chanceBase = 0.08f * Mathf.Max(0.5f, biome.plantDensity);
                for (int i = 0; i < carvedList.Count; i++)
                {
                    IntVec3 c = carvedList[i];
                    TerrainDef t = c.GetTerrain(map);
                    if (t.fertility <= 0.01f || !c.Standable(map) || c.GetPlant(map) != null
                        || c.GetEdifice(map) != null || !Rand.Chance(chanceBase * t.fertility))
                    {
                        continue;
                    }
                    ThingDef plantDef = plants.RandomElementByWeight(p => biome.CommonalityOfPlant(p));
                    if (plantDef?.plant == null || plantDef.plant.fertilityMin > t.fertility)
                    {
                        continue;
                    }
                    Plant plant = GenSpawn.Spawn(plantDef, c, map, WipeMode.Vanish) as Plant;
                    if (plant != null)
                    {
                        plant.Growth = Rand.Range(0.2f, 0.95f);
                    }
                }
            }

            // 7. A small starting fauna population. Vanilla's ambient spawner
            // wants map-edge entry cells that a sealed basement cannot offer,
            // so the level gets its residents up front.
            List<PawnKindDef> animals = biome.AllWildAnimals.ToListSafe();
            if (animals.Count > 0)
            {
                int count = Mathf.Clamp(Mathf.RoundToInt(carvedList.Count / 10000f
                    * Mathf.Max(1f, biome.animalDensity) * 1.5f), 2, 10);
                for (int i = 0; i < count; i++)
                {
                    IntVec3 c = carvedList.RandomElement();
                    if (!c.Standable(map))
                    {
                        continue;
                    }
                    PawnKindDef kind = animals.RandomElementByWeight(k => biome.CommonalityOfAnimal(k));
                    if (kind == null)
                    {
                        continue;
                    }
                    Pawn animal = PawnGenerator.GeneratePawn(kind);
                    GenSpawn.Spawn(animal, c, map, WipeMode.Vanish);
                }
            }

            // Fog stays map-wide from the solid fill: the caves are sealed and
            // dark until someone breaks into them, like any vanilla cavern.
        }
    }

    internal static class ABListExtensions
    {
        /// <summary>Defensive copy that tolerates a null source.</summary>
        internal static List<T> ToListSafe<T>(this IEnumerable<T> source)
        {
            List<T> list = new List<T>();
            if (source == null)
            {
                return list;
            }
            foreach (T item in source)
            {
                list.Add(item);
            }
            return list;
        }
    }
}
