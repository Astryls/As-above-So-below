using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Decides whether an item should be carried to a linked level: true when a
    /// storage with better priority than the item's current cell exists there,
    /// evaluated by vanilla StoreUtility while the pawn is virtually placed at the
    /// stairwell exit. Verdicts are cached per item for 600 ticks so idle scan
    /// passes stay cheap.
    /// </summary>
    public static class CrossLevelHaul
    {
        private static int VerdictTtlTicks => ABMod.Settings?.jobCacheTtl ?? 600;

        private static readonly Dictionary<int, VerdictEntry> verdictCache = new Dictionary<int, VerdictEntry>();

        private struct VerdictEntry
        {
            public int tick;
            public int mapId;
            /// <summary>Store cell discovered when the verdict was made, so the
            /// cached path can still pick the stairwell nearest the storage.
            /// Explicitly Invalid when unknown (default IntVec3 is a real cell).</summary>
            public IntVec3 cell;
        }

        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs)
        {
            stairs = null;
            if (!ABGuard.On(ABGuard.Logistics) || pawn == null || t == null)
            {
                return null;
            }
            Map map = pawn.Map;
            LevelComp comp = map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            if (!t.Spawned || t.Map != map || t.IsForbidden(pawn)
                || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced: false))
            {
                return null;
            }

            int now = Find.TickManager.TicksGame;
            if (verdictCache.TryGetValue(t.thingIDNumber, out VerdictEntry entry) && now - entry.tick < VerdictTtlTicks)
            {
                if (entry.mapId == -1)
                {
                    return null;
                }
                Map cached = FindLinked(comp, entry.mapId);
                if (cached != null)
                {
                    stairs = CrossLevelWork.NearestUsableStairsCached(pawn, cached);
                    Building_ABStairs cachedExit = stairs?.CounterpartTowards(cached);
                    if (cachedExit == null)
                    {
                        return null;
                    }
                    StairRouter.Reroute(pawn, cached, entry.cell, ref stairs, ref cachedExit);
                    return cached;
                }
                return null;
            }
            if (verdictCache.Count > 2048)
            {
                verdictCache.Clear();
            }

            Map found = null;
            IntVec3 foundCell = IntVec3.Invalid;
            // Quantity-aware pin: a level keeps only as much of a material as its
            // blueprints still need; surplus stacks export normally.
            bool exportBlocked = !CrossLevelDemand.ExportAllowed(map, t);
            if (!exportBlocked)
            {
                StoragePriority current = StoreUtility.CurrentStoragePriorityOf(t);
                if (Check(pawn, t, comp.upperMap, current, ref stairs, ref foundCell))
                {
                    found = comp.upperMap;
                }
                else if (Check(pawn, t, comp.lowerMap, current, ref stairs, ref foundCell))
                {
                    found = comp.lowerMap;
                }
                if (found == null)
                {
                    // No better storage anywhere: pull materials toward levels whose
                    // blueprints and frames still need them. Loose delivery at the
                    // stairs is fine, construct-deliver uses unstored resources.
                    if (DemandCheck(pawn, t, comp.upperMap, ref stairs))
                    {
                        found = comp.upperMap;
                    }
                    else if (DemandCheck(pawn, t, comp.lowerMap, ref stairs))
                    {
                        found = comp.lowerMap;
                    }
                }
            }
            verdictCache[t.thingIDNumber] = new VerdictEntry
            {
                tick = now,
                mapId = found?.uniqueID ?? -1,
                cell = foundCell
            };
            return found;
        }

        private static bool DemandCheck(Pawn pawn, Thing t, Map target, ref Building_ABStairs stairs)
        {
            if (target == null || target.Disposed || !CrossLevelDemand.Demands(target, t.def))
            {
                return false;
            }
            Building_ABStairs s = CrossLevelWork.NearestUsableStairsCached(pawn, target);
            if (s?.CounterpartTowards(target) == null)
            {
                return false;
            }
            stairs = s;
            return true;
        }

        private static Map FindLinked(LevelComp comp, int id)
        {
            if (comp.upperMap != null && !comp.upperMap.Disposed && comp.upperMap.uniqueID == id)
            {
                return comp.upperMap;
            }
            if (comp.lowerMap != null && !comp.lowerMap.Disposed && comp.lowerMap.uniqueID == id)
            {
                return comp.lowerMap;
            }
            return null;
        }

        private static bool Check(Pawn pawn, Thing t, Map target, StoragePriority current, ref Building_ABStairs stairs, ref IntVec3 destCell)
        {
            if (target == null || target.Disposed)
            {
                return false;
            }
            Building_ABStairs s = CrossLevelWork.NearestUsableStairsCached(pawn, target);
            Building_ABStairs exit = s?.CounterpartTowards(target);
            if (exit == null)
            {
                return false;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return false;
            }
            // The item's position must ride along: IsGoodStoreCell starts its
            // reachability test from the item, and the item's home coordinates
            // usually mirror into region-less open air on the other level.
            IntVec3 oldItemPos = ABVirtualPosition.SwapPositionOnly(t, exit.Position);
            bool better;
            IntVec3 storeCell = IntVec3.Invalid;
            try
            {
                // Storage-FOR, not store-CELL (verify sweep 2026-07-23): the
                // cell-only search misses container destinations - graves,
                // caskets, and modded container storage (Deep Storage style) on
                // the linked level were invisible to the push side, so corpses
                // never rode down to a basement crypt. Containers resolve to
                // their own position for stair routing.
                better = StoreUtility.TryFindBestBetterStorageFor(t, pawn, target, current, pawn.Faction,
                    out storeCell, out IHaulDestination haulDest, needAccurateResult: false);
                if (better && !storeCell.IsValid && haulDest is Thing destThing)
                {
                    storeCell = destThing.Position;
                }
            }
            finally
            {
                ABVirtualPosition.RestorePositionOnly(t, oldItemPos);
                ABVirtualPosition.Restore(pawn, token);
            }
            if (better)
            {
                // Real positions are restored: safe to route by the store cell.
                StairRouter.Reroute(pawn, target, storeCell, ref s, ref exit);
                stairs = s;
                destCell = storeCell;
                return true;
            }
            return false;
        }
    }
}
