using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Destination-aware stairwell selection. NearestUsableStairs minimizes the
    /// pawn's walk to the stairwell on THIS level and ignores where the exit
    /// lands on the other one, so a pawn heading to bed happily climbs the
    /// nearest shaft and then crosses the whole destination level on foot. When
    /// the final destination is known, the right choice minimizes the whole
    /// trip: pawn -> stairs here, climb, exit -> destination over there.
    ///
    /// Consumers keep their single virtual scan (which is what discovers the
    /// destination) and then call Reroute to upgrade the picked stairwell before
    /// making the job. Costs are straight-line cell distances plus the climb
    /// time converted to cells; exits that cannot region-reach the destination
    /// are skipped (checked pawn-less and door-permissive), but when no exit
    /// passes that filter the best by distance still wins so flows never brick
    /// on an approximation.
    /// </summary>
    public static class StairRouter
    {
        /// <summary>Rough cells-per-tick for converting climb ticks into walk
        /// distance so slow shafts lose ties against close pairs. One cardinal
        /// cell costs a typical pawn ~13 ticks; precision is pointless here.</summary>
        private const float CellsPerClimbTick = 1f / 13f;

        /// <summary>Best stairwell toward a known destination cell on the target
        /// map. Falls back to plain nearest-to-pawn when dest is invalid, so
        /// callers can pass IntVec3.Invalid to mean "no hint".</summary>
        public static bool TryBestToward(Pawn pawn, Map target, IntVec3 dest,
            out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            stairs = null;
            exit = null;
            if (pawn == null || pawn.Map == null || target == null || target.Disposed)
            {
                return false;
            }
            List<Building_ABStairs> all = pawn.Map.Levels()?.Stairs;
            if (all == null)
            {
                return false;
            }
            bool destValid = dest.IsValid && dest.InBounds(target);
            Building_ABStairs bestReach = null;
            float bestReachCost = float.MaxValue;
            Building_ABStairs bestAny = null;
            float bestAnyCost = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                Building_ABStairs s = all[i];
                if (s == null || !s.Spawned || (s.Ext != null && s.Ext.utilityOnly))
                {
                    continue;
                }
                Building_ABStairs cp = s.CounterpartTowards(target);
                if (cp == null)
                {
                    continue;
                }
                float cost = (s.Position - pawn.Position).LengthHorizontal + ClimbCost(s, pawn);
                if (destValid)
                {
                    cost += (cp.Position - dest).LengthHorizontal;
                }
                if (cost >= bestAnyCost && cost >= bestReachCost)
                {
                    // Cannot improve either slot; skip the region checks.
                    continue;
                }
                if (!pawn.CanReach(s, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                if (cost < bestAnyCost)
                {
                    bestAny = s;
                    bestAnyCost = cost;
                }
                if (destValid && cost < bestReachCost && ExitReaches(target, cp, dest))
                {
                    bestReach = s;
                    bestReachCost = cost;
                }
            }
            stairs = bestReach ?? bestAny;
            exit = stairs?.CounterpartTowards(target);
            return exit != null;
        }

        /// <summary>Upgrade an already-resolved (stairs, exit) pair once the
        /// actual destination is known. Keeps the original pair when nothing
        /// resolves (defensive: the original pair already passed its checks).</summary>
        public static void Reroute(Pawn pawn, Map target, IntVec3 dest,
            ref Building_ABStairs stairs, ref Building_ABStairs exit)
        {
            if (!dest.IsValid)
            {
                return;
            }
            if (TryBestToward(pawn, target, dest, out Building_ABStairs s, out Building_ABStairs e))
            {
                stairs = s;
                exit = e;
            }
        }

        /// <summary>Destination hint from a probe job produced by a virtual scan
        /// on the target map: the primary target's cell when it lives there.
        /// Cell targets are trusted (the scan ran on the target map); thing
        /// targets are verified against it.</summary>
        public static IntVec3 DestHint(Job job, Map target)
        {
            if (job == null || target == null)
            {
                return IntVec3.Invalid;
            }
            LocalTargetInfo a = job.targetA;
            if (!a.IsValid)
            {
                return IntVec3.Invalid;
            }
            if (a.HasThing)
            {
                Thing t = a.Thing;
                return t != null && t.MapHeld == target ? t.PositionHeld : IntVec3.Invalid;
            }
            return a.Cell.InBounds(target) ? a.Cell : IntVec3.Invalid;
        }

        /// <summary>Destination hint from a thing discovered by a virtual scan
        /// (bed, meal, haulable). Null-safe and map-checked.</summary>
        public static IntVec3 DestHint(Thing found, Map target)
        {
            return found != null && found.MapHeld == target ? found.PositionHeld : IntVec3.Invalid;
        }

        private static float ClimbCost(Building_ABStairs s, Pawn pawn)
        {
            return s.ClimbTicksFor(pawn) * CellsPerClimbTick;
        }

        /// <summary>Pawn-less region reachability from the exit to the
        /// destination on the target map. PassDoors because the real pawn can
        /// open most doors; this is a coarse filter against exits that dump into
        /// a sealed shaft room, not a path guarantee.</summary>
        private static bool ExitReaches(Map target, Building_ABStairs exit, IntVec3 dest)
        {
            return target.reachability.CanReach(exit.Position, dest, PathEndMode.Touch,
                TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly, canBashDoors: false));
        }
    }
}
