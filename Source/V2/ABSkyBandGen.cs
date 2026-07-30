using System;
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

            byte[] kind = Classify(solid, w, h, out int[] edgeDist);
            // ONE central fog hole, decided up front over the whole band rather than
            // per cell, so the mass shows a single soft fog border instead of a rash of
            // little ones.
            bool[] fogMask = ComputeFogMask(kind, w, h);

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
                            if (fogMask[idx])
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
            // ---- pass 3: the alpine plateau (option D) ----------------------
            // Order is deliberate: tarns first so a shoreline exists before anything is
            // scattered near it, then rim scree, then flora last so it plants against
            // the finished terrain (and never into open water, which has no fertility).
            CarveTarns(map, rect, w, h, kind, edgeDist, settings);
            ScatterRimScree(map, rect, w, h, kind, edgeDist, rocks, noises);
            SeedFlora(map, plateauCells, settings, edgeDist, rect, w);
        }

        /// <summary>No tarn water within this many cells of a drop. The user's rule -
        /// "lakes should never spawn on upper levels near the edge" - and a sound one: a
        /// lake lipping over a cliff would need the whole waterfall system (option E) to
        /// make any sense, and looks broken without it.</summary>
        private const int MinTarnEdgeDist = 6;

        /// <summary>Roughly one tarn per this many eligible interior cells.</summary>
        private const int TarnCellsPerSeed = 1100;

        /// <summary>
        /// Tarns: small alpine lakes in the plateau interior, shallow water with a mud
        /// fringe (the moisture field already puts rich soil and mud nearby, so a tarn
        /// reads as the reason that ground is damp).
        ///
        /// SHALLOW ONLY, deliberately: deep water is impassable, and an impassable pocket
        /// on a plateau reachable solely through our wormhole stairs is exactly the kind
        /// of thing that turns into a stuck-pawn report. Shallow water is wadeable, so the
        /// plateau stays fully traversable.
        ///
        /// The edge rule is enforced PER CELL, not on the seed: every water cell must
        /// itself be MinTarnEdgeDist from a drop, so the blob clips itself against the rim
        /// instead of relying on radius arithmetic being right.
        /// </summary>
        private static void CarveTarns(Map map, CellRect rect, int w, int h, byte[] kind,
            int[] edgeDist, ABSettings settings)
        {
            if (settings != null && !settings.naturalPeaks)
            {
                return;
            }
            TerrainDef water = TerrainDefOf.WaterShallow;
            if (water == null)
            {
                return;
            }
            TerrainDef fringe = TerrainDefOf.Mud;
            List<int> seeds = new List<int>();
            for (int i = 0; i < kind.Length; i++)
            {
                if (kind[i] == KindPlateau && edgeDist[i] >= MinTarnEdgeDist + 2)
                {
                    seeds.Add(i);
                }
            }
            if (seeds.Count == 0)
            {
                return;
            }
            int tarns = Mathf.Clamp(seeds.Count / TarnCellsPerSeed, seeds.Count >= 400 ? 1 : 0, 4);
            if (tarns <= 0)
            {
                return;
            }
            TerrainGrid grid = map.terrainGrid;
            int carved = 0;
            for (int t = 0; t < tarns; t++)
            {
                int seed = seeds[Rand.Range(0, seeds.Count)];
                int sx = seed % w;
                int sz = seed / w;
                float radius = Rand.Range(2.2f, 4.2f);
                int reach = Mathf.CeilToInt(radius) + 2;
                for (int dz = -reach; dz <= reach; dz++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int lx = sx + dx;
                        int lz = sz + dz;
                        if (lx < 0 || lz < 0 || lx >= w || lz >= h)
                        {
                            continue;
                        }
                        int idx = lz * w + lx;
                        if (kind[idx] != KindPlateau || edgeDist[idx] < MinTarnEdgeDist)
                        {
                            continue;
                        }
                        IntVec3 c = new IntVec3(rect.minX + lx, 0, rect.minZ + lz);
                        if (!c.InBounds(map))
                        {
                            continue;
                        }
                        // Deterministic per-cell jitter: an irregular shore instead of a
                        // disc, stable across regeneration.
                        float d = Mathf.Sqrt(dx * dx + dz * dz)
                            + (Rand.ValueSeeded(idx * 977 + 13) - 0.5f) * 0.9f;
                        if (d <= radius)
                        {
                            grid.SetTerrain(c, water);
                            carved++;
                        }
                        else if (fringe != null && d <= radius + 1.1f
                            && grid.TerrainAt(c) != water)
                        {
                            grid.SetTerrain(c, fringe);
                        }
                    }
                }
            }
            if (carved > 0)
            {
                ABLog.Dev("Sky tarns: " + tarns + " attempted, " + carved
                    + " shallow-water cells (all >= " + MinTarnEdgeDist + " cells from any drop).");
            }
        }

        private const float RimChunkChance = 0.014f;

        private const int MaxRimChunks = 60;

        /// <summary>
        /// Rim scree: loose stone chunks along the plateau shoulder, in the local rock
        /// type, so the edge reads as weathered rather than cut.
        ///
        /// These are real haulable chunks, which is a deliberate trade the user chose:
        /// they look right and give free stone, but every chunk is a Thing that a hauler
        /// will eventually want to carry down the stairs. Hence the hard cap and the low
        /// per-cell chance - this is scenery with a side of resource, not a quarry.
        /// Restricted to the rim shoulder (edgeDist 1-4) so the plateau interior stays
        /// clean and the chunks read as having tumbled from the edge.
        /// </summary>
        private static void ScatterRimScree(Map map, CellRect rect, int w, int h, byte[] kind,
            int[] edgeDist, List<ThingDef> rocks, List<Perlin> noises)
        {
            if (rocks == null || rocks.Count == 0)
            {
                return;
            }
            int placed = 0;
            for (int lz = 0; lz < h && placed < MaxRimChunks; lz++)
            {
                for (int lx = 0; lx < w && placed < MaxRimChunks; lx++)
                {
                    int idx = lz * w + lx;
                    byte k = kind[idx];
                    if (k != KindLedge && k != KindPlateau)
                    {
                        continue;
                    }
                    int ed = edgeDist[idx];
                    if (ed < 1 || ed > 4 || !Rand.Chance(RimChunkChance))
                    {
                        continue;
                    }
                    IntVec3 c = new IntVec3(rect.minX + lx, 0, rect.minZ + lz);
                    if (!c.InBounds(map) || !c.Standable(map) || c.GetEdifice(map) != null)
                    {
                        continue;
                    }
                    TerrainDef t = c.GetTerrain(map);
                    if (t == null || t.IsWater)
                    {
                        continue;
                    }
                    ThingDef chunk = ChunkFor(rocks[ABRockGen.PickIndex(noises, c)]);
                    if (chunk == null)
                    {
                        continue;
                    }
                    if (GenSpawn.Spawn(chunk, c, map, WipeMode.Vanish) != null)
                    {
                        placed++;
                    }
                }
            }
            if (placed > 0)
            {
                ABLog.Dev("Sky rim scree: " + placed + " stone chunks along the plateau shoulder.");
            }
        }

        /// <summary>The chunk def for a rock type, by the "Chunk" + defName convention
        /// every stone in the game (and every stone-adding mod) follows. Deliberately NOT
        /// falling back to `building.mineableThing` - for some rocks that is blocks or an
        /// ore product, and spawning steel bars on a mountain shoulder would be worse than
        /// spawning nothing.</summary>
        private static ThingDef ChunkFor(ThingDef rock)
        {
            return rock != null
                ? DefDatabase<ThingDef>.GetNamedSilentFail("Chunk" + rock.defName)
                : null;
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
        private static void SeedFlora(Map map, List<IntVec3> plateauCells, ABSettings settings,
            int[] edgeDist, CellRect rect, int w)
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
                    || c.GetEdifice(map) != null)
                {
                    continue; // open water and mud flats fall out here on fertility
                }
                // ALPINE CHARACTER (option D): exposure falls off inland, so growth
                // thins toward the rim and trees effectively refuse to stand there.
                // rim01 = 0 at the shoulder, 1 well inside the plateau.
                int idx = (c.z - rect.minZ) * w + (c.x - rect.minX);
                int ed = idx >= 0 && idx < edgeDist.Length ? edgeDist[idx] : 99;
                float rim01 = Mathf.Clamp01((ed - 1) / 8f);
                float exposure = Mathf.Lerp(0.30f, 1f, rim01);
                if (!Rand.Chance(chanceBase * t.fertility * exposure))
                {
                    continue;
                }
                float treeWeight = Mathf.Lerp(0.05f, 1f, rim01);
                ThingDef plantDef = plants.RandomElementByWeight(p =>
                    biome.CommonalityOfPlant(p)
                        * (p.plant != null && p.plant.IsTree ? treeWeight : 1f));
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
        private static byte[] Classify(bool[] solid, int w, int h, out int[] edgeDistOut)
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

            // Handed back for the alpine pass: tarn placement and flora exposure both
            // need "how far is this cell from a drop", and recomputing the BFS would be
            // both wasteful and a chance to disagree with the classifier.
            edgeDistOut = edgeDist;

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
            Despeckle(kind, solid, w, h, 2);
            return kind;
        }

        /// <summary>
        /// THE REAL CAUSE OF "TEXTURE OUTLINE ERRORS", fixed at its source.
        ///
        /// Two independent noise fields (meadow and terrace) crossed at cell resolution
        /// speckle the mass into dozens of one- and two-cell wall stubs and pinhole floor
        /// pockets. Every one of those is a real wall/floor boundary, so vanilla draws it
        /// a lip, an outline and an edge shadow - correctly - and the summit ends up laced
        /// with nested rings. Hiding the wall sprites "fixed" the symptom and destroyed
        /// the wall/floor read (minable rock looked like floor, ore veins turned into
        /// glyphs); the honest fix is to stop generating the speckle.
        ///
        /// A majority filter: a wall with 6+ open neighbours dissolves into ledge, a floor
        /// cell with 6+ wall neighbours fills in. Conversions require all EIGHT neighbours
        /// to be inside the mass, which means edgeDist >= 2 - so the rim is never touched
        /// and the "ledge band >= 1 cell" invariant survives untouched. Two passes clears
        /// stubs and pinholes while leaving any 3x3-or-larger formation intact.
        /// </summary>
        private static void Despeckle(byte[] kind, bool[] solid, int w, int h, int passes)
        {
            int count = w * h;
            byte[] next = new byte[count];
            for (int p = 0; p < passes; p++)
            {
                Array.Copy(kind, next, count);
                for (int z = 0; z < h; z++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = z * w + x;
                        if (!solid[i])
                        {
                            continue;
                        }
                        int wall = 0;
                        int open = 0;
                        int inMass = 0;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dz == 0)
                                {
                                    continue;
                                }
                                int nx = x + dx;
                                int nz = z + dz;
                                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                                {
                                    continue;
                                }
                                int n = nz * w + nx;
                                if (!solid[n])
                                {
                                    continue;
                                }
                                inMass++;
                                if (kind[n] == KindWall)
                                {
                                    wall++;
                                }
                                else
                                {
                                    open++;
                                }
                            }
                        }
                        if (inMass < 8)
                        {
                            continue; // rim cell - leave the ledge band alone
                        }
                        if (kind[i] == KindWall)
                        {
                            // 7, not 6, and the difference is visible from orbit.
                            //
                            // At 6 a wall standing on any CONVEX bend of a plateau boundary
                            // has enough open neighbours to dissolve, so two passes chewed
                            // the outermost wall row off every meadow edge and left walkable
                            // ledge floor in its place. Floor against floor gets a fade, not
                            // a lip - which is why the plateau edge stopped reading as a
                            // mountain edge even though the surface's did. At 7 only genuinely
                            // isolated stubs and one-cell spurs dissolve (the speckle this
                            // pass exists for), boundaries survive intact, and the rock walls
                            // sit right at the meadow where VANILLA draws their pale top lip
                            // and dark outline for us.
                            if (open >= 7)
                            {
                                next[i] = KindLedge;
                            }
                        }
                        else if (wall >= 6)
                        {
                            // Filling notches stays at 6: it makes boundaries cleaner rather
                            // than eroding them.
                            next[i] = KindWall;
                        }
                    }
                }
                Array.Copy(next, kind, count);
            }
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

        /// <summary>Wall depth at which the interior fogs.</summary>
        private const int FogDepth = 3;

        /// <summary>Fog regions smaller than this are dropped outright: a handful of
        /// fogged cells buys nothing and costs another soft border.</summary>
        private const int MinFogComponent = 24;

        /// <summary>
        /// The fog mask: wall cells at least FogDepth into the rock, keeping only
        /// components big enough to be worth a border - "one centre hole" rather than a
        /// scatter of small ones, which is what multiplied the border lines before.
        ///
        /// Distance is measured out of everything that is NOT wall (open mass cells and
        /// off-mass cells alike), so a tunnel or a plateau bay pushes the fog line back
        /// exactly as a player would expect.
        /// </summary>
        private static bool[] ComputeFogMask(byte[] kind, int w, int h)
        {
            int count = w * h;
            int[] depth = new int[count];
            Queue<int> queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                if (kind[i] == KindWall)
                {
                    depth[i] = int.MaxValue;
                }
                else
                {
                    depth[i] = 0;
                    queue.Enqueue(i);
                }
            }
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                int cx = cur % w;
                int cz = cur / w;
                int d = depth[cur] + 1;
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
                        int n = nz * w + nx;
                        if (depth[n] > d)
                        {
                            depth[n] = d;
                            queue.Enqueue(n);
                        }
                    }
                }
            }
            bool[] mask = new bool[count];
            for (int i = 0; i < count; i++)
            {
                mask[i] = kind[i] == KindWall && depth[i] >= FogDepth;
            }
            // Prune fog islands below the size threshold.
            bool[] seen = new bool[count];
            List<int> comp = new List<int>();
            for (int i = 0; i < count; i++)
            {
                if (!mask[i] || seen[i])
                {
                    continue;
                }
                comp.Clear();
                queue.Clear();
                queue.Enqueue(i);
                seen[i] = true;
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    comp.Add(cur);
                    int cx = cur % w;
                    int cz = cur / w;
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
                            int n = nz * w + nx;
                            if (mask[n] && !seen[n])
                            {
                                seen[n] = true;
                                queue.Enqueue(n);
                            }
                        }
                    }
                }
                if (comp.Count < MinFogComponent)
                {
                    for (int k = 0; k < comp.Count; k++)
                    {
                        mask[comp[k]] = false;
                    }
                }
            }
            return mask;
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
