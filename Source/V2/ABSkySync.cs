using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Keeps the band ABOVE in step with what the player builds below.
    ///
    /// THE REGRESSION THIS FIXES. The sky band's terrain was computed exactly once, during
    /// map generation (ABSkyBandGen), and never again. `AB_RoofSurface` was written in only
    /// two places in the whole codebase - that generator and Building_ABStairs2.CarveLanding
    /// - so anything the player built afterwards was invisible to the level above:
    ///   * roof a room after generation and it never appeared from the sky (BUG2);
    ///   * build a wall and there was still only open air above it, so a wall could not be
    ///     stacked on a wall (BUG3).
    /// V1 had this system - ABGuard still carries an unused `RoofSync` switch from it - and
    /// it was not carried into V2.
    ///
    /// THE RULE, in precedence order, applied to the cell one Slot above `below`:
    ///   1. constructed roof below    -> AB_RoofSurface (buildable AND walkable)
    ///   2. impassable edifice below  -> AB_WallTop     (buildable, NOT walkable)
    ///   3. natural roof below        -> AB_MountainTop
    ///   4. otherwise                 -> AB_OpenAir
    ///
    /// ROOF BEATS EDIFICE, and the order was wrong the first time. Testing the edifice
    /// first gave every wall a non-walkable ledge even when the building it belonged to was
    /// fully roofed - so a finished, roofed structure had walkable rooftop over its interior
    /// and impassable strips along all four walls, which is not a roof anyone can use.
    /// A roof is a continuous surface INCLUDING the walls it rests on. AB_WallTop is
    /// therefore only for walls with no roof over them: a free-standing wall, or the outer
    /// ring of an unroofed compound, where a bare ledge is exactly right.
    ///
    /// ⚠ ONLY DERIVED CELLS ARE TOUCHED. The sky band also holds generated mountain and
    /// plateau terrain and any floor the player has laid up there, none of which is a
    /// function of the level below. Writing those would erase a player's work and dissolve
    /// the generated summit, so a cell is only ever rewritten when it currently holds one
    /// of the four terrains this system owns.
    ///
    /// EVENT-DRIVEN, NOT SCANNED. A banded map is 3x the cells and this codebase has been
    /// bitten repeatedly by per-frame sweeps, so nothing here polls: the two hooks fire on
    /// the exact events that can change the answer (a roof written, an edifice registered or
    /// removed) and each one touches a handful of cells.
    /// </summary>
    public static class ABSkySync
    {
        /// <summary>Terrains this system owns. Anything else in a sky cell was put there by
        /// the generator or the player and is left strictly alone.</summary>
        private static bool IsDerived(TerrainDef t)
        {
            return t != null
                && (t == ABDefOf.AB_OpenAir
                    || t == ABDefOf.AB_RoofSurface
                    || t == ABDefOf.AB_WallTop
                    || t == ABDefOf.AB_MountainTop);
        }

        /// <summary>Recompute the cell directly above <paramref name="below"/>.</summary>
        public static void SyncAbove(Map map, IntVec3 below)
        {
            if (map == null || !ABGuard.On(ABGuard.RoofSync))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            int band = bands.BandOf(below);
            int above = band + 1;
            if (!bands.BandExists(above) || bands.InGutter(below))
            {
                return;
            }
            IntVec3 target = bands.Translate(below, above);
            if (!target.InBounds(map) || bands.InGutter(target))
            {
                return;
            }

            TerrainGrid grid = map.terrainGrid;
            TerrainDef current = grid.TerrainAt(target);
            if (!IsDerived(current))
            {
                return; // generated summit or player-laid floor - not ours to rewrite
            }
            TerrainDef want = Resolve(map, below);
            if (want == null || want == current)
            {
                return;
            }
            grid.SetTerrain(target, want);
        }

        private static TerrainDef Resolve(Map map, IntVec3 below)
        {
            RoofDef roof = map.roofGrid.RoofAt(below);
            if (roof != null && !roof.isNatural)
            {
                // A constructed roof covers its walls too - the whole footprint is one
                // continuous surface to walk on.
                return ABDefOf.AB_RoofSurface;
            }
            Building edifice = below.GetEdifice(map);
            if (edifice != null && edifice.def != null
                && edifice.def.passability == Traversability.Impassable)
            {
                // An UNROOFED wall: build on it to raise the structure, but there is nothing
                // up here to walk along.
                return ABDefOf.AB_WallTop;
            }
            if (roof != null)
            {
                return ABDefOf.AB_MountainTop; // natural roof = mountain mass
            }
            return ABDefOf.AB_OpenAir;
        }

        /// <summary>Every cell a multi-cell building covers.</summary>
        public static void SyncAbove(Map map, CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                SyncAbove(map, c);
            }
        }
    }

    /// <summary>Roofs: built, removed, or collapsed. SetRoof is the single writer for the
    /// roof grid, so one hook covers player roof-building, deconstruction, mining out a
    /// mountain and roof collapse alike.</summary>
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    public static class Patch_RoofGrid_ABSyncAbove
    {
        private static readonly AccessTools.FieldRef<RoofGrid, Map> MapRef =
            AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        private static void Postfix(RoofGrid __instance, IntVec3 c)
        {
            try
            {
                ABSkySync.SyncAbove(MapRef(__instance), c);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "V2 sky roof sync");
            }
        }
    }

    /// <summary>Walls and other impassable edifices appearing. Registering with the edifice
    /// grid is what actually makes a cell impassable, so it is a truer trigger than
    /// SpawnSetup - it fires for every path a building can arrive by, including replacement
    /// and load.</summary>
    [HarmonyPatch(typeof(EdificeGrid), nameof(EdificeGrid.Register))]
    public static class Patch_EdificeGrid_ABRegister
    {
        private static readonly AccessTools.FieldRef<EdificeGrid, Map> MapRef =
            AccessTools.FieldRefAccess<EdificeGrid, Map>("map");

        private static void Postfix(EdificeGrid __instance, Building ed)
        {
            try
            {
                if (ed != null)
                {
                    ABSkySync.SyncAbove(MapRef(__instance), ed.OccupiedRect());
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "V2 sky edifice sync");
            }
        }
    }

    /// <summary>...and going away again. Runs as a POSTFIX so the grid has already been
    /// cleared - a prefix would still see the wall and re-derive AB_WallTop, leaving a
    /// phantom ledge above a wall that no longer exists.</summary>
    [HarmonyPatch(typeof(EdificeGrid), nameof(EdificeGrid.DeRegister))]
    public static class Patch_EdificeGrid_ABDeRegister
    {
        private static readonly AccessTools.FieldRef<EdificeGrid, Map> MapRef =
            AccessTools.FieldRefAccess<EdificeGrid, Map>("map");

        private static void Postfix(EdificeGrid __instance, Building ed)
        {
            try
            {
                if (ed != null)
                {
                    ABSkySync.SyncAbove(MapRef(__instance), ed.OccupiedRect());
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "V2 sky edifice sync");
            }
        }
    }
}
