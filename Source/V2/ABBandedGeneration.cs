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

        /// <summary>Bands per column. 3 = basement / surface / sky.</summary>
        public const int BandCount = 3;

        /// <summary>Index of the surface band in a 3-band map.</summary>
        public const int SurfaceBand = 1;
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
            PendingLayout p = pending;
            if (p == null || map == null)
            {
                return false;
            }
            slot = ABBandMap.SlotFor(p.bandHeight);
            surface = new CellRect(0, p.surfaceBand * slot, map.Size.x, p.bandHeight);
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
                    int h = mapSize.z;
                    pending = new PendingLayout
                    {
                        bandCount = ABV2.BandCount,
                        bandHeight = h,
                        surfaceBand = ABV2.SurfaceBand
                    };
                    int slot = ABBandMap.SlotFor(h);
                    mapSize = new IntVec3(mapSize.x, mapSize.y, ABV2.BandCount * slot);
                    ABLog.Dev("V2: banding new colony map -> " + mapSize + " (" + ABV2.BandCount
                        + " bands of " + h + " + " + (slot - h) + " gutter, slot " + slot + ").");
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
                    bands.Setup(p.bandCount, p.bandHeight, p.surfaceBand);
                    RescueStrandedColonists(__result, bands);
                    Carve(__result, bands);
                    FixPlayerStartSpot(__result, bands);
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: band carve failed: " + e);
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
                        found = safe.CenterCell;
                        Log.Warning(ABLog.Tag + " V2: no standable start cell in the surface"
                            + " band; falling back to " + found + "." + lastSearchCensus);
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

            for (int band = 0; band < bands.bandCount; band++)
            {
                if (band == bands.surfaceBand)
                {
                    continue;
                }
                CellRect rect = bands.RectOfBand(band);
                if (band < bands.surfaceBand)
                {
                    FillRock(map, rect, rocks, noises);
                    // Then optionally hollow it back out into a living cave system.
                    // Runs on the filled rock deliberately: the carve reads and destroys
                    // the rock it opens, and the untouched remainder becomes the walls.
                    ABCavernGen.Generate(map, bands, band);
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
                    ABSkyBandGen.Generate(map, bands, band, rocks, noises);
                }
            }
            CarveGutters(map, bands);

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
        }

        private static void FillRock(Map map, CellRect rect, List<ThingDef> rocks, List<Perlin> noises)
        {
            TerrainGrid terrain = map.terrainGrid;
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                ClearCellHard(map, c);
                ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                terrain.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                GenSpawn.Spawn(rock, c, map);
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
            }
            ABOreGen.ScatterOres(map, rect.Cells.ToList(),
                Mathf.Clamp(ABMod.Settings?.basementOreDensity ?? 6f, 0f, 12f));
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
                    }
                    continue;
                }
                t.Destroy(DestroyMode.Vanish);
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
