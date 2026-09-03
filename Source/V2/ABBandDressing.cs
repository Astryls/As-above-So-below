using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §99 BAND DRESSING - "every level is a real level, not a stage set".
    ///
    /// THE REPORT. "Upper bands (and lower) devoid of rock chunks, vegetation (too sparse for
    /// biome) and features (lakes, vents, etc) including Odyssey landmarks and Vanilla
    /// Landmarks Expanded. Steam Geysers, Helixian Gas Vents, and Boulders from Regrowth 2."
    ///
    /// THE DIAGNOSIS, and it is not one bug but three with a common root.
    ///
    ///   (1) THE CARVE IS TOTAL, AND IT RUNS LAST. Every vanilla and modded genstep generates
    ///       across the whole 126x896 map - SteamGeysers at order 950, RockChunks at 970, mod
    ///       vents and boulders alongside them - and then our postfix on
    ///       <c>GenerateContentsIntoMap</c> erases every non-surface band and rebuilds it.
    ///       Nothing re-places what it erased, so the only content a band has ever had is
    ///       what ABSkyBandGen and ABCavernGen seed themselves. That is by construction, and
    ///       it has been by construction since V2 existed.
    ///
    ///   (2) THE INITIAL PLANT PASS IS DELIBERATELY SUPPRESSED on doomed bands
    ///       (<c>Patch_WildPlantSpawner_ABSkipDoomedBands</c>, a real and correct perf fix -
    ///       it was planting three bands' worth of carve fodder). So band vegetation was only
    ///       ever our own seeders' constants: 0.16 * plantDensity on the sky plateau, thinned
    ///       as far as 0.30 by the alpine rim curve, and 0.08 * plantDensity in caverns.
    ///       Vanilla's own desired-plant count is far above that. Hence "too sparse for
    ///       biome" - an exact description of the cause.
    ///
    ///   (3) ⚠⚠ AND THE GROUND LEVEL WAS SHORT TOO, WHICH NOBODY HAD REPORTED.
    ///       <c>GenStep_Scatterer.CountFromPer10kCells</c> squares <c>map.Size.x</c> and never
    ///       reads <c>Size.z</c>, so a 126x896 map is issued the count a 126x126 map would get
    ///       - one band's worth - and vanilla then spreads it uniformly over seven bands. The
    ///       surface has been receiving roughly ONE SEVENTH of its correct geyser, chunk and
    ///       vent count, with the other six sevenths generated straight into the carve's path.
    ///       See <c>Patch_GenStep_ABSurfaceBandScope</c>: confining the original pass to the
    ///       surface band fixes the ground level and deletes the wasted work in one move.
    ///
    /// THE FIX. Re-run the real gensteps, once per non-surface band, under ABBandScope - so
    /// each band is dressed by the same code that dresses the ground, with the same counts and
    /// the same validators. GENERALISED, NOT ENUMERATED (the user's explicit choice): the
    /// pass auto-detects what ran and subtracts a blocklist, rather than naming defs. That is
    /// what makes unlisted and future mods work, and it is not a theoretical benefit -
    /// ReGrowth 2's boulders are a TRANSPILER on <c>GenStep_RockChunks</c> with no def of
    /// their own, so nothing name-based could ever have found them, and running vanilla's
    /// genstep gets them for free.
    ///
    /// ⚠ WHAT IS DELIBERATELY EXCLUDED, AND WHY EACH ONE (rule 33: say which clause).
    ///   * <c>isJunk</c> - vanilla's own flag on every ancient-wreckage scatterer
    ///     (AncientPollutionJunk, AncientTuneller, ScarlandsJunk*, AncientMiscDebris). The
    ///     user's rule is that wreckage belongs on the ground level only, and vanilla already
    ///     marks exactly that content. Rule 54: search the capability.
    ///   * <c>nearPlayerStart</c> / <c>nearMapCenter</c> - both name a place that exists only
    ///     on the ground level. Colony-anchored content is not band content.
    ///   * ruins, shrines, monoliths, quest and mech-cluster scatterers - structures, not
    ///     scenery. §ABStructureFit exists because these fit badly across seams; multiplying
    ///     them by band count would multiply that problem too.
    ///   * mineable lumps - ABOreGen already owns basement ore and ABSkyBandGen owns plateau
    ///     ore, both with their own depth-scaled density. Two authors, one grid (rule 16).
    ///   * anima and other special trees - gated separately and always-on, see ABGroundOnly.
    ///
    /// ⚠ IT RUNS INSIDE THE CARVE WINDOW, WHICH IS NOT AN IMPLEMENTATION DETAIL.
    /// <c>ABAirSpawnGuard</c>'s whole job is moving things OUT of non-surface bands, and it is
    /// live the moment <c>bands.Banded</c> becomes true. Dressing a band with the guard armed
    /// would relocate every geyser we place straight back onto the surface - the same trap
    /// §57 recorded when the carve first moved in-window and 18,075 basement rocks walked
    /// upstairs. Running under <c>CarveInProgress</c> is what makes the dressing pass the
    /// authority on its own bands, exactly as the carve is on its.
    /// </summary>
    internal static class ABBandDressing
    {
        /// <summary>Exactly the gensteps that RAN for this map, captured from
        /// <c>GenerateContentsIntoMap</c>'s own argument. Deliberately not reconstructed from
        /// <c>MapGeneratorDef.genSteps</c>: that list misses everything tile mutators and
        /// mods inject at runtime, and reconstructing it would be a second, worse answer to a
        /// question the engine already answered (rule 12: a foreign snapshot is an input).
        /// </summary>
        private static readonly List<GenStepWithParams> ranSteps = new List<GenStepWithParams>();

        /// <summary>Registered by a band generator that stood down in favour of the vanilla
        /// plant pass, to be invoked ONLY if that pass yields nothing (rule 33).</summary>
        private static readonly List<KeyValuePair<int, Action>> floraFallbacks
            = new List<KeyValuePair<int, Action>>();

        /// <summary>Stands down <c>Patch_WildPlantSpawner_ABSkipDoomedBands</c>: during the
        /// dressing pass the "doomed" bands are the ones we are deliberately planting, and
        /// the carve that doomed them has already finished.</summary>
        internal static bool Active;

        // ---- reporting (rule 29: report outcomes) ------------------------------------
        internal static int stepsRun;

        internal static int thingsPlaced;

        internal static int plantsPlaced;

        internal static string lastSummary = "no banded map dressed yet";

        internal static void CaptureSteps(IEnumerable<GenStepWithParams> steps)
        {
            ranSteps.Clear();
            floraFallbacks.Clear();
            if (steps == null)
            {
                return;
            }
            foreach (GenStepWithParams s in steps)
            {
                ranSteps.Add(s);
            }
        }

        internal static bool FeaturesEnabled => ABMod.Settings?.bandFeatures ?? true;

        internal static bool FloraParityEnabled => ABMod.Settings?.bandVegetationParity ?? true;

        /// <summary>Asked by a band generator before it seeds its own flora. When true the
        /// generator should skip and register a fallback instead.</summary>
        internal static bool WillDressFlora(Map map)
        {
            return FloraParityEnabled
                && ABBandedGeneration.TryPendingSurfaceRect(map, out _, out _);
        }

        internal static void RegisterFloraFallback(int band, Action fallback)
        {
            if (fallback != null)
            {
                floraFallbacks.Add(new KeyValuePair<int, Action>(band, fallback));
            }
        }

        // -------------------------------------------------------------------
        // The pass
        // -------------------------------------------------------------------

        /// <summary>
        /// Called from the tail of <c>CarveInner</c>, after the gutters exist and before the
        /// fog policy is applied. The ordering is load bearing at both ends: the gutters must
        /// already be open air so a scatterer cannot straddle a seam, and the basement refog
        /// must still be ahead of us so cave dressing is hidden until it is mined out, exactly
        /// like vanilla mountain content.
        /// </summary>
        internal static void Dress(Map map, ABBandMap bands)
        {
            stepsRun = 0;
            thingsPlaced = 0;
            plantsPlaced = 0;
            ABBandScope.airRejections = 0;
            if (map == null || bands == null || !bands.Banded)
            {
                return;
            }

            Active = true;
            try
            {
                // Regions are filthy after ~70k carve spawns, and several validators the
                // scatterers use (Buildable, room checks, reachability) read them. Vanilla's
                // own gensteps ran against clean regions; giving ours the same footing is
                // one call, and it is the same call Map.FinalizeInit is about to make.
                try
                {
                    map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                }
                catch (Exception e)
                {
                    ABLog.Dev("Band dressing: region rebuild declined (" + e.Message
                        + "); continuing with whatever regions exist.");
                }

                // ⚠ COMPUTED OUTSIDE EVERY SCOPE, ON PURPOSE. This property walks
                // map.AllCells, which ABBandScope redirects - reading it under a band scope
                // would silently return one band's demand and make every band saturate at a
                // seventh of the right density. Rule 47 in a new costume: a cached global is
                // a coordinate assumption too.
                float wholeMapDesired = 0f;
                float baseDensity = 1f;
                if (FloraParityEnabled)
                {
                    try
                    {
                        baseDensity = map.wildPlantSpawner.CurrentPlantDensityFactor;
                        wholeMapDesired = map.wildPlantSpawner.CurrentWholeMapNumDesiredPlants;
                    }
                    catch (Exception e)
                    {
                        ABLog.Dev("Band dressing: plant demand unavailable (" + e.Message
                            + "); flora parity skipped this map.");
                        wholeMapDesired = 0f;
                    }
                }

                for (int band = 0; band < bands.bandCount; band++)
                {
                    if (band == bands.surfaceBand)
                    {
                        continue; // the ground level was dressed by the real generation pass
                    }
                    CellRect rect = bands.RectOfBand(band);
                    bool sky = band > bands.surfaceBand;
                    var watch = System.Diagnostics.Stopwatch.StartNew();

                    if (FeaturesEnabled)
                    {
                        DressFeatures(map, band, rect, sky);
                    }
                    if (FloraParityEnabled && wholeMapDesired > 0f)
                    {
                        DressFlora(map, band, rect, sky, baseDensity, wholeMapDesired);
                    }
                    ABGenProfile.Phase("BandDressing band " + band,
                        watch.Elapsed.TotalMilliseconds);
                }

                lastSummary = stepsRun + " genstep run(s), " + thingsPlaced + " thing(s), "
                    + plantsPlaced + " plant(s), " + ABBandScope.airRejections
                    + " open-air cell(s) refused";
                ABLog.Dev("Band dressing complete: " + lastSummary + ".");
                ABGroundOnly.AuditBands(map, bands);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2: band dressing failed: " + e);
            }
            finally
            {
                Active = false;
                ABBandScope.AssertNoneOutstanding("band dressing");
            }
        }

        private static void DressFeatures(Map map, int band, CellRect rect, bool sky)
        {
            for (int i = 0; i < ranSteps.Count; i++)
            {
                GenStepDef def = ranSteps[i].def;
                GenStep step = def?.genStep;
                if (!Eligible(def, step))
                {
                    continue;
                }
                int before = map.listerThings.AllThings.Count;
                ABBandScope.Push(map, rect, sky);
                try
                {
                    // Vanilla reseeds per genstep so two maps with the same seed agree.
                    // Folding the band in keeps that property per band instead of giving
                    // every level of the stack an identical scatter (rule 19-adjacent: a
                    // shared seed is a shared answer).
                    Rand.PushState();
                    Rand.Seed = Gen.HashCombineInt(map.Tile.GetHashCode(),
                        Gen.HashCombineInt(def.index, band * 7919));
                    try
                    {
                        step.Generate(map, ranSteps[i].parms);
                    }
                    finally
                    {
                        Rand.PopState();
                    }
                    stepsRun++;
                }
                catch (Exception e)
                {
                    // One foreign scatterer throwing must not cost the band everything
                    // after it - the §98.b lesson, applied before it can bite again
                    // (rule 78: one try around N lookups makes N features one feature).
                    ABLog.Dev("Band dressing: genstep " + def.defName + " failed on band "
                        + band + " (ignored): " + e.Message);
                }
                finally
                {
                    ABBandScope.Pop();
                }
                thingsPlaced += Mathf.Max(0, map.listerThings.AllThings.Count - before);
            }
        }

        /// <summary>
        /// Vanilla's own initial plant pass, per band - the user's choice over raising our
        /// seeders' constants, and the only version that can be called parity: it is the same
        /// method, with the same density factor and the same whole-map demand that
        /// <c>GenStep_Plants</c> uses, so every mod plant, every biome plant list and every
        /// <c>wildTerrainTags</c> rule applies exactly as it does on the ground.
        ///
        /// ⚠ NO BAND SCOPE HERE, DELIBERATELY. We walk the rect ourselves; arming the scope
        /// would also redirect the spawner's INTERNAL map-wide reads (saturation is measured
        /// against the whole map's plant count) and quietly change the answer.
        ///
        /// ⚠ THE 0.001 SKIP IS COPIED FROM VANILLA, NOT INVENTED. GenStep_Plants leaves one
        /// cell in a thousand unconsidered; keeping it means a band and the ground differ by
        /// nothing at all.
        ///
        /// Composes with the existing seeders rather than replacing them:
        /// <c>CheckSpawnWildPlantAt</c> refuses any cell that already holds a plant, so
        /// ABCavernGen's characterful cave flora stays exactly as it was and this tops it up
        /// to biome density on the cells it left bare. The sky seeder stands down entirely
        /// (its alpine rim-thinning is the thing the user asked to drop) and registers itself
        /// as a fallback in case vanilla declines the whole band.
        /// </summary>
        private static void DressFlora(Map map, int band, CellRect rect, bool sky,
            float baseDensity, float wholeMapDesired)
        {
            float density = baseDensity;
            if (sky)
            {
                // The existing sky slider keeps its meaning: it now scales vanilla's density
                // factor instead of our own constant, so 1.0 is true parity with the ground
                // and the player's setting still moves it.
                density *= Mathf.Clamp(ABMod.Settings?.skyVegetationDensity ?? 1f, 0f, 2f);
            }
            if (density <= 0f)
            {
                return;
            }
            WildPlantSpawner spawner = map.wildPlantSpawner;
            TerrainDef air = ABDefOf.AB_OpenAir;
            TerrainGrid terrain = map.terrainGrid;
            int placed = 0;
            int airSkipped = 0;
            foreach (IntVec3 c in rect)
            {
                if (Rand.Chance(0.001f))
                {
                    continue;
                }
                // ⚠⚠ NOTHING COMES TO REST ON OPEN AIR - AND VANILLA CANNOT ENFORCE THAT.
                //
                // The seeders this pass replaced were safe by accident: both required
                // `c.Standable(map)`, and AB_OpenAir is Impassable. Handing the job to
                // vanilla dropped that guard, and vanilla has no equivalent.
                // `CheckSpawnWildPlantAt`'s fertility veto is skipped ENTIRELY whenever the
                // map holds any plant with `completelyIgnoreFertility`, and
                // `PlantUtility.CanEverPlantAt` then tests fertility, terrain TAGS and
                // blocking things - but never passability. So a zero-fertility impassable
                // void cell is not, in itself, something vanilla refuses to plant.
                //
                // ⚠ IT SURVIVES TODAY ONLY BY COINCIDENCE. Every vanilla ignore-fertility
                // plant also sets `wildPlantUseDistanceToShore`, whose shore weight rolls 0
                // away from water - so they self-reject. That is two unrelated flags
                // happening to line up, not a rule, and the first modded cave or alpine
                // plant that sets one without the other puts trees over the drop. Rule 14:
                // ask what is at the destination.
                //
                // ⚠ AND ABAirSpawnGuard CANNOT BACKSTOP THIS ONE. It stands down for the
                // whole of CarveInProgress, which is exactly when this pass runs - by
                // design, since otherwise it would walk our band content upstairs (§57).
                // This pass is the authority on its own bands, so it carries the invariant
                // itself.
                //
                // Deliberately NOT `!c.Standable(map)`: that would also refuse deep water,
                // and reeds in a band tarn are correct vanilla behaviour. The invariant is
                // about the VOID, so the test names the void.
                if (air != null && terrain.TerrainAt(c) == air)
                {
                    airSkipped++;
                    continue;
                }
                try
                {
                    if (spawner.CheckSpawnWildPlantAt(c, density, wholeMapDesired, true))
                    {
                        placed++;
                    }
                }
                catch (Exception e)
                {
                    ABLog.Dev("Band dressing: plant pass aborted on band " + band + " at " + c
                        + " (" + e.Message + ").");
                    break;
                }
            }
            plantsPlaced += placed;
            if (airSkipped > 0)
            {
                ABLog.Dev("Band dressing: band " + band + " plant pass skipped " + airSkipped
                    + " open-air cell(s), placed " + placed + ".");
            }

            if (placed == 0)
            {
                // Rule 33: a filter that can reject everything must say so - and here it must
                // also do something about it. A band whose biome offers no legal species (a
                // cave biome with no cave plants, an extreme sky biome) falls back to the
                // generator's own seeder rather than shipping bare ground.
                for (int i = 0; i < floraFallbacks.Count; i++)
                {
                    if (floraFallbacks[i].Key != band)
                    {
                        continue;
                    }
                    ABLog.Dev("Band dressing: vanilla plant pass placed nothing on band "
                        + band + "; falling back to the band generator's own seeder.");
                    try
                    {
                        floraFallbacks[i].Value();
                    }
                    catch (Exception e)
                    {
                        ABLog.Dev("Band dressing: flora fallback for band " + band
                            + " failed: " + e.Message);
                    }
                }
            }
        }

        // -------------------------------------------------------------------
        // Selection
        // -------------------------------------------------------------------

        /// <summary>Type-name fragments, matched against the genStep's runtime type. Matching
        /// on NAME rather than on a type list is what lets a mod's own subclass of a blocked
        /// family stay blocked - the alternative is an <c>is</c> chain that a mod can walk
        /// straight out of by deriving one level deeper.</summary>
        private static readonly string[] BlockedTypeFragments =
        {
            "Ruin", "Shrine", "Monolith", "Cluster", "Quest", "Sleeping", "Danger",
            "LumpsMineable", "Wreck", "Junk", "Ancient", "Prefab", "Structure", "Settlement"
        };

        private static readonly string[] BlockedDefFragments =
        {
            "Ruin", "Shrine", "Monolith", "Ancient", "Wreck", "Junk", "Mech", "Cluster",
            "Anima", "Polux", "Exostrider", "Tunneler", "Tuneller", "Crash", "Ship",
            "Vehicle", "Quarry", "Lumps", "Ore"
        };

        /// <summary>
        /// ⚠ THE ORDER OF THESE CLAUSES IS THE POLICY, so it is worth reading top to bottom:
        /// capability first (what vanilla itself says this step is), then shape, then names.
        /// Names are last because they are the least reliable and the most likely to
        /// mis-hit - they exist only to catch mods that set none of the flags.
        /// </summary>
        private static bool Eligible(GenStepDef def, GenStep step)
        {
            if (def == null || step == null)
            {
                return false;
            }
            // The cell-walking chunk generator. Not a Scatterer, and the single most
            // important step in the whole pass: it is both vanilla's loose rock AND
            // ReGrowth 2's boulders (their transpiler lives inside it).
            if (step is GenStep_RockChunks)
            {
                return true;
            }
            GenStep_Scatterer sc = step as GenStep_Scatterer;
            if (sc == null)
            {
                return false;
            }
            if (sc.isJunk)
            {
                return false; // ancient wreckage: ground level only, by the user's rule
            }
            if (sc.nearPlayerStart || sc.nearMapCenter)
            {
                return false; // colony-anchored, and neither place exists off the surface
            }
            string type = step.GetType().Name;
            for (int i = 0; i < BlockedTypeFragments.Length; i++)
            {
                if (type.IndexOf(BlockedTypeFragments[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            string name = def.defName ?? string.Empty;
            for (int i = 0; i < BlockedDefFragments.Length; i++)
            {
                if (name.IndexOf(BlockedDefFragments[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// ⚠⚠ THE GROUND LEVEL'S OWN SHARE, which is the half of the report nobody filed.
    ///
    /// Confines the ORIGINAL generation-time scatter pass to the surface band on a banded map.
    /// Two effects, and the first is a bug fix rather than an optimisation:
    ///
    ///   * THE COUNT BECOMES RIGHT. <c>CountFromPer10kCells</c> issues a count derived from
    ///     <c>Size.x</c> squared - already exactly one band's worth - and vanilla was spraying
    ///     it over seven bands, six of which the carve then erased. The ground level was
    ///     getting a seventh of its geysers, chunks and vents. Confining the pass hands it the
    ///     whole count it was always being issued.
    ///   * THE WASTE DISAPPEARS. Everything this pass used to place outside the surface band
    ///     was carve fodder, and every one of those spawns was paid for at generation prices.
    ///     This is the same argument <c>Patch_WildPlantSpawner_ABSkipDoomedBands</c> makes for
    ///     plants, arriving four windows later for scatterers.
    ///
    /// ⚠ SUBCLASSES THAT OVERRIDE <c>Generate</c> WITHOUT CALLING BASE ARE NOT COVERED, and
    /// that is a deliberate non-fix. They keep vanilla's map-wide behaviour, which is what
    /// they had before this patch existed - so the failure mode is "no improvement", never
    /// "new breakage". Rule 6: a vanilla helper can be a no-op, and so can a patch site.
    ///
    /// ⚠ IT MUST NOT ARM INSIDE THE DRESSING PASS. Dressing runs these same gensteps under a
    /// band scope; pushing a surface scope on top would be a nested scope, and ABBandScope
    /// treats that as a hard error precisely so this cannot happen quietly.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GenStep_ABSurfaceBandScope
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GenStep_Scatterer),
                nameof(GenStep_Scatterer.Generate));
            yield return AccessTools.Method(typeof(GenStep_RockChunks),
                nameof(GenStep_RockChunks.Generate));
        }

        private static void Prefix(Map map, out bool __state)
        {
            __state = false;
            // Every scatterer Generate passes through here - vanilla's own pass AND the
            // dressing pass's re-runs - which makes it the one place that can give the
            // exhaustive cell sweep a per-CALL budget instead of a per-instance one.
            Patch_GenStep_Scatterer_ABBandScopeCell.ResetSweepBudget();
            try
            {
                if (ABBandScope.Active || map == null)
                {
                    return; // dressing pass in flight, or nothing to scope
                }
                if (!(ABMod.Settings?.bandFeatures ?? true))
                {
                    return;
                }
                if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out _))
                {
                    return; // ordinary unbanded generation
                }
                ABBandScope.Push(map, surface, false);
                __state = true;
            }
            catch
            {
                __state = false;
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                ABBandScope.Pop();
            }
        }
    }
}
