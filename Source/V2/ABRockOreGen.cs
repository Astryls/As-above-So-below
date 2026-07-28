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
    /// Rock and ore placement shared by V2's band generation.
    ///
    /// RESCUED FROM V1. These two helpers were the only things V2 still needed from the
    /// 42.6k-line V1 tree - they lived inside Levels/GenStep_ABSolidRock.cs next to a GenStep
    /// that V2 does not use, so deleting V1 wholesale would have taken them with it. Moved
    /// here verbatim (visibility widened from internal to public is unnecessary - same
    /// assembly - so they stay internal).
    ///
    /// Callers: ABBandedGeneration (basement fill) and ABSkyBandGen (mountain stone), both of
    /// which want the surface tile's real geology rather than a single rock type.
    /// </summary>
    /// <remarks>
    /// Ore lumps are weighted by each ore's vanilla scatter commonality so modded ores
    /// participate automatically. Only ever replaces natural rock edifices: stairs, landings
    /// and already-placed lumps are untouched. Null candidates means the whole map (the
    /// basement fill); the sky pass hands in its mountain wall cells.
    /// </remarks>
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
