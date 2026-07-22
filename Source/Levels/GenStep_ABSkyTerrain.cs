using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lays out the sky level from a snapshot of the ground map.
    ///
    /// Classic mode (naturalPeaks off, or a mass too small to open up): the
    /// mountain mass rises one step contracted - outer ring stays an open
    /// walkable rock ledge, the eroded core is solid mineable rock under thick
    /// rock roof, fully fogged, exactly a vanilla unexplored mountain.
    ///
    /// Naturalistic mode (default): masses thick enough to hold one open into
    /// highland plateaus. From the open air inward: a walkable rock terrace of
    /// wandering width (the classic 1-wide ledge, cut deeper into the mountain
    /// where the noise says so), then a cliff band of solid mineable rock whose
    /// depth wanders 2-7 cells, then open mountain-top ground - rough stone,
    /// gravel and soil patches (slider), seeded with the surface biome's own
    /// wild flora (slider) and studded with rocky outcrop mounts that stay
    /// solid, roofed and fogged like miniature peaks. Rooftops, overhang strips
    /// and open air outside the mass are identical in both modes, and the
    /// mined-peak-exposes-sky rule keeps working because rim and outcrop walls
    /// carry the same thick natural roof as the classic core.
    /// </summary>
    public class GenStep_ABSkyTerrain : GenStep
    {
        public override int SeedPart => 762195842;

        // Classification of solid-mass cells.
        private const byte KindOutside = 0;
        private const byte KindLedge = 1;   // walkable rim: legacy ring or wider terrace
        private const byte KindWall = 2;    // solid mineable mass under thick roof
        private const byte KindPlateau = 3; // open mountain-top ground

        public override void Generate(Map map, GenStepParams parms)
        {
            Map ground = map.GroundMap();
            // Biome inheritance (2026-07-22): the sky level lives in the same
            // climate as the ground it caps, so by default the pocket map's
            // deep-scribed PrimaryBiome swaps from the AB_OpenSky placeholder
            // to the surface biome - regrowth, wild plant lists, weather and
            // every other biome-scoped system follow (the cavern basement's
            // proven mechanism). Applies at creation only: existing saves keep
            // the biome their sky map was born with; the toggle restores the
            // stark alpine AB_OpenSky look for new levels.
            if (ground != null && map.pocketTileInfo != null
                && (ABMod.Settings?.skyBiomeInherit ?? true))
            {
                BiomeDef surfaceBiome = ground.TileInfo?.PrimaryBiome;
                if (surfaceBiome != null)
                {
                    map.pocketTileInfo.PrimaryBiome = surfaceBiome;
                    ABLog.Dev("Sky level biome inherited from surface: " + surfaceBiome.defName + ".");
                }
            }
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile).ToList();
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }
            List<Perlin> noises = ABRockGen.MakeNoises(rocks.Count);
            TerrainGrid grid = map.terrainGrid;
            CellIndices indices = map.cellIndices;
            int cellCount = indices.NumGridCells;

            // Pass 1: the solid mountain mass, projected from below.
            bool[] solid = new bool[cellCount];
            foreach (IntVec3 c in map.AllCells)
            {
                RoofDef roof = null;
                Building edifice = null;
                if (ground != null && c.InBounds(ground))
                {
                    roof = ground.roofGrid.RoofAt(c);
                    edifice = ground.edificeGrid[c];
                }
                solid[indices.CellToIndex(c)] = (edifice != null && edifice.def.mineable)
                    || (roof != null && roof.isNatural && roof.isThickRoof);
            }

            // Pass 2: classify the mass. Legacy = 1-wide ledge + all-wall core;
            // naturalistic = terrace / cliff band / plateau per-component.
            ABSettings settings = ABMod.Settings;
            bool naturalistic = settings == null || settings.naturalPeaks;
            byte[] kind = ClassifyMass(map, indices, solid, naturalistic);

            // Outcrop mounts on the plateaus, then treated exactly like walls.
            if (naturalistic)
            {
                float outcropDensity = Mathf.Clamp(settings?.peakOutcropDensity ?? 1f, 0f, 2f);
                if (outcropDensity > 0.001f)
                {
                    AddOutcrops(map, indices, kind, outcropDensity);
                }
                // Hidden-valley control runs AFTER outcrops: outcrop lumps can
                // themselves seal a meadow pocket.
                BreachHiddenValleys(map, indices, kind,
                    Mathf.Clamp(settings?.peakHiddenValleys ?? 1f, 0f, 1f));
            }

            bool[] wall = new bool[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                wall[i] = kind[i] == KindWall;
            }

            // Pass 3: terrain, walls, roofs; collect the fog core and the wall
            // cells that can host ore lumps, plus the plateau for flora.
            List<IntVec3> fogCells = new List<IntVec3>();
            List<IntVec3> oreCells = new List<IntVec3>();
            List<IntVec3> plateauCells = new List<IntVec3>();
            Perlin soilNoise = new Perlin(0.05, 2.0, 0.5, 5, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            float soilFrac = Mathf.Clamp(settings != null ? settings.peakSoilFraction : 0.15f, 0f, 0.5f);
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                byte k = kind[idx];
                if (k == KindLedge)
                {
                    // Walkable rim: flat fog-toned cap terrain, never fogged, so
                    // from above the mountain reads exactly like the fogged mass
                    // seen from the surface (playtest request). Floors can cover it.
                    grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                    continue;
                }
                if (k == KindWall)
                {
                    ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                    GenSpawn.Spawn(rock, c, map);
                    oreCells.Add(c);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                    if (AllWithinRadius(map, indices, wall, c, 2))
                    {
                        // Deep interior only: fogged like vanilla unexplored
                        // rock. The two outer face rows and the rim stay
                        // visible so the mountain reads as a mountain -
                        // fogging the faces let the fog's soft edge swallow
                        // the narrow ledge entirely (playtest regression).
                        fogCells.Add(c);
                    }
                    continue;
                }
                if (k == KindPlateau)
                {
                    // Open mountain top: rough stone near the noise floor,
                    // gravel in the middle, arable soil patches per the slider.
                    float n = (float)(soilNoise.GetValue(c.x, 0.0, c.z) + 1.0) * 0.5f;
                    TerrainDef terrain;
                    if (n > 1f - soilFrac)
                    {
                        terrain = TerrainDefOf.Soil;
                    }
                    else if (n < 0.22f)
                    {
                        ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                        terrain = rock.building?.naturalTerrain ?? TerrainDefOf.Gravel;
                    }
                    else
                    {
                        terrain = TerrainDefOf.Gravel;
                    }
                    grid.SetTerrain(c, terrain);
                    plateauCells.Add(c);
                    continue;
                }
                bool inGround = ground != null && c.InBounds(ground);
                RoofDef roofBelow = inGround ? ground.roofGrid.RoofAt(c) : null;
                if (inGround && LevelSync.CoveredBelow(ground, c))
                {
                    // Constructed roof below, or a wall supporting one: the
                    // rooftop runs to the outer edge of the wall blocks.
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
                else if (roofBelow != null && roofBelow.isNatural)
                {
                    // Thin-roof overhang strip outside the mass: part of the same
                    // flat fog-toned mountain cap.
                    grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                }
                else
                {
                    // No support below: open air, the ground stays visible.
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
            }

            // Pass 4: fog only the deep interior; the outer wall rows stay visible.
            FogGrid fog = map.fogGrid;
            for (int i = 0; i < fogCells.Count; i++)
            {
                IntVec3 c = fogCells[i];
                fog.Refog(new CellRect(c.x, c.z, 1, 1));
            }

            // Pass 4b: meadow pockets fully enclosed by rock are unexplored
            // hidden valleys - fog them like the mass around them. Reachable
            // meadows (linked to the walkable rim, however tortuously) stay
            // visible.
            FogEnclosedMeadows(map, indices, kind, fog);

            // Pass 5: ore lumps inside the mass walls.
            ABOreGen.ScatterOres(map, oreCells,
                Mathf.Clamp(settings?.skyOreDensity ?? 6f, 0f, 12f));

            // Pass 5b: mountain tarns - small water bodies pooled on the
            // plateau (settings; watered cells leave the flora list).
            SeedTarns(map, indices, kind, plateauCells, settings);

            // Pass 6: plateau flora from the surface biome.
            SeedPlateauFlora(map, ground, plateauCells, settings);

            // Pass 7: landmarks (settings): roll and add tile mutators; the
            // vanilla Mutator* gen steps in the AB_Sky generator def run the
            // actual workers after this step returns. The plateau mask (open
            // meadow minus tarn water) both gates the roll (a clear 7x7 must
            // exist) and fences worker placement to real mountain ground.
            bool[] plateauMask = new bool[cellCount];
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                plateauMask[idx] = kind[idx] == KindPlateau
                    && !(grid.TerrainAt(c)?.IsWater ?? false);
            }
            ABSkyLandmarks.RollAndApply(map, plateauCells.Count, plateauMask, settings);
        }

        /// <summary>Hidden-valley control (settings): each enclosed meadow
        /// pocket stays sealed with probability keepChance (1 = always, the
        /// classic behavior); the rest get a 1-wide walkable notch carved to
        /// the nearest exposed cell (shortest BFS cut through the wall), so
        /// they generate as reachable canyon meadows instead. Runs pre-terrain
        /// on the kind[] grid, so notch cells get normal ledge treatment and
        /// the later fog pass sees them as connected.</summary>
        private static void BreachHiddenValleys(Map map, CellIndices indices, byte[] kind, float keepChance)
        {
            if (keepChance >= 0.999f)
            {
                return;
            }
            int cellCount = indices.NumGridCells;
            IntVec3[] adj = GenAdj.AdjacentCells;
            // Reachability flood, exactly FogEnclosedMeadows' seeding rule.
            bool[] reached = new bool[cellCount];
            Queue<IntVec3> queue = new Queue<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                byte k = kind[idx];
                bool seed = k == KindLedge;
                if (!seed && k == KindPlateau)
                {
                    for (int i = 0; i < adj.Length && !seed; i++)
                    {
                        IntVec3 n = c + adj[i];
                        seed = n.InBounds(map) && kind[indices.CellToIndex(n)] == KindOutside;
                    }
                }
                if (seed)
                {
                    reached[idx] = true;
                    queue.Enqueue(c);
                }
            }
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                for (int i = 0; i < adj.Length; i++)
                {
                    IntVec3 n = c + adj[i];
                    if (!n.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(n);
                    if (reached[idx] || (kind[idx] != KindPlateau && kind[idx] != KindLedge))
                    {
                        continue;
                    }
                    reached[idx] = true;
                    queue.Enqueue(n);
                }
            }
            // Group unreached plateau cells into pockets.
            int[] comp = new int[cellCount];
            List<List<IntVec3>> pockets = new List<List<IntVec3>>();
            Stack<IntVec3> stack = new Stack<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                if (kind[idx] != KindPlateau || reached[idx] || comp[idx] != 0)
                {
                    continue;
                }
                List<IntVec3> pocket = new List<IntVec3>();
                pockets.Add(pocket);
                int id = pockets.Count;
                comp[idx] = id;
                stack.Push(c);
                while (stack.Count > 0)
                {
                    IntVec3 p = stack.Pop();
                    pocket.Add(p);
                    for (int i = 0; i < adj.Length; i++)
                    {
                        IntVec3 n = p + adj[i];
                        if (!n.InBounds(map))
                        {
                            continue;
                        }
                        int nIdx = indices.CellToIndex(n);
                        if (comp[nIdx] == 0 && !reached[nIdx] && kind[nIdx] == KindPlateau)
                        {
                            comp[nIdx] = id;
                            stack.Push(n);
                        }
                    }
                }
            }
            if (pockets.Count == 0)
            {
                return;
            }
            IntVec3[] card = GenAdj.CardinalDirections;
            int breachedCount = 0;
            for (int p = 0; p < pockets.Count; p++)
            {
                if (Rand.Chance(keepChance))
                {
                    continue;
                }
                List<IntVec3> pocket = pockets[p];
                // Shortest cut: BFS from the pocket through wall cells
                // (4-connected, so the notch is a clean 1-wide path) until a
                // wall cell touches walkable daylight.
                bool[] visited = new bool[cellCount];
                int[] parent = new int[cellCount];
                Queue<IntVec3> bfs = new Queue<IntVec3>();
                for (int i = 0; i < pocket.Count; i++)
                {
                    int idx = indices.CellToIndex(pocket[i]);
                    visited[idx] = true;
                    parent[idx] = -1;
                    bfs.Enqueue(pocket[i]);
                }
                IntVec3 hit = IntVec3.Invalid;
                while (bfs.Count > 0 && hit == IntVec3.Invalid)
                {
                    IntVec3 c = bfs.Dequeue();
                    int cIdx = indices.CellToIndex(c);
                    for (int i = 0; i < card.Length; i++)
                    {
                        IntVec3 n = c + card[i];
                        if (!n.InBounds(map))
                        {
                            continue;
                        }
                        int nIdx = indices.CellToIndex(n);
                        if (visited[nIdx])
                        {
                            continue;
                        }
                        byte nk = kind[nIdx];
                        if (nk == KindWall)
                        {
                            visited[nIdx] = true;
                            parent[nIdx] = cIdx;
                            bfs.Enqueue(n);
                            continue;
                        }
                        if (kind[cIdx] == KindWall
                            && (nk == KindOutside || ((nk == KindLedge || nk == KindPlateau) && reached[nIdx])))
                        {
                            hit = c;
                            break;
                        }
                    }
                }
                if (hit == IntVec3.Invalid)
                {
                    continue;
                }
                int walk = indices.CellToIndex(hit);
                while (walk >= 0 && kind[walk] == KindWall)
                {
                    kind[walk] = KindLedge;
                    walk = parent[walk];
                }
                breachedCount++;
            }
            if (breachedCount > 0)
            {
                ABLog.Dev("Peak plateau: breached " + breachedCount + " enclosed valley(s) per settings.");
            }
        }

        /// <summary>Mountain tarns (settings): small shallow pools scattered
        /// on open plateau ground, deep water at the heart of the larger
        /// ones. Density 1 lands roughly one tarn per ~1400 open cells; 0
        /// disables. Watered cells are removed from the flora list.</summary>
        private static void SeedTarns(Map map, CellIndices indices, byte[] kind,
            List<IntVec3> plateauCells, ABSettings settings)
        {
            float density = Mathf.Clamp(settings?.peakTarns ?? 1f, 0f, 2f);
            if (density <= 0.001f || plateauCells.Count < 120)
            {
                return;
            }
            float expected = plateauCells.Count / 1400f * density;
            int count = Mathf.FloorToInt(expected);
            if (Rand.Chance(expected - count))
            {
                count++;
            }
            if (count <= 0)
            {
                return;
            }
            TerrainGrid grid = map.terrainGrid;
            HashSet<IntVec3> watered = new HashSet<IntVec3>();
            for (int i = 0; i < count; i++)
            {
                IntVec3 center = plateauCells.RandomElement();
                int size = Rand.RangeInclusive(8, 26);
                List<IntVec3> lump = GridShapeMaker.IrregularLump(center, map, size);
                int placed = 0;
                for (int j = 0; j < lump.Count; j++)
                {
                    IntVec3 c = lump[j];
                    if (!c.InBounds(map) || kind[indices.CellToIndex(c)] != KindPlateau)
                    {
                        continue;
                    }
                    grid.SetTerrain(c, TerrainDefOf.WaterShallow);
                    watered.Add(c);
                    placed++;
                }
                // Deep heart for the bigger pools, vanilla-lake style.
                if (placed >= 14)
                {
                    List<IntVec3> heart = GridShapeMaker.IrregularLump(center, map, Mathf.Max(4, placed / 4));
                    for (int j = 0; j < heart.Count; j++)
                    {
                        IntVec3 c = heart[j];
                        if (watered.Contains(c))
                        {
                            grid.SetTerrain(c, TerrainDefOf.WaterDeep);
                        }
                    }
                }
            }
            if (watered.Count > 0)
            {
                plateauCells.RemoveAll(watered.Contains);
                ABLog.Dev("Peak plateau: " + watered.Count + " tarn cell(s) pooled.");
            }
        }

        /// <summary>Distance-from-open-air classification of the solid mass.
        /// Distances are 8-connected BFS steps; out-of-bounds never seeds, so
        /// masses touching the border stay solid there (legacy behavior).</summary>
        private static byte[] ClassifyMass(Map map, CellIndices indices, bool[] solid, bool naturalistic)
        {
            int cellCount = indices.NumGridCells;
            byte[] kind = new byte[cellCount];
            int[] edgeDist = new int[cellCount];
            Queue<IntVec3> queue = new Queue<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                if (solid[idx])
                {
                    edgeDist[idx] = int.MaxValue;
                }
                else
                {
                    edgeDist[idx] = 0;
                    queue.Enqueue(c);
                }
            }
            IntVec3[] adj = GenAdj.AdjacentCells;
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                int d = edgeDist[indices.CellToIndex(c)] + 1;
                for (int i = 0; i < adj.Length; i++)
                {
                    IntVec3 n = c + adj[i];
                    if (!n.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(n);
                    if (edgeDist[idx] > d)
                    {
                        edgeDist[idx] = d;
                        queue.Enqueue(n);
                    }
                }
            }

            if (!naturalistic)
            {
                // Legacy: ring at distance 1 is ledge, everything deeper is wall
                // (equivalent to the old erode-by-one AllNeighbors rule).
                for (int i = 0; i < cellCount; i++)
                {
                    if (solid[i])
                    {
                        kind[i] = edgeDist[i] <= 1 ? KindLedge : KindWall;
                    }
                }
                return kind;
            }

            // Naturalistic: ONE low-frequency meadow field decides rock vs open
            // across the whole mass (~70-75% rock at the cutoff). Where it
            // opens, plateau runs to the very edge of the mass - a natural
            // landing over the drop, no ledge, no cliff band (playtest feedback
            // run #42: meadows ringed by mandatory rock walls never met the
            // rim). Where it stays rocky, the classic silhouette forms: a
            // walkable ledge of wandering width 1-4, solid wall behind it.
            // Rock/meadow boundaries inside the mass become the cliffs for
            // free. Components that never open fall back to the legacy 1-wide
            // ledge so small crags keep their classic look.
            ABSettings gs = ABMod.Settings;
            float meadowCutoff = Mathf.Clamp(gs?.peakMeadowCutoff ?? MeadowCutoffDefault, 0.45f, 0.75f);
            float meadowScale = Mathf.Clamp(gs?.peakMeadowScale ?? 0.024f, 0.012f, 0.048f);
            int terraceMax = Mathf.Clamp(gs?.peakTerraceMax ?? 4, 1, 6);
            Perlin terraceNoise = new Perlin(0.035, 2.0, 0.5, 4, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            Perlin meadowNoise = new Perlin(meadowScale, 2.0, 0.5, 5, Rand.Range(0, int.MaxValue), QualityMode.Medium);
            int[] comp = new int[cellCount];
            List<bool> compHasPlateau = new List<bool> { false };
            Stack<IntVec3> stack = new Stack<IntVec3>();
            foreach (IntVec3 seed in map.AllCells)
            {
                int seedIdx = indices.CellToIndex(seed);
                if (!solid[seedIdx] || comp[seedIdx] != 0)
                {
                    continue;
                }
                int compId = compHasPlateau.Count;
                compHasPlateau.Add(false);
                stack.Push(seed);
                comp[seedIdx] = compId;
                while (stack.Count > 0)
                {
                    IntVec3 c = stack.Pop();
                    int idx = indices.CellToIndex(c);
                    int ed = edgeDist[idx];
                    byte k;
                    if (Noise01(meadowNoise, c) > meadowCutoff)
                    {
                        // Open zone: meadow landing, but ALWAYS one cell back
                        // from the mass edge - the outermost ring stays
                        // mountain-top lip exactly like ledges, or the rock
                        // face below stops rendering (run #43 regression).
                        if (ed <= 1)
                        {
                            k = KindLedge;
                        }
                        else
                        {
                            k = KindPlateau;
                            compHasPlateau[compId] = true;
                        }
                    }
                    else
                    {
                        // Rock zone: classic ledge-and-wall silhouette.
                        k = ed <= TerraceWidth(terraceNoise, c, terraceMax) ? KindLedge : KindWall;
                    }
                    kind[idx] = k;
                    for (int i = 0; i < adj.Length; i++)
                    {
                        IntVec3 n = c + adj[i];
                        if (!n.InBounds(map))
                        {
                            continue;
                        }
                        int nIdx = indices.CellToIndex(n);
                        if (solid[nIdx] && comp[nIdx] == 0)
                        {
                            comp[nIdx] = compId;
                            stack.Push(n);
                        }
                    }
                }
            }
            // Demote plateau-less components to the classic silhouette.
            for (int i = 0; i < cellCount; i++)
            {
                if (solid[i] && !compHasPlateau[comp[i]])
                {
                    kind[i] = edgeDist[i] <= 1 ? KindLedge : KindWall;
                }
            }
            return kind;
        }

        /// <summary>Default meadow-field threshold over the clamped perlin
        /// (bell-shaped around 0.5): values above open plateau. 0.60 lands
        /// near 25-30% of the deep interior open, 70-75% rock. The live value
        /// comes from the plateau openness setting.</summary>
        private const float MeadowCutoffDefault = 0.60f;

        /// <summary>Walkable rim depth, 1..max cells: mostly the classic
        /// single ledge, with stretches cut deeper into the mountain. The
        /// power curve reproduces the original 1/2/3/4 quantiles at max=4
        /// (thresholds 0.5 / 0.75 / 0.9), so the default look is unchanged.</summary>
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

        private static float Noise01(Perlin noise, IntVec3 c)
        {
            return Mathf.Clamp01((float)(noise.GetValue(c.x, 0.0, c.z) + 1.0) * 0.5f);
        }

        /// <summary>Flood from every exposed cell - ledges, plus meadow cells
        /// touching the outside of the mass (edge landings) - across walkable
        /// plateau (8-connected); any plateau cell never reached is sealed
        /// inside rock and gets vanilla fog so it reads as unexplored mountain
        /// until someone mines into it.</summary>
        private static void FogEnclosedMeadows(Map map, CellIndices indices, byte[] kind, FogGrid fog)
        {
            int cellCount = indices.NumGridCells;
            bool[] reached = new bool[cellCount];
            Queue<IntVec3> queue = new Queue<IntVec3>();
            IntVec3[] adjSeed = GenAdj.AdjacentCells;
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                byte k = kind[idx];
                bool seed = k == KindLedge;
                if (!seed && k == KindPlateau)
                {
                    for (int i = 0; i < adjSeed.Length && !seed; i++)
                    {
                        IntVec3 n = c + adjSeed[i];
                        seed = n.InBounds(map) && kind[indices.CellToIndex(n)] == KindOutside;
                    }
                }
                if (seed)
                {
                    reached[idx] = true;
                    queue.Enqueue(c);
                }
            }
            IntVec3[] adj = GenAdj.AdjacentCells;
            while (queue.Count > 0)
            {
                IntVec3 c = queue.Dequeue();
                for (int i = 0; i < adj.Length; i++)
                {
                    IntVec3 n = c + adj[i];
                    if (!n.InBounds(map))
                    {
                        continue;
                    }
                    int idx = indices.CellToIndex(n);
                    if (reached[idx] || (kind[idx] != KindPlateau && kind[idx] != KindLedge))
                    {
                        continue;
                    }
                    reached[idx] = true;
                    queue.Enqueue(n);
                }
            }
            int fogged = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                if (kind[idx] == KindPlateau && !reached[idx])
                {
                    fog.Refog(new CellRect(c.x, c.z, 1, 1));
                    fogged++;
                }
            }
            if (fogged > 0)
            {
                ABLog.Dev("Peak plateau: " + fogged + " hidden-valley cell(s) fogged.");
            }
        }

        /// <summary>Rocky mounts scattered over the plateaus: irregular lumps
        /// reclassified as wall, so the main pass gives them rock, thick roof,
        /// ore eligibility and (when bulky) a fogged core - miniature peaks
        /// standing on the mountain top.</summary>
        private static void AddOutcrops(Map map, CellIndices indices, byte[] kind, float density)
        {
            List<IntVec3> plateau = new List<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                if (kind[indices.CellToIndex(c)] == KindPlateau)
                {
                    plateau.Add(c);
                }
            }
            if (plateau.Count < 60)
            {
                return;
            }
            int lumps = Mathf.Max(1, Mathf.RoundToInt(plateau.Count / 900f * density));
            for (int i = 0; i < lumps; i++)
            {
                IntVec3 center = plateau.RandomElement();
                int size = Rand.RangeInclusive(9, 42);
                List<IntVec3> lump = GridShapeMaker.IrregularLump(center, map, size);
                for (int j = 0; j < lump.Count; j++)
                {
                    int idx = indices.CellToIndex(lump[j]);
                    if (kind[idx] == KindPlateau)
                    {
                        kind[idx] = KindWall;
                    }
                }
            }
        }

        /// <summary>Wild flora on the fresh plateau, drawn from the SURFACE
        /// biome (the sky level's own biome is the featureless open sky) and
        /// scaled by the vegetation slider. Cave plants are skipped; regrowth
        /// afterwards comes from the open-sky biome's hardy highland mix.</summary>
        private static void SeedPlateauFlora(Map map, Map ground, List<IntVec3> plateauCells, ABSettings settings)
        {
            if (plateauCells.Count == 0)
            {
                return;
            }
            float veg = Mathf.Clamp(settings != null ? settings.peakVegetation : 1f, 0f, 2f);
            if (veg <= 0.01f)
            {
                return;
            }
            BiomeDef biome = ground?.Biome;
            if (biome == null)
            {
                return;
            }
            List<ThingDef> plants = biome.AllWildPlants.ToListSafe();
            if (plants.Count == 0)
            {
                return;
            }
            float chanceBase = 0.09f * Mathf.Max(0.4f, biome.plantDensity) * veg;
            for (int i = 0; i < plateauCells.Count; i++)
            {
                IntVec3 c = plateauCells[i];
                TerrainDef t = c.GetTerrain(map);
                if (t.fertility <= 0.01f || !c.Standable(map)
                    || c.GetPlant(map) != null || c.GetEdifice(map) != null)
                {
                    continue;
                }
                if (!Rand.Chance(chanceBase * t.fertility))
                {
                    continue;
                }
                ThingDef plantDef = plants.RandomElementByWeight(p => biome.CommonalityOfPlant(p));
                if (plantDef?.plant == null || plantDef.plant.cavePlant
                    || plantDef.plant.fertilityMin > t.fertility)
                {
                    continue;
                }
                Plant plant = GenSpawn.Spawn(plantDef, c, map, WipeMode.Vanish) as Plant;
                if (plant != null)
                {
                    plant.Growth = Rand.Range(0.25f, 0.9f);
                }
            }
        }

        /// <summary>True when every cell within the square radius is set; cells
        /// beyond the map edge count as set.</summary>
        private static bool AllWithinRadius(Map map, CellIndices indices, bool[] grid, IntVec3 c, int radius)
        {
            foreach (IntVec3 n in CellRect.CenteredOn(c, radius))
            {
                if (!n.InBounds(map))
                {
                    continue;
                }
                if (!grid[indices.CellToIndex(n)])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
