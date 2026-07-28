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

        /// <summary>How close the pawn must get to the near anchor to be carried across.</summary>
        private const int ArriveRadius = 2;

        private static readonly Dictionary<int, Transit> pending = new Dictionary<int, Transit>();

        /// <summary>Pawns that have been segmented at least once. Arrival diagnostics are
        /// scoped to these, otherwise every arrival of every pawn on the map logs a line and
        /// the one that matters is buried.</summary>
        private static readonly HashSet<int> everSegmented = new HashSet<int>();

        public static bool HasPending(Pawn p) => p != null && pending.ContainsKey(p.thingIDNumber);

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
            ABV2Debug.Transit(pawn.LabelShort + " " + pawn.Position
                + " (band " + ABBands.BandOf(map, pawn.Position) + ")"
                + " -> " + destCell + " (band " + ABBands.BandOf(map, destCell) + ")"
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
            if (pending.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpDone.Clear();
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
                    tmpDone.Add(kv.Key);
                    Carry(pawn, t);
                }
            }
            for (int i = 0; i < tmpDone.Count; i++)
            {
                pending.Remove(tmpDone[i]);
            }
            tmpDone.Clear();
        }

        private static readonly List<int> tmpDone = new List<int>();

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

        /// <summary>Move the pawn to the far anchor and resume toward the real destination.</summary>
        private static void Carry(Pawn pawn, Transit t)
        {
            ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                + " -> " + t.far.Position + "; resuming to " + t.realDest.Cell);
            // Stop tracking: the pawn's NEXT arrival (at the real destination, just after
            // this) would otherwise trip the ARRIVED-NO-PENDING diagnostic and read as a
            // failure when it is simply the journey finishing normally.
            everSegmented.Remove(pawn.thingIDNumber);
            pawn.Position = t.far.Position;
            pawn.Notify_Teleported(false, true);
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
            if (pawn.Position != t.near.Position
                && !pawn.Position.AdjacentTo8WayOrInside(t.near))
            {
                ABV2Debug.Transit("ARRIVE-MISMATCH " + pawn.LabelShort + " at "
                    + pawn.Position + " expected " + t.near.Position);
                return false; // arrived somewhere else; not our transit
            }


            // Clear BEFORE re-dispatching: StartPath re-enters TrySegment, and after the
            // teleport the pawn is in the destination band, so it resolves as an
            // ordinary same-band path.
            Clear(pawn);

            pawn.Position = t.far.Position;
            // endCurrentJob:false - the job is mid-flight and must survive the hop.
            pawn.Notify_Teleported(false, true);

            if (t.realDest.IsValid && !pawn.Position.Equals(t.realDest.Cell))
            {
                ABV2Debug.Transit("TRANSITED " + pawn.LabelShort + " " + t.near.Position
                    + " -> " + t.far.Position + "; resuming to " + t.realDest.Cell);
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
