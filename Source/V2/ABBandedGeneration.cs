using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>V2 master switch. Lives here (not in ABSettings) so the V2 branch never
    /// perturbs V1's 983-line settings model while both coexist.</summary>
    public static class ABV2
    {
        /// <summary>When on, newly generated player colony maps are banded.</summary>
        public static bool Enabled = true;

        /// <summary>Bands per column, from the player's level plan. 3 (one below, one
        /// above) is the default; 1 means an ordinary unbanded map. Was a const until the
        /// level plan made it a per-colony choice - every consumer already looped on
        /// `bands.bandCount`, so this const was the only thing pinning it to 3.</summary>
        public static int BandCount => ABMapSizeLimit.BandCount;

        /// <summary>Index of the surface band = however many levels sit below it.</summary>
        public static int SurfaceBand => ABMapSizeLimit.SurfaceBand;
    }

    /// <summary>
    /// V2 - creating a banded map.
    ///
    /// Two hooks on the single MapGenerator.GenerateMap entry point:
    ///  - PREFIX inflates mapSize.z from h to bandCount * (h + Gutter) and records the
    ///    intended layout. The caller's own IntVec3 is untouched (it is passed by value
    ///    from Game.InitNewGame), so World.info.initialMapSize stays the SURFACE size -
    ///    which matters, because every other map in the game is sized from it.
    ///  - POSTFIX runs after every GenStep and carves the non-surface bands: vanilla has
    ///    by then generated ordinary content across the whole tall map, and we overwrite
    ///    everything outside the surface band with rock (below) and open air (above).
    ///
    /// Why carve after rather than constrain vanilla during: vanilla GenSteps are not
    /// rect-scoped and there are dozens of them (plus modded ones). Letting them run and
    /// then overwriting is O(cells) once at generation, and is robust against any GenStep
    /// we have never heard of. The cost is a one-off ~3x generation time.
    ///
    /// KNOWN LIMITATION (documented, not hidden): the surface band is a horizontal slice
    /// of a 3x-tall generated map, so tile features anchored to the MAP EDGE - coastlines
    /// above all, and to a lesser degree rivers and roads - can land in a carved band and
    /// be lost. Continuous noise (elevation, fertility, rock) slices correctly and looks
    /// normal. The real fix is Stage 4 transplant (generate a normal map, move it into the
    /// band), which is also the save-migration path.
    /// </summary>
    public static class ABBandedGeneration
    {
        private sealed class PendingLayout
        {
            public int bandCount;
            public int bandHeight;
            public int surfaceBand;
        }

        private static PendingLayout pending;

        /// <summary>The surface band's rect for a map that is CURRENTLY being generated.
        ///
        /// Needed because ABBandMap.Setup only runs in the GenerateMap postfix, so for the
        /// whole duration of map generation <c>bands.Banded</c> is still false and every
        /// band helper answers as if the map were ordinary. Anything that has to be
        /// band-correct DURING generation has to read the pending layout instead.
        /// </summary>
        internal static bool TryPendingSurfaceRect(Map map, out CellRect surface, out int slot)
        {
            surface = default(CellRect);
            slot = 0;
            if (map == null)
            {
                return false;
            }
            PendingLayout p = pending;
            if (p == null)
            {
                // No real generation in flight. It may still be a MAP PREVIEW: Map Preview
                // builds its own bare Map and runs gensteps on it without ever calling
                // MapGenerator.GenerateMap, so no layout was ever recorded - yet the preview
                // must reproduce the real map exactly, which means every generation-time
                // patch that consults this has to answer the same way there.
                //
                // Inferred from the SIZE, which is safe because we chose that size ourselves
                // (MapPreviewCompat.Stacked). An ordinary square map can never match: Slot is
                // at least bandHeight + 2, so bandCount * Slot always exceeds the width once
                // more than one band exists.
                return TryInferredSurfaceRect(map, out surface, out slot);
            }
            slot = ABBandMap.SlotFor(p.bandHeight);
            surface = new CellRect(0, p.surfaceBand * slot, map.Size.x, p.bandHeight);
            return true;
        }

        /// <summary>Recover the band layout from a map's dimensions alone - see the preview
        /// note in TryPendingSurfaceRect. Requires the current level plan to be the one the
        /// size was built from, which for a preview is true by construction.</summary>
        private static bool TryInferredSurfaceRect(Map map, out CellRect surface, out int slot)
        {
            surface = default(CellRect);
            slot = 0;
            int bands = ABV2.BandCount;
            if (!ABV2.Enabled || bands <= 1)
            {
                return false;
            }
            int bandHeight = map.Size.x;
            int s = ABBandMap.SlotFor(bandHeight);
            if (s <= 0 || bands * s != map.Size.z)
            {
                return false;
            }
            slot = s;
            surface = new CellRect(0, ABV2.SurfaceBand * s, map.Size.x, bandHeight);
            return true;
        }

        private static bool ShouldBand(MapParent parent, bool isPocketMap)
        {
            if (!ABV2.Enabled || isPocketMap || parent == null)
            {
                return false;
            }
            // Only the player's own colony maps. Raid-target maps, caravan ambushes and
            // every pocket map stay ordinary - banding those would triple their cost for
            // no benefit and would drag the whole world into V2 semantics.
            return parent is Settlement s && s.Faction != null && s.Faction.IsPlayer;
        }

        [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateMap))]
        public static class Patch_MapGenerator_GenerateMap
        {
            private static void Prefix(ref IntVec3 mapSize, MapParent parent, bool isPocketMap)
            {
                pending = null;
                try
                {
                    if (!ShouldBand(parent, isPocketMap))
                    {
                        return;
                    }
                    // Cap the BAND size before inflating. Enforced here as well as in the
                    // chooser so a scenario, another mod or an old config cannot slip a
                    // 325-wide colony past it - that would be ~317k cells through the
                    // pathfinding grid job every request.
                    int capped = ABMapSizeLimit.Clamp(mapSize.z);
                    int cappedX = ABMapSizeLimit.Clamp(mapSize.x);
                    if (capped != mapSize.z || cappedX != mapSize.x)
                    {
                        ABLog.Dev("V2: clamped colony map from " + mapSize.x + "x" + mapSize.z
                            + " to " + cappedX + "x" + capped + " (unclamp in mod settings).");
                        mapSize = new IntVec3(cappedX, mapSize.y, capped);
                    }
                    int bandCount = ABV2.BandCount;
                    if (bandCount <= 1)
                    {
                        // The player asked for no levels above or below: an ordinary map.
                        // Banding a single band would leave Banded false anyway, but the
                        // gutter carve and the z inflation would still run.
                        ABLog.Dev("V2: level plan is a single level - generating an ordinary map.");
                        return;
                    }
                    int h = mapSize.z;
                    pending = new PendingLayout
                    {
                        bandCount = bandCount,
                        bandHeight = h,
                        surfaceBand = ABV2.SurfaceBand
                    };
                    int slot = ABBandMap.SlotFor(h);
                    mapSize = new IntVec3(mapSize.x, mapSize.y, bandCount * slot);
                    ABLog.Dev("V2: banding new colony map -> " + mapSize + " (" + bandCount
                        + " bands of " + h + " + " + (slot - h) + " gutter, slot " + slot
                        + ", surface band " + ABV2.SurfaceBand + ").");
                }
                catch (Exception e)
                {
                    pending = null;
                    Log.Error(ABLog.Tag + " V2: band size inflation failed, generating an ordinary map: " + e);
                }
            }

            private static void Postfix(Map __result)
            {
                PendingLayout p = pending;
                pending = null;
                if (p == null || __result == null)
                {
                    return;
                }
                try
                {
                    ABBandMap bands = __result.GetComponent<ABBandMap>();
                    if (bands == null)
                    {
                        Log.Error(ABLog.Tag + " V2: ABBandMap component missing on a banded map.");
                        return;
                    }
                    // The heavy work (Setup + Rescue + Carve) has already happened in the
                    // GenerateContentsIntoMap postfix below, INSIDE the generation window.
                    // This safety net only fires if that patch somehow did not run.
                    if (!carved)
                    {
                        Log.Warning(ABLog.Tag + " V2: in-window carve did not run; carving"
                            + " post-init (slow path).");
                        bands.Setup(p.bandCount, p.bandHeight, p.surfaceBand);
                        bands.SnapshotClimate(ABMod.Settings);
                        var slow = System.Diagnostics.Stopwatch.StartNew();
                        RescueStrandedColonists(__result, bands);
                        Carve(__result, bands);
                        carveMs = slow.Elapsed.TotalMilliseconds;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    FixPlayerStartSpot(__result, bands);
                    ABGenProfile.Report(__result, carveMs, watch.Elapsed.TotalMilliseconds);
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: band carve failed: " + e);
                }
                finally
                {
                    carved = false;
                    carveMs = 0;
                }
            }
        }

        private static bool carved;

        private static double carveMs;

        /// <summary>
        /// Carves INSIDE the generation window - the single biggest map-gen optimization
        /// this mod has, found by measurement, not theory.
        ///
        /// The first profile of a banded generation read:
        ///     all 41 vanilla gensteps:  1,304.7 ms
        ///     our band carve:           3,075.8 ms   <-- 2.4x everything vanilla does
        ///     Map.FinalizeInit:           164.2 ms
        ///
        /// The carve used to run in the GenerateMap POSTFIX - i.e. after Map.FinalizeInit
        /// had brought the map fully alive. At that point every one of the carve's ~70k
        /// GenSpawn.Spawn / Destroy / SetTerrain operations pays live-map bookkeeping:
        /// region dirtying, mesh sections, lister updates, incremental path costs. The
        /// profile's tell was vanilla's own RocksFromGrid: it spawns a comparable volume of
        /// rock across the whole map in 78 ms, because gensteps run with all of that
        /// DEFERRED. Same work, ~40x the price, purely for running after init.
        ///
        /// This postfix runs after every genstep (vanilla AND modded - so the
        /// carve-last semantics are unchanged, and ScenParts has already spawned the
        /// colonists for RescueStrandedColonists) but BEFORE Scenario.PostMapGenerate and
        /// Map.FinalizeInit. Two knock-on wins, both free:
        ///   - regions/rooms/path costs are built ONCE, on the already-carved map, instead
        ///     of built for vanilla's full map and then re-dirtied wholesale;
        ///   - ABBandMap.FinalizeInit now sees Banded == true on FRESH generation, exactly
        ///     as it does on load - previously Setup ran after Map.FinalizeInit, so every
        ///     Banded-gated hook in it silently no-opped for a brand-new colony.
        /// </summary>
        [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateContentsIntoMap))]
        public static class Patch_GenerateContents_ABCarveInWindow
        {
            private static void Postfix(Map map)
            {
                PendingLayout p = pending;
                if (p == null || map == null || carved)
                {
                    return;
                }
                try
                {
                    ABBandMap bands = map.GetComponent<ABBandMap>();
                    if (bands == null)
                    {
                        return; // GenerateMap postfix will complain and slow-path it
                    }
                    // Stop the genstep profiler first so the carve's own DeepProfiler
                    // traffic (from GenSpawn internals) cannot pollute the genstep table.
                    ABGenProfile.Disarm();
                    bands.Setup(p.bandCount, p.bandHeight, p.surfaceBand);
                    // Freeze the climate onto the colony at the same moment as its shape:
                    // read live from settings instead and moving a slider would re-climate
                    // every existing save.
                    bands.SnapshotClimate(ABMod.Settings);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    RescueStrandedColonists(map, bands);
                    Carve(map, bands);
                    carveMs = watch.Elapsed.TotalMilliseconds;
                    carved = true;
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: in-window band carve failed: " + e);
                }
            }
        }

        /// <summary>
        /// THE ROOT-CAUSE FIX for "colonists sometimes don't spawn".
        ///
        /// The old code corrected MapGenerator.PlayerStartSpot in the GenerateMap POSTFIX,
        /// on the stated assumption that "scenario pawns spawn AFTER generation". That is
        /// wrong. ScenPart_PlayerPawnsArriveMethod spawns them from GenerateIntoMap, which
        /// is driven by the ScenParts GenStep - and the vanilla genstep order is
        /// FindPlayerStartSpot (40) then ScenParts (41), both well inside generation. So
        /// the real sequence was:
        ///
        ///   1. FindPlayerStartSpot picks a cell anywhere in the WHOLE inflated map.
        ///   2. ScenParts immediately drops the colonists on it.
        ///   3. our postfix carves the non-surface bands, and ClearCellHard / FillRock
        ///      call Destroy(DestroyMode.Vanish) on everything standing there.
        ///   4. our postfix then moved the start spot - long after the pawns were gone.
        ///
        /// Hence the intermittency: CellFinderLoose.TryFindCentralCell starts at the map
        /// centre, which for a 3-band map with surfaceBand 1 happens to land INSIDE the
        /// surface band most of the time. It is only when the central cells fail the
        /// validator and the search wanders into the gutter or another band that the
        /// colony is silently deleted. No error is logged, because from vanilla's point of
        /// view nothing went wrong.
        ///
        /// Clamping here - after vanilla has chosen, before anything consumes the choice -
        /// also fixes every other in-generation consumer of the spot for free: GenStep_Fog
        /// unfogs around it, and GenStep_Scatterer falls back to it.
        /// </summary>
        [HarmonyPatch(typeof(GenStep_FindPlayerStartSpot), nameof(GenStep_FindPlayerStartSpot.Generate))]
        public static class Patch_GenStep_FindPlayerStartSpot_ABSurfaceBand
        {
            /// <summary>Keep the spot this far from the band's z edges. DropCellFinder
            /// scatters pods well away from the requested centre, so a spot that merely
            /// clears the band boundary can still throw a pod across the gutter into the
            /// next band - where carving would destroy it.</summary>
            private const int PodScatterMargin = 24;

            private static void Postfix(Map map)
            {
                try
                {
                    if (!TryPendingSurfaceRect(map, out CellRect surface, out int slot))
                    {
                        return; // not a banded generation
                    }
                    IntVec3 spot = MapGenerator.PlayerStartSpotValid
                        ? MapGenerator.PlayerStartSpot
                        : IntVec3.Invalid;

                    CellRect safe = new CellRect(surface.minX,
                        surface.minZ + PodScatterMargin, surface.Width,
                        Mathf.Max(1, surface.Height - 2 * PodScatterMargin));

                    if (spot.IsValid && safe.Contains(spot) && spot.Standable(map))
                    {
                        return; // vanilla's pick was already fine
                    }

                    // Translate vanilla's own pick into the surface band rather than jumping
                    // to the centre: it chose that spot for terrain reasons that still hold,
                    // and band centres are very often mountain or lake.
                    //
                    // The band stride is SLOT (band height PLUS gutter), not the band
                    // height - taking the modulo by height instead silently skews the
                    // in-band offset by a growing multiple of the gutter.
                    IntVec3 seed;
                    if (spot.IsValid && slot > 0)
                    {
                        int withinSlot = ((spot.z % slot) + slot) % slot;
                        seed = new IntVec3(spot.x, 0,
                            surface.minZ + Mathf.Clamp(withinSlot, 0, surface.Height - 1));
                    }
                    else
                    {
                        seed = safe.CenterCell;
                    }
                    if (!safe.Contains(seed))
                    {
                        // Keep the column, pull the row into the safe strip.
                        seed = new IntVec3(
                            Mathf.Clamp(seed.x, safe.minX, safe.maxX), 0,
                            Mathf.Clamp(seed.z, safe.minZ, safe.maxZ));
                    }

                    IntVec3 found;
                    if (TryFindStartCell(map, safe, seed, requireApron: true, out IntVec3 strict))
                    {
                        found = strict;
                    }
                    else if (TryFindStartCell(map, safe, seed, requireApron: false, out IntVec3 relaxed))
                    {
                        found = relaxed;
                    }
                    else if (TryFindStartCell(map, surface, seed, requireApron: false, out IntVec3 wide))
                    {
                        // The margin is a preference, not a requirement - better a start
                        // spot near the band edge than one inside rock.
                        found = wide;
                    }
                    else
                    {
                        // The full-band sweep proved the surface generated as SOLID ROCK.
                        // Vanilla's elevation noise has no edge falloff and our surface band
                        // is a 190-row window sliced from the middle of a much taller field,
                        // so on mountainous tiles the entire window can land above the rock
                        // threshold - no valley anywhere. (Recurred as run #115 and #121;
                        // the census read "100% blocked by an edifice, 0 water" both times.)
                        //
                        // Leaving the fallback in rock produced an unplayable colony: the
                        // start spot sat inside a mountain, GenStep_Fog's flood-unfog could
                        // not spread, and the whole surface stayed black. Carving a clearing
                        // turns the same tile into a legitimate mountain-base start instead.
                        // Runs in the generation window, on an uncrowded map, so the ~1,800
                        // rock removals are cheap; GenStep_Fog (order 48) then unfogs the
                        // clearing naturally because the start spot is genuinely open.
                        found = safe.CenterCell;
                        CarveStartClearing(map, surface, found);
                        Log.Warning(ABLog.Tag + " V2: surface band generated as solid rock;"
                            + " carved a starting clearing at " + found
                            + " (mountain-base start)." + lastSearchCensus);
                    }

                    MapGenerator.PlayerStartSpot = found;
                    ABLog.Dev("V2: start spot clamped into the surface band at " + found
                        + " (was " + (spot.IsValid ? spot.ToString() : "invalid")
                        + ", surface " + surface + ") before ScenParts spawns the colony.");
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: start-spot clamp failed: " + e);
                }
            }
        }

        /// <summary>
        /// Stops the initial plant pass from planting the bands the carve will erase.
        ///
        /// Found by the generation profiler, in two steps. First profile: the carve cost
        /// 2.4x all vanilla gensteps combined. Second and third profiles (after cheaper
        /// fixes): per-operation cost stuck at ~0.19 ms across ~77k spawn/destroy ops with
        /// our own patches suspended - so the cost is the ENGINE's, and it scales with how
        /// crowded the map is (ListerThings removal is a linear List.Remove over lists that
        /// hold ~100k things on a lush 3-band map; every destroy pays it).
        ///
        /// GenStep_Plants is the main crowd-maker: 2.7-3.3 s planting all three bands on a
        /// lush tile, two-thirds of it content the carve immediately destroys - each destroy
        /// paying that linear removal. Skipping the doomed bands is SAFE here specifically
        /// because CheckSpawnWildPlantAt is a PER-CELL probabilistic roll: there is no
        /// fixed count being redistributed, so surface density is untouched. (Contrast
        /// scatterer gensteps, whose counts derive from map.Area - scoping those without
        /// scaling the count would concentrate 3x the things into one band. Deliberately
        /// not attempted.)
        ///
        /// Gated on the pending layout, so it costs one null check outside banded
        /// generation and never fires in normal play - regrowth on the sky band stays
        /// exactly as the per-band biome system provides.
        /// </summary>
        [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt))]
        public static class Patch_WildPlantSpawner_ABSkipDoomedBands
        {
            private static bool Prefix(Map ___map, IntVec3 c, ref bool __result)
            {
                if (pending == null)
                {
                    return true; // not generating a banded map - normal play path
                }
                if (!TryPendingSurfaceRect(___map, out CellRect surface, out _)
                    || surface.Contains(c))
                {
                    return true;
                }
                __result = false;
                return false; // doomed band: this plant would only ever be carve fodder
            }
        }

        // -------------------------------------------------------------------
        // Carving
        // -------------------------------------------------------------------

        private static void Carve(Map map, ABBandMap bands)
        {
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile).ToList();
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }
            List<Perlin> noises = ABRockGen.MakeNoises(rocks.Count);

            // Per-op sky sync is pure waste during a bulk carve - ABSkyBandGen derives the
            // sky terrain from final state right after. See ABSkySync.Suspended.
            ABSkySync.Suspended = true;
            try
            {
                CarveInner(map, bands, rocks, noises);
            }
            finally
            {
                ABSkySync.Suspended = false;
            }
        }

        private static void CarveInner(Map map, ABBandMap bands, List<ThingDef> rocks, List<Perlin> noises)
        {
            for (int band = 0; band < bands.bandCount; band++)
            {
                if (band == bands.surfaceBand)
                {
                    continue;
                }
                CellRect rect = bands.RectOfBand(band);
                var phase = System.Diagnostics.Stopwatch.StartNew();
                if (band < bands.surfaceBand)
                {
                    // depth 1 = the level immediately below the surface. Ore richness and
                    // cave openness both scale with it.
                    FillRock(map, rect, rocks, noises, bands.surfaceBand - band);
                    ABGenProfile.Phase("FillRock band " + band, phase.Elapsed.TotalMilliseconds);
                    phase.Restart();
                    // Then optionally hollow it back out into a living cave system.
                    // Runs on the filled rock deliberately: the carve reads and destroys
                    // the rock it opens, and the untouched remainder becomes the walls.
                    ABCavernGen.Generate(map, bands, band);
                    ABGenProfile.Phase("CavernGen band " + band, phase.Elapsed.TotalMilliseconds);
                }
                else
                {
                    // Clear first, then let the sky generator lay a real mountain over it.
                    //
                    // UNFOG as we go. V1's sky is a pocket map whose generator def has no
                    // GenStep_Fog at all, so it is born unfogged. A V2 banded map is built
                    // by the ordinary player-settlement generator, which fogs EVERY cell -
                    // including the sky band - leaving the whole level black behind vanilla
                    // fog of war (run #16). The sky is meant to be seen; only the deep rock
                    // interior gets re-fogged, which ABSkyBandGen does after it classifies.
                    foreach (IntVec3 c in rect)
                    {
                        if (c.InBounds(map))
                        {
                            ClearCellHard(map, c);
                            map.fogGrid.Unfog(c);
                        }
                    }
                    ABGenProfile.Phase("Sky clear band " + band, phase.Elapsed.TotalMilliseconds);
                    phase.Restart();
                    ABSkyBandGen.Generate(map, bands, band, rocks, noises);
                    ABGenProfile.Phase("SkyBandGen band " + band, phase.Elapsed.TotalMilliseconds);
                }
            }
            var tail = System.Diagnostics.Stopwatch.StartNew();
            CarveGutters(map, bands);
            ABGenProfile.Phase("CarveGutters", tail.Elapsed.TotalMilliseconds);
            tail.Restart();

            // AFTER the gutters are carved, deliberately: seeding walks the band rects and
            // the gutter rows are only turned into (non-snow-holding) open air above.
            ABBandWeather.SeedAltitudeSnow(map, bands);
            ABGenProfile.Phase("SeedAltitudeSnow", tail.Elapsed.TotalMilliseconds);
            tail.Restart();

            // Fog policy differs by direction, matching V1:
            //  - BELOW the surface is solid rock, so it is fogged and revealed by mining,
            //    exactly like a vanilla mountain.
            //  - ABOVE the surface is open sky and mountain top. V1 fogs only the deep
            //    rock interior and leaves the rest visible, because the whole point of the
            //    sky level is seeing the colony from above. Blanket-fogging it (run #5)
            //    produced a black screen with a single lit stair landing.
            for (int band = 0; band < bands.surfaceBand; band++)
            {
                map.fogGrid.Refog(bands.RectOfBand(band));
            }
            ABGenProfile.Phase("Refog", tail.Elapsed.TotalMilliseconds);
        }

        // The generation-time SNOW SEEDING that used to live here is deliberately gone.
        //
        // It blanketed level +2 and above in snow the moment the map was made, whenever the
        // level's effective temperature was below -2 C. As a design that reads badly: the
        // player buys three upper levels and the top one arrives pre-frozen, which makes the
        // highest and most expensive level look like a different biome rather than a higher
        // part of the same mountain.
        //
        // The real snow line is NOT lost with it, and that is why removing this is safe:
        // vanilla melts and accumulates snow using each CELL's own temperature, and
        // ABBandEnv's per-level temperature offset already makes high levels cold. So high
        // ground still collects snow from actual snowfall and holds it after the surface has
        // thawed, and the line still moves with the seasons - it is just earned rather than
        // painted on at t=0.

        private static void FillRock(Map map, CellRect rect, List<ThingDef> rocks,
            List<Perlin> noises, int depth)
        {
            TerrainGrid terrain = map.terrainGrid;
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                // KEEP vanilla's rock when it is exactly the rock we would spawn.
                // RocksFromGrid has already filled the mountainous share of this band, so a
                // destroy+respawn pair here is pure waste whenever the def matches - and the
                // phase profile priced that waste at ~0.25 ms per operation. Def-match only:
                // where vanilla placed a DIFFERENT rock than our vein noise picks, we still
                // swap it, so the basement's rock distribution is bit-identical to before.
                Building existing = c.GetEdifice(map);
                bool keep = existing != null && existing.def == rock
                    && existing.def.building != null && existing.def.building.isNaturalRock;
                if (keep)
                {
                    ClearCellExcept(map, c, existing);
                }
                else
                {
                    ClearCellHard(map, c);
                }
                terrain.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                if (!keep)
                {
                    GenSpawn.Spawn(rock, c, map);
                    ABGenProfile.rocksSpawned++;
                }
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            }
            // Richer the deeper you dig: the reward for driving a shaft down three levels
            // rather than one. Applied AFTER the settings clamp so the player's density is
            // the level-1 baseline rather than a ceiling.
            float density = Mathf.Clamp(ABMod.Settings?.basementOreDensity ?? 6f, 0f, 12f)
                * (1f + 0.45f * (Mathf.Max(depth, 1) - 1));
            ABOreGen.ScatterOres(map, rect.Cells.ToList(), Mathf.Min(density, 30f));
        }

        /// <summary>The seam rows. Impassable open air, permanently fogged, no roof - so
        /// no region, room or temperature zone can ever span two bands implicitly.</summary>
        private static void CarveGutters(Map map, ABBandMap bands)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            for (int band = 0; band < bands.bandCount; band++)
            {
                int gutterStartZ = band * bands.Slot + bands.bandHeight;
                int gutterEndZ = (band + 1) * bands.Slot;
                for (int z = gutterStartZ; z < gutterEndZ; z++)
                {
                    if (z >= map.Size.z)
                    {
                        break;
                    }
                    for (int x = 0; x < map.Size.x; x++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        ClearCellHard(map, c);
                        map.terrainGrid.SetTerrain(c, air);
                        map.roofGrid.SetRoof(c, null);
                    }
                }
            }
        }

        /// <summary>ClearCellHard, minus one thing worth keeping.</summary>
        private static void ClearCellExcept(Map map, IntVec3 c, Thing keep)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing t = things[i];
                if (t == null || t == keep || t.Destroyed)
                {
                    continue;
                }
                if (!t.def.destroyable)
                {
                    if (t.Spawned)
                    {
                        t.DeSpawn(DestroyMode.Vanish);
                        ABGenProfile.thingsDestroyed++;
                        ABGenProfile.NoteDestroyed(t.def);
                    }
                    continue;
                }
                ABGenProfile.NoteDestroyed(t.def);
                t.Destroy(DestroyMode.Vanish);
                ABGenProfile.thingsDestroyed++;
            }
        }

        /// <summary>Removes everything from a cell, pawns included. Generation-time only.</summary>
        private static void ClearCellHard(Map map, IntVec3 c)
        {
            List<Thing> things = c.GetThingList(map);
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed)
                {
                    continue;
                }
                // Steam geysers (and anything else with destroyable=false) refuse
                // Destroy() and log "Tried to destroy non-destroyable thing" - and worse,
                // they SURVIVE, so the band fill would then spawn rock on top of them.
                // DeSpawn removes them cleanly.
                if (!t.def.destroyable)
                {
                    if (t.Spawned)
                    {
                        t.DeSpawn(DestroyMode.Vanish);
                        ABGenProfile.thingsDestroyed++;
                        ABGenProfile.NoteDestroyed(t.def);
                    }
                    continue;
                }
                ABGenProfile.NoteDestroyed(t.def);
                t.Destroy(DestroyMode.Vanish);
                ABGenProfile.thingsDestroyed++;
            }
        }

        /// <summary>
        /// Last-resort rescue for anything of the player's that ended up outside the
        /// surface band before carving destroys it.
        ///
        /// This is the safety net for the "colonists sometimes don't spawn" bug. The root
        /// cause is fixed upstream (see Patch_GenStep_FindPlayerStartSpot_ABSurfaceBand),
        /// but the drop-pod finder scatters pods up to ~30 cells from the start spot, so a
        /// start spot legitimately inside the surface band can still throw a pod across the
        /// gutter into the band above or below. Carve then runs ClearCellHard / FillRock
        /// over those bands, which calls Destroy(DestroyMode.Vanish) - and a starting
        /// colonist quietly ceases to exist, with no error and no missing-pawn warning.
        ///
        /// Moving rather than destroying is the whole point: the pawn is already fully
        /// generated with relations, possessions and a scenario role, so losing one is not
        /// recoverable later. Also covers gravship starts and any modded ScenPart that
        /// spawns its own pawns during generation.
        /// </summary>
        private static void RescueStrandedColonists(Map map, ABBandMap bands)
        {
            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            List<Pawn> stranded = null;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == null || !p.Spawned)
                {
                    continue;
                }
                // Player pawns and anything they brought along (tamed animals included).
                if (p.Faction == null || !p.Faction.IsPlayer)
                {
                    continue;
                }
                if (surface.Contains(p.Position))
                {
                    continue;
                }
                (stranded ?? (stranded = new List<Pawn>())).Add(p);
            }
            if (stranded == null)
            {
                return;
            }

            // Aim at the band-local equivalent column so the rescued group stays together
            // and near whatever terrain the generator picked for them.
            for (int i = 0; i < stranded.Count; i++)
            {
                Pawn p = stranded[i];
                IntVec3 target = bands.Translate(p.Position, bands.surfaceBand);
                if (!target.InBounds(map) || !surface.Contains(target))
                {
                    target = surface.CenterCell;
                }
                if (!TryFindStartCell(map, surface, target, requireApron: false, out IntVec3 landing))
                {
                    landing = target;
                }
                p.Position = landing;
                p.Notify_Teleported(false, false);
            }
            // Warning, not Dev: this firing means the upstream clamp let something through,
            // and it is the only trace that would otherwise exist.
            Log.Warning(ABLog.Tag + " V2: rescued " + stranded.Count + " player pawn(s) that"
                + " generated outside the surface band; they would have been destroyed by"
                + " band carving. Start spot was " + (MapGenerator.PlayerStartSpotValid
                    ? MapGenerator.PlayerStartSpot.ToString() : "invalid") + ".");
        }

        /// <summary>Post-generation correction of the start spot, kept as a safety net now
        /// that the spot is clamped before ScenParts runs. Still load-bearing for consumers
        /// that read it AFTER generation - Game.InitNewGame jumps the camera to it.</summary>
        private static void FixPlayerStartSpot(Map map, ABBandMap bands)
        {
            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            IntVec3 spot = MapGenerator.PlayerStartSpotValid ? MapGenerator.PlayerStartSpot : IntVec3.Invalid;
            if (spot.IsValid && surface.Contains(spot) && spot.Standable(map))
            {
                return;
            }
            // Translate vanilla's own choice into the surface band rather than jumping to
            // the band centre: it picked that COLUMN for terrain reasons that still hold,
            // and the centre of the band is very often inside a mountain or a lake.
            IntVec3 seed = spot.IsValid ? bands.Translate(spot, bands.surfaceBand) : surface.CenterCell;
            if (!seed.InBounds(map) || !surface.Contains(seed))
            {
                seed = surface.CenterCell;
            }
            // Two passes. The strict one wants a clear 5x5 apron so pods and pawns fit; if
            // the surface band has no such spot (heavy forest, lakes, dense rock) the
            // relaxed pass takes any standable dry cell. Falling straight through to the
            // seed was the cause of BUG1 - colonists occasionally not spawning at all,
            // because the seed could be rock or water and the scenario spawn silently failed.
            IntVec3 found;
            if (TryFindStartCell(map, surface, seed, requireApron: true, out IntVec3 strict))
            {
                found = strict;
            }
            else if (TryFindStartCell(map, surface, seed, requireApron: false, out IntVec3 relaxed))
            {
                ABLog.Dev("V2: no clear apron in the surface band; using a relaxed start cell.");
                found = relaxed;
            }
            else
            {
                found = seed;
                Log.Warning(ABLog.Tag + " V2: could not find any standable start cell in the"
                    + " surface band; falling back to " + seed + ". Colonists may fail to spawn."
                    + lastSearchCensus);
            }
            MapGenerator.PlayerStartSpot = found;
            ABLog.Dev("V2: player start spot moved into the surface band at " + found + ".");
        }

        /// <summary>Finds somewhere the starting colony can actually land: standable, dry,
        /// unobstructed, and with a clear apron around it so drop pods and pawns fit.
        ///
        /// Deliberately does NOT test Fogged - by this point GenStep_Fog has fogged the
        /// whole map, so a !Fogged test rejects every cell, the search fails, and the
        /// colony gets dumped on the band's centre cell (frequently solid rock). That was
        /// the run #4 "no colonists spawned" bug.</summary>
        /// <summary>
        /// Opens a pocket in a solid-rock surface band so the colony has somewhere to
        /// exist. Radius matches PodScatterMargin: DropCellFinder scatters pods well away
        /// from the requested centre, and every pod must land on carved ground.
        ///
        /// The roof is cleared along with the rock. Skipping that seemed harmless and is
        /// not: an unsupported thick rock roof over the new clearing collapses onto the
        /// colony the moment the roof-support check next runs.
        /// </summary>
        private static void CarveStartClearing(Map map, CellRect surface, IntVec3 centre)
        {
            float radius = Mathf.Min(24f, GenRadial.MaxRadialPatternRadius - 1f);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(centre, radius, useCenter: true))
            {
                if (!c.InBounds(map) || !surface.Contains(c))
                {
                    continue;
                }
                Building edifice = c.GetEdifice(map);
                if (edifice != null && edifice.def.building != null
                    && (edifice.def.building.isNaturalRock || edifice.def.building.isResourceRock))
                {
                    edifice.Destroy(DestroyMode.Vanish);
                }
                map.roofGrid.SetRoof(c, null);
            }
        }

        /// <summary>Why the last failed search failed, for the warning message. Without it
        /// "no standable start cell" is unactionable - a band that is solid mountain and a
        /// band that is solid ocean need completely different responses, and they produce
        /// the identical message.</summary>
        private static string lastSearchCensus = string.Empty;

        /// <summary>
        /// Finds somewhere the starting colony can actually land: standable, dry,
        /// unobstructed, and (optionally) with a clear apron so drop pods and pawns fit.
        ///
        /// TWO PASSES, and the second one is the fix for a real failure. The radial pass
        /// searches outward from the seed so the colony lands near the spot vanilla chose
        /// for terrain reasons - but GenRadial's precomputed pattern tops out at
        /// MaxRadialPatternRadius (~79.8), so it can only ever see ~70 cells. The surface
        /// band is 200x200. When the seed fell inside a lake or a mountain and open ground
        /// was further away than that, the search reported "no standable cell in the surface
        /// band" while thousands of perfectly good cells sat just outside the disc. The
        /// colony then fell back to the band centre, the drop-pod finder scattered pods
        /// looking for somewhere valid, and four colonists ended up outside the band
        /// entirely (caught by RescueStrandedColonists, which is the only reason they
        /// survived).
        ///
        /// So a full deterministic sweep of the band backs it up. It is O(band) exactly
        /// once at generation, and it means failure now genuinely means "this band contains
        /// no standable dry cell at all" rather than "none within 70 cells of a guess".
        ///
        /// Deliberately does NOT test Fogged - by this point GenStep_Fog has fogged the
        /// whole map, so a !Fogged test rejects every cell (that was the run #4 "no
        /// colonists spawned" bug).
        /// </summary>
        private static bool TryFindStartCell(Map map, CellRect surface, IntVec3 seed,
            bool requireApron, out IntVec3 result)
        {
            int water = 0;
            int edifice = 0;
            int unstandable = 0;
            int noApron = 0;
            int considered = 0;

            // Pass 1: near the seed, so the colony keeps vanilla's choice of neighbourhood.
            float radius = Mathf.Min(70f, GenRadial.MaxRadialPatternRadius - 1f);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(seed, radius, useCenter: true))
            {
                if (Qualifies(map, surface, c, requireApron, ref considered, ref water,
                    ref edifice, ref unstandable, ref noApron))
                {
                    result = c;
                    return true;
                }
            }

            // Reset before the full sweep so the census describes the BAND exactly once.
            // Leaving the radial tallies in double-counted the overlap and produced totals
            // larger than the band itself (55,373 counted in a 200x200 = 40,000 cell band),
            // which makes the one number a reader checks first obviously untrustworthy.
            considered = 0;
            water = 0;
            edifice = 0;
            unstandable = 0;
            noApron = 0;

            // Pass 2: the whole band. Slower, but it is the difference between "we looked
            // everywhere" and "we looked in a circle".
            foreach (IntVec3 c in surface)
            {
                if (Qualifies(map, surface, c, requireApron, ref considered, ref water,
                    ref edifice, ref unstandable, ref noApron))
                {
                    result = c;
                    return true;
                }
            }

            lastSearchCensus = " [searched " + considered + " cells in " + surface
                + ": " + unstandable + " unstandable, " + edifice + " blocked by an edifice, "
                + water + " water"
                + (requireApron ? ", " + noApron + " lacked a clear apron" : "") + "]";
            result = seed;
            return false;
        }

        private static bool Qualifies(Map map, CellRect surface, IntVec3 c, bool requireApron,
            ref int considered, ref int water, ref int edifice, ref int unstandable,
            ref int noApron)
        {
            if (!c.InBounds(map) || !surface.Contains(c))
            {
                return false;
            }
            considered++;
            if (map.terrainGrid.TerrainAt(c).IsWater)
            {
                water++;
                return false;
            }
            if (c.GetEdifice(map) != null)
            {
                edifice++;
                return false;
            }
            if (!c.Standable(map))
            {
                unstandable++;
                return false;
            }
            if (requireApron && !ApronClear(map, surface, c))
            {
                noApron++;
                return false;
            }
            return true;
        }

        private static bool ApronClear(Map map, CellRect surface, IntVec3 center)
        {
            CellRect apron = CellRect.CenteredOn(center, 2);
            foreach (IntVec3 c in apron)
            {
                if (!c.InBounds(map) || !surface.Contains(c) || !c.Standable(map)
                    || map.terrainGrid.TerrainAt(c).IsWater)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
