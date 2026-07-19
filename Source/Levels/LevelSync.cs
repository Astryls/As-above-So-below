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
                RoofGrid roofs = ground.roofGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef rooftop = ABDefOf.AB_RoofSurface;
                int fixedCells = 0;
                foreach (IntVec3 c in sky.AllCells)
                {
                    TerrainDef top = grid.TerrainAt(c);
                    if (top == air)
                    {
                        RoofDef roof = c.InBounds(ground) ? roofs.RoofAt(c) : null;
                        if (roof != null && !roof.isNatural)
                        {
                            grid.SetTerrain(c, rooftop);
                            fixedCells++;
                        }
                    }
                    else if (top == rooftop)
                    {
                        RoofDef roof = c.InBounds(ground) ? roofs.RoofAt(c) : null;
                        if (roof == null)
                        {
                            grid.SetTerrain(c, air);
                            fixedCells++;
                        }
                    }
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
                RoofDef roof = ground.roofGrid.RoofAt(c);
                TerrainGrid grid = sky.terrainGrid;
                TerrainDef top = grid.TerrainAt(c);
                if (roof != null)
                {
                    // Support gained: air becomes buildable rooftop. Rock surfaces and
                    // existing floors are already supported, leave them alone.
                    if (top == ABDefOf.AB_OpenAir)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                    }
                }
                else
                {
                    // Support lost.
                    if (top == ABDefOf.AB_RoofSurface)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                    }
                    else if (top != ABDefOf.AB_OpenAir && top.Removable)
                    {
                        // A built floor was riding on that roof: it collapses first,
                        // then the exposed rooftop reverts to air.
                        grid.RemoveTopLayer(c);
                        if (grid.TerrainAt(c) == ABDefOf.AB_RoofSurface)
                        {
                            grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                        }
                    }
                    // Natural rock surfaces above mountains are left alone. Known
                    // limitation (T6 #3, deferred by decision): if the natural roof
                    // supporting a sky-mountain rim or ledge cell below is mined out,
                    // that cell keeps its rock terrain and "floats" rather than
                    // collapsing to air and dropping. It is a rare edge case (natural
                    // roof removal under the ledge ring), so we accept and document
                    // it in the field manual rather than force a collapse cascade
                    // that could destabilize the mountain shell.
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
