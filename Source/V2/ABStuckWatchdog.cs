using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Automatic detector for "pawn is stuck", in the two shapes it actually takes.
    ///
    /// Manual probing cannot catch either reliably: any single sample lands on a frame where
    /// everything looks healthy. Only a comparison ACROSS ticks can see them.
    ///
    /// EPISODE A - WALKING, NOT ARRIVING (the original). The pawn reports moving=True with a
    /// valid short path but keeps stepping and re-targeting without making progress.
    ///
    /// EPISODE B - FROZEN UNDER A LIVE JOB (added after the stairwell hang). moving=False,
    /// and nothing will ever change that.
    ///
    /// ⚠⚠ EPISODE B EXISTED FOR MONTHS AND THIS FILE COULD NOT SEE IT. The original report
    /// was gated on `moving`, which is precisely the flag a wedged pawn does not have:
    /// Notify_Teleported -> Pawn_PathFollower.StopDead() clears the path and sets
    /// moving=false WITHOUT notifying the JobDriver, so a toil that completes on
    /// ToilCompleteMode.PatherArrival waits forever. The instrument was structurally unable
    /// to report the bug it was built to find - three of five early returns again.
    ///
    /// ⚠ THE DISCRIMINATOR IS THE TOIL'S COMPLETE MODE, NOT THE STILL TIME. Plenty of healthy
    /// pawns stand motionless under a live job for minutes (crafting, mining, surgery,
    /// charging, sleeping) - a pure "has not moved" test would drown the log. A toil that can
    /// ONLY be completed by a pushed pather arrival, on a pather that is not moving, is
    /// unambiguously wedged: there is no code path left that can finish it. Delay / Never /
    /// FinishedBusy toils are excluded because something else is still driving them.
    ///
    /// Watch set: anything within WatchRadius of a wormhole anchor (episode A happens at the
    /// stairwell) plus every player-faction pawn anywhere (episode B strands a pawn wherever
    /// its last leg ended, typically nowhere near the stairs - a mech parked on the surface
    /// that "forgot" to go back down is the reported case).
    ///
    /// Reading the report:
    ///   FROZEN                       -> live job on a dead pather; see the toil fields
    ///   path NOT FOUND               -> connectivity: regions connected, no walkable route
    ///   path FOUND, destChanges high -> re-targeting loop (a job giver re-picking every tick)
    ///   path FOUND, destChanges low  -> movement blocked (traffic, door, reservation)
    /// </summary>
    public static class ABStuckWatchdog
    {
        private const int StuckTicks = 180;   // ~3 seconds at normal speed
        private const int WatchRadius = 8;    // cells from an anchor

        private struct Watch
        {
            public IntVec3 lastPos;
            public IntVec3 lastDest;
            public int stillSinceTick;
            public int destChanges;
            public bool reported;
        }

        private static readonly Dictionary<int, Watch> watching = new Dictionary<int, Watch>();

        private static readonly List<int> tmpDrop = new List<int>();

        /// <summary>Pawn-id keyed, so it must not cross a game load - see the banner on
        /// ABWormholePather.ResetForNewGame. Stale entries here only cost a false episode
        /// (the watchdog would compare a loaded pawn against a previous session's position),
        /// but a diagnostic that cries wolf after every load is worse than none.</summary>
        [ABGameReset]
        public static void ResetForNewGame()
        {
            watching.Clear();
            tmpDrop.Clear();
        }

        [ABGameTick(75)]
        public static void Tick()
        {
            if (!ABV2Debug.LogTransit)
            {
                if (watching.Count > 0) watching.Clear();
                return; // opt-in: this is a diagnostic, not a gameplay system
            }
            Map map = Find.CurrentMap;
            if (map == null || !ABBands.Banded(map) || ABWormhole.PairCount(map) == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            tmpDrop.Clear();

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || !p.Spawned || p.Dead)
                {
                    continue;
                }
                // Episode B is NOT a stairwell phenomenon - the pawn is stranded wherever its
                // last leg happened to end - so the anchor filter cannot be the only gate.
                if (!ABWormhole.NearAnyAnchor(map, p.Position, WatchRadius)
                    && p.Faction != Faction.OfPlayer)
                {
                    watching.Remove(p.thingIDNumber);
                    continue;
                }

                IntVec3 dest = p.pather != null && p.pather.Destination.IsValid
                    ? p.pather.Destination.Cell
                    : IntVec3.Invalid;

                if (!watching.TryGetValue(p.thingIDNumber, out Watch w))
                {
                    watching[p.thingIDNumber] = new Watch
                    {
                        lastPos = p.Position,
                        lastDest = dest,
                        stillSinceTick = now,
                        destChanges = 0,
                        reported = false
                    };
                    continue;
                }

                if (dest != w.lastDest)
                {
                    w.destChanges++;
                    w.lastDest = dest;
                }

                if (p.Position != w.lastPos)
                {
                    // Real progress: reset the episode.
                    w.lastPos = p.Position;
                    w.stillSinceTick = now;
                    w.destChanges = 0;
                    w.reported = false;
                    watching[p.thingIDNumber] = w;
                    continue;
                }

                bool moving = p.pather != null && p.pather.Moving;
                if (!w.reported && now - w.stillSinceTick >= StuckTicks
                    && (moving || WedgedOnArrival(p)))
                {
                    w.reported = true;
                    Report(map, p, dest, now - w.stillSinceTick, w.destChanges, moving);
                }
                watching[p.thingIDNumber] = w;
            }

            for (int i = 0; i < tmpDrop.Count; i++)
            {
                watching.Remove(tmpDrop[i]);
            }
            tmpDrop.Clear();
        }

        /// <summary>
        /// True when the pawn's current toil can only ever be completed by a pather arrival
        /// that can no longer happen.
        ///
        /// `JobDriver.CurToil` is protected, so it is read reflectively - acceptable here and
        /// nowhere else: this whole file is opt-in behind LogTransit and runs for a handful of
        /// pawns every 75 ticks. CurToilIndex and CurJob are checked first because the getter
        /// itself logs an error on an inconsistent driver, and a diagnostic that spams errors
        /// while diagnosing is worse than no diagnostic.
        /// </summary>
        private static readonly MethodInfo CurToilGetter =
            AccessTools.PropertyGetter(typeof(JobDriver), "CurToil");

        private static bool WedgedOnArrival(Pawn p)
        {
            return CurToilMode(p) == ToilCompleteMode.PatherArrival;
        }

        private static ToilCompleteMode? CurToilMode(Pawn p)
        {
            JobDriver driver = p.jobs?.curDriver;
            if (driver == null || CurToilGetter == null || p.CurJob == null
                || driver.CurToilIndex < 0)
            {
                return null;
            }
            try
            {
                return (CurToilGetter.Invoke(driver, null) as Toil)?.defaultCompleteMode;
            }
            catch
            {
                return null;
            }
        }

        private static void Report(Map map, Pawn p, IntVec3 dest, int stillFor, int destChanges,
            bool moving)
        {
            bool canReach = false;
            string pathState = "no destination";
            if (dest.IsValid)
            {
                canReach = p.CanReach(dest, PathEndMode.OnCell, Danger.Deadly);
                PawnPath path = null;
                try
                {
                    path = map.pathFinder.FindPathNow(p.Position, dest,
                        TraverseParms.For(p), null, PathEndMode.OnCell);
                    pathState = path != null && path.Found
                        ? "FOUND (" + path.NodesLeftCount + " nodes)"
                        : "NOT FOUND";
                }
                finally
                {
                    if (path != null) path.ReleaseToPool();
                }
            }

            // WHAT is in the way. Without this the BLOCKED verdict names a category but not a
            // culprit, and the three culprits below need entirely different fixes.
            string nextCellInfo = "n/a";
            if (p.pather != null)
            {
                IntVec3 next = p.pather.nextCell;
                if (next.IsValid && next.InBounds(map))
                {
                    Pawn occupant = next.GetFirstPawn(map);
                    Building edifice = next.GetEdifice(map);
                    Building_Door door = edifice as Building_Door;
                    nextCellInfo = next
                        + " walkable=" + next.Walkable(map)
                        + " occupant=" + (occupant != null && occupant != p
                            ? occupant.LabelShortCap + "(" + (occupant.pather != null && occupant.pather.Moving
                                ? "moving" : "idle") + ")"
                            : "none")
                        + " edifice=" + (edifice != null ? edifice.def.defName : "-")
                        + (door != null
                            ? " door[open=" + door.Open + " freePassage=" + door.FreePassage
                              + " blockedOpenMomentary=" + door.BlockedOpenMomentary + "]"
                            : string.Empty);
                }
            }

            string verdict = !moving
                ? "FROZEN - live job on a stopped pather; its toil completes only on a pather "
                  + "arrival that can no longer fire (teleport without re-dispatch, or a leg "
                  + "that ended without notifying the driver). Draft/undraft would clear it."
                : (pathState == "NOT FOUND"
                    ? "CONNECTIVITY - region says reachable, no walkable route"
                    : (destChanges >= 3
                        ? "RE-TARGETING - destination changed " + destChanges + " times while standing still"
                        : "BLOCKED - path exists and destination is stable, so movement is obstructed"));

            // ONE self-contained message: separate Log calls from here would share a stack
            // signature and be folded into a single class by the log monitor.
            Log.Warning(ABLog.Tag + " V2 STUCK: " + p.LabelShortCap
                + " at " + p.Position + " band " + ABBands.BandOf(map, p.Position)
                + " | still for " + stillFor + " ticks"
                + " | job=" + (p.CurJob?.def?.defName ?? "none")
                + " | toil=" + (p.jobs?.curDriver != null ? p.jobs.curDriver.CurToilIndex : -1)
                + "/" + (CurToilMode(p)?.ToString() ?? "unknown")
                + " ticksLeft=" + (p.jobs?.curDriver != null ? p.jobs.curDriver.ticksLeftThisToil : -1)
                + " | moving=" + moving
                + " | carrying=" + (p.carryTracker?.CarriedThing?.LabelCap ?? "nothing")
                + " | dest=" + dest + " band " + (dest.IsValid ? ABBands.BandOf(map, dest) : -1)
                + " | CanReach=" + canReach
                + " | path=" + pathState
                + " | destChanges=" + destChanges
                + " | pendingTransit=" + ABWormholePather.HasPending(p)
                + " | nextCell=" + nextCellInfo
                + " | verdict=" + verdict);
        }
    }
}
