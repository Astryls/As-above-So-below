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

        // ⚠⚠ VANILLA'S OWN TWO GATES, READ RATHER THAN RE-DERIVED (rule 36). PatherTick
        // opens with `if (WillCollideWithPawnAt(Position, forceOnlyStanding: true,
        // useId: true)) { if (FailedToFindCloseUnoccupiedCellRecently()) return; }` -
        // an early return AHEAD of every movement statement, which is the whole of
        // §89. Reimplementing the collision test was rejected: the `useId` tie-break
        // decides WHICH of two co-located pawns is allowed to yield, and guessing it
        // wrong inverts the answer on exactly the case being diagnosed.
        //
        // Reflected as MethodInfo/FieldInfo rather than delegates so a vanilla rename
        // leaves them null and the report degrades to its old wording, instead of
        // throwing at type-init and taking the whole watchdog down with it.
        private static readonly MethodInfo WillCollideMethod =
            AccessTools.Method(typeof(Pawn_PathFollower), "WillCollideWithPawnAt",
                new[] { typeof(IntVec3), typeof(bool), typeof(bool) });

        private static readonly FieldInfo FailedTicksField = AccessTools.Field(
            typeof(Pawn_PathFollower), "failedToFindCloseUnoccupiedCellTicks");

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
            // culprit, and the culprits below need entirely different fixes.
            //
            // ⚠⚠ THIS USED TO ASK FOR *THE* OCCUPANT AND THAT IS WHY §89 SURVIVED FIVE
            // SIGHTINGS (rule 71). It did `next.GetFirstPawn(map)` and then discarded the
            // answer when it was the watched pawn itself - so for the one arrangement
            // that actually matters, TWO PAWNS ON ONE CELL, it printed `occupant=none`
            // and the report read as "nothing is in the way" while the pawn was, in
            // effect, in its own way. A probe that peeks at the first entry cannot see a
            // stack. It now COUNTS, and names every pawn including self.
            bool selfStack = false;
            string nextCellInfo = "n/a";
            if (p.pather != null)
            {
                IntVec3 next = p.pather.nextCell;
                selfStack = next.IsValid && next == p.Position;
                if (next.IsValid && next.InBounds(map))
                {
                    Building edifice = next.GetEdifice(map);
                    Building_Door door = edifice as Building_Door;
                    nextCellInfo = next
                        + (selfStack ? " (== PAWN'S OWN CELL)" : string.Empty)
                        + " walkable=" + next.Walkable(map)
                        + " pawnsHere=" + PawnCensus(map, next, p)
                        + " edifice=" + (edifice != null ? edifice.def.defName : "-")
                        + (door != null
                            ? " door[open=" + door.Open + " freePassage=" + door.FreePassage
                              + " blockedOpenMomentary=" + door.BlockedOpenMomentary + "]"
                            : string.Empty);
                }
            }

            // Vanilla's verdict on the same question, straight from its own state.
            bool collides = CollidesHere(p);
            int gaveUpAge = UnstickGaveUpTicksAgo(p);
            // ⚠ SAY SO WHEN THE INSTRUMENT IS DEGRADED (rule 33). Both members are
            // private vanilla state; a rename leaves them null and this file quietly
            // reverts to the exact wording that hid §89 for five sightings. A
            // diagnostic that loses a sense must announce it, or the next person
            // spends four runs trusting a verdict it can no longer support.
            if (WillCollideMethod == null || FailedTicksField == null)
            {
                Log.WarningOnce(ABLog.Tag + " V2: stuck watchdog could not bind vanilla's"
                    + " PatherTick gates (WillCollideWithPawnAt="
                    + (WillCollideMethod != null)
                    + ", failedToFindCloseUnoccupiedCellTicks="
                    + (FailedTicksField != null)
                    + "); SELF-STACK episodes will be misreported as BLOCKED.",
                    0x2B10C1);
            }

            string verdict = !moving
                ? "FROZEN - live job on a stopped pather; its toil completes only on a pather "
                  + "arrival that can no longer fire (teleport without re-dispatch, or a leg "
                  + "that ended without notifying the driver). Draft/undraft would clear it."
                : (pathState == "NOT FOUND"
                    ? "CONNECTIVITY - region says reachable, no walkable route"
                    : (destChanges >= 3
                        ? "RE-TARGETING - destination changed " + destChanges + " times while standing still"
                        : (gaveUpAge >= 0 || (collides && selfStack)
                            ? "SELF-STACK - another STANDING pawn shares this cell. Vanilla's "
                              + "PatherTick returns before ANY movement code while "
                              + "TryFindBestPawnStandCell keeps failing"
                              + (gaveUpAge >= 0
                                  ? " (it gave up " + gaveUpAge + " ticks ago; it re-tries "
                                    + "every 100)"
                                  : " (collision confirmed, timestamp unreadable)")
                              + ". NOT obstructed - co-located. Read pawnsHere: a "
                              + "co-occupant marked `transit` makes this OURS (\u00a778's hold "
                              + "parks a crossing pawn with StopDead for 90t, \u00a785.19 "
                              + "queues onto the approach tile); anything else is vanilla "
                              + "traffic and not our bug"
                            : "BLOCKED - path exists and destination is stable, so movement "
                              + "is obstructed")));

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
                + " | collidesHere=" + collides
                + " | unstickGaveUp=" + (gaveUpAge >= 0 ? gaveUpAge + "t ago" : "no")
                + " | verdict=" + verdict);
        }

        /// <summary>
        /// Every pawn standing on a cell, SELF INCLUDED and labelled as such.
        ///
        /// Self is counted rather than filtered because the count is the finding: one
        /// is normal, two is <see href="#">\u00a789</see>. Each is tagged moving/idle
        /// (vanilla's gate is <c>forceOnlyStanding</c>, so only idle ones wedge a
        /// pather) and, decisively, whether it is mid-transit - which is what tells
        /// a stairs-side stack of ours apart from ordinary vanilla traffic.
        /// </summary>
        private static string PawnCensus(Map map, IntVec3 c, Pawn self)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            int count = 0;
            string names = string.Empty;
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is Pawn q))
                {
                    continue;
                }
                count++;
                names += (names.Length > 0 ? "," : string.Empty)
                    + (q == self ? "SELF" : q.LabelShortCap)
                    + "(" + (q.pather != null && q.pather.Moving ? "moving" : "idle")
                    + (ABWormholePather.HasPending(q) ? ",TRANSIT" : string.Empty) + ")";
            }
            return count + (count > 0 ? " [" + names + "]" : string.Empty);
        }

        /// <summary>Vanilla's own "another standing pawn is on my cell" test, invoked
        /// rather than reimplemented - see the banner on WillCollideMethod.</summary>
        private static bool CollidesHere(Pawn p)
        {
            if (WillCollideMethod == null || p?.pather == null)
            {
                return false;
            }
            try
            {
                return WillCollideMethod.Invoke(p.pather,
                    new object[] { p.Position, true, true }) is bool b && b;
            }
            catch
            {
                return false; // a diagnostic must never be the thing that throws
            }
        }

        /// <summary>
        /// How long ago vanilla's unstick search last failed, or -1 if it has not
        /// failed recently.
        ///
        /// ⚠ THE 100-TICK WINDOW IS VANILLA'S, COPIED VERBATIM from
        /// <c>FailedToFindCloseUnoccupiedCellRecently</c>
        /// (<c>failedToFindCloseUnoccupiedCellTicks + 100 &gt; TicksGame</c>). Inside it,
        /// PatherTick returns early every tick. The default is -999999, so an
        /// untouched field falls out of the window and reads as "no".
        ///
        /// Reflection cost is irrelevant here: this runs once per REPORTED episode,
        /// not per tick.
        /// </summary>
        private static int UnstickGaveUpTicksAgo(Pawn p)
        {
            if (FailedTicksField == null || p?.pather == null)
            {
                return -1;
            }
            try
            {
                if (!(FailedTicksField.GetValue(p.pather) is int t))
                {
                    return -1;
                }
                int age = Find.TickManager.TicksGame - t;
                return age >= 0 && age < 100 ? age : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
