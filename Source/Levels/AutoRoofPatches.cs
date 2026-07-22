using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// On the sky level nothing ever touches the map edge: the platform is ringed
    /// by impassable open air with no regions. Vanilla auto-roof uses
    /// Room.TouchesMapEdge as its only "this is the outdoors" test, so any deck
    /// up to 320 cells bordered by player walls reads as an enclosed courtyard
    /// and gets silently marked for roofing - colonists then roof over the sky
    /// level's outdoors. That blocks rain, snow, sun and rooftop growing, and
    /// makes Open The Windows read both sides of every sky window as roofed
    /// (facing indeterminate, no light cast, "cannot determine which side goes
    /// outside").
    ///
    /// Rule: open air counts as map edge. A sky-level room whose border contains
    /// an open-air cell is never auto-roofed, and any build-roof marks already
    /// queued for it are cleared. Fully walled sky rooms do not border open air
    /// (the void sits beyond the wall ring, two cells from the interior) and
    /// keep vanilla auto-roof. Manual roof designations are untouched, mirroring
    /// vanilla's allowance for manual roofs at the map edge. Kill switch:
    /// RoofSync, fail open.
    /// </summary>
    [HarmonyPatch(typeof(AutoBuildRoofAreaSetter), "TryGenerateAreaNow")]
    internal static class Patch_AutoBuildRoofAreaSetter_TryGenerateAreaNow
    {
        private static bool Prefix(Room room)
        {
            if (!ABGuard.On(ABGuard.RoofSync))
            {
                return true;
            }
            try
            {
                if (room == null || room.Dereferenced)
                {
                    return true;
                }
                Map map = room.Map;
                if (map == null || map.Level() != 1)
                {
                    return true;
                }
                // Transient rebuild state: a queued room can still hold detached
                // regions (valid == false, no map) while the region grid is
                // mid-rebuild; enumerating BorderCells then NREs deep inside
                // RegionAt. First seen run #41 when plateau rock pockets made
                // sky-level rooms common enough to hit the map-init window.
                // Skip resolution for such rooms entirely - vanilla would trip
                // over the same enumeration, and the rebuild re-queues the room
                // once it settles.
                List<Region> regions = room.Regions;
                if (regions == null || regions.Count == 0)
                {
                    return false;
                }
                for (int i = 0; i < regions.Count; i++)
                {
                    Region r = regions[i];
                    if (r == null || !r.valid || r.Map != map)
                    {
                        ABLog.Dev("Sky auto-roof guard: skipped a mid-rebuild room (detached region).");
                        return false;
                    }
                }
                TerrainGrid grid = map.terrainGrid;
                foreach (IntVec3 c in room.BorderCells)
                {
                    if (!c.InBounds(map) || grid.TerrainAt(c) != ABDefOf.AB_OpenAir)
                    {
                        continue;
                    }
                    // The room fronts the void: it IS the sky level's outdoors.
                    // Also drop any marks a previous evaluation already queued so
                    // no colonist climbs up to roof the deck.
                    Area buildRoof = map.areaManager.BuildRoof;
                    foreach (IntVec3 rc in room.Cells)
                    {
                        if (buildRoof[rc])
                        {
                            buildRoof[rc] = false;
                        }
                    }
                    return false;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "sky auto-roof guard");
            }
            return true;
        }
    }
}
