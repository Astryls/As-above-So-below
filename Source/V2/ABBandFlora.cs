using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - the shared plant chooser for every band seeding pass.
    ///
    /// THE BUG THIS EXISTS TO CLOSE. Both seeders (ABCavernGen's cavern flora and
    /// ABSkyBandGen's plateau flora) used to gate a candidate on nothing but
    /// <c>plant.fertilityMin &gt; terrain.fertility</c> and then spawn it. Vanilla never
    /// does that: <c>WildPlantSpawner.CalculatePlantsWhichCanGrowAt</c> runs every
    /// candidate through <c>PlantUtility.CanEverPlantAt</c>, which also enforces
    /// <c>plant.wildTerrainTags</c>, <c>plant.terrainBlacklist</c>, pollution and
    /// <c>completelyIgnoreFertility</c>.
    ///
    /// Skipping <c>wildTerrainTags</c> is not cosmetic. A plant standing on terrain whose
    /// tags do not overlap its own reports <c>Plant.DyingBecauseOfTerrainTags</c>, takes
    /// 0.005 damage EVERY TICK from the moment it spawns, and is dead within a few in-game
    /// hours. Field report: Biomes! Caverns fungal forests came up with most of their
    /// fungal trees and all three marsh mushroom variants dying immediately, because
    /// Biomes! Core tags vanilla terrain (Soil -&gt; SoilBasic, SoilRich -&gt; SoilRich,
    /// MarshyTerrain -&gt; MarshySoil, Mud -&gt; Mud, Marsh -&gt; WaterMarshy) and every
    /// Biomes! Caverns plant is gated on those tags. Our seeder planted marsh species on
    /// dry soil and the tree species on marshy soil, so both sides died.
    ///
    /// ⚠ THE SECOND HALF OF THE SAME BUG WAS THE FERTILITY GATE ITSELF. `Mud` and `Marsh`
    /// both have fertility 0, so a `fertility &lt;= 0.01 -&gt; skip` test threw away exactly
    /// the wet ground the hydrophytes wanted while leaving them nowhere to go but dry
    /// soil. Vanilla handles this with <c>completelyIgnoreFertility</c> plus the water
    /// floor in <c>GetBaseDesiredPlantsCountAt</c>, which is what FertilityWeightOn
    /// reproduces. Mud stays bare, because it is not tagged Water and vanilla leaves it
    /// bare on swamp maps too - that is parity, not an oversight.
    ///
    /// ⚠ PICK, THEN VERIFY - NEVER PICK, THEN DISCARD. The old code drew one plant at
    /// random and abandoned the CELL when the draw turned out to be illegal, so mixed
    /// terrain came out thinner than the density setting asked for. Here the candidate
    /// list is filtered to the terrain FIRST, so an ordinary draw is already legal, and
    /// the exact per-cell check only ever rejects for reasons a neighbouring cell would
    /// not share (a blocker, pollution).
    /// </summary>
    public sealed class ABFloraPicker
    {
        private readonly Map map;

        private readonly BiomeDef biome;

        /// <summary>Vanilla's own map-temperature clause, passed straight through to
        /// CanEverPlantAt so a relaxed picker stays relaxed at the point of spawn too.</summary>
        private readonly bool checkMapTemperature;

        /// <summary>Every plant of the biome that could grow SOMEWHERE on this map.</summary>
        private readonly List<ThingDef> pool = new List<ThingDef>();

        /// <summary>Terrain-keyed candidate cache. The tag / blacklist / fertility clauses
        /// of CanEverPlantAt depend only on the TerrainDef, so they are answered once per
        /// distinct terrain rather than once per cell - a carve is tens of thousands of
        /// cells across at most a handful of terrains.</summary>
        private readonly Dictionary<TerrainDef, List<ThingDef>> byTerrain =
            new Dictionary<TerrainDef, List<ThingDef>>();

        /// <summary>Per-instance draw scratch. Deliberately NOT static: two bands can be
        /// seeded inside one generation pass and a shared buffer would let one band's
        /// rejected draws leak into the other's list.</summary>
        private readonly List<ThingDef> scratch = new List<ThingDef>();

        public bool AnyIgnoresFertility { get; private set; }

        public int PoolCount => pool.Count;

        /// <summary>True when the strict picker matched nothing and the map-temperature
        /// clause had to be dropped to get a non-empty pool.</summary>
        public bool TemperatureGateRelaxed { get; private set; }

        private ABFloraPicker(Map map, BiomeDef biome, bool checkMapTemperature)
        {
            this.map = map;
            this.biome = biome;
            this.checkMapTemperature = checkMapTemperature;

            List<ThingDef> all = biome.AllWildPlants;
            if (all == null)
            {
                return;
            }
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef p = all[i];
                if (p?.plant == null || p.IsDeadPlant)
                {
                    continue;
                }
                if (biome.CommonalityOfPlant(p) <= 0f)
                {
                    continue;
                }
                if (checkMapTemperature && !TileTempAllows(p))
                {
                    continue;
                }
                pool.Add(p);
                if (p.plant.completelyIgnoreFertility)
                {
                    AnyIgnoresFertility = true;
                }
            }
        }

        /// <summary>
        /// Build a picker for one band's biome, or null when the biome has no wild plants.
        ///
        /// ⚠ RULE 33 - A FILTER THAT CAN REJECT EVERYTHING MUST SAY SO. Vanilla's
        /// map-temperature clause compares the plant's growth range against the WORLD
        /// TILE's min/max, which for a sealed basement is the surface's weather, not the
        /// cave's. On a cold tile that clause can reject the entire cavern flora and hand
        /// back a barren level with no error anywhere. So a strict pool that comes out
        /// EMPTY is retried without it, and the retry is announced rather than passing
        /// silently as a clean run.
        /// </summary>
        public static ABFloraPicker For(Map map, BiomeDef biome)
        {
            if (map == null || biome == null)
            {
                return null;
            }
            ABFloraPicker strict = new ABFloraPicker(map, biome, true);
            if (strict.pool.Count > 0)
            {
                return strict;
            }
            ABFloraPicker relaxed = new ABFloraPicker(map, biome, false);
            if (relaxed.pool.Count == 0)
            {
                ABLog.Dev("Flora picker (" + biome.defName + "): pool EMPTY even with the "
                    + "map-temperature gate dropped - the biome has no plantable wild flora.");
                return relaxed;
            }
            relaxed.TemperatureGateRelaxed = true;
            ABLog.Dev("Flora picker (" + biome.defName + "): every plant was rejected by the "
                + "world tile temperature range (" + map.TileInfo.MinTemperature.ToString("0.#")
                + " to " + map.TileInfo.MaxTemperature.ToString("0.#")
                + "); gate relaxed, " + relaxed.pool.Count + " plant(s) available.");
            return relaxed;
        }

        private bool TileTempAllows(ThingDef p)
        {
            return !(map.TileInfo.MinTemperature > p.plant.maxGrowthTemperature
                || map.TileInfo.MaxTemperature < p.plant.minGrowthTemperature);
        }

        /// <summary>
        /// The plants of this biome that this TERRAIN admits - the terrain-only clauses of
        /// CanEverPlantAt, hoisted out of the per-cell loop.
        /// </summary>
        public List<ThingDef> CandidatesOn(TerrainDef t)
        {
            if (t == null)
            {
                return null;
            }
            if (byTerrain.TryGetValue(t, out List<ThingDef> cached))
            {
                return cached;
            }
            List<ThingDef> list = new List<ThingDef>();
            for (int i = 0; i < pool.Count; i++)
            {
                ThingDef p = pool[i];
                PlantProperties pp = p.plant;
                // ⚠ THIS CLAUSE IS THE WHOLE FIX. A missing tag overlap is what makes a
                // spawned plant start dying on the tick it lands.
                if (pp.WildTerrainTags.Count > 0
                    && (t.tags == null || !pp.WildTerrainTags.Overlaps(t.tags)))
                {
                    continue;
                }
                if (pp.terrainBlacklist != null && pp.terrainBlacklist.Contains(t))
                {
                    continue;
                }
                if (!pp.completelyIgnoreFertility && t.fertility < pp.fertilityMin)
                {
                    continue;
                }
                list.Add(p);
            }
            byTerrain.Add(t, list);
            return list;
        }

        /// <summary>
        /// How strongly this terrain should attract plants, shaped exactly like vanilla's
        /// <c>WildPlantSpawner.GetBaseDesiredPlantsCountAt</c>: terrain fertility, floored
        /// to 0.1 on WATER when the biome has anything that ignores fertility. Zero means
        /// "seed nothing here".
        /// </summary>
        public float FertilityWeightOn(TerrainDef t)
        {
            if (t == null)
            {
                return 0f;
            }
            float f = t.fertility;
            if (f <= 0f && AnyIgnoresFertility && t.IsWater)
            {
                f = 0.1f;
            }
            return f;
        }

        /// <summary>
        /// Weighted pick from the plants this cell can actually keep alive, verified
        /// against the real vanilla predicate before it is handed back. Null means the
        /// cell has nothing legal, which is a normal answer, not a failure.
        /// </summary>
        /// <param name="bias">Optional extra weight per plant (the sky band uses it to
        /// thin trees toward the plateau rim). Multiplied onto biome commonality.</param>
        public ThingDef Pick(IntVec3 c, TerrainDef t, Func<ThingDef, float> bias)
        {
            List<ThingDef> candidates = CandidatesOn(t);
            if (candidates == null || candidates.Count == 0)
            {
                return null;
            }
            scratch.Clear();
            scratch.AddRange(candidates);
            // Four draws is plenty: the terrain clauses are already satisfied, so the only
            // way the exact check still says no is a per-cell reason (a blocker, pollution)
            // and those reject the whole cell rather than one species.
            for (int attempt = 0; attempt < 4 && scratch.Count > 0; attempt++)
            {
                ThingDef pick = scratch.RandomElementByWeightWithFallback(p =>
                {
                    float w = biome.CommonalityOfPlant(p);
                    return bias != null ? w * bias(p) : w;
                });
                if (pick == null)
                {
                    return null; // every remaining weight was zero
                }
                if (pick.CanEverPlantAt(c, map, false, checkMapTemperature))
                {
                    return pick;
                }
                scratch.Remove(pick);
            }
            return null;
        }
    }
}
