using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §99.B1 - THE BAND BIOME'S OWN TERRAIN CHARACTER.
    ///
    /// THE GAP. What makes a lava field READ as a lava field is not its stone - it is
    /// <c>biome.terrainPatchMakers</c>. Odyssey's LavaField declares threshold bands
    /// producing <c>LavaDeep</c>; Alpha Biomes' Pyroclastic Conflagration lays
    /// <c>AB_Obsidian</c>, <c>AB_SolidifiedLava</c> and <c>AB_LiquidLava</c> from two perlin
    /// makers; its Tar Pits do the same for tar. Vanilla applies all of it in
    /// <c>GenStep_Terrain</c> (order 210) across the WHOLE map - and it was, at 1,256 ms in
    /// run #516, the single most expensive genstep of the colony. Then the carve erased
    /// every non-surface band and re-authored terrain from our own rules
    /// (<c>naturalTerrain ?? Gravel</c> below, stone/gravel/soil by noise above), and
    /// nothing ever put the biome's character back. That is the "normal stone" report.
    ///
    /// ⚠ ELEVEN OF ALPHA BIOMES' TWELVE BIOMES USE PATCH MAKERS AND NONE USE
    /// <c>extraGenSteps</c>. That is why this, and not a genstep re-run, is the lever that
    /// makes modded biomes work on every level. Rule 54: search the capability.
    ///
    /// ⚠ ADDITIVE, NOT AUTHORITATIVE. This decorates terrain the band generators authored;
    /// it does not replace them. Stone-category and water cells are skipped exactly as
    /// <c>TileMutatorWorker_Patches</c> skips them, so a cavern's rock floor and a tarn's
    /// water survive, and only the ordinary ground picks up the biome's blotches. The
    /// alternative - re-running <c>GenStep_Terrain</c> under the band scope - would give two
    /// owners one grid and flatten ABSkyBandGen's plateau/ledge classification.
    ///
    /// ⚠ NO BAND-LOCAL NOISE WRAP HERE, DELIBERATELY, AND IT IS NOT AN OVERSIGHT.
    /// <c>TerrainPatchMaker.Init</c> seeds itself with <c>Rand.Range(0, int.MaxValue)</c> and
    /// caches the field on the SHARED def instance until <c>Cleanup()</c>. Two consequences
    /// decide the design: (a) the field is re-seeded per Init anyway, so bands naturally get
    /// different blotches - which is right, because patch makers are decorative scatter, not
    /// geography that must line up across levels the way a coastline must; (b) the instance
    /// is shared, so we MUST Cleanup afterwards or a band-generated field would stay
    /// attached to the biome def and leak into the next map (rule 51: a factory consumed its
    /// input).
    /// </summary>
    internal static class ABBandBiomeTerrain
    {
        internal static int cellsPainted;

        internal static int bandsPainted;

        /// <summary>
        /// Apply the band biome's patch makers over one band.
        ///
        /// Called from inside the band scope with the terrain guard armed, so out-of-band
        /// writes, void cells and hazardous terrain at a drop are all refused by
        /// <c>ABBandScope.AllowTerrainWrite</c> - this method does not need to know about
        /// any of that, which is the point of having a guard at all (rule 37).
        /// </summary>
        internal static void PaintBand(Map map, CellRect rect, BiomeDef biome, int band)
        {
            if (map == null || biome?.terrainPatchMakers == null
                || biome.terrainPatchMakers.Count == 0)
            {
                return;
            }
            List<TerrainPatchMaker> makers = biome.terrainPatchMakers;
            int painted = 0;
            try
            {
                foreach (IntVec3 c in rect)
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    TerrainDef cur = c.GetTerrain(map);
                    if (cur == null)
                    {
                        continue;
                    }
                    // Vanilla's own skip list (TileMutatorWorker_Patches): never overwrite
                    // stone or water. Stone is the band's structure - a cavern floor and a
                    // mountain shoulder are load-bearing for how the level reads - and water
                    // is somebody else's feature (a tarn, a cave pool).
                    if (cur.categoryType == TerrainDef.TerrainCategoryType.Stone || cur.IsWater)
                    {
                        continue;
                    }
                    float fertility = c.GetFertility(map);
                    for (int i = 0; i < makers.Count; i++)
                    {
                        TerrainDef t = makers[i].TerrainAt(c, map, fertility);
                        if (t != null && t != cur)
                        {
                            map.terrainGrid.SetTerrain(c, t);
                            painted++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABLog.Dev("Band biome terrain: patch makers failed on band " + band + " ("
                    + biome.defName + "): " + e.Message);
            }
            finally
            {
                // ⚠ ALWAYS, even on failure. These makers hang off a shared BiomeDef and
                // hold a map-sized noise field plus a `currentlyInitializedForMap` back
                // reference; leaving them initialised pins this map alive and hands the
                // next one our band's field.
                for (int i = 0; i < makers.Count; i++)
                {
                    try
                    {
                        makers[i].Cleanup();
                    }
                    catch
                    {
                    }
                }
            }
            if (painted > 0)
            {
                bandsPainted++;
                cellsPainted += painted;
                ABLog.Dev("Band biome terrain: band " + band + " (" + biome.defName + ") - "
                    + painted + " cell(s) painted from " + makers.Count
                    + " terrain patch maker(s).");
            }
        }

        internal static void Reset()
        {
            cellsPainted = 0;
            bandsPainted = 0;
        }
    }
}
