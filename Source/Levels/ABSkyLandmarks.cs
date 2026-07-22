using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Landmarks on sky levels (2026-07-22, user feature). Odyssey's landmark
    /// system is LandmarkDef -> TileMutatorDefs -> workers whose gen hooks
    /// take a plain Map, invoked by the named Mutator* GenSteps that iterate
    /// map.TileInfo.Mutators - and a pocket map's TileInfo IS our deep-scribed
    /// pocketTileInfo. So the whole integration is: roll landmarks during
    /// GenStep_ABSkyTerrain (order 200), AddMutator() them onto the pocket
    /// tile, and let the vanilla MutatorPostTerrain (220) /
    /// MutatorCriticalStructures (500) / MutatorNonCriticalStructures (700) /
    /// MutatorFinal gen steps - added to the AB_Sky generator def - run the
    /// real workers. Ticking, weather hooks, and save/load ride the engine's
    /// own tile-mutator machinery. Vanilla Landmarks Expanded (or any
    /// landmark mod) enumerates in automatically via DefDatabase.
    ///
    /// Per-landmark mode (user-approved semantics ladder):
    ///   Disabled - never on sky levels.
    ///   Random   - enters the pool; one master chance decides whether a new
    ///              level rolls a landmark from it (commonality-weighted).
    ///   Enabled  - always spawns when the level suits it (biome whitelist
    ///              honored against the sky level's inherited biome).
    ///   Forced   - always spawns, suitability bypassed.
    /// Defaults: Random, except landmarks whose defs smell aquatic/cave-bound
    /// (coast, river, lake, cave...) which default Disabled. All overridable.
    /// Kill switch: LevelGen. Requires Odyssey (the landmark system itself).
    /// </summary>
    internal static class ABSkyLandmarks
    {
        internal const int ModeDisabled = 0;
        internal const int ModeRandom = 1;
        internal const int ModeEnabled = 2;
        internal const int ModeForced = 3;

        /// <summary>Terms marking landmarks that make no sense on a mountain
        /// top; matched against defName + category + mutator defNames.</summary>
        private static readonly string[] UnsuitableTerms =
        {
            "coast", "river", "lake", "island", "ocean", "water", "harbor",
            "harbour", "bay", "pond", "cave", "cove", "reef", "beach",
            "delta", "fjord", "lagoon", "marsh", "swamp", "archipelago"
        };

        private static List<LandmarkDef> cachedAll;
        private static Dictionary<LandmarkDef, string> displayLabels;

        internal static bool SystemActive => ModsConfig.OdysseyActive;

        internal static List<LandmarkDef> AllLandmarks()
        {
            if (cachedAll == null)
            {
                cachedAll = new List<LandmarkDef>(DefDatabase<LandmarkDef>.AllDefsListForReading);
                cachedAll.SortBy(d => DisplayLabel(d));
            }
            return cachedAll;
        }

        /// <summary>Row label with duplicate disambiguation: families like the
        /// ancient vents all inherit ONE label ("ancient vent") from their
        /// abstract parent, with the distinguishing name living on the REQUIRED
        /// mutator ("ancient smoke vent" / "ancient tox vent" / "ancient heat
        /// vent"). When several landmarks share a label, each shows its required
        /// mutator's label instead; defName is the last resort.</summary>
        internal static string DisplayLabel(LandmarkDef def)
        {
            if (displayLabels == null)
            {
                displayLabels = new Dictionary<LandmarkDef, string>();
                List<LandmarkDef> all = DefDatabase<LandmarkDef>.AllDefsListForReading;
                Dictionary<string, int> counts = new Dictionary<string, int>();
                for (int i = 0; i < all.Count; i++)
                {
                    string baseLabel = all[i].label ?? all[i].defName;
                    counts.TryGetValue(baseLabel, out int c);
                    counts[baseLabel] = c + 1;
                }
                for (int i = 0; i < all.Count; i++)
                {
                    LandmarkDef d = all[i];
                    string baseLabel = d.label ?? d.defName;
                    string resolved = baseLabel;
                    if (counts[baseLabel] > 1)
                    {
                        string mutatorLabel = RequiredMutatorLabel(d);
                        resolved = !mutatorLabel.NullOrEmpty() && mutatorLabel != baseLabel
                            ? mutatorLabel
                            : baseLabel + " (" + d.defName + ")";
                    }
                    displayLabels[d] = resolved;
                }
            }
            return displayLabels.TryGetValue(def, out string label) ? label : (def.label ?? def.defName);
        }

        private static string RequiredMutatorLabel(LandmarkDef def)
        {
            for (int i = 0; i < def.mutatorChances.Count; i++)
            {
                MutatorChance mc = def.mutatorChances[i];
                if (mc.required && mc.mutator != null && !mc.mutator.label.NullOrEmpty())
                {
                    return mc.mutator.label;
                }
            }
            return null;
        }

        /// <summary>Row tooltip: the landmark's own description when it has
        /// one, else the required mutator's (the vents keep theirs there).</summary>
        internal static string DescriptionFor(LandmarkDef def)
        {
            if (!def.description.NullOrEmpty())
            {
                return def.description;
            }
            for (int i = 0; i < def.mutatorChances.Count; i++)
            {
                MutatorChance mc = def.mutatorChances[i];
                if (mc.required && mc.mutator != null && !mc.mutator.description.NullOrEmpty())
                {
                    return mc.mutator.description;
                }
            }
            return null;
        }

        internal static int DefaultModeFor(LandmarkDef def)
        {
            string haystack = (def.defName + " " + (def.category ?? "")).ToLowerInvariant();
            for (int i = 0; i < def.mutatorChances.Count; i++)
            {
                TileMutatorDef m = def.mutatorChances[i].mutator;
                if (m != null)
                {
                    haystack += " " + m.defName.ToLowerInvariant();
                }
            }
            for (int i = 0; i < UnsuitableTerms.Length; i++)
            {
                if (haystack.Contains(UnsuitableTerms[i]))
                {
                    return ModeDisabled;
                }
            }
            return ModeRandom;
        }

        internal static int ModeFor(ABSettings settings, LandmarkDef def)
        {
            if (settings?.landmarkModes != null
                && settings.landmarkModes.TryGetValue(def.defName, out int mode))
            {
                return Mathf.Clamp(mode, ModeDisabled, ModeForced);
            }
            return DefaultModeFor(def);
        }

        internal static void SetMode(ABSettings settings, LandmarkDef def, int mode)
        {
            if (settings.landmarkModes == null)
            {
                settings.landmarkModes = new Dictionary<string, int>();
            }
            if (mode == DefaultModeFor(def))
            {
                settings.landmarkModes.Remove(def.defName);
            }
            else
            {
                settings.landmarkModes[def.defName] = mode;
            }
        }

        internal static string ModeLabel(int mode)
        {
            switch (mode)
            {
                case ModeForced: return "AB_LandmarkForced".Translate();
                case ModeEnabled: return "AB_LandmarkEnabled".Translate();
                case ModeDisabled: return "AB_LandmarkDisabled".Translate();
                default: return "AB_LandmarkRandom".Translate();
            }
        }

        /// <summary>Suitability of one landmark for a given sky map: every
        /// REQUIRED mutator must accept the level's (inherited) biome via its
        /// whitelist/blacklist. Landmark-level tile checks (coast rotation,
        /// neighboring settlements) are world-map concepts and do not apply
        /// to a pocket tile.</summary>
        internal static bool SuitableFor(LandmarkDef def, Map skyMap)
        {
            BiomeDef biome = skyMap.TileInfo?.PrimaryBiome;
            for (int i = 0; i < def.mutatorChances.Count; i++)
            {
                MutatorChance mc = def.mutatorChances[i];
                if (mc.mutator == null)
                {
                    return false;
                }
                if (!mc.required && mc.chance < 1f)
                {
                    continue;
                }
                if (!BiomeOk(mc.mutator, biome))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool BiomeOk(TileMutatorDef mutator, BiomeDef biome)
        {
            if (biome == null)
            {
                return true;
            }
            List<BiomeDef> white = mutator.biomeWhitelist;
            if (white != null && white.Count > 0 && !white.Contains(biome))
            {
                return false;
            }
            List<BiomeDef> black = mutator.biomeBlacklist;
            if (black != null && black.Count > 0 && black.Contains(biome))
            {
                return false;
            }
            return true;
        }

        /// <summary>Called from GenStep_ABSkyTerrain (order 200, before the
        /// vanilla mutator gen steps at 220+): roll the configured landmarks
        /// and add their mutators to the sky map's pocket tile.</summary>
        internal static void RollAndApply(Map skyMap, int plateauCellCount, ABSettings settings)
        {
            if (!SystemActive || settings == null || !settings.skyLandmarks)
            {
                return;
            }
            if (!ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            try
            {
                // Landmarks are surface features: they need open plateau
                // ground. All-rock classic peaks skip them.
                if (plateauCellCount < 80)
                {
                    ABLog.Dev("Sky landmarks: no open plateau (" + plateauCellCount + " cells), skipped.");
                    return;
                }
                int max = Mathf.Clamp(settings.skyLandmarkMax, 1, 3);
                int applied = 0;
                List<LandmarkDef> all = AllLandmarks();
                // Forced: always, suitability bypassed.
                for (int i = 0; i < all.Count && applied < max; i++)
                {
                    if (ModeFor(settings, all[i]) == ModeForced)
                    {
                        Apply(skyMap, all[i]);
                        applied++;
                    }
                }
                // Enabled: deterministic when suitable.
                for (int i = 0; i < all.Count && applied < max; i++)
                {
                    if (ModeFor(settings, all[i]) == ModeEnabled && SuitableFor(all[i], skyMap))
                    {
                        Apply(skyMap, all[i]);
                        applied++;
                    }
                }
                // Random pool: one master-chance roll, commonality-weighted.
                if (applied < max && Rand.Chance(Mathf.Clamp01(settings.skyLandmarkChance)))
                {
                    List<LandmarkDef> pool = new List<LandmarkDef>();
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (ModeFor(settings, all[i]) == ModeRandom && SuitableFor(all[i], skyMap))
                        {
                            pool.Add(all[i]);
                        }
                    }
                    if (pool.Count > 0
                        && pool.TryRandomElementByWeight(d => Mathf.Max(d.commonality, 0.05f), out LandmarkDef pick))
                    {
                        Apply(skyMap, pick);
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "sky landmark roll");
            }
        }

        private static void Apply(Map skyMap, LandmarkDef def)
        {
            Tile tile = skyMap.pocketTileInfo;
            if (tile == null)
            {
                return;
            }
            int added = 0;
            for (int i = 0; i < def.mutatorChances.Count; i++)
            {
                MutatorChance mc = def.mutatorChances[i];
                if (mc.mutator == null)
                {
                    continue;
                }
                if (mc.required || Rand.Chance(mc.chance))
                {
                    tile.AddMutator(mc.mutator);
                    added++;
                }
            }
            ABLog.Dev("Sky landmark applied: " + def.defName + " (" + added + " mutator(s)).");
        }
    }
}
