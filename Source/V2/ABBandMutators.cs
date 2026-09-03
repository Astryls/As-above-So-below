using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §99 TIER 2 - LANDMARKS ON EVERY LEVEL (the user's "option B": whitelist the safe
    /// families rather than all-or-nothing).
    ///
    /// Odyssey landmarks and Vanilla Landmarks Expanded are NOT gensteps. A `LandmarkDef` is
    /// a bundle of `TileMutatorDef`s, each with a `TileMutatorWorker` whose hooks the five
    /// `GenStep_Mutator*` steps call in a fixed order. Tier 1's scatterer machinery therefore
    /// cannot see them at all, and `ABBandLocal` deliberately pins the ones we already patch
    /// (Coast, River, Lake, VEE's LoneIsland and Crater families) to the SURFACE band.
    ///
    /// Each mutator genstep is a three-line loop over `map.TileInfo.Mutators` calling one
    /// worker hook, so this file skips the gensteps entirely and calls the hook directly on
    /// the workers it has cleared. That is both cheaper and safer than re-running the
    /// gensteps, which would re-enter every mutator on the tile including the ones we are
    /// deliberately not running.
    ///
    /// ⚠⚠ ONLY `GeneratePostTerrain` IS RE-RUN, AND THAT IS A DESIGN DECISION, NOT AN
    /// OVERSIGHT. `GeneratePostElevationFertility` writes `MapGenerator.Elevation`, a
    /// generation-time grid whose only consumer (`GenStep_RocksFromGrid`, order 200) ran long
    /// before the carve; re-running it post-carve would write a grid nobody will read again
    /// while fighting the carve for authorship of the band's shape. `GenerateCriticalStructures`
    /// and `GenerateNonCriticalStructures` are structures, which §ABStructureFit already
    /// documents as the thing that fits worst across seams. `GeneratePostFog` runs after our
    /// fog policy and would undo it.
    ///
    /// ⚠⚠ THE WHITELIST IS SHORT ON PURPOSE, AND TWO OF THE EXCLUSIONS ARE CORRECTIONS TO MY
    /// OWN EARLIER PROPOSAL. When the user picked option B I offered "hot springs, plant
    /// groves, rocky/boulder, animal habitat, mixed biome". Reading the actual workers
    /// changed two of those:
    ///
    ///   * MIXED BIOME IS OUT. `TileMutatorWorker_MixedBiome` rewrites the biome grid per
    ///     region, and §ABBandEnv already owns "which biome is this cell in" per band - that
    ///     is the mechanism the whole per-level climate system reads. Two authors, one grid
    ///     (rule 16), and the other author is load-bearing.
    ///   * PLANT GROVE AND THE WILD-PLANT MUTATORS ARE OUT, BECAUSE THEY ARE ALREADY IN.
    ///     `WildPlantSpawner.CalculatePlantsWhichCanGrowAt` does
    ///     `tmpWildPlants.AddRange(MutatorWildPlants)`, so every mutator's
    ///     `additionalWildPlants` list already reaches every band through Tier 1's per-band
    ///     plant pass. Re-running the grove worker would place a SECOND grove on top of
    ///     flora that already reflects it. Rule 68's cousin: check whether the feature
    ///     arrived by another road before building a second one.
    ///
    /// ⚠ AND THE LAKE FAMILY - INCLUDING `Pond` - IS OUT FOR A CONCRETE REASON, not caution.
    /// `TileMutatorWorker_Pond` derives from `TileMutatorWorker_Lake`, and
    /// `Patch_TileMutatorWorker_Lake_ABSurfaceBand` (§ABWaterV2) forces `GetLakeCenter` into
    /// the SURFACE band by design. Re-running Pond on band 2 would compute a centre in band
    /// 0, every write would land outside the scope rect, and the guard would refuse all of
    /// them - a silent no-op that looks like a working feature. Making it work means making
    /// the water system scope-aware, which is a separate change to a subsystem §96 has
    /// already flagged as delicate and unattributed. The sky band has its own tarns (with the
    /// edge rule) and the basement has cavern water; neither is left dry by this exclusion.
    /// </summary>
    internal static class ABBandMutators
    {
        /// <summary>
        /// ⚠⚠ A BLOCKLIST, NOT A WHITELIST - AND THAT INVERSION IS THE WHOLE POINT.
        ///
        /// This started as a whitelist of three vanilla worker names (HotSprings, Patches,
        /// ObsidianDeposits). Surveying Alpha Biomes killed it: that mod ships TWELVE
        /// TileMutatorWorkers - TarLakes, PropaneLakes, MagmaticQuagmire, HealingSprings,
        /// MutagenicSprings, QuicksandPits, AgariluxPrime, OnlyGrass and three ancient vents
        /// - and NOT ONE of them matches a vanilla name fragment. A name-based whitelist
        /// cannot be mod-aware; it structurally excludes every mod that ever ships, which is
        /// the exact opposite of what §99 is for.
        ///
        /// So the default is RUN IT, and the list below is what must not run. Each entry
        /// names a reason, and the terrain guard bounds whatever gets through: void cells,
        /// out-of-band writes and hazardous terrain beside a drop are all refused before
        /// they land (rule 37 - the guard is the enforcement point, this list is policy).
        ///
        /// Matched as a FRAGMENT of the runtime type name so a mod subclassing a blocked
        /// family cannot walk out of the block by deriving one level deeper.
        /// </summary>
        private static readonly string[] BlockedWorkerFragments =
        {
            // Geography that only makes sense measured from the ground level.
            "Coast", "River", "Headwater", "Confluence", "Delta", "Island", "Archipelago",
            "Peninsula", "Bay", "Cove", "Atoll", "Fjord", "Iceberg", "Lakeshore", "Harbor",
            "Shore", "Beach",
            // Shape. ABCavernGen owns the basement's shape and ABSkyBandGen owns the
            // plateau's; these author elevation or carve caves and would fight both.
            "Cavern", "Cave", "Chasm", "Crevasse", "Hollow", "Basin", "Valley", "Plateau",
            "Cliffs", "Dune", "Crater", "Volcano", "Caldera", "TerraformingScar", "Scar",
            "Mountain",
            // Geological Landforms' own worker. §56r lends it a borrowed decision context
            // during generation; re-entering it post-carve, outside that borrow, would ask
            // it to author a landform against inputs it was never given.
            "Landform",
            // Man-made and wreckage: the user's ground-level rule, plus §ABStructureFit's
            // standing objection to structures near seams.
            "Ancient", "Ruins", "Abandoned", "Stockpile", "Uplink", "Quarry", "Junkyard",
            "Megahive", "Settlement", "Colony",
            // Biome authorship. ABBandEnv owns "which biome is this cell in" per band and
            // the whole per-level climate system reads it (rule 16).
            "MixedBiome", "Biome"
        };

        internal static int workersRun;

        /// <summary>
        /// Does this worker actually implement the hook we are about to call?
        ///
        /// ⚠ RULE 29 / RULE 74: REPORTED OUTCOMES MUST MEAN SOMETHING. Run #517 proudly
        /// logged "3 mutator run(s)" - all three were <c>TileMutatorWorker_Mountain</c>,
        /// which overrides <c>GeneratePostElevationFertility</c> ONLY. We were calling the
        /// empty base method three times and counting it as work done. A count that includes
        /// no-ops cannot tell you whether the feature fired, which is the single thing that
        /// count exists to answer.
        ///
        /// Cheap to ask (one reflection lookup per worker per map) and it also skips real
        /// work: no scope arming, no noise rebinding, no restore, for a call that would do
        /// nothing.
        /// </summary>
        private static bool ImplementsPostTerrain(TileMutatorWorker worker)
        {
            try
            {
                MethodInfo m = worker.GetType().GetMethod(
                    nameof(TileMutatorWorker.GeneratePostTerrain),
                    BindingFlags.Instance | BindingFlags.Public);
                return m != null && m.DeclaringType != typeof(TileMutatorWorker);
            }
            catch
            {
                return true; // cannot tell - let it run, the guard bounds it anyway
            }
        }

        internal static bool Enabled => ABMod.Settings?.bandLandmarks ?? true;

        /// <summary>
        /// ⚠⚠ THE LAKE FAMILY IS BLOCKED BY TYPE, NOT BY NAME, AND THAT DISTINCTION IS THE
        /// WHOLE REASON THIS METHOD IS SHAPED LIKE THIS.
        ///
        /// A name test for "Lake" would be both too broad and too narrow. Alpha Biomes'
        /// <c>TileMutatorWorker_TarLakes</c> and <c>TileMutatorWorker_PropaneLakes</c> derive
        /// from vanilla's <c>TileMutatorWorker_Lake</c> - so they inherit
        /// <c>GetLakeCenter</c>, which <c>Patch_TileMutatorWorker_Lake_ABSurfaceBand</c>
        /// (§ABWaterV2) deliberately forces into the SURFACE band. Run one on band 2 and it
        /// computes a centre in band 0, every write lands outside the scope rect, the guard
        /// refuses all of them, and the result is a silent no-op that LOOKS like a working
        /// feature. That is the exact trap §99 recorded for Pond, and an <c>is</c> test is
        /// what catches it for subclasses nobody has heard of.
        ///
        /// The content is not lost: Alpha Biomes' tar and lava also arrive through
        /// <c>terrainPatchMakers</c>, which §99.B1 replays per band. Lake-derived workers
        /// stay blocked until the water system itself is made scope-aware.
        /// </summary>
        private static bool Cleared(TileMutatorWorker worker)
        {
            if (worker == null)
            {
                return false;
            }
            if (worker is TileMutatorWorker_Lake)
            {
                return false;
            }
            string type = worker.GetType().Name;
            for (int i = 0; i < BlockedWorkerFragments.Length; i++)
            {
                if (type.IndexOf(BlockedWorkerFragments[i],
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Run every cleared mutator over one band. Called from inside the band scope, BEFORE
        /// the scatterers and the plant pass - mutators write TERRAIN, and both of the later
        /// passes validate against terrain, so running them in the other order would let a
        /// geyser sit on ground that a hot spring is about to become.
        /// </summary>
        internal static void DressBand(Map map, int band)
        {
            if (!Enabled || map?.TileInfo == null)
            {
                return;
            }
            List<TileMutatorDef> mutators = map.TileInfo.Mutators as List<TileMutatorDef>
                ?? new List<TileMutatorDef>(map.TileInfo.Mutators);
            if (mutators.Count == 0)
            {
                return;
            }
            if (!ABBandLocal.TryBandGeometry(map, out _, out int slot, out int offset))
            {
                return;
            }
            for (int i = 0; i < mutators.Count; i++)
            {
                TileMutatorWorker worker = mutators[i]?.Worker;
                if (!Cleared(worker) || !ImplementsPostTerrain(worker))
                {
                    continue;
                }
                List<KeyValuePair<FieldInfo, ModuleBase>> saved = WrapNoiseFields(worker, slot, offset);
                try
                {
                    worker.GeneratePostTerrain(map);
                    workersRun++;
                    ABLog.Dev("Band dressing: mutator " + mutators[i].defName + " ("
                        + worker.GetType().Name + ") re-run on band " + band + ".");
                }
                catch (Exception e)
                {
                    ABLog.Dev("Band dressing: mutator " + mutators[i].defName
                        + " failed on band " + band + " (ignored): " + e.Message);
                }
                finally
                {
                    RestoreNoiseFields(worker, saved);
                    // ⚠ ALWAYS RESTORE, AND RESTORE IN A finally. A TileMutatorWorker is a
                    // singleton hanging off its def and it OUTLIVES generation - it is asked
                    // for weather commonality, animal commonality and plant commonality for
                    // the entire life of the colony. Leaving a band-local wrapper on its
                    // noise field would quietly answer every one of those questions with the
                    // wrong coordinate forever. Same discipline as ABGLContextBorrow's
                    // half-taken unwind (§56r).
                }
            }
        }

        /// <summary>
        /// Swap every `ModuleBase` field on the worker for a band-local wrapper.
        ///
        /// ⚠ WHY WRAP THE FIELD RATHER THAN PATCH THE SAMPLER. `TileMutatorWorker_HotSprings`
        /// builds its field in `Init` as a falloff radius of `map.Size.x * 0.35` centred on
        /// `map.Center` - which on a 190x576 map is a point inside ONE band. It then samples
        /// `springNoise.GetValue(allCell)` straight out of `GeneratePostTerrain` with no
        /// intermediate hook to patch. Wrapping the module repairs the FIELD, so every
        /// present and future reader of it is corrected at once. That is exactly the argument
        /// `VanillaLandmarksCompat.RebindCraterModules` makes for VEE's Crater family, and
        /// this is the same trick generalised: reflect over all ModuleBase fields instead of
        /// naming three.
        ///
        /// ⚠ NEVER DOUBLE-WRAP. The rewrite is modulo-then-offset, so applying it twice folds
        /// an already-centred coordinate back toward zero and the feature vanishes - the
        /// precise bug VanillaLandmarksCompat records for craters.
        /// </summary>
        private static List<KeyValuePair<FieldInfo, ModuleBase>> WrapNoiseFields(
            TileMutatorWorker worker, int slot, int offset)
        {
            List<KeyValuePair<FieldInfo, ModuleBase>> saved = null;
            try
            {
                FieldInfo[] fields = worker.GetType().GetFields(BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (!typeof(ModuleBase).IsAssignableFrom(fields[i].FieldType))
                    {
                        continue;
                    }
                    ModuleBase current = fields[i].GetValue(worker) as ModuleBase;
                    if (current == null || current is ABBandLocal.BandLocalModule)
                    {
                        continue;
                    }
                    (saved ?? (saved = new List<KeyValuePair<FieldInfo, ModuleBase>>()))
                        .Add(new KeyValuePair<FieldInfo, ModuleBase>(fields[i], current));
                    fields[i].SetValue(worker, ABBandLocal.Wrap(current, slot, offset));
                }
            }
            catch (Exception e)
            {
                ABLog.Dev("Band dressing: could not rebind noise fields on "
                    + worker.GetType().Name + " (" + e.Message + "); running unwrapped.");
            }
            return saved;
        }

        private static void RestoreNoiseFields(TileMutatorWorker worker,
            List<KeyValuePair<FieldInfo, ModuleBase>> saved)
        {
            if (saved == null || worker == null)
            {
                return;
            }
            for (int i = 0; i < saved.Count; i++)
            {
                try
                {
                    saved[i].Key.SetValue(worker, saved[i].Value);
                }
                catch (Exception e)
                {
                    // A failed restore is a LEAK, not a cosmetic problem: this worker will
                    // answer weather, animal and plant commonality questions for the rest of
                    // the colony's life with a band-local coordinate. Rule 15 - it shouts.
                    Log.Warning(ABLog.Tag + " could not restore mutator noise field "
                        + saved[i].Key.Name + " on " + worker.GetType().Name + " ("
                        + e.Message + "); it stays band-local for the rest of this session.");
                }
            }
        }
    }
}
