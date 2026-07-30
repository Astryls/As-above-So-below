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
    /// THE LEDGE BAND IS >= 1 CELL BY CONSTRUCTION (option A's requirement, and it holds
    /// without a special case): a plateau cell requires edgeDist >= 2, and TerraceWidth()
    /// never returns less than 1, so no plateau or wall cell can ever touch a non-mass
    /// cell - there is always at least one ledge cell between the mass interior and the
    /// drop. The renderer leans on this: the rim is what carries the silhouette lip and
    /// the cliff face.
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
            // An INDEPENDENT moisture field, so damp ground is not just "more fertile":
            // crossing it with the fertility ramp gives rich-soil pockets and boggy
            // hollows in different places, which is what makes a plateau read as terrain
            // rather than as a noise gradient.
            Perlin moistNoise = new Perlin(0.06, 2.0, 0.5, 4, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            TerrainDef arable = ArableTerrainFor(map.Biome);
            List<IntVec3> oreCells = new List<IntVec3>();
            List<IntVec3> fogCells = new List<IntVec3>();
            List<IntVec3> plateauCells = new List<IntVec3>();

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
                            // Plateau ground palette (option A). Every terrain here is
                            // natural + FadeRough, so vanilla's own terrain layer blends
                            // all the ground-to-ground boundaries for free; our cap layer
                            // only has to hand-fade ground against the rock field.
                            float n = Noise01(soilNoise, c);
                            float wet = Noise01(moistNoise, c);
                            TerrainDef t;
                            if (n < 0.22f)
                            {
                                // Bare stone shoulders, in the plateau's own rock type.
                                ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                                t = rock.building?.naturalTerrain ?? TerrainDefOf.Gravel;
                            }
                            else if (n < 0.40f)
                            {
                                t = TerrainDefOf.Gravel;
                            }
                            else if (n > 1f - soilFrac)
                            {
                                // The fertile heart: the biome's own arable terrain, going
                                // to RICH soil where the moisture field also peaks.
                                t = wet > 0.80f ? (TerrainDefOf.SoilRich ?? arable) : arable;
                            }
                            else
                            {
                                // Ordinary soil, with MUD in the wettest hollows only
                                // (deliberately rare - mud everywhere reads as damage).
                                t = wet > 0.90f ? (TerrainDefOf.Mud ?? TerrainDefOf.Soil) : TerrainDefOf.Soil;
                            }
                            grid.SetTerrain(c, t ?? TerrainDefOf.Gravel);
                            plateauCells.Add(c);
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
            SeedFlora(map, plateauCells, settings);
        }

        /// <summary>
        /// Starting vegetation for the plateau.
        ///
        /// Without this the sky band generated completely barren: nothing here spawned a
        /// single plant, so the only vegetation was whatever WildPlantSpawner trickled in
        /// over subsequent in-game months. A player climbing to a brand-new summit found
        /// bare soil and gravel.
        ///
        /// The species list comes from ABBandEnv.BiomeOf, which for the sky band resolves
        /// to the SURFACE biome - a plateau above a boreal forest should read boreal. The
        /// filtering that keeps that honest is per-cell and already vanilla's own: a plant
        /// is only placed where terrain fertility clears its fertilityMin, so lowland and
        /// water species simply have nowhere to land on soil and gravel, and open air and
        /// roof surfaces are never candidates because they are not plateau cells at all.
        /// </summary>
        private static void SeedFlora(Map map, List<IntVec3> plateauCells, ABSettings settings)
        {
            if (plateauCells == null || plateauCells.Count == 0)
            {
                return;
            }
            float density = Mathf.Clamp(settings?.skyVegetationDensity ?? 1f, 0f, 2f);
            if (density <= 0f)
            {
                return;
            }
            BiomeDef biome = ABBandEnv.BiomeOf(map, plateauCells[0]);
            // AllWildPlants is already a List here and is only read, so no copy is needed.
            List<ThingDef> plants = biome?.AllWildPlants;
            if (plants == null || plants.Count == 0)
            {
                return;
            }

            // Scaled by the biome's own plantDensity so a desert summit stays sparse and a
            // temperate one comes in thick, then by the player's setting.
            float chanceBase = 0.16f * Mathf.Max(0.15f, biome.plantDensity) * density;
            int placed = 0;
            for (int i = 0; i < plateauCells.Count; i++)
            {
                IntVec3 c = plateauCells[i];
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
                    // Staggered growth so the summit does not look planted all at once.
                    plant.Growth = Rand.Range(0.15f, 0.95f);
                    placed++;
                }
            }
            ABLog.Dev("Sky flora: " + placed + " plants seeded across " + plateauCells.Count
                + " plateau cells (biome " + (biome.defName ?? "?") + ", density "
                + density.ToString("0.0") + ").");
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
                    kind[i] = ed <= TerraceWidth(terraceNoise, c, terraceMax) ? KindLedge : KindWall;
                }
            }
            return kind;
        }

        /// <summary>Width of the walkable rim before solid rock begins.
        ///
        /// Ported verbatim from V1 — the shape matters enormously. HALF of all cells get
        /// width exactly 1 (n &lt; 0.5), and above that it grows on a 1.6 power curve, so
        /// wide terraces are rare and the mass is mostly ROCK.
        ///
        /// The naive `1 + floor(noise * max)` (uniform 1..5, mean ~3) made almost every
        /// cell a ledge, so no rock ever spawned and the sky read as a flat plate — the
        /// run #8 "ledges don't spawn with rock, it's all flat" report.</summary>
        private static int TerraceWidth(Perlin noise, IntVec3 c, int max)
        {
            float n = Noise01(noise, c);
            if (n < 0.5f || max <= 1)
            {
                return 1;
            }
            int w = 1 + Mathf.FloorToInt(Mathf.Pow(Mathf.InverseLerp(0.5f, 1f, n), 1.6f) * max);
            return Mathf.Clamp(w, 1, max);
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
