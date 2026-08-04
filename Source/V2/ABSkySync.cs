using System;
using System.Collections.Generic;
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
    /// V1 had this system and it was not carried into V2; this file is its V2 rebuild.
    /// (ABGuard's `RoofSync` switch, originally a V1 orphan, is now THIS FILE's kill
    /// switch - all of its consumers are below. An earlier version of this sentence
    /// called the switch "unused", which became false the moment this file adopted it:
    /// left uncorrected, a cleanup pass trusting the comment would have deleted a LIVE
    /// guard. False comments outlive their truth window; date them or delete them.)
    ///
    /// THE RULE, in precedence order, applied to the cell one Slot above `below`:
    ///   1. constructed roof below    -> AB_RoofSurface (buildable AND walkable)
    ///   2. natural roof below        -> AB_MountainTop (mountain mass)
    ///   3. impassable edifice below  -> AB_WallTop     (buildable, NOT walkable)
    ///   4. otherwise                 -> AB_OpenAir
    ///
    /// ⚠ ROOF BEATS EDIFICE - BOTH KINDS OF ROOF. Getting that only half right is the
    /// whole of §21, the "fog and rocks come back on reload" bug. Testing the edifice first
    /// gave every wall a non-walkable ledge even when the building it belonged to was fully
    /// roofed - so a finished, roofed structure had walkable rooftop over its interior and
    /// impassable strips along all four walls, which is not a roof anyone can use. That was
    /// fixed for CONSTRUCTED roofs and the identical argument was never carried to NATURAL
    /// ones: undug rock is an impassable edifice under a natural roof, so the edifice rule
    /// shadowed the natural-roof rule and EVERY cell above unmined surface rock resolved to
    /// AB_WallTop instead of AB_MountainTop.
    ///
    /// ⚠ THAT ONE SWAP HAS THREE VISIBLE CONSEQUENCES, because AB_WallTop is dontRender,
    /// ShowsBelow-true and Impassable while AB_MountainTop is opaque, solid and Standable:
    ///   * the sky band's mountain mass turns SEE-THROUGH, and SectionLayer_ABBelowV2 paints
    ///     the surface's fog through it - a mountain-shaped fog blob over open sky ground;
    ///   * that same mass silently stops being walkable;
    ///   * §24b's mass rendering has nothing left to draw.
    /// A roof is a continuous surface INCLUDING the walls it rests on, and a mountain is the
    /// roof of its own rock. AB_WallTop is only for impassable things with NO roof over
    /// them: a free-standing wall, or the outer ring of an unroofed compound.
    ///
    /// ⚠ ONLY DERIVED CELLS ARE TOUCHED. The sky band also holds generated mountain and
    /// plateau terrain and any floor the player has laid up there, none of which is a
    /// function of the level below. Writing those would erase a player's work and dissolve
    /// the generated summit, so a cell is only ever rewritten when it currently holds one
    /// of the terrains this system owns.
    ///
    /// ⚠ AB_MountainTop IS NOT ONE OF THEM ANY MORE. The sky band's OWN mass is
    /// AB_MountainTop terrain (ABSkyBandGen KindLedge / KindWall) - the band's own mountain,
    /// not a mirror of anything beneath it, and a KindLedge cell carries no edifice and no
    /// roof to identify it by. While it was writable, mining a surface rock deleted the
    /// summit standing above it. The rule is now one-way: this system may CREATE
    /// AB_MountainTop over a cell that gains a natural roof, and may never overwrite one.
    ///
    /// ⚠ AND IT NEVER DISSOLVES GROUND OUT FROM UNDER SOMETHING STANDING ON IT. A stair
    /// landing is a platform Building_ABStairs2.CarveLanding carves into the sky band. It is
    /// AB_RoofSurface, so it is writable, but it is NOT derived from the cell below - and
    /// the cell below it is an unroofed door, which resolves to AB_OpenAir. So the landing
    /// survived exactly until the next sync on that column and then became open air with the
    /// link still standing in it, which is what put the LEVEL BELOW'S stairwell on screen on
    /// top of the sky one: two stairwell sprites in one cell, and because up-art and
    /// down-art are vertical mirrors of each other, the report was "stair textures invert
    /// and start doubly".
    ///
    /// EVENT-DRIVEN, NOT SCANNED. A banded map is 3x the cells and this codebase has been
    /// bitten repeatedly by per-frame sweeps, so nothing here polls: the two hooks fire on
    /// the exact events that can change the answer (a roof written, an edifice registered or
    /// removed) and each one touches a handful of cells.
    ///
    /// ⚠ BUT ONE OF THOSE EVENTS FIRES FOR THE WHOLE MAP, ON EVERY LOAD, AND THAT IS WHY
    /// BOTH BUGS ABOVE WERE RELOAD-ONLY. Building.SpawnSetup calls edificeGrid.Register
    /// unconditionally - `respawningAfterLoad` is not consulted - so Map.FinalizeLoading
    /// re-registers every building on the map, including every compressed Mineable, and this
    /// system re-derives the entire sky band from scratch. Anything the generator or
    /// CarveLanding wrote that this resolver would not reproduce is silently overwritten at
    /// that moment and then saved. An event hook that looks incremental is a FULL SWEEP on
    /// the load path; assume it and make the resolver agree with the generator.
    /// </summary>
    public static class ABSkySync
    {
        /// <summary>
        /// Suspended during the band carve, and the generation profiler is why.
        ///
        /// The carve performs ~36k rock spawns, ~47k destroys and tens of thousands of
        /// SetRoof calls in one burst - and every one of them fired these postfixes. Each
        /// sync resolves the band component, does band math and reads grids, which is
        /// nothing per call and seconds in aggregate (the phase profile showed FillRock at
        /// 9,038 ms for work vanilla's RocksFromGrid does in ~300 ms - per-op patch
        /// overhead, not engine cost). Every one of those syncs is REDUNDANT during the
        /// carve: ABSkyBandGen derives the sky band's terrain itself, from final post-carve
        /// state, immediately afterwards.
        ///
        /// Set/cleared in a try/finally by ABBandedGeneration.Carve only. Normal play never
        /// suspends - event-driven sync is exactly right at play-time rates.
        /// </summary>
        internal static bool Suspended;

        /// <summary>Terrains this system may OVERWRITE. Anything else in a sky cell was put
        /// there by the generator or the player and is left strictly alone.
        ///
        /// AB_MountainTop is deliberately absent even though Resolve can still return it:
        /// see the one-way rule in the class comment. It is the band's own mass, and there
        /// is no way to tell a generated ledge apart from a derived one by terrain.</summary>
        private static bool IsDerived(TerrainDef t)
        {
            return t != null
                && (t == ABDefOf.AB_OpenAir
                    || t == ABDefOf.AB_RoofSurface
                    || t == ABDefOf.AB_WallTop);
        }

        /// <summary>True when the sky cell carries structure of its OWN, so re-deriving it
        /// from the level below would pull the ground out from under something.
        ///
        /// Covers three cases with one test each: the band's own KindWall mass (a real rock
        /// edifice under a natural roof), anything the player has built or roofed up here,
        /// and a link's own footprint (Building_ABStairs2 is a Building_Door, hence an
        /// edifice). The landing APRON around a link has neither, so it gets its own test in
        /// SyncAbove - kept separate because that one costs a small scan.</summary>
        private static bool CarriesOwnStructure(Map map, IntVec3 target)
        {
            return map.roofGrid.RoofAt(target) != null || target.GetEdifice(map) != null;
        }

        /// <summary>Is this cell part of the platform a vertical link carved for itself?
        ///
        /// Radius is ABWormholePather.LandingRadius, the same number CarveLanding used, so
        /// the protected area and the carved area are the same area by construction rather
        /// than by coincidence. Only ever called on the one transition that can destroy a
        /// landing (solid -> open air), so the scan does not sit on the load path.</summary>
        private static bool OnLinkLanding(Map map, IntVec3 target)
        {
            CellRect around = CellRect.CenteredOn(target, ABWormholePather.LandingRadius)
                .ClipInsideMap(map);
            foreach (IntVec3 c in around)
            {
                if (c.GetFirstThing<Building_ABStairs2>(map) != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Recompute the cell directly above <paramref name="below"/>.</summary>
        public static void SyncAbove(Map map, IntVec3 below)
        {
            if (Suspended || map == null || !ABGuard.On(ABGuard.RoofSync))
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
            if (CarriesOwnStructure(map, target))
            {
                return; // the band's own mass, a built structure, or a link footprint
            }
            TerrainDef want = Resolve(map, below);
            if (want == null || want == current)
            {
                return;
            }
            if (want == ABDefOf.AB_OpenAir && OnLinkLanding(map, target))
            {
                return; // a carved landing apron is not a mirror of the cell below
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
            if (roof != null)
            {
                // NATURAL roof, and it is tested BEFORE the edifice. Undug rock is an
                // impassable edifice sitting under exactly this roof, so the other order
                // turned every cell above unmined surface rock into a see-through ledge.
                // Matches ABSkyBandGen's own "outside the mass" classification, which has no
                // edifice test at all - the two resolvers have to agree or a reload silently
                // rewrites the map into a shape generation would never have produced.
                return ABDefOf.AB_MountainTop;
            }
            Building edifice = below.GetEdifice(map);
            if (edifice != null && edifice.def != null
                && edifice.def.passability == Traversability.Impassable)
            {
                // An UNROOFED wall: build on it to raise the structure, but there is nothing
                // up here to walk along.
                return ABDefOf.AB_WallTop;
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

        /// <summary>
        /// REPAIR, once per map load, for damage the pre-fix resolver already baked in.
        ///
        /// The resolver fix stops the map getting worse; it cannot undo what is already in
        /// the saved terrain grid, and the damage compounded on every load. A colony that
        /// has been reloaded a few times has its sky mass already rewritten to AB_WallTop
        /// and its stair landings already dissolved, so without this the fix reads as "no
        /// change" to the only people who reported the bug.
        ///
        /// ⚠ DELIBERATELY TWO NARROW TRANSITIONS, NOT A RE-DERIVE. Re-running Resolve over
        /// the whole band would be the obvious sweep and it is the wrong one: it cannot tell
        /// generated summit from derived, it would happily rewrite terrain the player laid,
        /// and any future disagreement between the two resolvers would then be applied to
        /// every existing save at once. These two rules can only ever move a cell back to a
        /// state the generator itself produces:
        ///
        ///   1. AB_WallTop whose cell one Slot below has a NATURAL roof -> AB_MountainTop.
        ///      A player's wall never has a natural roof over it, so player-made wall tops
        ///      are outside the rule by construction.
        ///   2. AB_OpenAir on or beside a link in a sky band -> AB_RoofSurface, over exactly
        ///      the rect CarveLanding carved.
        ///
        /// Idempotent, so running it on every load (including a brand new colony, where it
        /// finds nothing) is safe. Returns the number of cells repaired.
        /// </summary>
        public static int RepairAfterLoad(Map map, ABBandMap bands)
        {
            if (map == null || bands == null || !bands.Banded)
            {
                return 0;
            }
            TerrainGrid grid = map.terrainGrid;
            RoofGrid roofs = map.roofGrid;
            int slot = bands.Slot;
            int mass = 0;
            int landing = 0;

            // ---- 1. the band's own mass, turned see-through by the old precedence -------
            for (int band = bands.surfaceBand + 1; band < bands.bandCount; band++)
            {
                foreach (IntVec3 c in bands.RectOfBand(band))
                {
                    if (!c.InBounds(map) || grid.TerrainAt(c) != ABDefOf.AB_WallTop)
                    {
                        continue;
                    }
                    IntVec3 below = new IntVec3(c.x, c.y, c.z - slot);
                    if (!below.InBounds(map) || bands.InGutter(below))
                    {
                        continue;
                    }
                    RoofDef roof = roofs.RoofAt(below);
                    if (roof != null && roof.isNatural)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_MountainTop);
                        mass++;
                    }
                }
            }

            // ---- 2. stair landings dissolved out from under their own link -------------
            // Walked from the LINKS, not by sweeping cells: a landing is defined by the
            // building that carved it, and there are a handful of links against ~180k cells.
            List<Thing> buildings =
                map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!(buildings[i] is Building_ABStairs2 link) || !link.Spawned)
                {
                    continue;
                }
                int band = bands.BandOf(link.Position);
                if (band <= bands.surfaceBand)
                {
                    continue; // only sky landings are AB_RoofSurface; basements got Gravel
                }
                CellRect bandRect = bands.RectOfBand(band);
                CellRect apron = link.OccupiedRect()
                    .ExpandedBy(ABWormholePather.LandingRadius).ClipInsideMap(map);
                foreach (IntVec3 c in apron)
                {
                    if (c.InBounds(map) && bandRect.Contains(c)
                        && grid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                    {
                        grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                        landing++;
                    }
                }
            }

            if (mass > 0 || landing > 0)
            {
                Log.Message(ABLog.Tag + " V2: sky-sync repair on map " + map.uniqueID + " - "
                    + mass + " mass cell(s) restored to mountain top, " + landing
                    + " landing cell(s) restored to rooftop.");
            }
            return mass + landing;
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
