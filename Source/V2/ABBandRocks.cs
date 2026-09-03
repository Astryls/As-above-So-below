using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §99.A - EVERY BAND GETS ITS OWN BIOME'S STONE AND ITS OWN BIOME'S GROUND.
    ///
    /// THE BUG. The carve resolved rock ONCE, from the TILE:
    ///     <c>Find.World.NaturalRockTypesIn(map.Tile)</c>
    /// and handed that single list to every band - <c>FillRock</c>, <c>ABCavernGen</c>,
    /// <c>ABSkyBandGen</c> and the mountain-cap renderer all shared it. So a basement band
    /// running a Biomes! Caverns biome was built out of the SURFACE tile's stone, and any
    /// biome-specific rock belonging to the band's own biome could never appear at all.
    ///
    /// ⚠ THE MACHINERY WAS ALREADY MOD-AWARE; ONLY ITS SCOPE WAS WRONG. Vanilla's
    /// <c>NaturalRockTypesIn</c> honours <c>biome.forceRockTypes</c> outright, and otherwise
    /// filters candidates through <c>RockAllowedInBiome</c>, which admits a rock declaring
    /// <c>building.biomeSpecific</c> only when the biome's <c>extraRockTypes</c> lists it.
    /// Mod rocks ride in for free on <c>IsNonResourceNaturalRock</c>. Alpha Biomes is the
    /// proof: six of its twelve biomes declare <c>forceRockTypes</c> (Pyroclastic
    /// Conflagration wants <c>AB_Obsidianstone</c> + <c>Slate</c>), and none of it could
    /// reach a band while the tile was the only thing being asked.
    ///
    /// So this does not invent a selection rule - it re-asks VANILLA'S question with the
    /// BAND'S biome instead of the tile's (rule 36: run vanilla's predicate).
    ///
    /// ⚠ AND IT DELEGATES OUTRIGHT WHEN THE BAND'S BIOME IS THE TILE'S BIOME. The surface
    /// band, and every band on a map without a per-band biome system, must produce EXACTLY
    /// the list vanilla produced - not a re-derivation that happens to agree. Vanilla's
    /// selection is seeded on <c>tile.GetHashCode()</c> and picks 2-3 at random; a
    /// reimplementation that drifted by one <c>Rand</c> call would silently re-stone the
    /// colony. Delegating removes the possibility.
    ///
    /// §99.A2 - <c>gravelTerrain</c>. Vanilla writes <c>biomeDef.gravelTerrain ??
    /// TerrainDefOf.Gravel</c> (MapGenUtility:651). We hardcoded <c>TerrainDefOf.Gravel</c>
    /// in four places, so Pyroclastic Conflagration's <c>AB_VolcanicGravel</c> never
    /// appeared and a correct rock palette would still have been standing on wrong ground.
    /// </summary>
    internal static class ABBandRocks
    {
        /// <summary>Per-carve cache. The rock list AND its noise set must be built together -
        /// <c>ABRockGen.PickIndex</c> indexes the noise list by rock index, so a mismatched
        /// pair is an index-out-of-range waiting to happen (rule 16: count the write paths).
        /// </summary>
        private sealed class Palette
        {
            internal List<ThingDef> rocks;

            internal List<Perlin> noises;

            internal TerrainDef gravel;
        }

        private static readonly Dictionary<BiomeDef, Palette> cache
            = new Dictionary<BiomeDef, Palette>();

        /// <summary>Cleared at the start of every carve. The cache is keyed on BiomeDef,
        /// which is a global def - holding it across colonies would hand map B the noise
        /// fields generated for map A (rule 21: the parallel grids do not travel).</summary>
        internal static void Reset()
        {
            cache.Clear();
        }

        /// <summary>The band's rock palette and its matching noise set.</summary>
        internal static void ForBand(Map map, ABBandMap bands, int band,
            out List<ThingDef> rocks, out List<Perlin> noises)
        {
            Palette p = PaletteFor(map, BiomeOfBand(map, bands, band));
            rocks = p.rocks;
            noises = p.noises;
        }

        /// <summary>The gravel this band's biome wants under its rock (§99.A2).</summary>
        internal static TerrainDef GravelFor(Map map, BiomeDef biome)
        {
            return PaletteFor(map, biome).gravel;
        }

        /// <summary>Gravel for whatever biome owns this cell - the convenience overload for
        /// runtime callers (stair carving) that have a cell but no band in hand.</summary>
        internal static TerrainDef GravelAt(Map map, IntVec3 c)
        {
            BiomeDef biome = null;
            try
            {
                biome = ABBandEnv.BiomeOf(map, c);
            }
            catch
            {
            }
            return biome?.gravelTerrain ?? TerrainDefOf.Gravel;
        }

        internal static BiomeDef BiomeOfBand(Map map, ABBandMap bands, int band)
        {
            try
            {
                CellRect rect = bands.RectOfBand(band);
                return ABBandEnv.BiomeOf(map, rect.CenterCell) ?? map.Biome;
            }
            catch
            {
                return map.Biome;
            }
        }

        private static Palette PaletteFor(Map map, BiomeDef biome)
        {
            if (biome == null)
            {
                biome = map.Biome;
            }
            if (cache.TryGetValue(biome, out Palette existing))
            {
                return existing;
            }
            List<ThingDef> rocks = ResolveRocks(map, biome);
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }
            Palette p = new Palette
            {
                rocks = rocks,
                noises = ABRockGen.MakeNoises(rocks.Count),
                gravel = biome.gravelTerrain ?? TerrainDefOf.Gravel
            };
            cache[biome] = p;
            ABLog.Dev("Band rock palette for biome " + (biome.defName ?? "?") + ": "
                + rocks.Count + " rock type(s) [" + RockNames(rocks) + "], gravel "
                + (p.gravel?.defName ?? "none") + ".");
            return p;
        }

        private static string RockNames(List<ThingDef> rocks)
        {
            string s = string.Empty;
            for (int i = 0; i < rocks.Count; i++)
            {
                s += (i > 0 ? ", " : "") + rocks[i].defName;
            }
            return s;
        }

        /// <summary>
        /// Vanilla's own rule, re-asked for an arbitrary biome.
        ///
        /// ⚠ THE DELEGATION BRANCH IS THE IMPORTANT ONE - see the class header. When the
        /// band's biome IS the tile's primary biome we do not re-derive anything.
        /// </summary>
        private static List<ThingDef> ResolveRocks(Map map, BiomeDef biome)
        {
            PlanetTile tile = map.Tile;
            BiomeDef tileBiome = null;
            try
            {
                tileBiome = tile.Valid ? tile.Tile.PrimaryBiome : null;
            }
            catch
            {
            }
            if (biome == null || biome == tileBiome)
            {
                return new List<ThingDef>(Find.World.NaturalRockTypesIn(tile));
            }
            if (biome.forceRockTypes != null && biome.forceRockTypes.Count > 0)
            {
                return new List<ThingDef>(biome.forceRockTypes);
            }

            List<ThingDef> candidates = new List<ThingDef>();
            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (!d.IsNonResourceNaturalRock)
                {
                    continue;
                }
                // RockAllowedInBiome, with THIS biome's extraRockTypes rather than the
                // tile's. This is the clause that lets a band biome contribute its own
                // biome-specific stone (Odyssey's SolidIce, a mod's cavern rock).
                if (d.building != null && d.building.biomeSpecific
                    && !biome.extraRockTypes.NotNullAndContains(d))
                {
                    continue;
                }
                candidates.Add(d);
            }
            if (candidates.Count == 0)
            {
                return new List<ThingDef>(Find.World.NaturalRockTypesIn(tile));
            }

            // Seeded on tile AND biome, so a given band biome on a given tile always
            // produces the same stone across regenerations, and two different band biomes
            // do not accidentally share one palette (rule 19: a shared seed is a shared
            // answer).
            Rand.PushState();
            try
            {
                Rand.Seed = Gen.HashCombineInt(tile.GetHashCode(), biome.shortHash);
                int take = Rand.RangeInclusive(2, 3);
                if (take > candidates.Count)
                {
                    take = candidates.Count;
                }
                return new List<ThingDef>(candidates.TakeRandomDistinct(take));
            }
            finally
            {
                Rand.PopState();
            }
        }

        /// <summary>
        /// Every rock that can appear anywhere on this map, across all bands.
        ///
        /// The mountain-cap renderer builds materials from the tile's rock list; once bands
        /// can carry their own stone, a band rock absent from that list would render with a
        /// missing material. Rule 34: invisible = absent from every layer.
        /// </summary>
        internal static List<ThingDef> AllRocksOnMap(Map map)
        {
            List<ThingDef> result = new List<ThingDef>();
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                result.AddRange(Find.World.NaturalRockTypesIn(map.Tile));
                return result;
            }
            for (int band = 0; band < bands.bandCount; band++)
            {
                Palette p = PaletteFor(map, BiomeOfBand(map, bands, band));
                for (int i = 0; i < p.rocks.Count; i++)
                {
                    if (!result.Contains(p.rocks[i]))
                    {
                        result.Add(p.rocks[i]);
                    }
                }
            }
            if (result.Count == 0)
            {
                result.AddRange(Find.World.NaturalRockTypesIn(map.Tile));
            }
            return result;
        }
    }
}
