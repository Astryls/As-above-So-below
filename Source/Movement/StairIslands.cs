using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Island-aware stairwell enumeration (2026-07-24 logistics rework). A
    /// target level is often several disconnected walkable islands (two house
    /// roofs, a bridge, a sealed vault). Every cross-level scan used to place
    /// the pawn at ONE stairwell exit - the nearest to the pawn - so storage,
    /// work, and demand on any other island was invisible, and deliveries
    /// routed through stairs that could never reach their goal (the two-house
    /// wood ferry loop). This helper returns ONE representative usable stair
    /// pair per distinct target-map island so scans can cover the whole level
    /// at bounded cost.
    ///
    /// Grouping uses pawn-less PassDoors region reachability between exits
    /// (same coarse filter StairRouter uses); pairs are pawn-reachability
    /// checked on the source side and ordered nearest-first. Memoized per
    /// (pawn, target) per tick because haul scans query per candidate item.
    /// </summary>
    public static class StairIslands
    {
        /// <summary>Hard cap on islands examined. More than this many stair
        /// islands on one level is pathological; the nearest ones win.</summary>
        public const int MaxIslands = 4;

        public struct Pair
        {
            public Building_ABStairs stairs;
            public Building_ABStairs exit;
        }

        private struct MemoEntry
        {
            public int tick;
            public List<Pair> pairs;
        }

        private static readonly Dictionary<long, MemoEntry> memo = new Dictionary<long, MemoEntry>();

        /// <summary>One usable (stairs, exit) pair per distinct island of the
        /// target map, nearest-to-pawn first. Empty list when nothing links.
        /// The returned list is owned by the memo - do not mutate it.</summary>
        public static List<Pair> EntryPairs(Pawn pawn, Map target)
        {
            if (pawn == null || pawn.Map == null || target == null || target.Disposed)
            {
                return emptyList;
            }
            long key = ((long)pawn.thingIDNumber << 32) | (uint)target.uniqueID;
            int now = Find.TickManager.TicksGame;
            if (memo.TryGetValue(key, out MemoEntry entry) && entry.tick == now)
            {
                return Validate(entry.pairs, target);
            }
            if (memo.Count > 512)
            {
                memo.Clear();
            }
            List<Pair> pairs = Build(pawn, target);
            memo[key] = new MemoEntry { tick = now, pairs = pairs };
            return pairs;
        }

        private static readonly List<Pair> emptyList = new List<Pair>();

        /// <summary>Cheap re-validation of a memoized list (stairs can despawn
        /// mid-tick). Any dead entry invalidates the whole list for this call;
        /// next tick rebuilds.</summary>
        private static List<Pair> Validate(List<Pair> pairs, Map target)
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                Building_ABStairs s = pairs[i].stairs;
                if (s == null || !s.Spawned || s.CounterpartTowards(target) == null)
                {
                    return emptyList;
                }
            }
            return pairs;
        }

        private static List<Pair> Build(Pawn pawn, Map target)
        {
            List<Building_ABStairs> all = pawn.Map.Levels()?.Stairs;
            if (all == null || all.Count == 0)
            {
                return emptyList;
            }
            // Collect usable candidates sorted nearest-first so each island's
            // representative is automatically the closest stairwell to the pawn.
            List<Building_ABStairs> candidates = null;
            for (int i = 0; i < all.Count; i++)
            {
                Building_ABStairs s = all[i];
                if (s == null || !s.Spawned || (s.Ext != null && s.Ext.utilityOnly))
                {
                    continue;
                }
                Building_ABStairs cpEnd = s.CounterpartTowards(target);
                if (cpEnd == null || s.EndForbiddenFor(pawn) || cpEnd.EndForbiddenFor(pawn))
                {
                    continue; // unlinked, or door-parity forbidden passage
                }
                (candidates ?? (candidates = new List<Building_ABStairs>())).Add(s);
            }
            if (candidates == null)
            {
                return emptyList;
            }
            IntVec3 pawnPos = pawn.Position;
            candidates.Sort((a, b) =>
                (a.Position - pawnPos).LengthHorizontalSquared
                    .CompareTo((b.Position - pawnPos).LengthHorizontalSquared));
            List<Pair> pairs = new List<Pair>(2);
            for (int i = 0; i < candidates.Count && pairs.Count < MaxIslands; i++)
            {
                Building_ABStairs s = candidates[i];
                Building_ABStairs exit = s.CounterpartTowards(target);
                if (exit == null)
                {
                    continue;
                }
                // Same island as an already-kept representative? Skip: that
                // representative is nearer (sorted) and covers this island.
                bool duplicate = false;
                for (int j = 0; j < pairs.Count; j++)
                {
                    if (PawnlessReaches(target, pairs[j].exit.Position, exit.Position))
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (duplicate)
                {
                    continue;
                }
                // Source-side reachability last: it is the expensive check and
                // duplicates were already discarded without paying it.
                if (!pawn.CanReach(s, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                pairs.Add(new Pair { stairs = s, exit = exit });
            }
            return pairs;
        }

        /// <summary>Pawn-less PassDoors region reachability - the shared coarse
        /// connectivity test for island grouping and demand-site math. Not a
        /// path guarantee, but exactly the leniency a real pawn has.</summary>
        public static bool PawnlessReaches(Map map, IntVec3 from, IntVec3 to)
        {
            if (map == null || !from.InBounds(map) || !to.InBounds(map))
            {
                return false;
            }
            return map.reachability.CanReach(from, to, PathEndMode.Touch,
                TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly, canBashDoors: false));
        }
    }
}
