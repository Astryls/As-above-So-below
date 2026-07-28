using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Automatic detector for "pawn is stuck near a stairwell".
    ///
    /// Manual probing cannot catch this reliably. The pawn reports moving=True with a valid
    /// short path, so any single sample lands on a frame where everything looks healthy - the
    /// failure is not a frozen pawn, it is a pawn that keeps stepping and re-targeting without
    /// making progress. Only a comparison ACROSS ticks can see that.
    ///
    /// Watches only pawns within WatchRadius of a wormhole anchor, so the per-tick cost is a
    /// handful of distance checks even on a busy colony. A pawn whose position has not changed
    /// for StuckTicks while its pather still claims to be moving is reported ONCE per episode,
    /// with everything needed to classify it: the job, the destination, whether a path exists,
    /// and how often its destination has been changing.
    ///
    /// Reading the report:
    ///   path NOT FOUND        -> connectivity: region graph connected, no walkable route
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
                if (!ABWormhole.NearAnyAnchor(map, p.Position, WatchRadius))
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
                if (!w.reported && moving && now - w.stillSinceTick >= StuckTicks)
                {
                    w.reported = true;
                    Report(map, p, dest, now - w.stillSinceTick, w.destChanges);
                }
                watching[p.thingIDNumber] = w;
            }

            for (int i = 0; i < tmpDrop.Count; i++)
            {
                watching.Remove(tmpDrop[i]);
            }
            tmpDrop.Clear();
        }

        private static void Report(Map map, Pawn p, IntVec3 dest, int stillFor, int destChanges)
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

            string verdict = pathState == "NOT FOUND"
                ? "CONNECTIVITY - region says reachable, no walkable route"
                : (destChanges >= 3
                    ? "RE-TARGETING - destination changed " + destChanges + " times while standing still"
                    : "BLOCKED - path exists and destination is stable, so movement is obstructed");

            // ONE self-contained message: separate Log calls from here would share a stack
            // signature and be folded into a single class by the log monitor.
            Log.Warning(ABLog.Tag + " V2 STUCK: " + p.LabelShortCap
                + " at " + p.Position + " band " + ABBands.BandOf(map, p.Position)
                + " | still for " + stillFor + " ticks"
                + " | job=" + (p.CurJob?.def?.defName ?? "none")
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
