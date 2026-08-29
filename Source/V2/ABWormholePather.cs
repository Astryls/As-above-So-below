using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE - cross-band movement, done at the ONE place every pawn movement
    /// funnels through.
    ///
    /// 1.6's pathfinder is jobified (PathFinderJob is an IJob over NativeArray, with a
    /// NativePriorityQueue frontier), so A* neighbour expansion cannot be patched and
    /// the wormhole is invisible to it. Reachability says "yes" but the pathfinder
    /// would return NotFound -> PatherFailed -> job restart loop.
    ///
    /// The fix is to never ask the pathfinder to cross: segment the journey at
    /// Pawn_PathFollower.StartPath into (leg to near anchor) -> (transit) -> (leg to
    /// real destination). Both legs are ordinary intra-band paths.
    ///
    /// Why this single interception is the whole point of V2: EVERY job, every pawn,
    /// every faction and every third-party JobDriver reaches movement through
    /// StartPath. V1 needed ~130 patches because it had to widen each CONSUMER's idea
    /// of "my map"; here the consumers are untouched and only the movement primitive
    /// knows bands exist.
    /// </summary>
    public static class ABWormholePather
    {
        private struct Transit
        {
            public LocalTargetInfo realDest;
            public PathEndMode realPeMode;
            public Building_Door near;
            public Building_Door far;
            public int expiresAtTick;

            /// <summary>§78c: the cell ON the link that the pawn is walking to, or Invalid
            /// when no usable one exists. Standing here means standing on the stair art,
            /// which is where the clip is allowed to start.</summary>
            public IntVec3 entryCell;

            /// <summary>When the record was made, for the approach-patience backstop.</summary>
            public int startedTick;
        }

        /// <summary>
        /// How long a pawn that is already within ArriveRadius may keep walking toward its
        /// entry cell before we carry it anyway.
        ///
        /// ⚠⚠ THIS IS THE ONLY THING STANDING BETWEEN §78c AND A RE-RUN OF THE OLD STALLS.
        /// Waiting for the pawn to reach a specific cell is a NARROWER carry condition, and
        /// narrowing this condition has caused a stairwell jam every single time it has been
        /// tried (see the ArriveRadius banner). The patience makes the narrowing
        /// time-bounded rather than conditional: within two seconds of getting close, the
        /// pawn crosses whether or not it ever reached the cell.
        /// </summary>
        private const int ApproachPatienceTicks = 120;

        /// <summary>A transit record lives this long before being abandoned. Long enough to
        /// cross a band on foot, short enough that a stranded record cannot linger.</summary>
        private const int TransitTimeoutTicks = 4000;

        /// <summary>How far from the far anchor a transiting pawn may be set down.</summary>
        internal const int LandingRadius = 2;

        /// <summary>How close the pawn must get to the near anchor to be carried across.
        ///
        /// Deliberately LandingRadius + 1, not an independent number. Pawns that have already
        /// crossed are set down within LandingRadius of the anchor, so they occupy exactly the
        /// cells the next pawn wants to walk through - which makes that pawn come to rest up
        /// to LandingRadius cells short. An arrival test tighter than that rejects a pawn that
        /// is standing as close as it can physically get, the transit never completes, the job
        /// re-issues, and the stairwell jams. Observed as:
        ///   ARRIVE-MISMATCH Chewy at (92, 0, 659) expected (90, 0, 661)
        /// where the pawn was 2.83 cells away - blocked by a pawn that had just landed.</summary>
        private const int ArriveRadius = LandingRadius + 1;

        private static readonly Dictionary<int, Transit> pending = new Dictionary<int, Transit>();

        /// <summary>
        /// Snapshot of every in-flight transit, for the "AB2: transit health" dev action.
        ///
        /// A PROBE, not a fix. "Pawns get stuck at the stairs" has now had three different
        /// causes (stacked landings, mismatched arrival radii, cross-band wander roots), and
        /// each was diagnosed only after a wrong guess. The distinguishing facts are always
        /// the same four: how OLD the record is, what JOB owns it, how far the pawn still is
        /// from its near anchor, and whether that anchor is even in the pawn's band. A record
        /// ageing without the distance shrinking is a stuck pawn; a young record with a large
        /// distance is just a pawn still walking.
        /// </summary>
        public static string HealthReport(Map map)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("pending transits: " + pending.Count);
            // One line that splits "the transit side never fires" from "the draw side eats
            // it" - the two invisible halves of "the stair animations are gone". See the
            // counter banner in ABStairAnim.
            sb.AppendLine(ABStairAnim.CountersLine());
            if (map == null)
            {
                return sb.ToString();
            }
            int now = Find.TickManager.TicksGame;
            IReadOnlyList<Pawn> spawned = map.mapPawns.AllPawnsSpawned;
            foreach (KeyValuePair<int, Transit> kv in pending)
            {
                Pawn pawn = FindPawn(spawned, kv.Key);
                Transit t = kv.Value;
                int age = TransitTimeoutTicks - (t.expiresAtTick - now);
                if (pawn == null)
                {
                    sb.AppendLine("  [id " + kv.Key + "] pawn not on this map; age=" + age);
                    continue;
                }
                float dist = t.near != null
                    ? pawn.Position.DistanceTo(t.near.Position)
                    : -1f;
                sb.AppendLine("  " + pawn.LabelShortCap
                    + " (" + (pawn.RaceProps != null && pawn.RaceProps.Animal ? "animal" : "humanlike") + ")"
                    + " at " + pawn.Position + " band " + ABBands.BandOf(map, pawn.Position)
                    + " | job=" + (pawn.CurJob?.def?.defName ?? "none")
                    + " | age=" + age + "/" + TransitTimeoutTicks
                    + " | distToNear=" + dist.ToString("0.0") + " (need <=" + ArriveRadius + ")"
                    + " | near=" + (t.near != null ? t.near.Position.ToString() : "null")
                    + " band " + (t.near != null ? ABBands.BandOf(map, t.near.Position) : -1)
                    + " | moving=" + (pawn.pather != null && pawn.pather.Moving));
            }
            return sb.ToString();
        }

        /// <summary>Pawns that have been segmented at least once. Arrival diagnostics are
        /// scoped to these, otherwise every arrival of every pawn on the map logs a line and
        /// the one that matters is buried.</summary>
        private static readonly HashSet<int> everSegmented = new HashSet<int>();

        public static bool HasPending(Pawn p) => p != null && pending.ContainsKey(p.thingIDNumber);

        /// <summary>Is ANY transit in flight. A plain count read, so the renderer can skip
        /// the dictionary probe entirely on the hot path - see ABStairAnim.ProgressFor.</summary>
        public static bool AnyPending => pending.Count > 0;

        /// <summary>
        /// The in-flight transit for a pawn, for the SELECTION OVERLAYS to draw.
        ///
        /// ⚠ THE PATH LINE IS NOT MISSING PAST THE STAIRS, IT GENUINELY DOES NOT EXIST.
        /// TrySegment rewrites the destination to the near anchor BEFORE the pather builds a
        /// path, so `curPath` correctly ends at the stairwell and the rest of the journey
        /// lives only here. Anything that wants to show the whole trip has to read this
        /// record and compute the far side itself - see ABTransitVisuals.
        /// </summary>
        public static bool TryGetPending(Pawn p, out IntVec3 nearCell, out IntVec3 farCell,
            out LocalTargetInfo realDest)
        {
            nearCell = IntVec3.Invalid;
            farCell = IntVec3.Invalid;
            realDest = LocalTargetInfo.Invalid;
            if (p == null || !pending.TryGetValue(p.thingIDNumber, out Transit t))
            {
                return false;
            }
            if (t.near == null || t.far == null || !t.near.Spawned || !t.far.Spawned)
            {
                return false;
            }
            nearCell = t.near.Position;
            farCell = t.far.Position;
            realDest = t.realDest;
            return true;
        }

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                pending.Remove(p.thingIDNumber);
            }
        }

        /// <summary>
        /// ⚠⚠ EVERY STATIC HERE IS KEYED BY thingIDNumber, AND THOSE IDS ARE STABLE ACROSS A
        /// SAVE LINEAGE - so without this hook a loaded game inherits the PREVIOUS session's
        /// transit records and they bind straight onto the live pawns that share those ids.
        ///
        /// Reported as "sometimes pawns refuse to move when you load a prior save, until they
        /// complete an action on the level they're on". The record survives the load holding
        /// doors from the discarded map; dropping a Game does not despawn its things, so
        /// `near.Spawned` can still read true and the sweep happily acts on it - hijacking a
        /// pawn toward a destination that belonged to a different session, or overwriting the
        /// transit it is legitimately trying to make. Finishing a local job clears it because
        /// the next segmentation overwrites the entry.
        ///
        /// ⚠ AND "PRIOR SAVE" IS THE TELL, NOT A DETAIL. Loading an EARLIER save rewinds
        /// TicksGame, so `now > expiresAtTick` is false for as long as the rewind lasts and
        /// the 4000-tick self-cleanup - the thing that would otherwise have hidden this bug -
        /// cannot fire at all. See the matching guard in TickTransits.
        /// </summary>
        [ABGameReset]
        public static void ResetForNewGame()
        {
            pending.Clear();
            everSegmented.Clear();
            tmpDone.Clear();
            tmpCarry.Clear();
            holding.Clear();
            tmpHoldDone.Clear();
            tmpHoldFire.Clear();
        }

        // ================================================== §78 the held crossing

        /// <summary>
        /// A transit that has been DECIDED and is now waiting out its entry clip before the
        /// position write. See the banner on BeginCrossing for why this is not the thing
        /// that broke in run #297.
        /// </summary>
        private struct Crossing
        {
            public Pawn pawn;
            public Building_Door near;
            public Building_Door far;
            public LocalTargetInfo realDest;
            public PathEndMode realPeMode;
            public int fireAtTick;
            public IntVec3 holdCell;

            /// <summary>The job that owned the pather when the hold started. If it changes,
            /// something else has taken command and this teleport must not happen.</summary>
            public Job job;
        }

        private static readonly Dictionary<int, Crossing> holding =
            new Dictionary<int, Crossing>();

        private static readonly List<int> tmpHoldDone = new List<int>();

        private static readonly List<Crossing> tmpHoldFire = new List<Crossing>();

        /// <summary>Hard ceiling on a hold. Longer than any clip; if this ever fires,
        /// something stopped ticking and finishing late beats never finishing.</summary>
        private const int MaxHoldTicks = 300;

        /// <summary>Returns true when the destination was rewritten to a near anchor.</summary>
        public static bool TrySegment(Pawn pawn, ref LocalTargetInfo dest, ref PathEndMode peMode)
        {
            if (pawn == null || !pawn.Spawned || !dest.IsValid)
            {
                return false;
            }
            Map map = pawn.Map;
            if (!ABBands.Banded(map))
            {
                return false;
            }
            IntVec3 destCell = dest.Cell;
            // PHASE 3 (§34): SAME BAND IS NOT THE SAME QUESTION AS SAME ISLAND.
            //
            // This used to early-out on SameBand alone, which silently assumed that sharing a
            // level means being able to walk between them. On a fragmented band it does not:
            // two plateaus on a sky level, or two buildings' upper floors separated by open
            // air, are the same band and different islands, and the trip genuinely requires
            // going down, across and back up. We declined to segment, vanilla correctly found
            // no path - but `CanReach` had already said TRUE, because the region graph IS
            // connected through our wormholes. That mismatch is the `CanReach=True` +
            // `path=NOT FOUND` stall.
            //
            // ⚠ THE TEST IS "KNOWN DIFFERENT", NOT "NOT SAME". An endpoint with no region
            // (a wall, the gutter, unfogged rock) must fall through to the old behaviour and
            // be left alone - reading unknown as "different island" would route every
            // ordinary intra-band order through a staircase.
            //
            // ⚠ AND IT IS ORDERED CHEAP-FIRST. `KnownDifferentComponents` returns on a single
            // bool when no band on the map is fragmented, which is the common case and the
            // only reason this is affordable on StartPath.
            // ⚠⚠ §59 THE PAWN IS AN ARGUMENT TO THIS QUESTION. Islands are flooded from the
            // path grid, where a forbidden door is walkable - so pawnlessly, two rooms joined
            // only by a door the player has forbidden are ONE island and this branch declines
            // to segment. The band-scoped pathfinder then refuses that door and the pawn does
            // nothing at all. With the pawn, a colonist sees the split, falls through, and
            // gets routed down and around; a raider still sees one island, correctly.
            if (ABBands.SameBand(map, pawn.Position, destCell)
                && !ABBandComponents.KnownDifferentComponents(map, pawn.Position, destCell,
                    pawn))
            {
                // Same island: nothing to segment. The record is deliberately LEFT ALONE.
                //
                // Clearing here was wrong. This method rewrites the destination to the near
                // anchor - in the pawn's own band - so every re-issue of that leg comes back
                // through this branch, as does any incidental short path the pawn takes on
                // the way. Clearing on any of them wiped the in-flight transit and the pawn
                // arrived at the stairs with nothing pending. Records now expire on a
                // timeout instead (see the tick sweep), which cannot misfire.
                return false;
            }
            // ONE log call per attempt, carrying the whole outcome. Separate calls share an
            // identical stack signature, so a log-grouping monitor folds them into a single
            // class and only the first is ever seen - which is exactly what happened while
            // chasing this: "wants ..." arrived and the outcome line never did.
            bool got = ABWormhole.TryGetTransit(map, pawn.Position, destCell, pawn,
                out Building_Door near, out Building_Door far);
            // The JOB is the single most useful field here and was missing. Without it a
            // transit line cannot be told apart from an idle pawn commuting across a band to
            // wander - which is a bug - versus a pawn crossing to haul or eat, which is the
            // feature working. Both look identical as bare coordinates.
            //
            // ⚠ GUARDED AT THE CALL SITE, NOT JUST INSIDE Transit(). The argument string
            // was built on every segmentation attempt even with LogTransit off - including
            // two ComponentOf calls that can trigger a lazy band flood purely for a line
            // that was about to be discarded (2026-08 survey, applied with §36e-C1).
            if (ABV2Debug.LogTransit)
            {
                ABV2Debug.Transit(pawn.LabelShort + " " + pawn.Position
                    + " (band " + ABBands.BandOf(map, pawn.Position) + ")"
                    + " comp " + ABBandComponents.ComponentOf(map, pawn.Position, pawn)
                    + " -> " + destCell + " (band " + ABBands.BandOf(map, destCell) + ")"
                    + " comp " + ABBandComponents.ComponentOf(map, destCell, pawn)
                    + " | forbidAware=" + ABBandComponents.RespectsForbiddenDoors(pawn)
                    + " | job=" + (pawn.CurJob?.def?.defName ?? "none")
                    + " | pairs=" + ABWormhole.PairCount(map)
                    + " | transit=" + (got
                        ? ("YES via " + near.Position + " -> " + far.Position)
                        : "NONE (pawn will try to walk it and fail)"));
            }
            if (!got)
            {
                // No wormhole joins these bands. Let vanilla fail honestly rather than
                // sending the pawn somewhere arbitrary.
                Clear(pawn);
                return false;
            }
            IntVec3 entry = EntryCellFor(near, pawn);
            everSegmented.Add(pawn.thingIDNumber);
            pending[pawn.thingIDNumber] = new Transit
            {
                realDest = dest,
                realPeMode = peMode,
                near = near,
                far = far,
                entryCell = entry,
                startedTick = Find.TickManager.TicksGame,
                expiresAtTick = Find.TickManager.TicksGame + TransitTimeoutTicks
            };
            // §78b: WALK TO THE WAY IN, NOT TO THE MIDDLE. The links are directional - the
            // art leads the way it faces and is entered from the opposite edge - so pathing
            // at the building itself let the pawn arrive on whichever side the route
            // happened to favour, i.e. usually across a handrail. EntryCellFor returns
            // IntVec3.Invalid whenever that cell is not usable, and then this falls back to
            // the old behaviour verbatim.
            dest = entry.IsValid ? new LocalTargetInfo(entry) : (LocalTargetInfo)near;
            peMode = PathEndMode.OnCell;
            return true;
        }

        /// <summary>
        /// The cell a pawn should stand in to use this link: the footprint cell on the edge
        /// OPPOSITE the link's facing, centred on the run - i.e. just inside the notch.
        ///
        /// ⚠ ON THE FOOTPRINT, NOT ONE STEP OUTSIDE IT (§78c). It was outside, and the field
        /// report was "the animation still starts before they hit the stair texture": the
        /// clip legitimately began while the pawn was standing next to the staircase, so it
        /// shrank into a descent it had not walked onto yet. Pathing the pawn ONTO the art
        /// means the descent clip starts on the frame the pawn is standing on the treads,
        /// and the approach is done by the pawn's own pather at its own walk speed instead
        /// of being faked inside the clip.
        ///
        /// ⚠⚠ EVERY FAILURE RETURNS Invalid AND THE CALLER FALLS BACK. A link whose entry
        /// cell is walled in, out of bounds or unreachable must stay USABLE - degrading to
        /// a slightly wrong-looking animation is fine, refusing to path is the
        /// ladder-to-nowhere class of bug and is not.
        ///
        /// ⚠ CanReach IS THE EXPENSIVE CLAUSE AND IT IS ORDERED LAST. The cheap tests
        /// (bounds, standable) reject the walled-in case without touching the reachability
        /// cache. This runs inside a StartPath prefix, but only on the cross-band path that
        /// was already about to do a wormhole lookup, and CanReach is a read-only cache
        /// query - it cannot re-enter StartPath.
        ///
        /// ⚠ THE EDGE IS TAKEN FROM OccupiedRect, NOT FROM Position. For an even-sized
        /// footprint (the 2x2 stairs and elevator) Position is not the geometric centre -
        /// GenAdj.AdjustForRotation shifts it - so deriving the edge arithmetically from
        /// Position lands a cell off on half the rotations.
        /// </summary>
        private static IntVec3 EntryCellFor(Building_Door link, Pawn pawn)
        {
            if (link == null || pawn == null)
            {
                return IntVec3.Invalid;
            }
            Map map = link.Map;
            if (map == null)
            {
                return IntVec3.Invalid;
            }
            CellRect r = link.OccupiedRect();
            IntVec3 face = link.Rotation.FacingCell;
            IntVec3 c;
            if (face.z > 0)
            {
                c = new IntVec3(r.CenterCell.x, 0, r.minZ);
            }
            else if (face.z < 0)
            {
                c = new IntVec3(r.CenterCell.x, 0, r.maxZ);
            }
            else if (face.x > 0)
            {
                c = new IntVec3(r.minX, 0, r.CenterCell.z);
            }
            else
            {
                c = new IntVec3(r.maxX, 0, r.CenterCell.z);
            }
            // ⚠ WALKABLE, NOT STANDABLE. This cell is INSIDE the footprint, and the link is
            // a Building_Door - `passability` is PassThroughOnly, so GenGrid.Standable is
            // false for it and a Standable test would reject every link there is. Walkable
            // reads the path grid, where a door is passable, which is the question actually
            // being asked: can the pawn stand here on its way through.
            if (!c.InBounds(map) || !c.Walkable(map))
            {
                return IntVec3.Invalid;
            }
            // Must be on the pawn's own side of the wormhole, or we would be asking it to
            // path to a cell it can only get to by crossing the link it has not crossed yet.
            if (!ABBands.SameBand(map, pawn.Position, c))
            {
                return IntVec3.Invalid;
            }
            if (!pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly))
            {
                return IntVec3.Invalid;
            }
            return c;
        }

        /// <summary>
        /// Per-tick sweep: carry across any pawn that has reached its near anchor.
        ///
        /// This, not PatherArrived, is now the primary trigger. Hanging the transit off the
        /// pather's arrival callback meant it only fired if the pawn finished a leg exactly
        /// on (or beside) the anchor, and the pather has many ways to end a leg somewhere
        /// else - a re-issued path, an interrupting job, stopping short. Sins ended four
        /// cells away and the transit never ran.
        ///
        /// Position is the honest condition: if the pawn is standing at the stairwell with a
        /// transit pending, take it. Cheap - the dictionary is empty almost always.
        /// </summary>
        [ABGameTick(70)]
        public static void TickTransits()
        {
            ABStairAnim.Sweep();
            // ⚠ BEFORE THE `pending` EARLY-RETURN. A held crossing has already left
            // `pending`, so gating this on it would strand every hold the moment the last
            // record was consumed - which is the common case, since the hold begins in the
            // same tick the record is removed.
            TickCrossings();
            // Route previews are built HERE, on the tick, never from a draw callback.
            // See the banner on ABTransitVisuals.DrawRemainingRoute.
            ABTransitVisuals.TickRoutes();
            if (pending.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpDone.Clear();
            tmpCarry.Clear();

            // PHASE 1 - decide only. Nothing in this loop may touch `pending`, directly or
            // indirectly.
            //
            // Carry() calls pather.StartPath, which re-enters TrySegment, which can ADD a
            // record - mutating the dictionary while we are enumerating it. That throws
            // "InvalidOperationException: Collection was modified" out of GameComponentTick,
            // killing the whole sweep for that tick and stranding every other pending
            // transit. Landing pawns ON the anchor made it far more likely, because the
            // resumed path now starts from a door cell and re-segments more often.
            foreach (KeyValuePair<int, Transit> kv in pending)
            {
                Transit t = kv.Value;
                // ⚠ THE SECOND TEST IS THE CLOCK RUNNING BACKWARDS. A record can only ever be
                // TransitTimeoutTicks in the future; more than that means `now` is BEFORE the
                // record was made, which happens when an earlier save is loaded. Without it a
                // rewound clock makes a stale record effectively immortal - it is neither
                // expired nor expiring - which is precisely why this class of bug only ever
                // showed up on loading a PRIOR save. ResetForNewGame should have cleared it
                // already; this is the belt to that pair of braces.
                int ticksLeft = t.expiresAtTick - now;
                if (ticksLeft <= 0 || ticksLeft > TransitTimeoutTicks
                    || t.near == null || t.far == null
                    || !t.near.Spawned || !t.far.Spawned)
                {
                    tmpDone.Add(kv.Key);
                    continue;
                }
                Pawn pawn = t.near.Map?.mapPawns?.AllPawnsSpawned is IReadOnlyList<Pawn> list
                    ? FindPawn(list, kv.Key)
                    : null;
                if (pawn == null || !pawn.Spawned)
                {
                    tmpDone.Add(kv.Key);
                    continue;
                }
                // ⚠⚠ THIS CONDITION MAY BE DELAYED BUT NEVER DENIED. An entry-animation hold
                // was once wired in here and in TryConsumeArrival as a CONDITIONAL gate and
                // it broke cross-level movement outright ("can't command pawns across
                // levels anymore", run #297). What follows is not a gate: every clause is a
                // trigger, and ApproachPatienceTicks guarantees one of them fires within two
                // seconds of the pawn getting close. A cosmetic effect must never be able to
                // decide whether a transit happens - only, briefly, when.
                bool nearEnough = pawn.Position.InHorDistOf(t.near.Position, ArriveRadius);
                if (nearEnough)
                {
                    // Standing on the link itself: the clip can start on the art, which is
                    // the whole point of §78c.
                    bool onEntry = t.entryCell.IsValid && pawn.Position == t.entryCell;
                    // Stopped short - blocked by a pawn that just landed, or the entry cell
                    // was never reachable. Carrying now is the old behaviour and is right.
                    bool stoppedShort = pawn.pather == null || !pawn.pather.Moving;
                    // And the backstop, so "still walking" can never mean "never crosses".
                    bool outOfPatience = now - t.startedTick > ApproachPatienceTicks;
                    if (onEntry || stoppedShort || outOfPatience)
                    {
                        tmpDone.Add(kv.Key);
                        tmpCarry.Add(new KeyValuePair<Pawn, Transit>(pawn, t));
                    }
                }
            }

            // PHASE 2 - remove first, so a record re-added by StartPath below survives.
            for (int i = 0; i < tmpDone.Count; i++)
            {
                pending.Remove(tmpDone[i]);
            }

            // PHASE 3 - now safe to re-enter TrySegment.
            for (int i = 0; i < tmpCarry.Count; i++)
            {
                try
                {
                    Carry(tmpCarry[i].Key, tmpCarry[i].Value);
                }
                catch (Exception e)
                {
                    // Keyed on the pawn, so a pawn stuck in a failing transit logs once
                    // rather than every tick it retries. TickTransits runs every game tick.
                    Log.ErrorOnce(ABLog.Tag + " V2: carry threw for "
                        + tmpCarry[i].Key.LabelShortCap + ": " + e,
                        tmpCarry[i].Key.thingIDNumber ^ 762195935);
                }
            }
            tmpCarry.Clear();
            tmpDone.Clear();
        }

        private static readonly List<int> tmpDone = new List<int>();

        /// <summary>Carries deferred out of the enumeration - see TickTransits phase 1.</summary>
        private static readonly List<KeyValuePair<Pawn, Transit>> tmpCarry =
            new List<KeyValuePair<Pawn, Transit>>();

        private static Pawn FindPawn(IReadOnlyList<Pawn> list, int thingId)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].thingIDNumber == thingId)
                {
                    return list[i];
                }
            }
            return null;
        }

        /// <summary>
        /// Where a transiting pawn should land on the far side.
        ///
        /// NOT simply the far anchor's own cell. Every transit used to teleport onto that one
        /// cell, so whenever two or more pawns crossed at around the same time they stacked on
        /// top of each other and read as a "clump" at the stairs - and because the anchor is a
        /// Building_Door subclass, a pawn parked on it is standing in a doorway, blocking the
        /// next arrival and any normal traffic through the stairwell. A single drafted pawn
        /// never showed it, which is exactly why it survived this long.
        ///
        /// PREFER THE ANCHOR ITSELF whenever it is free, and only step aside when it is not.
        ///
        /// The first version of this did the opposite - it excluded the anchor outright to
        /// stop pawns stacking - and that made things worse. The anchor is a Building_Door,
        /// which can only be entered from its four CARDINAL neighbours, so landing pawns
        /// beside it drops them straight into the approach lane. Two or three arrivals
        /// standing there wall the stairwell off, and following pawns jam on a corner and
        /// re-path the same failing route indefinitely.
        ///
        /// Landing ON the door is what vanilla door traffic does: a pawn occupies the cell
        /// for a moment and immediately walks on, because Carry re-issues its path. Stacking
        /// is only a risk while the cell is genuinely occupied, which is exactly the case the
        /// fallback handles.
        /// </summary>
        private static IntVec3 LandingCell(Pawn pawn, Building_Door far)
        {
            IntVec3 anchor = far.Position;
            Map map = far.Map;
            if (map == null)
            {
                return anchor;
            }
            if (anchor.GetFirstPawn(map) == null)
            {
                return anchor;
            }
            // Occupied: step aside, but stay in the same band so a landing can never be
            // placed into the gutter or through a seam.
            if (CellFinder.TryFindRandomCellNear(anchor, map, LandingRadius,
                    c => c.InBounds(map)
                         && c != anchor
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && ABBands.SameBand(map, c, anchor),
                    out IntVec3 spot))
            {
                return spot;
            }
            return anchor;
        }

        /// <summary>
        /// Either start the §78 hold - the pawn stays where it is and plays the entry clip -
        /// or carry instantly, which is precisely what this method did before §78 and is
        /// still what happens whenever there is no clip to play.
        /// </summary>
        private static void Carry(Pawn pawn, Transit t)
        {
            // Stop tracking: the pawn's NEXT arrival (at the real destination, just after
            // this) would otherwise trip the ARRIVED-NO-PENDING diagnostic and read as a
            // failure when it is simply the journey finishing normally. Done here rather
            // than in CarryNow so it happens once, whichever route the crossing takes.
            everSegmented.Remove(pawn.thingIDNumber);
            if (BeginCrossing(pawn, t))
            {
                return;
            }
            CarryNow(pawn, t);
        }

        /// <summary>
        /// Hold the pawn at the stairwell for the length of its entry clip, then teleport.
        /// Returns false when no hold was started, in which case the caller must carry now.
        ///
        /// ⚠⚠ WHY THIS IS NOT THE RUN #297 BUG WEARING A NEW HAT. That attempt held the pawn
        /// by REFUSING AN ARRIVAL - it gated TryConsumeArrival/ReadyToCarry, so vanilla's
        /// PatherArrived never ran, the leg never completed, the job re-issued StartPath,
        /// TrySegment re-segmented (the real destination is still on another band) and the
        /// pawn re-arrived at the same anchor forever. The transit DECISION was the thing
        /// being deferred, and it was deferred by a cosmetic condition.
        ///
        /// Here the decision is already final: by the time this runs the record has left
        /// `pending` (tick sweep phase 2, or Clear() on the arrival path) and everSegmented
        /// has been dropped. Nothing can re-segment this leg, because there is no leg left -
        /// StopDead ends it. What remains is a plain timer that owns one thing, the position
        /// write, and that timer cannot decline to fire: every exit from TickCrossings either
        /// teleports or abandons, and MaxHoldTicks backstops both.
        ///
        /// ⚠ STOPDEAD, NOT A SUPPRESSED ARRIVAL. StopDead releases the path and clears
        /// `moving` WITHOUT notifying the JobDriver, so the toil that completes on
        /// ToilCompleteMode.PatherArrival simply waits - no arrival is fired at the
        /// stairwell, so no toil completes in the wrong place, and because `moving` is false
        /// PatherTick never re-tests AtDestinationPosition and there is no per-tick loop.
        /// That IS the difference: #297 deferred the DECISION, this defers the MOVE.
        ///
        /// ⚠ THE STAGGER IS BELT TO STOPDEAD'S BRACES, and it is aspirational per pawn
        /// (StaggerDurationFactor, Anomaly awoken corpses). If a pawn shrugs it off and
        /// steps away, TickCrossings sees the cell change and fires the teleport early - a
        /// shorter show, never a desync.
        /// </summary>
        private static bool BeginCrossing(Pawn pawn, Transit t)
        {
            if (pawn == null || !pawn.Spawned || t.near == null || t.far == null)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(pawn.Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            if (!ABStairAnim.Begin(pawn, t.near, t.far, bands.BandOf(t.near.Position),
                    bands.BandOf(t.far.Position), out int entryTicks)
                || entryTicks <= 0)
            {
                return false; // animation off, or nothing to play: carry instantly
            }
            pawn.pather?.StopDead();
            pawn.stances?.stagger?.StaggerFor(entryTicks, 0f);
            holding[pawn.thingIDNumber] = new Crossing
            {
                pawn = pawn,
                near = t.near,
                far = t.far,
                realDest = t.realDest,
                realPeMode = t.realPeMode,
                fireAtTick = Find.TickManager.TicksGame + entryTicks,
                holdCell = pawn.Position,
                job = pawn.CurJob
            };
            ABV2Debug.Transit("HOLDING " + pawn.LabelShort + " at " + pawn.Position
                + " for " + entryTicks + "t (entry clip) before " + t.far.Position);
            return true;
        }

        /// <summary>
        /// Age every held crossing. Collect-then-act, for the same reason TickTransits is:
        /// CarryNow calls StartPath, which re-enters TrySegment, which writes `pending`.
        /// </summary>
        private static void TickCrossings()
        {
            if (holding.Count == 0 || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpHoldDone.Clear();
            tmpHoldFire.Clear();
            foreach (KeyValuePair<int, Crossing> kv in holding)
            {
                Crossing x = kv.Value;
                Pawn p = x.pawn;
                if (p == null || !p.Spawned || p.Dead
                    || x.near == null || x.far == null || !x.near.Spawned || !x.far.Spawned)
                {
                    // ⚠ END THE JOB. The pawn is sitting on a StopDead pather under a toil
                    // that only a pather arrival can complete; abandoning the crossing
                    // without this is precisely the "frozen under a live job" wedge that
                    // ABStuckWatchdog episode B exists to report.
                    tmpHoldDone.Add(kv.Key);
                    ABStairAnim.Clear(p);
                    if (p != null && p.Spawned && !p.Dead && p.CurJob == x.job)
                    {
                        ABV2Debug.Transit("HOLD ABANDONED for " + p.LabelShort
                            + " (anchor lost mid-clip); ending job");
                        p.jobs?.EndCurrentJob(JobCondition.Incompletable);
                    }
                    continue;
                }
                if (p.CurJob != x.job)
                {
                    // Re-ordered, drafted, or the job ended under us. Whatever took command
                    // owns the pather now and issued its own path; teleporting the pawn a
                    // level away at this point would be the mod overriding the player.
                    tmpHoldDone.Add(kv.Key);
                    ABStairAnim.Clear(p);
                    ABV2Debug.Transit("HOLD CANCELLED for " + p.LabelShort
                        + " (job changed mid-clip); no teleport");
                    continue;
                }
                int ticksLeft = x.fireAtTick - now;
                // Second clause is the clock running backwards on a loaded save, third is
                // the stagger-immune pawn walking off. Both mean "fire now".
                if (ticksLeft <= 0 || ticksLeft > MaxHoldTicks || p.Position != x.holdCell)
                {
                    tmpHoldDone.Add(kv.Key);
                    tmpHoldFire.Add(x);
                }
            }
            for (int i = 0; i < tmpHoldDone.Count; i++)
            {
                holding.Remove(tmpHoldDone[i]);
            }
            for (int i = 0; i < tmpHoldFire.Count; i++)
            {
                Crossing x = tmpHoldFire[i];
                try
                {
                    CarryNow(x.pawn, new Transit
                    {
                        near = x.near,
                        far = x.far,
                        realDest = x.realDest,
                        realPeMode = x.realPeMode
                    });
                }
                catch (Exception e)
                {
                    // The pawn is on a stopped pather. Failing silently here strands it, so
                    // end the job rather than leave a wedge behind an exception.
                    Log.ErrorOnce(ABLog.Tag + " V2: held carry threw for "
                        + x.pawn.LabelShortCap + ": " + e,
                        x.pawn.thingIDNumber ^ 762195936);
                    x.pawn.jobs?.EndCurrentJob(JobCondition.Errored);
                }
            }
            tmpHoldDone.Clear();
            tmpHoldFire.Clear();
        }

        /// <summary>The position write and everything downstream of it. Reached either
        /// straight from Carry (no clip) or from TickCrossings when the hold expires.</summary>
        private static void CarryNow(Pawn pawn, Transit t)
        {
            IntVec3 landing = LandingCell(pawn, t.far);
            ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                + " -> " + landing + " (anchor " + t.far.Position + ")"
                + "; resuming to " + t.realDest.Cell);
            pawn.Position = landing;
            pawn.Notify_Teleported(false, true);
            // Walking through a door reveals what is on the other side of it. The transit is
            // a teleport, so neither vanilla's door hook nor anything else fires here - see
            // ABFogReveal.RevealArrival for why both halves of the vanilla path miss.
            ABFogReveal.RevealArrival(pawn, landing);
            // Cosmetic only, and deliberately AFTER the move: flips the §78 clip to its
            // emerge half, anchored on the FAR link's own axis and art offset. Never a gate
            // on the carry itself. See the banner on ABStairAnim.
            ABStairAnim.NotifyCarried(pawn, t.far, landing);
            // A camera locked to this pawn (Perspective Shift avatar, follow-selected)
            // treats its transit as the player's own level change. No-op for everyone else.
            ABBandView.FollowTransit(pawn);
            ABTransitVisuals.Clear(pawn);
            // The real destination was captured when the trip STARTED, and the walk to the
            // stairwell takes time - the target can die, be hauled away or be deconstructed
            // in the meantime. Resuming onto a destroyed thing makes vanilla log
            // "pathing to destroyed thing" and fail the pather.
            bool destGone = t.realDest.HasThing
                && (t.realDest.ThingDestroyed || !t.realDest.Thing.Spawned);
            if (destGone)
            {
                // Land at the far anchor and let the job re-evaluate from there.
                ABV2Debug.Transit("  destination gone mid-transit; stopping at " + t.far.Position);
                pawn.jobs?.EndCurrentJob(JobCondition.Incompletable);
            }
            else if (t.realDest.IsValid)
            {
                // ⚠⚠ NO "ALREADY THERE" SHORTCUT HERE. EVERY EXIT FROM THIS METHOD MUST
                // EITHER RE-DISPATCH THE PATHER OR END THE JOB.
                //
                // This used to be guarded with `&& !pawn.Position.Equals(t.realDest.Cell)`,
                // on the reasonable-sounding grounds that a pawn standing on its destination
                // has nothing left to walk. It does not - but its JOB has not been told.
                // Notify_Teleported above runs Pawn_PathFollower.Notify_Teleported_Int ->
                // StopDead(), which releases the path and sets moving=false WITHOUT
                // notifying the JobDriver. A toil with ToilCompleteMode.PatherArrival is
                // completed only by a pushed Notify_PatherArrived, so skipping the dispatch
                // left a live job on a dead pather: the pawn stood at the stairwell forever
                // and only a draft/undraft (jobs.StopAll) freed it.
                //
                // It fires exactly when realDest IS the far anchor - LandingCell prefers the
                // anchor's own cell whenever it is free - i.e. every "go down the stairs"
                // order, which is how it was reported. Reported for mechs, but nothing here
                // is mech-specific; any pawn ordered onto a stairwell could hang.
                //
                // Handing the case to vanilla is both shorter and more correct: StartPath
                // opens with `if (AtDestinationPosition()) { PatherArrived(); return; }`, so
                // an already-arrived pawn gets a REAL arrival - through our own PatherArrived
                // prefix, which no-ops because the record was already removed in phase 2 -
                // and the toil completes. An unreachable destination becomes PatherFailed,
                // which ends the job honestly instead of wedging it.
                pawn.pather?.StartPath(t.realDest, t.realPeMode);
            }
            else
            {
                // Unreachable in practice (TrySegment only records a valid destination) but
                // it must not become a third silent exit if that ever changes.
                ABV2Debug.Transit("  destination no longer valid; ending job at " + landing);
                pawn.jobs?.EndCurrentJob(JobCondition.Incompletable);
            }
        }

        /// <summary>Called from the PatherArrived prefix. Returns true when it consumed
        /// the arrival (pawn transited and was re-dispatched), meaning the JobDriver
        /// must NOT be told it arrived - the journey is not over.</summary>
        public static bool TryConsumeArrival(Pawn_PathFollower pather, Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (!pending.TryGetValue(pawn.thingIDNumber, out Transit t))
            {
                if (everSegmented.Contains(pawn.thingIDNumber))
                {
                    ABV2Debug.Transit("ARRIVED-NO-PENDING " + pawn.LabelShort + " at " + pawn.Position
                        + " - record was cleared before arrival");
                }
                return false;
            }
            if (t.near == null || t.far == null || !t.near.Spawned || !t.far.Spawned)
            {
                ABV2Debug.Transit("ARRIVED-ANCHOR-GONE " + pawn.LabelShort + " at " + pawn.Position);
                Clear(pawn);
                return false;
            }
            // ON the anchor cell OR adjacent to it both count as "reached the stairwell".
            //
            // Requiring the exact cell caused an infinite shuffle: the pawn is pathing to a
            // Building (the anchor), and depending on how PathEndMode resolves it can come
            // to rest one cell short. Arrival then failed to match, the transit was not
            // consumed, vanilla completed the leg, the job re-issued StartPath toward the
            // far anchor, segmentation ran again - and the pawn walked into the stairs over
            // and over without ever going up.
            // Same radius the tick sweep uses. These two MUST agree: they are the same
            // question asked from two different triggers, and when they disagreed the sweep
            // accepted pawns this path rejected, so whether a transit completed depended on
            // which trigger happened to fire first.
            if (!pawn.Position.InHorDistOf(t.near.Position, ArriveRadius))
            {
                // Print BOTH anchors and BOTH bands. A pure distance miss and a stale record
                // whose near anchor is in the band the pawn already left look identical when
                // only one coordinate is logged - and they need completely different fixes.
                Map m = pawn.Map;
                ABV2Debug.Transit("ARRIVE-MISMATCH " + pawn.LabelShort
                    + " at " + pawn.Position + " (band " + ABBands.BandOf(m, pawn.Position) + ")"
                    + " expected within " + ArriveRadius + " of near " + t.near.Position
                    + " (band " + ABBands.BandOf(m, t.near.Position) + ")"
                    + ", far " + t.far.Position
                    + " (band " + ABBands.BandOf(m, t.far.Position) + ")"
                    + " | job=" + (pawn.CurJob?.def?.defName ?? "none"));

                // A record whose NEAR anchor is not in the pawn's own band can never complete:
                // the pawn is past it. Drop it rather than let it linger for the full 4000-tick
                // timeout re-failing every arrival.
                if (!ABBands.SameBand(m, pawn.Position, t.near.Position))
                {
                    ABV2Debug.Transit("  stale record dropped (near anchor is in another band)");
                    Clear(pawn);
                }
                return false; // arrived somewhere else; not our transit
            }

            // ⚠ AND NOTHING MAY GATE IT HERE EITHER, WHICH IS THE MORE DANGEROUS OF THE TWO.
            // Returning true suppresses vanilla's PatherArrived entirely, so the pather never
            // completes the leg, the job re-issues StartPath, TrySegment re-segments (the
            // real destination is still on another band), and the pawn re-arrives at the same
            // anchor next tick - a re-segmentation loop that reads exactly like "the order
            // does nothing". Reverted; see §33c.

            // Clear BEFORE re-dispatching: StartPath re-enters TrySegment, and after the
            // teleport the pawn is in the destination band, so it resolves as an
            // ordinary same-band path.
            Clear(pawn);

            // §78: hold here too, or whether the entry clip plays would depend on which of
            // the two triggers happened to fire first - the exact class of inconsistency
            // the ArriveRadius comment above exists to prevent.
            //
            // ⚠ RETURNING TRUE IS LOAD-BEARING HERE, AND IT IS SAFE ONLY BECAUSE OF
            // STOPDEAD. True skips vanilla's PatherArrived. If it ran, the toil would
            // complete AT THE STAIRWELL and the job would advance as though it had reached
            // its real destination. And because BeginCrossing has already cleared `moving`,
            // PatherTick never re-tests AtDestinationPosition, so suppressing the arrival
            // cannot turn into the per-tick re-arrival loop described above.
            if (BeginCrossing(pawn, t))
            {
                return true;
            }

            // Same landing rule as the tick sweep - this path had its own copy of the
            // teleport and kept dropping every pawn onto the anchor cell itself, so half the
            // transits still stacked even after the sweep was fixed.
            IntVec3 landing = LandingCell(pawn, t.far);
            pawn.Position = landing;
            // endCurrentJob:false - the job is mid-flight and must survive the hop.
            pawn.Notify_Teleported(false, true);
            // Paired with the tick sweep's copy above - both teleport sites must reveal, or
            // the fog lifts on some transits and not others depending on which path ran.
            ABFogReveal.RevealArrival(pawn, landing);
            ABStairAnim.NotifyCarried(pawn, t.far, landing);
            // Paired with the tick sweep's copy above, for the same reason both teleport
            // sites reveal fog: whichever trigger carries the followed pawn must also move
            // the view, or whether the camera follows depends on which path happened to run.
            ABBandView.FollowTransit(pawn);
            ABTransitVisuals.Clear(pawn);

            if (t.realDest.IsValid && !pawn.Position.Equals(t.realDest.Cell))
            {
                ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                    + " -> " + landing + " (anchor " + t.far.Position + ")"
                    + "; resuming to " + t.realDest.Cell);
                pather.StartPath(t.realDest, t.realPeMode);
                return true;
            }
            ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                + " -> " + t.far.Position + "; landed on destination");
            return false; // landed on the destination itself; let vanilla arrive
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.StartPath))]
    public static class Patch_PathFollower_ABStartPath
    {
        private static void Prefix(Pawn ___pawn, ref LocalTargetInfo dest, ref PathEndMode peMode)
        {
            try
            {
                ABWormholePather.TrySegment(___pawn, ref dest, ref peMode);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2 spike: StartPath segmentation threw: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PathFollower), "PatherArrived")]
    public static class Patch_PathFollower_ABPatherArrived
    {
        private static bool Prefix(Pawn_PathFollower __instance, Pawn ___pawn)
        {
            try
            {
                return !ABWormholePather.TryConsumeArrival(__instance, ___pawn);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2 spike: transit arrival threw: " + e);
                ABWormholePather.Clear(___pawn);
                return true;
            }
        }
    }
}
