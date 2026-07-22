using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Fills the basement with solid mineable rock matching the surface tile's
    /// natural rock types, blended with per-map Perlin noise. The engine already
    /// roofed every cell with thick rock (MapGeneratorDef.isUnderground), we add
    /// the walls, the rough terrain under them, and full fog.
    /// </summary>
    public class GenStep_ABSolidRock : GenStep
    {
        public override int SeedPart => 762195841;

        public override void Generate(Map map, GenStepParams parms)
        {
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile).ToList();
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }

            List<Perlin> noises = ABRockGen.MakeNoises(rocks.Count);

            TerrainGrid terrainGrid = map.terrainGrid;
            foreach (IntVec3 c in map.AllCells)
            {
                ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                TerrainDef terrain = rock.building?.naturalTerrain ?? TerrainDefOf.Gravel;
                terrainGrid.SetTerrain(c, terrain);
                GenSpawn.Spawn(rock, c, map);
            }

            map.fogGrid.Refog(CellRect.WholeMap(map));

            // Ore veins throughout the fill so the basement is worth mining.
            // Density from settings (applies to newly generated basements).
            ABOreGen.ScatterOres(map, null,
                Mathf.Clamp(ABMod.Settings?.basementOreDensity ?? 6f, 0f, 12f));
        }
    }

    /// <summary>Scatters mineable ore lumps into natural rock, weighted by each
    /// ore's vanilla scatter commonality so modded ores participate
    /// automatically. Only ever replaces natural rock edifices: stairs,
    /// landings, and already-placed lumps are untouched. Null candidates means
    /// the whole map (the basement fill); the sky pass hands in its mountain
    /// wall cells.</summary>
    internal static class ABOreGen
    {
        internal static void ScatterOres(Map map, List<IntVec3> candidates, float lumpsPer10kCells)
        {
            try
            {
                if (candidates != null && candidates.Count == 0)
                {
                    return;
                }
                List<ThingDef> ores = new List<ThingDef>();
                List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    ThingDef d = defs[i];
                    if (d.building != null && d.building.isResourceRock
                        && d.building.mineableScatterCommonality > 0f)
                    {
                        ores.Add(d);
                    }
                }
                if (ores.Count == 0)
                {
                    return;
                }
                int cellBase = candidates?.Count ?? map.Area;
                int lumps = Mathf.Max(1, Mathf.RoundToInt(cellBase / 10000f * lumpsPer10kCells));
                for (int i = 0; i < lumps; i++)
                {
                    ThingDef ore = ores.RandomElementByWeight(d => d.building.mineableScatterCommonality);
                    IntVec3 center = candidates != null ? candidates.RandomElement() : CellFinder.RandomCell(map);
                    int size = ore.building.mineableScatterLumpSizeRange.RandomInRange;
                    List<IntVec3> lump = GridShapeMaker.IrregularLump(center, map, size);
                    for (int j = 0; j < lump.Count; j++)
                    {
                        IntVec3 c = lump[j];
                        Building edifice = c.GetEdifice(map);
                        if (edifice != null && edifice.def.building != null
                            && edifice.def.building.isNaturalRock && !edifice.def.building.isResourceRock)
                        {
                            GenSpawn.Spawn(ore, c, map, WipeMode.Vanish);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "ore scatter");
            }
        }
    }

    /// <summary>Shared rock-type blending used by the basement fill and the sky
    /// level's mountain stone so both match the surface geology.</summary>
    internal static class ABRockGen
    {
        internal static List<Perlin> MakeNoises(int count)
        {
            List<Perlin> noises = new List<Perlin>(count);
            for (int i = 0; i < count; i++)
            {
                noises.Add(new Perlin(0.005, 2.0, 0.5, 6, Rand.Range(0, int.MaxValue), QualityMode.Medium));
            }
            return noises;
        }

        internal static int PickIndex(List<Perlin> noises, IntVec3 c)
        {
            int best = 0;
            double bestVal = double.MinValue;
            for (int i = 0; i < noises.Count; i++)
            {
                double v = noises[i].GetValue(c.x, 0.0, c.z);
                if (v > bestVal)
                {
                    bestVal = v;
                    best = i;
                }
            }
            return best;
        }
    }
}
