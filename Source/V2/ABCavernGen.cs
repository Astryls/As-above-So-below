using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Turns a freshly filled solid-rock basement band into a Biomes! Caverns cave system:
    /// one connected worm-carved tunnel network with chambers, floored with the cavern
    /// biome's own terrain resolution (patch makers + fertility bands, so mycelial soil,
    /// magma ash and crystal floors come out authentic), seeded with the biome's cave flora
    /// and a small starting fauna population, plus Biomes! Caverns' own stalagmite and
    /// crystal scatterers.
    ///
    /// Ported from V1's GenStep_ABCavernCarve. Three things had to change for V2, and they
    /// are the whole reason this was not a copy-paste:
    ///
    ///  1. BAND SCOPING. V1's basement WAS the map, so it carved
    ///     CellRect.WholeMap(map).ContractedBy(Margin). Here that rect spans all three
    ///     bands - it would carve tunnels through the player's surface colony and the open
    ///     sky. Every rect in this file is the basement band's own rect.
    ///  2. THE BIOME. V1 assigned map.pocketTileInfo.PrimaryBiome, which vanilla
    ///     deep-scribes. There is no per-band equivalent, so the choice is recorded on
    ///     ABBandMap and served through ABBandEnv.BiomeOf via the BiomeAt patch
    ///     (see ABBandBiome for why that is the honest choke point).
    ///  3. FOREIGN GENSTEPS. Biomes! Caverns' scatterers pick cells map-wide; they are run
    ///     through BiomesCavernsCompat.RunForeignGenStep, which confines their output to
    ///     the band.
    ///
    /// Fog is not touched here: ABBandedGeneration re-fogs every sub-surface band after
    /// carving, so the caves stay sealed and dark until someone mines into them, exactly
    /// like a vanilla mountain interior.
    /// </summary>
    public static class ABCavernGen
    {
        /// <summary>Solid border kept inside the band so the network never touches the band
        /// edge (no walk-ins from the gutter, and it mirrors the sealed-box feel).</summary>
        private const int Margin = 8;

        public static void Generate(Map map, ABBandMap bands, int band)
        {
            if (map == null || bands == null || !ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            string choice = settings?.basementBiomeChoice ?? BiomesCavernsCompat.RandomChoice;
            if (choice == BiomesCavernsCompat.NoneChoice)
            {
                return; // the player explicitly asked for plain solid rock
            }
            // A null biome from here on means VANILLA CAVES, not "do nothing". That
            // distinction is the whole of the "basements have no caves" report: the old code
            // treated "no cavern biome available" as "carve nothing", so every player without
            // Biomes! Caverns got a solid block of rock and no way to ask for anything else.
            BiomeDef biome = choice == BiomesCavernsCompat.VanillaChoice
                ? null
                : BiomesCavernsCompat.Resolve(choice);
            // More cave the deeper you go. depth 1 = immediately below the surface, and the
            // player's setting is that level's baseline rather than a ceiling - so a third
            // basement reads as an open cave system instead of a slightly bigger warren.
            int depth = Mathf.Max(1, bands.surfaceBand - band);
            float openness = Mathf.Clamp(settings?.cavernOpenness ?? 0.3f, 0.1f, 0.6f)
                * (1f + 0.35f * (depth - 1));
            Carve(map, bands, band, biome, Mathf.Min(openness, 0.8f));
        }

        /// <param name="biome">The cavern biome to dress the caves with, or NULL for vanilla
        /// caves. Null is a first-class mode here, not a failure: it means carve the same
        /// tunnel network but leave the tile's own rock terrain, flora and fauna exactly as
        /// vanilla would - which is what a player who does not run Biomes! Caverns expects a
        /// cave under a mountain to look like.</param>
        private static void Carve(Map map, ABBandMap bands, int band, BiomeDef biome, float openness)
        {
            bool vanillaCaves = biome == null;

            // 1. Record the biome. Scribed on the band component, read back through
            // ABBandEnv.BiomeOf - so plant regrowth, wildlife and ambience follow it for
            // the life of the save without any of it leaking onto the surface band.
            // Left null for vanilla caves, which is the same state the field has always had
            // when no cavern biome was chosen, so every reader already handles it.
            bands.basementBiome = biome;

            float chamberFreq = Mathf.Clamp(ABMod.Settings?.cavernChamberFreq ?? 0.02f, 0.01f, 0.05f);
            ABLog.Dev("Cave basement (band " + band + "): "
                + (vanillaCaves ? "vanilla caves" : biome.defName)
                + ", openness " + openness.ToString("0.00")
                + ", chambers " + chamberFreq.ToString("0.000") + ".");

            // 2. Worm-carve one connected network inside the BAND. Every worm after the
            // first starts on an already carved cell, so the whole system links up.
            CellRect inner = bands.RectOfBand(band).ContractedBy(Margin);
            if (inner.Width < 16 || inner.Height < 16)
            {
                ABLog.Dev("Cavern carve skipped: band rect too small (" + inner + ").");
                return;
            }
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
                        CarveDisc(c, Rand.Range(3f, 4.8f));
                    }
                }
            }

            // 3. Open the carved cells: drop the rock fill and floor them with the biome's
            // own terrain resolution - patch makers first (lakes, magma, crystal fields),
            // then the fertility bands.
            Perlin fertNoise = new Perlin(0.021, 2.0, 0.5, 6, Rand.Range(0, int.MaxValue),
                QualityMode.Medium);
            TerrainGrid grid = map.terrainGrid;
            List<TerrainPatchMaker> patchMakers = vanillaCaves ? null : biome.terrainPatchMakers;
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
                if (vanillaCaves)
                {
                    // Vanilla caves are floored by the rock they were cut out of. FillRock
                    // already laid that terrain when it filled the band, and vanilla's own
                    // GenStep_RocksFromGrid does exactly the same thing for a cave cell, so
                    // the honest vanilla result is to leave it completely alone.
                    continue;
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

            // 4. Thick rock roofs stay everywhere underground, so any carved cell out of
            // roof-holder range gets a natural pillar planted at it, exactly where support
            // is missing. Scan order means later checks already see earlier pillars.
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile)?.ToList()
                ?? new List<ThingDef>();
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

            // 5. Biomes! Caverns dressing: stalagmites everywhere, crystals in the crystal
            // biome. Both are self-contained scatterers with their own placement
            // validators, and both are confined to this band by the compat wrapper.
            if (vanillaCaves)
            {
                // Nothing below this line is vanilla: stalagmites and crystals are Biomes!
                // Caverns scatterers, and the flora/fauna seeding reads a cavern biome's own
                // lists. A vanilla cave is bare rock, and vanilla's own ambient spawners take
                // it from here once the player mines in.
                ABLog.Dev("Vanilla cave carve complete: " + carvedList.Count + " cells opened.");
                return;
            }

            CellRect bandRect = bands.RectOfBand(band);
            float formations = Mathf.Clamp(ABMod.Settings?.cavernFormations ?? 1f, 0f, 2f);
            int formationRuns = Mathf.FloorToInt(formations);
            if (Rand.Chance(formations - formationRuns))
            {
                formationRuns++;
            }
            for (int fi = 0; fi < formationRuns; fi++)
            {
                BiomesCavernsCompat.RunForeignGenStep("BMT_ScatterStalagmiteGenerator", map, bandRect);
            }
            if (biome.defName == "BMT_CrystalCaverns")
            {
                BiomesCavernsCompat.RunForeignGenStep("BMT_CrystalsGenerator", map, bandRect);
            }

            // 6. Starting flora from the biome's own cave plant list, weighted by
            // commonality and gated by ABFloraPicker - which is to say by vanilla's own
            // CanEverPlantAt, wildTerrainTags included. Before that gate existed here, a
            // Biomes! Caverns fungal forest generated with its fungal trees standing on
            // marshy soil and its marsh mushrooms standing on dry soil, and every one of
            // them started dying on the tick it spawned. See ABFloraPicker's header.
            // Regrowth afterwards is vanilla WildPlantSpawner business, which follows the
            // band biome on its own.
            ABFloraPicker picker = ABFloraPicker.For(map, biome);
            if (picker != null && picker.PoolCount > 0)
            {
                float chanceBase = 0.08f * Mathf.Max(0.5f, biome.plantDensity);
                int placed = 0;
                int noCandidate = 0;
                for (int i = 0; i < carvedList.Count; i++)
                {
                    IntVec3 c = carvedList[i];
                    if (!c.Standable(map) || c.GetPlant(map) != null || c.GetEdifice(map) != null)
                    {
                        continue;
                    }
                    TerrainDef t = c.GetTerrain(map);
                    // Vanilla's weighting, so marsh and shallow water still get their
                    // hydrophytes instead of being thrown away for having fertility 0.
                    float fertWeight = picker.FertilityWeightOn(t);
                    if (fertWeight <= 0f || !Rand.Chance(chanceBase * fertWeight))
                    {
                        continue;
                    }
                    ThingDef plantDef = picker.Pick(c, t, null);
                    if (plantDef == null)
                    {
                        noCandidate++;
                        continue; // this terrain admits nothing from this biome
                    }
                    Plant plant = GenSpawn.Spawn(plantDef, c, map, WipeMode.Vanish) as Plant;
                    if (plant != null)
                    {
                        plant.Growth = Rand.Range(0.2f, 0.95f);
                        placed++;
                    }
                }
                // Rule 33: a rolled cell that produced nothing is reported, so "the cave
                // came up bare" is never an unfalsifiable observation again.
                ABLog.Dev("Cavern flora (" + biome.defName + "): " + placed + " placed, "
                    + noCandidate + " cell(s) rolled with no legal species"
                    + (picker.TemperatureGateRelaxed ? ", temperature gate relaxed" : "") + ".");
            }

            // 7. A small starting fauna population. Vanilla's ambient spawner wants map-edge
            // entry cells that a sealed basement band cannot offer, so the level gets its
            // residents up front.
            List<PawnKindDef> animals = biome.AllWildAnimals?.ToList() ?? new List<PawnKindDef>();
            if (animals.Count > 0 && carvedList.Count > 0)
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

            ABLog.Dev("Cavern carve complete: " + carvedList.Count + " cells opened.");
        }
    }
}
