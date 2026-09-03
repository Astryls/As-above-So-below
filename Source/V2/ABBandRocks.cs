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
    /// running its own biome was built out of the SURFACE tile's stone, and any
    /// biome-specific rock belonging to the band's own biome could never appear.
    ///
    /// ⚠ THE MACHINERY WAS ALREADY MOD-AWARE; ONLY ITS SCOPE WAS WRONG. Vanilla's
    /// <c>NaturalRockTypesIn</c> honours <c>biome.forceRockTypes</c> outright, and otherwise
    /// filters candidates through <c>RockAllowedInBiome</c>, which admits a rock declaring
    /// <c>building.biomeSpecific</c> only when the biome's <c>extraRockTypes</c> lists it.
    /// Mod rocks ride in for free on <c>IsNonResourceNaturalRock</c>. Alpha Biomes is the
    /// proof: six of its twelve biomes declare <c>forceRockTypes</c>, and none of it could
    /// reach a band while the tile was the only thing being asked.
    ///
    /// §99.A2 - <c>gravelTerrain</c>. Vanilla writes <c>biomeDef.gravelTerrain ??
    /// TerrainDefOf.Gravel</c> (MapGenUtility:651). We hardcoded <c>TerrainDefOf.Gravel</c>
    /// in four places, so Ocular Forest's <c>GU_AlienSand</c> and Pyroclastic
    /// Conflagration's <c>AB_VolcanicGravel</c> never appeared and a correct rock palette
    /// would still have been standing on wrong ground.
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
        /// ⚠⚠ DIVERGENCE MUST BE REQUESTED, NEVER INCIDENTAL - AND THAT COST 11 SECONDS OF
        /// GENERATION TIME TO LEARN (run #517).
        ///
        /// The first version re-derived a palette for ANY band biome that differed from the
        /// tile's: two or three natural rocks picked at random, seeded on tile+biome.
        /// Defensible in isolation, badly wrong in practice. On an <c>AB_OcularForest</c>
        /// tile - whose biome forces the single rock <c>GU_RoseQuartz</c> - the basement band
        /// resolved to <c>AB_Underground</c>, a biome with NO opinion about rock at all, and
        /// the random roll handed it [Sandstone, Slate, Granite]. All 36,100 cells then
        /// mismatched the rose quartz vanilla had already placed, so <c>FillRock</c>'s
        /// "keep vanilla's rock when the def matches" shortcut never fired once:
        /// 36,208 destroys instead of ~8,000, and <c>FillRock band 0</c> went from 2.4 s to
        /// <b>13.7 s</b>. The destroys are the bill (§91: ListerThings removal is linear),
        /// and a random re-roll had bought exactly nothing to justify paying it.
        ///
        /// So a band now inherits the tile's stone UNLESS its biome actually asks for
        /// something else:
        ///   * <c>forceRockTypes</c>  - an explicit demand; honour it and pay the destroys.
        ///   * <c>extraRockTypes</c>  - an addition, not a replacement; the tile's palette
        ///                              plus these, so most cells still match and the
        ///                              shortcut still fires for them.
        ///   * neither                - the band has no opinion, so neither do we. This is
        ///                              also the better LOOK: the rock under an ocular
        ///                              forest should be the ocular forest's rock.
        ///
        /// ⚠ THE DELEGATION BRANCH IS LOAD-BEARING for a second reason. Vanilla's selection
        /// is seeded on <c>tile.GetHashCode()</c>; a reimplementation that drifted by one
        /// <c>Rand</c> call would silently re-stone the colony. Delegating removes the
        /// possibility rather than trying to match it.
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

            // An explicit demand from the band's own biome outranks everything.
            if (biome != null && biome.forceRockTypes != null && biome.forceRockTypes.Count > 0)
            {
                return new List<ThingDef>(biome.forceRockTypes);
            }

            List<ThingDef> tilePalette = new List<ThingDef>(Find.World.NaturalRockTypesIn(tile));
            if (biome == null || biome == tileBiome)
            {
                return tilePalette;
            }

            // An addition, not a replacement. Only rocks that are actually natural rock are
            // admitted - extraRockTypes is authored by hand and a typo should not put a
            // non-mineable def into the carve.
            if (biome.extraRockTypes != null && biome.extraRockTypes.Count > 0)
            {
                for (int i = 0; i < biome.extraRockTypes.Count; i++)
                {
                    ThingDef d = biome.extraRockTypes[i];
                    if (d != null && d.IsNonResourceNaturalRock && !tilePalette.Contains(d))
                    {
                        tilePalette.Add(d);
                    }
                }
            }
            return tilePalette;
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
