using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - the per-band biome bridge, and the answer to ABBandEnv's standing regression.
    ///
    /// THE PROBLEM. V1 gave every level a real BiomeDef for free: each level was a pocket
    /// map, so <c>PocketMapProperties.biome</c> / <c>pocketTileInfo.PrimaryBiome</c> was a
    /// single assignment and every biome-scoped system followed it for the life of the
    /// save. V2 has ONE Map on a real world tile, and <c>Map.Biome</c> is get-only and
    /// derived from that tile. Assigning it is not possible, and faking it would turn the
    /// player's surface colony into a fungal forest too.
    ///
    /// THE SOLUTION, and why it is not the pattern the architecture notes reject.
    /// RimWorld 1.6 ships a genuinely per-CELL biome API for Odyssey's mixed-biome maps:
    /// <c>map.BiomeAt(cell)</c>, which delegates to
    /// <c>MixedBiomeMapComponent.GetBiomeAt(cell)</c>. Vanilla routes its own biome-scoped
    /// systems through it - WildPlantSpawner uses it for plant CHOICE, density, regrow
    /// days and cave-plant commonality; WildAnimalSpawner for animal choice, commonality
    /// and scaria; MapGenUtility for water, beach, mud and riverbank terrain.
    ///
    /// So there is exactly one honest choke point, it takes the cell as a PARAMETER, and
    /// answering it correctly per band fixes every downstream consumer at once. This is
    /// emphatically NOT the rejected design: what was ruled out was a contextual
    /// <c>map.Biome</c> GETTER driven by an ambient "current cell" latch - lying to vanilla
    /// behind a global, which is what made V1 unmaintainable. A cell-parameterized query
    /// answered from the cell it was handed is the opposite: no ambient state, no latch,
    /// no ordering hazard, and it is precisely the case ABBandEnv's own header describes
    /// as "a postfix away".
    ///
    /// SCOPE. The surface band is deliberately left to vanilla. Overriding it would
    /// clobber Odyssey's real mixed-biome grid on the one band where that grid is
    /// meaningful, so the prefix only claims cells whose level is non-zero.
    /// </summary>
    [HarmonyPatch(typeof(MixedBiomeMapComponent), nameof(MixedBiomeMapComponent.GetBiomeAt))]
    public static class Patch_MixedBiome_ABBandBiomeAt
    {
        /// <summary>One-entry memo. GetBiomeAt is called per cell inside WildPlantSpawner's
        /// scan loops, so a ConditionalWeakTable probe per call is not acceptable; in
        /// practice every call in a burst comes from the same map.</summary>
        private static Map lastMap;

        private static ABBandMap lastBands;

        private static ABBandMap BandsOf(Map map)
        {
            if (ReferenceEquals(map, lastMap))
            {
                return lastBands;
            }
            lastMap = map;
            lastBands = ABBands.CompOf(map);
            return lastBands;
        }

        /// <summary>Dropped when a map goes away, so a recycled Map reference can never
        /// be answered from a stale component.</summary>
        public static void Forget()
        {
            lastMap = null;
            lastBands = null;
        }

        private static bool Prefix(MixedBiomeMapComponent __instance, IntVec3 cell,
            ref BiomeDef __result)
        {
            try
            {
                Map map = __instance?.map;
                if (map == null)
                {
                    return true;
                }
                ABBandMap bands = BandsOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                if (!cell.InBounds(map))
                {
                    return true; // vanilla's own out-of-bounds contract
                }
                int level = bands.LevelOf(cell);
                if (level == 0)
                {
                    return true; // surface stays vanilla, mixed-biome grid included
                }
                // Pass the already-memoized component through: this runs per cell inside
                // WildPlantSpawner's scan loops, and the map-only overload would re-resolve
                // it from the CWT twice more on every call.
                BiomeDef band = ABBandEnv.BiomeOf(map, bands, cell);
                if (band == null)
                {
                    return true;
                }
                __result = band;
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "V2 per-band biome lookup");
            }
            return true;
        }
    }
}
