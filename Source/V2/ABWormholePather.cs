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
        }

        private static readonly Dictionary<int, Transit> pending = new Dictionary<int, Transit>();

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
                // Same band: nothing to do. Any stale record is dead - the pawn either
                // arrived or was re-tasked mid-transit.
                Clear(pawn);
                return false;
            }
            if (!ABWormhole.TryGetTransit(map, pawn.Position, destCell,
                out Building_Door near, out Building_Door far))
            {
                // No wormhole joins these bands. Let vanilla fail honestly rather than
                // sending the pawn somewhere arbitrary.
                Clear(pawn);
                return false;
            }
            pending[pawn.thingIDNumber] = new Transit
            {
                realDest = dest,
                realPeMode = peMode,
                near = near,
                far = far
            };
            dest = near;
            peMode = PathEndMode.OnCell;
            return true;
        }

        /// <summary>Called from the PatherArrived prefix. Returns true when it consumed
        /// the arrival (pawn transited and was re-dispatched), meaning the JobDriver
        /// must NOT be told it arrived - the journey is not over.</summary>
        public static bool TryConsumeArrival(Pawn_PathFollower pather, Pawn pawn)
        {
            if (pawn == null || !pending.TryGetValue(pawn.thingIDNumber, out Transit t))
            {
                return false;
            }
            if (t.near == null || t.far == null || !t.near.Spawned || !t.far.Spawned)
            {
                Clear(pawn);
                return false;
            }
            if (pawn.Position != t.near.Position)
            {
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
                pather.StartPath(t.realDest, t.realPeMode);
                return true;
            }
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
