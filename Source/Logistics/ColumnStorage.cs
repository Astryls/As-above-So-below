using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// One-big-map storage query engine (pass 41). Answers "where in the WHOLE
    /// column should this item live?" by running VANILLA's own storage search
    /// (`StoreUtility.TryFindBestBetterStorageFor`) once per level and keeping the
    /// single best result.
    ///
    /// Why this shape:
    ///  - MOD-SAFE FOR FREE. LWM Deep Storage, Adaptive Storage Framework, shelves
    ///    and every vanilla stockpile all funnel through the same StoreUtility /
    ///    IsGoodStoreCell / NoStorageBlockersIn path, which those mods Harmony-patch.
    ///    Calling vanilla's search means their capacity and acceptance rules are
    ///    honored without us knowing anything about them.
    ///  - NO VIRTUAL-POSITION SWAP. We pass `carrier: null`, so vanilla uses the
    ///    item's own PositionHeld and skips reachability. That removes the biggest
    ///    per-item cost the old adjacent Check paid (a full pawn+item map swap per
    ///    linked level) and makes the query cheap enough to run inline.
    ///  - NO PER-ITEM CACHE. It reads live vanilla state every call, so it cannot
    ///    go stale as an item relays between levels (the whole class of cache bugs).
    ///  - VANILLA-PARITY PERF. Vanilla's WorkGiver_HaulGeneral already runs this
    ///    exact search once per haulable item; we run it once per level (<= 3),
    ///    each early-breaking out of the priority-sorted list.
    ///
    /// The DECISION is coarse (no reachability): it picks the best TIER and level.
    /// Reachability is validated at EXECUTION (the stair route per hop + the final
    /// vanilla store job), and an unreachable target self-heals into a set-down.
    /// </summary>
    public static class ColumnStorage
    {
        /// <summary>The best storage anywhere in the column that STRICTLY beats the
        /// item's current storage priority, respecting modded storage. Returns
        /// true only when that best option lives on a DIFFERENT level than the
        /// item - equal tiers, and the item's own level winning, both return false
        /// (a same-level move is vanilla's job, and an equal tier elsewhere must
        /// not cross). Ties in priority prefer the FEWEST hops, with the item's own
        /// level first.</summary>
        public static bool TryFindBetter(Pawn pawn, Thing item, out Map targetMap,
            out IntVec3 destCell, out IHaulDestination dest, out StoragePriority tier)
        {
            targetMap = null;
            destCell = IntVec3.Invalid;
            dest = null;
            tier = StoragePriority.Unstored;
            // SpawnedOrAnyParentSpawned covers both a loose item and one sitting in
            // storage; a carried item (parent pawn spawned) is fine too. This is
            // what lets the carrier-less vanilla search use PositionHeld safely.
            if (item == null || !item.SpawnedOrAnyParentSpawned || item.MapHeld == null)
            {
                return false;
            }
            LevelComp comp = item.MapHeld.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return false;
            }
            Faction faction = pawn?.Faction ?? Faction.OfPlayer;
            StoragePriority current = StoreUtility.CurrentStoragePriorityOf(item);
            int itemLevel = item.MapHeld.Level();

            StoragePriority bestTier = current;
            Map bestMap = null;
            IntVec3 bestCell = IntVec3.Invalid;
            IHaulDestination bestDest = null;
            int bestHops = int.MaxValue;

            foreach (Map m in ColumnMaps(comp, item.MapHeld))
            {
                if (m == null || m.Disposed)
                {
                    continue;
                }
                // Vanilla's own search: strictly-better-than-current storage on m,
                // carrier-less (no reachability, no swap). Mods patch this, so their
                // capacity/acceptance is honored.
                if (!StoreUtility.TryFindBestBetterStorageFor(item, null, m, current, faction,
                        out IntVec3 cell, out IHaulDestination d, needAccurateResult: false)
                    || d == null)
                {
                    continue;
                }
                StoragePriority p = d.GetStoreSettings().Priority;
                int hops = Mathf.Abs(m.Level() - itemLevel);
                if ((int)p > (int)bestTier || ((int)p == (int)bestTier && hops < bestHops))
                {
                    bestTier = p;
                    bestMap = m;
                    bestCell = cell.IsValid ? cell : (d is Thing dt ? dt.Position : IntVec3.Invalid);
                    bestDest = d;
                    bestHops = hops;
                }
            }

            // Cross-level only: the best tier lives on another level. (bestMap stays
            // null when nothing beats current; equal tiers never raise bestTier, so
            // an equal stockpile elsewhere cannot win.)
            if (bestMap == null || bestMap == item.MapHeld)
            {
                return false;
            }
            targetMap = bestMap;
            destCell = bestCell;
            dest = bestDest;
            tier = bestTier;
            return true;
        }

        /// <summary>Highest storage priority ANYWHERE in the column that would
        /// accept the item (its own level included). Cheap column-wide "is there a
        /// better home than where it is" signal for the upgrade driver and work
        /// migration - no reachability, no swap.</summary>
        public static StoragePriority BestTierInColumn(Pawn pawn, Thing item)
        {
            if (item == null || !item.SpawnedOrAnyParentSpawned || item.MapHeld == null)
            {
                return StoragePriority.Unstored;
            }
            LevelComp comp = item.MapHeld.Levels();
            if (comp == null)
            {
                return StoreUtility.CurrentStoragePriorityOf(item);
            }
            Faction faction = pawn?.Faction ?? Faction.OfPlayer;
            StoragePriority current = StoreUtility.CurrentStoragePriorityOf(item);
            StoragePriority best = current;
            foreach (Map m in ColumnMaps(comp, item.MapHeld))
            {
                if (m == null || m.Disposed)
                {
                    continue;
                }
                if (StoreUtility.TryFindBestBetterStorageFor(item, null, m, best, faction,
                        out IntVec3 _, out IHaulDestination d, needAccurateResult: false)
                    && d != null)
                {
                    StoragePriority p = d.GetStoreSettings().Priority;
                    if ((int)p > (int)best)
                    {
                        best = p;
                    }
                }
            }
            return best;
        }

        /// <summary>The column's maps, the item's OWN level first (so priority ties
        /// prefer the local option), then the up-chain and the down-chain.</summary>
        private static IEnumerable<Map> ColumnMaps(LevelComp comp, Map self)
        {
            yield return self;
            for (Map m = comp.upperMap; m != null && !m.Disposed; m = m.Levels()?.upperMap)
            {
                yield return m;
            }
            for (Map m = comp.lowerMap; m != null && !m.Disposed; m = m.Levels()?.lowerMap)
            {
                yield return m;
            }
        }
    }
}
