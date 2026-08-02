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
        }

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
            if (ABBands.SameBand(map, pawn.Position, destCell))
            {
                // Same band: nothing to segment. The record is deliberately LEFT ALONE.
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
            bool got = ABWormhole.TryGetTransit(map, pawn.Position, destCell,
                out Building_Door near, out Building_Door far);
            // The JOB is the single most useful field here and was missing. Without it a
            // transit line cannot be told apart from an idle pawn commuting across a band to
            // wander - which is a bug - versus a pawn crossing to haul or eat, which is the
            // feature working. Both look identical as bare coordinates.
            ABV2Debug.Transit(pawn.LabelShort + " " + pawn.Position
                + " (band " + ABBands.BandOf(map, pawn.Position) + ")"
                + " -> " + destCell + " (band " + ABBands.BandOf(map, destCell) + ")"
                + " | job=" + (pawn.CurJob?.def?.defName ?? "none")
                + " | pairs=" + ABWormhole.PairCount(map)
                + " | transit=" + (got
                    ? ("YES via " + near.Position + " -> " + far.Position)
                    : "NONE (pawn will try to walk it and fail)"));
            if (!got)
            {
                // No wormhole joins these bands. Let vanilla fail honestly rather than
                // sending the pawn somewhere arbitrary.
                Clear(pawn);
                return false;
            }
            everSegmented.Add(pawn.thingIDNumber);
            pending[pawn.thingIDNumber] = new Transit
            {
                realDest = dest,
                realPeMode = peMode,
                near = near,
                far = far,
                expiresAtTick = Find.TickManager.TicksGame + TransitTimeoutTicks
            };
            dest = near;
            peMode = PathEndMode.OnCell;
            return true;
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
                if (now > t.expiresAtTick || t.near == null || t.far == null
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
                if (pawn.Position.InHorDistOf(t.near.Position, ArriveRadius))
                {
                    // ⚠ NOTHING MAY GATE THIS CARRY. An entry-animation hold was wired in here
                    // and in TryConsumeArrival and it broke cross-level movement outright
                    // ("can't command pawns across levels anymore", run #297). Both call sites
                    // are reverted; ABStairAnim.ReadyToCarry still exists but is NOT WIRED.
                    // See §33c before re-attempting: a cosmetic effect must never sit on the
                    // path that decides whether a transit happens at all.
                    tmpDone.Add(kv.Key);
                    tmpCarry.Add(new KeyValuePair<Pawn, Transit>(pawn, t));
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

        /// <summary>Move the pawn to the far side and resume toward the real destination.</summary>
        private static void Carry(Pawn pawn, Transit t)
        {
            IntVec3 landing = LandingCell(pawn, t.far);
            ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                + " -> " + landing + " (anchor " + t.far.Position + ")"
                + "; resuming to " + t.realDest.Cell);
            // Stop tracking: the pawn's NEXT arrival (at the real destination, just after
            // this) would otherwise trip the ARRIVED-NO-PENDING diagnostic and read as a
            // failure when it is simply the journey finishing normally.
            everSegmented.Remove(pawn.thingIDNumber);
            pawn.Position = landing;
            pawn.Notify_Teleported(false, true);
            // Cosmetic only, and deliberately AFTER the move: the animation is a pop-out on
            // the far side, never a delay on the near one. See ABStairAnim.
            ABStairAnim.NotifyTransited(pawn);
            ABTransitVisuals.Clear(pawn);
            // The real destination was captured when the trip STARTED, and the walk to the
            // stairwell takes time - the target can die, be hauled away or be deconstructed
            // in the meantime. Resuming onto a destroyed thing makes vanilla log
            // "pathing to destroyed thing" and fail the pather.
            bool destGone = t.realDest.HasThing
                && (t.realDest.ThingDestroyed || !t.realDest.Thing.Spawned);
            if (t.realDest.IsValid && !destGone && !pawn.Position.Equals(t.realDest.Cell))
            {
                pawn.pather?.StartPath(t.realDest, t.realPeMode);
            }
            else if (destGone)
            {
                // Land at the far anchor and let the job re-evaluate from there.
                ABV2Debug.Transit("  destination gone mid-transit; stopping at " + t.far.Position);
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

            // Same landing rule as the tick sweep - this path had its own copy of the
            // teleport and kept dropping every pawn onto the anchor cell itself, so half the
            // transits still stacked even after the sweep was fixed.
            IntVec3 landing = LandingCell(pawn, t.far);
            pawn.Position = landing;
            // endCurrentJob:false - the job is mid-flight and must survive the hop.
            pawn.Notify_Teleported(false, true);
            ABStairAnim.NotifyTransited(pawn);
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
