using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// NOTHING COMES TO REST ON OPEN AIR.
    ///
    /// THE BUG THIS FIXES, and it is worth writing the whole chain down because the shape
    /// will recur with other mods. Vanilla Vehicles Expanded's map wrecks are not a GenStep.
    /// They are Vanilla Expanded Framework's `ObjectSpawnsDef` system, driven from
    /// `VEF.Maps.VanillaExpandedFramework_MapGenerator_GenerateMap_Patch.Postfix`, which
    /// wraps its work in `LongEventHandler.ExecuteWhenFinished(...)`. That queues the spawn
    /// to run after the ENTIRE map-generation long event has drained - long after
    /// ABBandedGeneration's carve (which is a postfix on `GenerateContentsIntoMap`), after
    /// `Scenario.PostMapGenerate`, after `Map.FinalizeInit`, and after our own GenerateMap
    /// postfix. The carve therefore cannot see it, and there is no later sweep that would.
    ///
    /// ⚠ AND THE SKY BANDS ARE NOT MERELY REACHABLE BY SUCH A SPAWNER, THEY ARE ITS
    /// FAVOURITE PLACE. VEF picks from `map.AllCells` in random order and its only cell
    /// tests are: no chunk here, terrain tag allowed, not water, and - the decisive one -
    /// "no non-Filth thing in this cell". It never asks about passability or standability.
    /// A generated surface band is dense with rock, plants, chunks and filth, so most
    /// surface cells FAIL that emptiness test, while a sky band is pristine void and passes
    /// every time. The result is not a rare misplacement; it is a systematic bias that puts
    /// the map's one wreck in mid-air above the colony nearly every run.
    ///
    /// ⚠ WHY THIS IS NOT ABBandSafety's EXISTING NET, WHICH LOOKS LIKE IT SHOULD COVER IT.
    /// `Patch_GenSpawn_ABNoVoidSpawn` is scoped two ways that both exclude this case, and
    /// both scopes are correct for what that file is about:
    ///   * it only fires for `Thing is Pawn` - a wreck is a Building;
    ///   * `ABBandSafety.InVoid` means THE GUTTER (out of bounds, or the impassable seam
    ///     rows between bands). A sky band's `AB_OpenAir` is not the gutter: it is a
    ///     legitimate in-band cell that simply has nothing under it.
    /// So the two files answer different questions. That one says "never spawn into the
    /// seam"; this one says "never come to rest on a cell with no floor". Keeping them
    /// apart keeps each rationale readable, and Harmony runs both prefixes happily.
    ///
    /// THE INVARIANT, stated once so future work can lean on it: a Thing that rests on the
    /// ground may not occupy a cell whose terrain is `AB_OpenAir`. That is a property of
    /// the MAP, not of any one mod, so enforcing it here fixes every present and future
    /// scatterer that reaches the normal spawn API - which is the same argument
    /// ABBandedGeneration makes for carving after the gensteps rather than constraining
    /// each one.
    ///
    /// ⚠ RELOCATE, NEVER REJECT. Cancelling the spawn would leave the other mod holding a
    /// live, unspawned Thing it believes it placed, and silently delete content the player
    /// paid for. We only correct `loc` before vanilla reads it, exactly as
    /// `Patch_GenSpawn_ABNoVoidSpawn` does, so nothing downstream ever observes the bad
    /// cell.
    ///
    /// ⚠ THE CATEGORY WHITELIST IS LOAD-BEARING, NOT TIDINESS. Several things legitimately
    /// exist over open air and must not be dragged down: skyfallers in flight (§ABSkyfaller
    /// relay), PawnFlyers mid-leap between bands (§ABBandLeap/§ABBandJump), projectiles
    /// crossing a level (§41 combat), motes, flecks and gas. All of those are either a
    /// distinct C# type or a non-ground ThingCategory, and both tests are applied.
    ///
    /// ⚠ INERT DURING GENERATION, ON PURPOSE. `ABBandMap.Banded` is still false while
    /// gensteps run, so this never fires in the generation window - where the carve is the
    /// better-informed authority and is about to delete the whole band's contents anyway.
    /// The bug being fixed happens strictly after that window, which is precisely why the
    /// carve missed it.
    /// </summary>
    public static class ABAirSpawnGuard
    {
        /// <summary>A guard clause that silently early-returns is indistinguishable from an
        /// unimplemented feature (§14). Counted, and printed by the subsystem report.</summary>
        public static int intercepted;

        public static int relocationFailures;

        public static int decorMoved;

        public static string lastIntercept = "none yet";

        public static void ResetStats()
        {
            intercepted = 0;
            relocationFailures = 0;
            decorMoved = 0;
            lastIntercept = "none yet";
        }

        // ---- the post-generation decoration window ---------------------------------
        //
        // ⚠ THE AIR RULE ALONE DOES NOT CATCH A WRECK IN THE BASEMENT, AND THAT IS NOT A
        // BUG IN IT. A basement cave floor is ordinary standable rock: it passes every test
        // the air guard makes, and passes every test VEF makes too. Nothing is floating and
        // nothing is wrong with the cell. What is wrong is the LEVEL, and no property of the
        // cell can tell you that - only knowing who put it there can.
        //
        // So this is the second, orthogonal rule: DURING THE DECORATION WINDOW, GROUND-RESTING
        // THINGS BELONG ON THE SURFACE BAND. The principle behind it is worth stating plainly
        // because it decides every future case of this shape: a decorator that has no concept
        // of levels is describing the ONE map it thinks it is decorating, and that map is the
        // one the player starts on.
        //
        // ⚠ THE WINDOW IS DEFINED BY THE TICK CLOCK, NOT BY A FLAG SOMEONE HAS TO CLEAR.
        // `LongEventHandler.ExecuteWhenFinished` callbacks - VEF's `DoMapSpawns` and every
        // other decorator riding that pattern - all run after generation and before the map
        // has ticked once. Arming with the current tick and treating the window as open only
        // while `TicksGame` still equals it makes the window close BY ITSELF the moment the
        // map comes alive. There is no disarm call to forget, no MapComponentTick to pay for
        // every tick of every game, and a load (which never arms) can never reopen it.
        //
        // ⚠ AND IT IS GATED ON FACTION AS WELL AS ON TIME. A brand new colony sits paused at
        // tick 0, so the window can stay open for as long as the player looks around before
        // unpausing. Requiring the thing to be unfactioned keeps the rule aimed at scattered
        // scenery and away from anything the player or a faction owns - which also means dev
        // mode placement during that pause behaves normally.

        // ⚠⚠ AND IT IS SCOPED TO THE MAP IT WAS ARMED FOR, WHICH THE TICK ALONE CANNOT DO.
        //
        // §57: `TicksGame` RESTARTS AT ZERO FOR EVERY NEW GAME, so "the tick I armed at" is
        // not a unique moment - it collides with every later colony generated in the same
        // session. Settle one colony (window arms at tick 0, paused), quit to the menu,
        // settle a second: that second map generates at tick 0 too, so the FIRST colony's
        // window was still reported open for the whole of the second colony's generation.
        //
        // That is not a cosmetic overlap. Since the carve moved INSIDE the generation window
        // it runs with `bands.Banded == true`, so this guard is live while the carve is
        // spawning basement rock - and natural rock is unfactioned and rests on ground, so
        // `IsLooseDecoration` says yes to every single block. Measured at run #46: 18,075
        // basement rocks relocated onto the surface band, which was left with 512 open cells
        // out of 36,100 and no standable start cell at all.
        //
        // The map reference is the fix: a tick number identifies WHEN, and we also need
        // WHICH. Held as a plain reference, replaced on every arm and never read after the
        // window closes, so it cannot pin a dead map alive for any meaningful time.
        private static int decorWindowTick = int.MinValue;

        private static Map decorWindowMap;

        internal static void ArmDecorationWindow(Map map)
        {
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            decorWindowTick = Find.TickManager?.TicksGame ?? 0;
            decorWindowMap = map;
        }

        /// <summary>Is the decoration window open FOR THIS MAP? Both halves are required:
        /// the tick says when, the map says which (§57).</summary>
        internal static bool DecorationWindowOpenFor(Map map)
        {
            if (decorWindowTick == int.MinValue || map == null
                || !ReferenceEquals(map, decorWindowMap))
            {
                return false;
            }
            return (Find.TickManager?.TicksGame ?? int.MinValue) == decorWindowTick;
        }

        /// <summary>Scenery placed by something that does not know levels exist. Faction-owned
        /// things are excluded: they belong to somebody, and somebody's property is not
        /// scenery.</summary>
        internal static bool IsLooseDecoration(Thing t)
        {
            return t != null && t.Faction == null && RestsOnGround(t);
        }

        /// <summary>
        /// Does this Thing rest on the ground?
        ///
        /// Two independent tests, deliberately. The TYPE test catches the things that are
        /// airborne by definition and whose defs do not agree on a category (Skyfaller and
        /// PawnFlyer are Ethereal in vanilla but modded subclasses have been seen declaring
        /// Building). The CATEGORY test is the positive list: only these four kinds of thing
        /// have any business needing a floor.
        /// </summary>
        internal static bool RestsOnGround(Thing t)
        {
            if (t == null || t.def == null)
            {
                return false;
            }
            if (t is Mote || t is Projectile || t is Skyfaller || t is PawnFlyer
                || t is Filth || t is Gas)
            {
                return false;
            }
            ThingCategory cat = t.def.category;
            return cat == ThingCategory.Building
                || cat == ThingCategory.Item
                || cat == ThingCategory.Pawn
                || cat == ThingCategory.Plant;
        }

        /// <summary>True when ANY cell of the footprint is open air.
        ///
        /// "Any" rather than "all" because a 2x4 wreck half on a rooftop and half over the
        /// void is exactly as wrong as one entirely over the void, and reads worse.</summary>
        internal static bool Floating(Map map, IntVec3 loc, Rot4 rot, Thing t)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            if (air == null)
            {
                return false;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, t.def.Size))
            {
                if (!c.InBounds(map))
                {
                    continue; // out of bounds is ABBandSafety's problem, not ours
                }
                if (map.terrainGrid.TerrainAt(c) == air)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Every cell of the footprint is standable and not open air.</summary>
        private static bool FootprintOk(Map map, IntVec3 at, Rot4 rot, Thing t, CellRect limit)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            foreach (IntVec3 c in GenAdj.OccupiedRect(at, rot, t.def.Size))
            {
                if (!c.InBounds(map) || !limit.Contains(c))
                {
                    return false;
                }
                if (map.terrainGrid.TerrainAt(c) == air || !c.Standable(map))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Find real ground for a thing that was about to be placed on air.
        ///
        /// FALL, DO NOT TELEPORT. The search walks DOWN the same column band by band and
        /// takes the first level that can hold the footprint, which is what gravity would
        /// have done and keeps the object where the other mod's own placement logic wanted
        /// it in map-plan terms (VEF's road and biome filters were evaluated on the column,
        /// not on the altitude). Only if the whole column is void does it widen to a search
        /// on the surface band.
        /// </summary>
        internal static bool TryFindFooting(Map map, ABBandMap bands, IntVec3 loc, Rot4 rot,
            Thing t, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            int from = bands.BandOf(loc);
            if (from < 0)
            {
                from = bands.surfaceBand;
            }
            for (int band = from - 1; band >= 0; band--)
            {
                if (!bands.BandExists(band))
                {
                    continue;
                }
                IntVec3 candidate = bands.Translate(loc, band);
                if (FootprintOk(map, candidate, rot, t, bands.RectOfBand(band)))
                {
                    result = candidate;
                    return true;
                }
            }

            // Nothing under this column at all. Land it on the surface band instead - the
            // level the player actually lives on, and the one ABBandSafety already treats
            // as the destination of last resort.
            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            IntVec3 seed = bands.Translate(loc, bands.surfaceBand);
            Predicate<IntVec3> ok = c => FootprintOk(map, c, rot, t, surface);
            if (ok(seed))
            {
                result = seed;
                return true;
            }
            if (CellFinder.TryFindRandomCellNear(seed, map, 24, ok, out result)
                || CellFinder.TryFindRandomCellNear(seed, map, 64, ok, out result))
            {
                return true;
            }
            // Deterministic sweep so a crowded surface cannot silently fail the net.
            foreach (IntVec3 c in surface)
            {
                if (ok(c))
                {
                    result = c;
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// The choke point. Every overload of GenSpawn.Spawn funnels into this one, so a single
    /// prefix covers map scatterers, quest spawns, dev-mode placement and any mod that
    /// spawns through the normal API.
    /// </summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new Type[]
    {
        typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode),
        typeof(bool), typeof(bool)
    })]
    public static class Patch_GenSpawn_ABNoAirSpawn
    {
        private static void Prefix(Thing newThing, ref IntVec3 loc, Map map, Rot4 rot,
            bool respawningAfterLoad)
        {
            try
            {
                // A load is replaying placements this guard already approved once. Moving
                // them again would drift the colony a little further every reload.
                if (respawningAfterLoad || map == null)
                {
                    return;
                }
                // ⚠ §57: THE CARVE IS AUTHORITATIVE ABOUT NON-SURFACE BANDS. It is
                // deliberately filling them with rock, and this guard exists to move things
                // OUT of them - so while it runs, every correction here is wrong by
                // construction. Same reasoning, and the same window, as ABSkySync.Suspended.
                if (ABBandedGeneration.CarveInProgress)
                {
                    return;
                }
                if (!ABAirSpawnGuard.RestsOnGround(newThing))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return; // non-banded map, or still mid-generation: not ours to correct
                }
                // Rule two, checked first because it subsumes rule one: during the decoration
                // window a loose thing outside the surface band goes to the surface band,
                // whether it was floating or sitting comfortably in a cave.
                if (ABAirSpawnGuard.DecorationWindowOpenFor(map)
                    && ABAirSpawnGuard.IsLooseDecoration(newThing)
                    && bands.BandOf(loc) != bands.surfaceBand)
                {
                    if (ABBandSafety.TryFindSurfaceCell(map, bands, loc, true,
                            out IntVec3 onSurface))
                    {
                        ABAirSpawnGuard.decorMoved++;
                        ABAirSpawnGuard.lastIntercept = newThing.def.defName + " " + loc
                            + " -> " + onSurface + " (off-surface decoration)";
                        ABLog.Dev("Decoration spawn moved to the surface band: "
                            + ABAirSpawnGuard.lastIntercept + ".");
                        loc = onSurface;
                    }
                    return;
                }
                if (!ABAirSpawnGuard.Floating(map, loc, rot, newThing))
                {
                    return; // the overwhelmingly common case, and it is one terrain read
                }
                if (ABAirSpawnGuard.TryFindFooting(map, bands, loc, rot, newThing,
                        out IntVec3 footing))
                {
                    ABAirSpawnGuard.intercepted++;
                    ABAirSpawnGuard.lastIntercept = newThing.def.defName + " " + loc
                        + " -> " + footing;
                    ABLog.Dev("Air spawn intercepted: " + ABAirSpawnGuard.lastIntercept + ".");
                    loc = footing;
                }
                else
                {
                    // Leaving it in the air is ugly; deleting it is worse, and silently
                    // doing neither is worst of all. Count it so the report can say so.
                    ABAirSpawnGuard.relocationFailures++;
                    ABAirSpawnGuard.lastIntercept = newThing.def.defName + " " + loc
                        + " -> NO FOOTING FOUND";
                }
            }
            catch
            {
                // Never let the safety net be the thing that breaks a spawn.
            }
        }
    }
}
