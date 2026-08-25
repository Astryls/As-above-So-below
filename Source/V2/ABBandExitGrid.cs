using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE RING: "the edge of the world", as a banded map means it - the SURFACE band's own
    /// perimeter, all four sides, not the outer ring of the whole stack.
    ///
    /// Vanilla's ExitMapGrid marks a two-cell ring around CellRect.WholeMap, which on a
    /// banded map lands on the deepest basement's floor rows, the sky band's top rows and
    /// the x-edges of every level - while the surface band's north and south boundaries
    /// (the seams) get nothing. ABBandEdges already redirects vanilla's edge-CELL pickers
    /// to the surface perimeter; this is the matching geometry for the exit GRID.
    ///
    /// One definition, three consumers: the IsExitCell verdict, the green overlay, and the
    /// leaver sweep below. They MUST agree - a ring the player can see but not use, or use
    /// but not see, is worse than no ring at all.
    /// </summary>
    internal static class ABBandExitRing
    {
        /// <summary>Within the two-cell ring of the surface band's rect - vanilla's own
        /// ring width, applied to the level instead of the stack.</summary>
        internal static bool OnSurfaceRing(Map map, ABBandMap bands, IntVec3 c)
        {
            if (map == null || bands == null || !c.InBounds(map) || bands.InGutter(c)
                || bands.BandOf(c) != bands.surfaceBand)
            {
                return false;
            }
            CellRect r = bands.RectOfBand(bands.surfaceBand);
            return c.x - r.minX <= 1 || r.maxX - c.x <= 1
                || c.z - r.minZ <= 1 || r.maxZ - c.z <= 1;
        }

        /// <summary>The exit direction a pawn leaving from <paramref name="c"/> departs in.
        ///
        /// ⚠ NOT `CellRect.WholeMap(map).GetClosestEdge(c)`, which is what every vanilla
        /// exit call site uses: on a banded map the closest edge of the STACK to a surface
        /// cell can be south through three basements, and that Rot4 is what picks the world
        /// tile the caravan or raider group walks to. The surface band's own rect gives the
        /// compass direction the player actually saw them leave by.</summary>
        internal static Rot4 SurfaceExitDir(ABBandMap bands, IntVec3 c)
        {
            return bands.RectOfBand(bands.surfaceBand).GetClosestEdge(c);
        }
    }

    /// <summary>
    /// Narrow vanilla's exit ring to the surface band's perimeter - and NOTHING MORE.
    ///
    /// ⚠⚠ THE `MapUsesExitGrid` GUARD IS THE WHOLE PATCH, NOT A FORMALITY. `ShouldBand`
    /// bands only the player's own colony Settlement maps, and `MapUsesExitGridNow` opens
    /// with `if (map.IsPlayerHome) return false` - so on a stock install vanilla's grid is
    /// OFF on 100% of banded maps. An earlier version of this postfix also ADDED cells when
    /// "the grid was off for this map", which did not narrow anything: it MANUFACTURED an
    /// exit grid on the player's colony, where vanilla deliberately has none. The cost was
    /// not theoretical. `FloatMenuOptionProvider_DraftedMove.PawnGotoAction` reads exactly
    /// this method to decide `job.exitMapOnArrival`, so an ordinary move order onto a cell
    /// within two of the surface perimeter - which includes the SEAM ROWS, i.e. mid-screen -
    /// made the colonist walk there and hit `Pawn.ExitMap`. On a home map
    /// `CanExitMapAndJoinOrCreateCaravanNow` is false (it reads `MapUsesExitGrid` itself),
    /// so ExitMap skipped the caravan branch and ran straight to `PassToWorld`: the colonist
    /// left the colony permanently, with no caravan, no letter and no confirmation. And the
    /// ring was INVISIBLE, because `Drawer` early-outs on the same property we were
    /// ignoring. `MultiPawnGotoController` reads it too, so one drag order could do it to a
    /// whole squad.
    ///
    /// So: when vanilla's grid is off, we are off. On a stock install this patch is a no-op
    /// and exit semantics are exactly vanilla's. It comes alive only where another mod turns
    /// the grid on for a colony map - Walk The World does precisely that (§46), which is the
    /// case this was written for - and there it fixes the reported "works in some places,
    /// silent in others": with the ring corrected the surface band's whole perimeter answers
    /// true, and the underground/sky rings answer false, because walking to a basement's
    /// x-edge is not standing at the edge of the world.
    ///
    /// The despawn side of §46 does NOT ride on this any more; it is served pawn-side by
    /// ABBandLeaverExit below, which can tell a committed leaver from a colonist the player
    /// just told to walk somewhere. A cell cannot.
    ///
    /// COMPOSITION RULES:
    ///   * __runOriginal: when another mod's prefix vetoed the call (WTW skips its own
    ///     special walk-in maps), the verdict is theirs and this patch stands down.
    ///   * A cell vanilla already accepted is only ever POSITION-filtered, never
    ///     re-validated - walkability was the grid builder's call to make.
    ///   * A cell this patch ADDS gets the walkable + unfogged test the grid builder
    ///     would have applied.
    /// </summary>
    [HarmonyPatch(typeof(ExitMapGrid), nameof(ExitMapGrid.IsExitCell))]
    public static class Patch_ExitMapGrid_ABSurfaceRing
    {
        internal static readonly AccessTools.FieldRef<ExitMapGrid, Map> MapRef =
            AccessTools.FieldRefAccess<ExitMapGrid, Map>("map");

        private static void Postfix(ExitMapGrid __instance, IntVec3 c, ref bool __result,
            bool __runOriginal)
        {
            try
            {
                if (!__runOriginal)
                {
                    return; // a foreign prefix decided; respect it
                }
                // ⚠ See the class note. No exit grid means no exit cells, ours included.
                // The original already read this (it is tick-cached), so it cannot throw
                // here without having thrown there first.
                if (!__instance.MapUsesExitGrid)
                {
                    return;
                }
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                bool onRing = ABBandExitRing.OnSurfaceRing(map, bands, c);
                if (__result)
                {
                    // Vanilla marked it (a stack-edge cell): keep only the surface band's.
                    if (!onRing)
                    {
                        __result = false;
                    }
                    return;
                }
                // Vanilla missed it (a seam-side perimeter cell): add it with the checks
                // the grid builder applies to its own.
                if (onRing && c.Walkable(map) && !c.Fogged(map))
                {
                    __result = true;
                }
            }
            catch
            {
                // Exit semantics must fail open to vanilla's answer.
            }
        }
    }

    /// <summary>
    /// The same correction for the GREEN OVERLAY, so what the player sees is what the game
    /// will do.
    ///
    /// `ExitMapGrid` is its own `ICellBoolGiver`: the drawer asks `GetCellBool`, which reads
    /// the raw `Grid` bitmap and never goes near `IsExitCell`. Correcting only the verdict
    /// therefore left the overlay lying in BOTH directions on a banded map - painted green
    /// over basement floor rows and sky rooftops that are not exits, and silent along the
    /// surface seams that are. This postfix mirrors the verdict patch cell for cell.
    ///
    /// No dirty-tracking is added: our added cells depend on `Walkable`, and vanilla already
    /// re-runs the drawer on `Notify_LOSBlockerSpawned/Despawned`, which is the same
    /// granularity its own `IsGoodExitCell` result has.
    /// </summary>
    [HarmonyPatch(typeof(ExitMapGrid), nameof(ExitMapGrid.GetCellBool))]
    public static class Patch_ExitMapGrid_ABSurfaceRingDraw
    {
        private static void Postfix(ExitMapGrid __instance, int index, ref bool __result,
            bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || !__instance.MapUsesExitGrid)
                {
                    return;
                }
                Map map = Patch_ExitMapGrid_ABSurfaceRing.MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                IntVec3 c = map.cellIndices.IndexToCell(index);
                bool onRing = ABBandExitRing.OnSurfaceRing(map, bands, c);
                if (__result)
                {
                    if (!onRing)
                    {
                        __result = false;
                    }
                    return;
                }
                // Vanilla's GetCellBool already excludes fogged cells from its own answer,
                // so an added cell owes the same two tests as in the verdict patch.
                if (onRing && c.Walkable(map) && !map.fogGrid.IsFogged(index))
                {
                    __result = true;
                }
            }
            catch
            {
                // A wrong overlay is cosmetic; an exception during map draw is not.
            }
        }
    }

    /// <summary>
    /// PAWN-SIDE DESPAWN: let a pawn that has already decided to leave actually leave when
    /// it is standing on the surface band's perimeter.
    ///
    /// ⚠⚠ WHY THIS IS NOT DONE BY WIDENING `IsExitCell` (this is §53's rule 9, one level up).
    /// `IsExitCell` answers a question about a CELL, but "may this pawn leave here" is a
    /// question about a PAWN - and the cell-level answer is the one vanilla has wired to
    /// irreversible pawn deletion through `FloatMenuOptionProvider_DraftedMove`. A cell
    /// cannot tell a retreating raider from a colonist the player just ordered to walk two
    /// tiles. So the cell question goes back to vanilla verbatim, and the pawn question is
    /// answered where the pawn is visible - where `p.Faction == player` is a check we can
    /// actually make, and do, first.
    ///
    /// WHAT IT IS FOR. Vanilla's despawn test on a home map reduces to
    /// `pawn.Position.OnEdge(map)`, i.e. the outer rows of the whole STACK. Both vanilla
    /// exit finders always return such a cell, so they are fine unaided. But
    /// `ABBandExits.Redirect` re-picks through `CellFinder.TryFindRandomEdgeCellWith`, which
    /// `Patch_CellFinder_ABTryFindRandomEdgeCellWith` answers with a uniform pick among the
    /// surface band rect's four sides - and two of those four are SEAM rows, which are not
    /// `OnEdge`. Left alone, roughly half of every redirected exit hands a leaver a cell it
    /// can arrive at and never despawn on. It does not freeze: the goto ends, the think tree
    /// re-issues, and the pawn loops around the ring forever. That is the "destChanges high"
    /// shape `ABStuckWatchdog` reports.
    ///
    /// ⚠ IT IS DELIBERATELY A SWEEP, NOT AN ARRIVAL HOOK. The re-issue loop means "has
    /// arrived and stopped" is a state the pawn may never hold for a sampled tick, so the
    /// condition is vanilla's PRE-TICK condition instead - committed to leaving, and
    /// standing on the ring - with no test on `pather.Moving`. Sampling every 20 ticks is
    /// enough because a looping leaver stays on the ring.
    ///
    /// ⚠ THE JOB FLAG IS NOT THE WHOLE COMMITMENT. `JobDriver_Goto` and `JobDriver_Flee`
    /// gate on `job.exitMapOnArrival`, but `JobDriver_TakeAndExitMap` - the raider walking
    /// off with your silver - carries no such flag and calls `ExitMap` on the grid verdict
    /// alone. Gate on the driver as well or thieves alone would be the ones left circling.
    /// </summary>
    public static class ABBandLeaverExit
    {
        private const int SweepInterval = 20;

        /// <summary>Dev counter: leavers this net had to despawn because the seam is not an
        /// engine map edge. A large number here means the exit redirect is aiming at seams
        /// far more often than expected, which is worth knowing on its own.</summary>
        public static int leaversExited;

        [ABGameTick(82)]
        public static void SweepLeavers()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing
                    || Scribe.mode != LoadSaveMode.Inactive
                    || Find.TickManager == null
                    || Find.TickManager.TicksGame % SweepInterval != 0)
                {
                    return;
                }
                Faction player = Faction.OfPlayerSilentFail;
                if (player == null)
                {
                    // ⚠ Cannot identify our own pawns, so despawn nobody. This net removes
                    // pawns from the game; it fails CLOSED, unlike the two patches above.
                    return;
                }
                List<Map> maps = Find.Maps;
                if (maps == null)
                {
                    return;
                }
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    ABBandMap bands = ABBands.CompOf(map);
                    if (bands == null || !bands.Banded)
                    {
                        continue;
                    }
                    IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                    // Backwards: ExitMap despawns, which mutates this list.
                    for (int j = pawns.Count - 1; j >= 0; j--)
                    {
                        Pawn p = j < pawns.Count ? pawns[j] : null;
                        if (p == null || !p.Spawned || p.Destroyed || p.Faction == player)
                        {
                            continue;
                        }
                        TryExit(map, bands, p);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: leaver sweep threw: " + e, 419772613);
            }
        }

        private static void TryExit(Map map, ABBandMap bands, Pawn p)
        {
            Job job = p.CurJob;
            if (job == null)
            {
                return;
            }
            if (!job.exitMapOnArrival && !(p.jobs?.curDriver is JobDriver_TakeAndExitMap))
            {
                return;
            }
            IntVec3 pos = p.Position;
            if (pos.OnEdge(map))
            {
                return; // vanilla's own check can see this one; do not race it
            }
            if (!ABBandExitRing.OnSurfaceRing(map, bands, pos))
            {
                return;
            }
            // Vanilla's gate, verbatim (JobDriver_Goto.TryExitMap and the FailOn that
            // JobDriver_TakeAndExitMap uses reduce to the same test).
            if (job.failIfCantJoinOrCreateCaravan
                && !CaravanExitMapUtility.CanExitMapAndJoinOrCreateCaravanNow(p))
            {
                return;
            }
            if (ModsConfig.BiotechActive)
            {
                MechanitorUtility.Notify_PawnGotoLeftMap(p, map);
            }
            // ⚠ Applied to the take-and-exit driver too, which vanilla does not do. A
            // metalhorror leaving on a stolen crate should still get the chance to emerge;
            // the asymmetry in vanilla reads like an oversight, and erring toward the
            // emergence is the safe direction.
            if (ModsConfig.AnomalyActive && !MetalhorrorUtility.TryPawnExitMap(p))
            {
                return;
            }
            leaversExited++;
            ABLog.Dev("Leaver exit: " + p.LabelShortCap + " left from the surface ring at "
                + pos + " (seam side; not an engine map edge).");
            p.ExitMap(true, ABBandExitRing.SurfaceExitDir(bands, pos));
        }
    }
}
