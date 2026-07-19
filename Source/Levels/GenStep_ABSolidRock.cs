using System.Collections.Generic;
using System.Linq;
using RimWorld;
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
