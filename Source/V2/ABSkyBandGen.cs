using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 sky-band terrain: a real mountain, not a flat projection.
    ///
    /// Ports V1's GenStep_ABSkyTerrain classification onto a band rect. The mass is
    /// projected from the band directly below (same Map, so one subtraction instead of a
    /// cross-map lookup plus a sync mirror), then classified:
    ///
    ///   solid      = mineable edifice below, or thick natural roof below
    ///   edgeDist   = 8-way BFS distance out of the mass
    ///   meadow field (one low-frequency Perlin) decides rock vs open per cell:
    ///     open  -> KindPlateau (walkable mountain top), except the outermost ring which
    ///              stays KindLedge so the rock face below still renders
    ///     rock  -> KindLedge within a wandering terrace width, KindWall behind it
    ///
    /// Terrain then follows V1 exactly: ledge = mountain-top lip, wall = mineable rock
    /// under thick roof (deep interior fogged like an unexplored vanilla mountain),
    /// plateau = stone/gravel/soil by noise. Outside the mass: constructed roof below ->
    /// rooftop, natural roof below -> mountain cap, nothing below -> open air (which is
    /// what SectionLayer_ABBelowV2 renders the level below through).
    ///
    /// NOT PORTED from V1: outcrop mounts and hidden-valley breaching. Both are additive
    /// polish on top of this classification and can be layered in later.
    /// </summary>
    internal static class ABSkyBandGen
    {
        private const byte KindOutside = 0;
        private const byte KindLedge = 1;
        private const byte KindWall = 2;
        private const byte KindPlateau = 3;

        private const float MeadowCutoffDefault = 0.62f;

        internal static void Generate(Map map, ABBandMap bands, int band,
            List<ThingDef> rocks, List<Perlin> noises)
        {
            CellRect rect = bands.RectOfBand(band);
            int w = rect.Width;
            int h = rect.Height;
            int count = w * h;
            int slot = bands.Slot;
            TerrainGrid grid = map.terrainGrid;
            RoofGrid roofs = map.roofGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;

            // ---- pass 1: the solid mass, projected from the band below -----
            bool[] solid = new bool[count];
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    IntVec3 below = new IntVec3(rect.minX + x, 0, rect.minZ + z - slot);
                    if (!below.InBounds(map))
                    {
                        continue;
                    }
                    Building edifice = map.edificeGrid[below];
                    RoofDef roof = roofs.RoofAt(below);
                    solid[z * w + x] = (edifice != null && edifice.def.mineable)
                        || (roof != null && roof.isNatural && roof.isThickRoof);
                }
            }

            byte[] kind = Classify(solid, w, h);

            // ---- pass 2: terrain, walls, roofs ------------------------------
            ABSettings settings = ABMod.Settings;
            float soilFrac = Mathf.Clamp(settings != null ? settings.peakSoilFraction : 0.15f, 0f, 0.5f);
            Perlin soilNoise = new Perlin(0.05, 2.0, 0.5, 5, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            TerrainDef arable = ArableTerrainFor(map.Biome);
            List<IntVec3> oreCells = new List<IntVec3>();
            List<IntVec3> fogCells = new List<IntVec3>();

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = z * w + x;
                    IntVec3 c = new IntVec3(rect.minX + x, 0, rect.minZ + z);
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    roofs.SetRoof(c, null);

                    switch (kind[idx])
                    {
                        case KindLedge:
                            grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                            continue;

                        case KindWall:
                        {
                            ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                            grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                            GenSpawn.Spawn(rock, c, map);
                            roofs.SetRoof(c, RoofDefOf.RoofRockThick);
                            oreCells.Add(c);
                            // Only the DEEP interior fogs. The outer wall rows stay
                            // visible so the mountain reads as a mountain from above -
                            // fogging the faces lets the fog's soft edge swallow the
                            // ledge entirely (V1 playtest regression).
                            if (AllWithinRadius(kind, w, h, x, z, 2, KindWall))
                            {
                                fogCells.Add(c);
                            }
                            continue;
                        }

                        case KindPlateau:
                        {
                            float n = Noise01(soilNoise, c);
                            TerrainDef t;
                            if (n > 1f - soilFrac)
                            {
                                t = arable;
                            }
                            else if (n < 0.22f)
                            {
                                ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                                t = rock.building?.naturalTerrain ?? TerrainDefOf.Gravel;
                            }
                            else
                            {
                                t = TerrainDefOf.Gravel;
                            }
                            grid.SetTerrain(c, t);
                            continue;
                        }
                    }

                    // Outside the mass: what is directly below decides.
                    IntVec3 below = new IntVec3(c.x, 0, c.z - slot);
                    RoofDef roofBelow = below.InBounds(map) ? roofs.RoofAt(below) : null;
                    if (roofBelow != null && !roofBelow.isNatural)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                    }
                    else if (roofBelow != null && roofBelow.isNatural)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                    }
                    else
                    {
                        grid.SetTerrain(c, air);
                    }
                }
            }

            for (int i = 0; i < fogCells.Count; i++)
            {
                IntVec3 c = fogCells[i];
                map.fogGrid.Refog(new CellRect(c.x, c.z, 1, 1));
            }
            if (oreCells.Count > 0)
            {
                ABOreGen.ScatterOres(map, oreCells,
                    Mathf.Clamp(settings?.basementOreDensity ?? 6f, 0f, 12f) * 0.5f);
            }
        }

        /// <summary>Edge-distance BFS then meadow/terrace classification. Band-local
        /// indices throughout: the band is a contiguous rect, so a local w*h grid keeps
        /// this independent of map size.</summary>
        private static byte[] Classify(bool[] solid, int w, int h)
        {
            int count = w * h;
            byte[] kind = new byte[count];
            int[] edgeDist = new int[count];
            Queue<int> queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                if (solid[i])
                {
                    edgeDist[i] = int.MaxValue;
                }
                else
                {
                    edgeDist[i] = 0;
                    queue.Enqueue(i);
                }
            }
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % w;
                int cz = cur / w;
                int d = edgeDist[cur] + 1;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                        {
                            continue;
                        }
                        int nIdx = nz * w + nx;
                        if (edgeDist[nIdx] > d)
                        {
                            edgeDist[nIdx] = d;
                            queue.Enqueue(nIdx);
                        }
                    }
                }
            }

            ABSettings gs = ABMod.Settings;
            bool naturalistic = gs == null || gs.naturalPeaks;
            if (!naturalistic)
            {
                for (int i = 0; i < count; i++)
                {
                    if (solid[i])
                    {
                        kind[i] = edgeDist[i] <= 1 ? KindLedge : KindWall;
                    }
                }
                return kind;
            }

            float cutoff = Mathf.Clamp(gs?.peakMeadowCutoff ?? MeadowCutoffDefault, 0.45f, 0.75f);
            float meadowScale = Mathf.Clamp(gs?.peakMeadowScale ?? 0.024f, 0.012f, 0.048f);
            int terraceMax = Mathf.Clamp(gs?.peakTerraceMax ?? 4, 1, 6);
            Perlin meadowNoise = new Perlin(meadowScale, 2.0, 0.5, 5, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            Perlin terraceNoise = new Perlin(0.035, 2.0, 0.5, 4, Rand.Range(0, int.MaxValue), QualityMode.Medium);

            for (int i = 0; i < count; i++)
            {
                if (!solid[i])
                {
                    continue;
                }
                int x = i % w;
                int z = i / w;
                IntVec3 c = new IntVec3(x, 0, z);
                int ed = edgeDist[i];
                if (Noise01(meadowNoise, c) > cutoff)
                {
                    // Open zone, but ALWAYS one cell back from the mass edge: the
                    // outermost ring stays a lip, or the rock face below stops
                    // rendering (V1 run #43 regression).
                    kind[i] = ed <= 1 ? KindLedge : KindPlateau;
                }
                else
                {
                    int terrace = 1 + Mathf.FloorToInt(Noise01(terraceNoise, c) * terraceMax);
                    kind[i] = ed <= Mathf.Clamp(terrace, 1, terraceMax) ? KindLedge : KindWall;
                }
            }
            return kind;
        }

        private static bool AllWithinRadius(byte[] kind, int w, int h, int x, int z, int radius, byte want)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = x + dx;
                    int nz = z + dz;
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    {
                        return false;
                    }
                    if (kind[nz * w + nx] != want)
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static float Noise01(Perlin noise, IntVec3 c)
        {
            return (float)(noise.GetValue(c.x, 0.0, c.z) + 1.0) * 0.5f;
        }

        /// <summary>Arable patches use the map biome's own fertile terrain, so modded
        /// biomes get their native soil instead of a hardcoded vanilla Soil.</summary>
        private static TerrainDef ArableTerrainFor(BiomeDef biome)
        {
            if (biome?.terrainsByFertility != null)
            {
                for (int i = 0; i < biome.terrainsByFertility.Count; i++)
                {
                    TerrainDef t = biome.terrainsByFertility[i].terrain;
                    if (t != null && t.fertility >= 0.7f)
                    {
                        return t;
                    }
                }
            }
            return TerrainDefOf.Soil;
        }
    }
}
