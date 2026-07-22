using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Biomes! Caverns (BiomesTeam.BiomesCaverns): newly
    /// generated basements can come out as one of its living cave biomes
    /// instead of plain solid rock. Detection is by packageId + defName lookup
    /// only; no compile-time reference, everything fails open to the vanilla
    /// solid-rock basement.
    /// </summary>
    public static class BiomesCavernsCompat
    {
        public const string PackageId = "BiomesTeam.BiomesCaverns";

        /// <summary>Settings sentinel for the weighted random pick.</summary>
        public const string RandomChoice = "Random";

        private static bool resolved;
        private static bool activeCached;

        public static bool Active
        {
            get
            {
                if (!resolved)
                {
                    resolved = true;
                    activeCached = ModsConfig.IsActive(PackageId) || ModsConfig.IsActive(PackageId + "_steam");
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

        /// <summary>Random weights. Fungal Forest is the classic cave; Earthen
        /// Depths is rare because its constant 55C outdoor temperature makes a
        /// brutal basement (pocket maps use the biome constant, not our pocket
        /// temperature).</summary>
        private static float WeightOf(string defName)
        {
            if (defName == "BMT_FungalForest")
            {
                return 50f;
            }
            if (defName == "BMT_CrystalCaverns")
            {
                return 35f;
            }
            return 15f;
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

        /// <summary>Resolve the settings choice ("Random" or a defName) to a
        /// biome, or null when the compat cannot run.</summary>
        public static BiomeDef Resolve(string choice)
        {
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
                // Unknown or unloaded choice: fall back to the weighted random
                // rather than silently disabling the feature.
            }
            return pool.RandomElementByWeight(b => WeightOf(b.defName));
        }

        /// <summary>Run one of Biomes! Caverns' self-contained scatter gensteps
        /// (stalagmites, crystals) by defName. Vanilla GenStepDef plumbing only,
        /// so a missing def or an internal change over there never breaks us.</summary>
        public static void RunForeignGenStep(string defName, Map map)
        {
            try
            {
                GenStepDef def = DefDatabase<GenStepDef>.GetNamedSilentFail(defName);
                if (def?.genStep == null)
                {
                    return;
                }
                def.genStep.Generate(map, default(GenStepParams));
            }
            catch (Exception e)
            {
                // Their scatterer failing must not lose the whole basement.
                ABLog.Dev("Foreign genstep " + defName + " failed (ignored): " + e.Message);
            }
        }
    }
}
