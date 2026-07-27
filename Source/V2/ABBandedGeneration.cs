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
                    int h = mapSize.z;
                    pending = new PendingLayout
                    {
                        bandCount = ABV2.BandCount,
                        bandHeight = h,
                        surfaceBand = ABV2.SurfaceBand
                    };
                    mapSize = new IntVec3(mapSize.x, mapSize.y, ABV2.BandCount * (h + ABBandMap.Gutter));
                    ABLog.Dev("V2: banding new colony map -> " + mapSize + " (" + ABV2.BandCount
                        + " bands of " + h + " + " + ABBandMap.Gutter + " gutter).");
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
                    Carve(__result, bands);
                    FixPlayerStartSpot(__result, bands);
                }
                catch (Exception e)
                {
                    Log.Error(ABLog.Tag + " V2: band carve failed: " + e);
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
                for (int z = gutterStartZ; z < gutterStartZ + ABBandMap.Gutter; z++)
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

        /// <summary>Scenario pawns spawn AFTER generation at MapGenerator.PlayerStartSpot
        /// (Game.InitNewGame calls Find.Scenario.PostMapGenerate once GenerateMap returns),
        /// so nudging the spot here is enough to land the starting colony on the surface.</summary>
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
            IntVec3 found = TryFindStartCell(map, surface, seed, out IntVec3 c2) ? c2 : seed;
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
        private static bool TryFindStartCell(Map map, CellRect surface, IntVec3 seed, out IntVec3 result)
        {
            // GenRadial's precomputed pattern tops out at MaxRadialPatternRadius (~79.8);
            // asking for more logs "Not enough squares to get to radius N" and silently
            // clamps. Stay inside it (run #7).
            float radius = Mathf.Min(70f, GenRadial.MaxRadialPatternRadius - 1f);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(seed, radius, useCenter: true))
            {
                if (!c.InBounds(map) || !surface.Contains(c))
                {
                    continue;
                }
                if (!c.Standable(map) || c.GetEdifice(map) != null
                    || map.terrainGrid.TerrainAt(c).IsWater)
                {
                    continue;
                }
                if (!ApronClear(map, surface, c))
                {
                    continue;
                }
                result = c;
                return true;
            }
            result = seed;
            return false;
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
