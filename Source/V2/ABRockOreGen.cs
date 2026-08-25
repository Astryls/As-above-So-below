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
        /// <summary>The ore table, built once. It was rebuilt by scanning the WHOLE
        /// ThingDef database on every call - and this is called once per basement band plus
        /// once for the sky, so a seven-level map walked several thousand defs four times
        /// over inside the generation window. Defs do not change after startup.</summary>
        private static List<ThingDef> oreDefs;

        private static List<ThingDef> OreDefs()
        {
            if (oreDefs != null)
            {
                return oreDefs;
            }
            oreDefs = new List<ThingDef>();
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef d = defs[i];
                if (d.building != null && d.building.isResourceRock
                    && d.building.mineableScatterCommonality > 0f)
                {
                    oreDefs.Add(d);
                }
            }
            return oreDefs;
        }

        /// <summary>
        /// Scatter into a whole RECT, without materialising it.
        ///
        /// The basement fill used to call the list overload as
        /// <c>ScatterOres(map, rect.Cells.ToList(), density)</c> - which built a
        /// <c>List&lt;IntVec3&gt;</c> of every cell in the band (36,100 entries, ~430 KB, plus
        /// the yield-return enumerator behind <c>rect.Cells</c>) purely so that two things
        /// could be read off it: <c>.Count</c>, and about twenty <c>.RandomElement()</c>
        /// picks. Both are answerable from the rect directly, in constant space. Repeated
        /// once per basement band, inside the generation window.
        /// </summary>
        internal static void ScatterOres(Map map, CellRect area, float lumpsPer10kCells)
        {
            if (area.Area <= 0)
            {
                return;
            }
            Scatter(map, null, area, area.Area, lumpsPer10kCells);
        }

        /// <summary>Scatter into an explicit, genuinely sparse candidate set - the sky
        /// generator's mountain wall cells, which are a small subset of their band.</summary>
        internal static void ScatterOres(Map map, List<IntVec3> candidates, float lumpsPer10kCells)
        {
            if (candidates != null && candidates.Count == 0)
            {
                return;
            }
            Scatter(map, candidates, default(CellRect), candidates?.Count ?? map.Area, lumpsPer10kCells);
        }

        private static void Scatter(Map map, List<IntVec3> candidates, CellRect area,
            int cellBase, float lumpsPer10kCells)
        {
            try
            {
                List<ThingDef> ores = OreDefs();
                if (ores.Count == 0)
                {
                    return;
                }
                int lumps = Mathf.Max(1, Mathf.RoundToInt(cellBase / 10000f * lumpsPer10kCells));
                for (int i = 0; i < lumps; i++)
                {
                    ThingDef ore = ores.RandomElementByWeight(d => d.building.mineableScatterCommonality);
                    IntVec3 center;
                    if (candidates != null)
                    {
                        center = candidates.RandomElement();
                    }
                    else if (area.Area > 0)
                    {
                        center = new IntVec3(Rand.RangeInclusive(area.minX, area.maxX), 0,
                            Rand.RangeInclusive(area.minZ, area.maxZ));
                    }
                    else
                    {
                        center = CellFinder.RandomCell(map);
                    }
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
