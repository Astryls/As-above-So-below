using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Biomes! Caverns (BiomesTeam.BiomesCaverns): a generated basement
    /// band can come out as one of its living cave biomes instead of plain solid rock.
    /// Detection is by packageId + defName lookup only; no compile-time reference, and
    /// everything fails open to the vanilla solid-rock basement.
    ///
    /// Ported from V1 essentially unchanged - this half had no pocket-map dependency, so
    /// it survives the V2 rearchitecture intact. What did NOT survive is how V1 applied
    /// the biome (<c>map.pocketTileInfo.PrimaryBiome</c>); see ABBandBiome for the V2
    /// replacement.
    /// </summary>
    public static class BiomesCavernsCompat
    {
        public const string PackageId = "BiomesTeam.BiomesCaverns";

        /// <summary>Settings sentinel for the weighted random pick.</summary>
        public const string RandomChoice = "Random";

        /// <summary>Settings sentinel for "leave the basement as plain solid rock".</summary>
        public const string NoneChoice = "None";

        /// <summary>Settings sentinel for vanilla-style caves: the same worm-carved network,
        /// floored with the tile's own natural rock terrain and dressed with nothing. No
        /// dependency on Biomes! Caverns, so it is the only cave option most players have.
        /// </summary>
        public const string VanillaChoice = "Vanilla";

        /// <summary>
        /// Should the basement be carved with plain vanilla caves rather than a cavern biome?
        ///
        /// ⚠ THE SECOND CLAUSE IS THE BUG FIX, NOT A CONVENIENCE. Before vanilla caves
        /// existed as an option, `Resolve` returned null whenever no cavern biome could be
        /// produced - and ABCavernGen read that null as "carve nothing at all". For everyone
        /// without Biomes! Caverns installed, that meant the basement was a completely solid
        /// block of rock with no cave system anywhere in it, which reads as a broken feature
        /// rather than a disabled one. "Random" now means "surprise me", and the honest
        /// fallback when there are no cavern biomes to be surprised by is vanilla caves.
        /// Only the explicit `NoneChoice` produces solid rock.
        /// </summary>
        public static bool WantsVanillaCaves(string choice)
        {
            if (choice == NoneChoice)
            {
                return false;
            }
            if (choice == VanillaChoice)
            {
                return true;
            }
            return Resolve(choice) == null;
        }

        private static bool resolved;

        private static bool activeCached;

        public static bool Active
        {
            get
            {
                if (!resolved)
                {
                    resolved = true;
                    activeCached = ModsConfig.IsActive(PackageId)
                        || ModsConfig.IsActive(PackageId + "_steam");
                    ABLog.Dev("Biomes! Caverns compat: " + (activeCached ? "ACTIVE" : "not present"));
                }
                return activeCached;
            }
        }

        private static readonly string[] BiomeNames =
        {
            "BMT_FungalForest",
            "BMT_CrystalCaverns",
            "BMT_EarthenDepths"
        };

        /// <summary>Random weights.
        ///
        /// V1 kept Earthen Depths rare (15) because its constant ~55C outdoor temperature
        /// made a brutal basement: a pocket map takes its temperature from the biome
        /// constant, so the whole level cooked. V2 does not inherit that problem the same
        /// way - the basement is roofed solid rock, so vanilla ROOM temperature governs
        /// and ABBandEnv's offset only applies to cells that are genuinely outdoors. It is
        /// therefore given a fair share here pending a play test; if a carved cavern
        /// (a large roofed room) still reads as an oven, this is the number to pull back.
        /// </summary>
        private static float WeightOf(string defName)
        {
            if (defName == "BMT_FungalForest")
            {
                return 40f;
            }
            if (defName == "BMT_CrystalCaverns")
            {
                return 30f;
            }
            return 30f;
        }

        /// <summary>Every Biomes! Caverns cavern biome present in this load.</summary>
        public static List<BiomeDef> CavernBiomes()
        {
            List<BiomeDef> list = new List<BiomeDef>();
            if (!Active)
            {
                return list;
            }
            for (int i = 0; i < BiomeNames.Length; i++)
            {
                BiomeDef def = DefDatabase<BiomeDef>.GetNamedSilentFail(BiomeNames[i]);
                if (def != null)
                {
                    list.Add(def);
                }
            }
            return list;
        }

        /// <summary>Resolve the settings choice ("Random", "None", or a defName) to a
        /// biome, or null when the compat cannot or should not run.</summary>
        public static BiomeDef Resolve(string choice)
        {
            if (choice == NoneChoice)
            {
                return null;
            }
            List<BiomeDef> pool = CavernBiomes();
            if (pool.Count == 0)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(choice) && choice != RandomChoice)
            {
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].defName == choice)
                    {
                        return pool[i];
                    }
                }
                // Unknown or unloaded choice: fall back to the weighted random rather than
                // silently disabling the feature.
            }
            return pool.RandomElementByWeight(b => WeightOf(b.defName));
        }

        /// <summary>
        /// Run one of Biomes! Caverns' self-contained scatter gensteps (stalagmites,
        /// crystals) by defName, CONFINED to one band.
        ///
        /// This is the one place the V1 port genuinely could not be copied. Over there the
        /// basement WAS the whole map, so handing their GenStep the map was correct. Here
        /// the map is all three bands, and their scatterer picks cells map-wide - it would
        /// happily plant stalagmites across the player's surface colony and the open sky.
        ///
        /// There is no way to scope a foreign GenStep from outside, so instead: snapshot
        /// what exists, run it, then destroy anything new that landed outside the target
        /// band. A one-off O(things) pass at generation, and it stays correct no matter
        /// what their scatterer does internally.
        /// </summary>
        public static void RunForeignGenStep(string defName, Map map, CellRect confineTo)
        {
            try
            {
                GenStepDef def = DefDatabase<GenStepDef>.GetNamedSilentFail(defName);
                if (def?.genStep == null)
                {
                    return;
                }

                HashSet<Thing> before = new HashSet<Thing>(map.listerThings.AllThings);
                def.genStep.Generate(map, default(GenStepParams));

                List<Thing> strays = null;
                List<Thing> after = map.listerThings.AllThings;
                for (int i = 0; i < after.Count; i++)
                {
                    Thing t = after[i];
                    if (t == null || !t.Spawned || before.Contains(t))
                    {
                        continue;
                    }
                    if (confineTo.Contains(t.Position))
                    {
                        continue;
                    }
                    (strays ?? (strays = new List<Thing>())).Add(t);
                }
                if (strays != null)
                {
                    for (int i = 0; i < strays.Count; i++)
                    {
                        if (strays[i].Spawned)
                        {
                            strays[i].Destroy(DestroyMode.Vanish);
                        }
                    }
                    ABLog.Dev("Foreign genstep " + defName + ": removed " + strays.Count
                        + " spawn(s) that landed outside the basement band.");
                }
            }
            catch (Exception e)
            {
                // Their scatterer failing must not lose the whole basement.
                ABLog.Dev("Foreign genstep " + defName + " failed (ignored): " + e.Message);
            }
        }
    }
}
