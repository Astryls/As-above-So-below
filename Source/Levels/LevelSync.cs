using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Event-driven cross-level rules, matching Z-Levels beta semantics:
    /// - Constructed roof placed below: open air above becomes buildable rooftop, live.
    /// - Roof removed below: rooftop (and any floor built on it) above reverts to open
    ///   air; pawns and items fall through, buildings collapse.
    /// - Constructed floor placed on the sky level: writes a constructed roof below.
    /// - Anything spawning on open air falls to the level below.
    /// Hooked through vanilla MapEvents, no Harmony involved. Kill switch: RoofSync.
    /// </summary>
    public static class LevelSync
    {
        /// <summary>One-pass air/rooftop reconciliation against the ground map's
        /// live roof grid. Events keep the pair in sync during play; this sweep
        /// self-heals any gap (events missed while a kill switch was tripped,
        /// mod interference, load ordering) whenever a sky map finalizes.
        /// Conservative by design: it only ever flips between the two terrains
        /// this mod owns, mirroring the genstep's rule (constructed roof below
        /// becomes rooftop, no roof below reverts rooftop to air); floors, rock,
        /// and natural-roof cells are never touched.</summary>
        public static void ReconcileRooftops(Map sky)
        {
            if (!ABGuard.On(ABGuard.RoofSync) || sky == null)
            {
                return;
            }
            try
            {
                Map ground = sky.LowerMap() ?? sky.GroundMap();
                if (ground == null || ground.Disposed || ground == sky)
                {
                    return;
                }
                TerrainGrid grid = sky.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef rooftop = ABDefOf.AB_RoofSurface;
                int fixedCells = 0;
                foreach (IntVec3 c in sky.AllCells)
                {
                    TerrainDef top = grid.TerrainAt(c);
                    if (top != air && top != rooftop)
                    {
                        // Floors, rock, and anything else are never touched.
                        continue;
                    }
                    TerrainDef want = c.InBounds(ground) && CoveredBelow(ground, c) ? rooftop : air;
                    if (top == want)
                    {
                        continue;
                    }
                    if (want == air && IsStairsPlatform(sky, c))
                    {
                        // Landing platforms are the arrival footing; they persist
                        // while their stairs stand.
                        continue;
                    }
                    grid.SetTerrain(c, want);
                    fixedCells++;
                }
                if (fixedCells > 0)
                {
                    ABLog.Dev("Rooftop reconciliation fixed " + fixedCells + " cell(s) on map " + sky.uniqueID + ".");
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "rooftop reconciliation");
            }
        }

        public static void OnGroundRoofChanged(Map ground, IntVec3 c)
        {
            if (!ABGuard.On(ABGuard.RoofSync))
            {
                return;
            }
            try
            {
                Map sky = ground.UpperMap();
                if (sky == null || sky.Disposed || !c.InBounds(sky))
                {
                    return;
                }
                SyncCellFromBelow(sky, ground, c, allowFloorCollapse: true);
                // The wall-edge rule makes neighbor verdicts depend on this roof
                // cell: promote or demote the adjacent wall tops in the same event.
                IntVec3[] adj = GenAdj.AdjacentCells;
                for (int i = 0; i < adj.Length; i++)
                {
                    IntVec3 n = c + adj[i];
                    if (n.InBounds(sky) && n.InBounds(ground))
                    {
                        SyncCellFromBelow(sky, ground, n, allowFloorCollapse: false);
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "roof to sky sync");
            }
        }

        public static void OnSkyTerrainChanged(Map sky, IntVec3 c)
        {
            if (!ABGuard.On(ABGuard.RoofSync))
            {
                return;
            }
            try
            {
                TerrainDef top = sky.terrainGrid.TerrainAt(c);
                Map ground = sky.LowerMap();
                bool groundOk = ground != null && !ground.Disposed && c.InBounds(ground);
                // The surface ceiling hint layer keys on the Roofs mesh flag. A sky
                // floor change over an EXISTING rooftop writes no roof below, so
                // nudge the surface section explicitly (event-driven, one section).
                if (groundOk)
                {
                    ground.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Roofs);
                }
                if (top == ABDefOf.AB_OpenAir)
                {
                    DropCellContents(sky, c);
                    return;
                }
                if (top.Removable)
                {
                    if (groundOk && ground.roofGrid.RoofAt(c) == null)
                    {
                        ground.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "sky floor to roof sync");
            }
        }

        public static void OnSkyThingSpawned(Map sky, Thing thing)
        {
            if (!ABGuard.On(ABGuard.RoofSync))
            {
                return;
            }
            try
            {
                if (thing == null || !thing.Spawned || thing.Map != sky || !ShouldFall(thing))
                {
                    return;
                }
                if (sky.terrainGrid.TerrainAt(thing.Position) != ABDefOf.AB_OpenAir)
                {
                    return;
                }
                Thing t = thing;
                Map m = sky;
                // Defer one frame: never despawn a thing while its spawn is still running.
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    try
                    {
                        if (t.Spawned && t.Map == m && !m.Disposed
                            && m.terrainGrid.TerrainAt(t.Position) == ABDefOf.AB_OpenAir)
                        {
                            DropThing(t, t.Position, m);
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.RoofSync, e, "air spawn fall");
                    }
                });
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "air spawn check");
            }
        }

        /// <summary>Guaranteed fog reveal when a mineable is removed on a level map
        /// (mined, collapsed, or destroyed). Vanilla's own fog-blocker path is gated
        /// on adjacency conditions; this makes the reveal unconditional and is
        /// idempotent when both run. Covers the sky level and the basement.</summary>
        public static void OnLevelMineableDespawned(Map levelMap, Thing thing)
        {
            if (!ABGuard.On(ABGuard.RoofSync))
            {
                return;
            }
            try
            {
                if (!(thing is Mineable))
                {
                    return;
                }
                IntVec3 c = thing.Position;
                if (!c.InBounds(levelMap))
                {
                    return;
                }
                FogGrid fog = levelMap.fogGrid;
                if (fog.IsFogged(c))
                {
                    fog.Unfog(c);
                }
                fog.FloodUnfogAdjacent(c, sendLetters: false);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "mining fog reveal");
            }
        }

        /// <summary>Applies the covered-below verdict to one sky cell: covered
        /// cells gain rooftop over air; uncovered cells lose unsupported rooftop
        /// (landing platforms exempt while their stairs stand) and, on the
        /// directly changed cell only, collapse floors whose roof is gone.
        /// Natural rock surfaces above mountains are never touched; floating rim
        /// rock on natural roof loss stays a documented rare limitation.</summary>
        private static void SyncCellFromBelow(Map sky, Map ground, IntVec3 c, bool allowFloorCollapse)
        {
            TerrainGrid grid = sky.terrainGrid;
            TerrainDef top = grid.TerrainAt(c);
            if (CoveredBelow(ground, c))
            {
                if (top == ABDefOf.AB_OpenAir)
                {
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
                return;
            }
            if (top == ABDefOf.AB_RoofSurface)
            {
                if (!IsStairsPlatform(sky, c))
                {
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
                return;
            }
            if (allowFloorCollapse && top != ABDefOf.AB_OpenAir && top.Removable
                && ground.roofGrid.RoofAt(c) == null)
            {
                // A built floor was riding on that roof: it collapses first,
                // then the exposed rooftop reverts to air.
                grid.RemoveTopLayer(c);
                if (grid.TerrainAt(c) == ABDefOf.AB_RoofSurface)
                {
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
            }
        }

        /// <summary>True when a sky cell should read as rooftop: a constructed
        /// roof below, OR an artificial impassable edifice (wall, door) below
        /// supporting an adjacent constructed roof - so the steel runs to the
        /// outer edge of the wall blocks instead of stopping at the interior
        /// (playtest spec). Wall changes that fire no roof event converge via
        /// the periodic sweep.</summary>
        internal static bool CoveredBelow(Map ground, IntVec3 c)
        {
            RoofDef roof = ground.roofGrid.RoofAt(c);
            if (roof != null)
            {
                return !roof.isNatural;
            }
            Building ed = ground.edificeGrid[c];
            if (ed == null || ed.def.passability != Traversability.Impassable
                || ed.def.mineable || ed is Building_ABStairs
                || (ed.def.building != null && ed.def.building.isNaturalRock))
            {
                return false;
            }
            IntVec3[] adj = GenAdj.AdjacentCells;
            for (int i = 0; i < adj.Length; i++)
            {
                IntVec3 n = c + adj[i];
                if (!n.InBounds(ground))
                {
                    continue;
                }
                RoofDef r = ground.roofGrid.RoofAt(n);
                if (r != null && !r.isNatural)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True when the cell belongs to a spawned stairwell's landing
        /// footing (footprint plus one rim cell). O(stairs on the map), called
        /// only for cells about to be demoted to open air.</summary>
        internal static bool IsStairsPlatform(Map sky, IntVec3 c)
        {
            List<Building_ABStairs> stairs = sky.Levels()?.Stairs;
            if (stairs == null)
            {
                return false;
            }
            for (int i = 0; i < stairs.Count; i++)
            {
                Building_ABStairs s = stairs[i];
                if (s != null && s.Spawned && s.OccupiedRect().ExpandedBy(1).Contains(c))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ShouldFall(Thing t)
        {
            if (t is Mineable || t is Blueprint || t is Frame || t is Explosion || t is Building_ABStairs)
            {
                return false;
            }
            ThingCategory cat = t.def.category;
            return cat == ThingCategory.Item || cat == ThingCategory.Pawn || cat == ThingCategory.Building;
        }

        private static void DropCellContents(Map sky, IntVec3 c)
        {
            Map lower = sky.LowerMap();
            if (lower == null || lower.Disposed)
            {
                return;
            }
            List<Thing> things = c.GetThingList(sky).ToList();
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing t = things[i];
                if (t.Destroyed || !t.Spawned)
                {
                    continue;
                }
                if (t is Building_ABStairs stairs)
                {
                    Messages.Message("AB_StairsCollapsed".Translate(), new TargetInfo(c, lower), MessageTypeDefOf.NegativeEvent);
                    stairs.Destroy(DestroyMode.KillFinalize);
                    continue;
                }
                if (ShouldFall(t))
                {
                    DropThing(t, c, sky);
                }
            }
        }

        private static void DropThing(Thing t, IntVec3 c, Map sky)
        {
            Map lower = sky.LowerMap();
            if (lower == null || lower.Disposed || !c.InBounds(lower))
            {
                return;
            }
            if (t is Pawn pawn)
            {
                pawn.DeSpawn();
                IntVec3 cell = c.Standable(lower) ? c : CellFinder.StandableCellNear(c, lower, 4f);
                if (!cell.IsValid)
                {
                    cell = c;
                }
                GenSpawn.Spawn(pawn, cell, lower);
                pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, 9f));
                pawn.stances?.stunner?.StunFor(90, null, addBattleLog: false);
                return;
            }
            if (t.def.category == ThingCategory.Item)
            {
                t.DeSpawn();
                GenPlace.TryPlaceThing(t, c, lower, ThingPlaceMode.Near);
                return;
            }
            if (t is Building building)
            {
                // Buildings do not survive the fall; debris spawning on air falls
                // through via the spawn hook.
                building.Destroy(DestroyMode.KillFinalize);
            }
        }
    }
}
