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
        private const int VerdictTtlTicks = 600;

        private static readonly Dictionary<int, VerdictEntry> verdictCache = new Dictionary<int, VerdictEntry>();

        private struct VerdictEntry
        {
            public int tick;
            public int mapId;
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
                    stairs = CrossLevelWork.NearestUsableStairs(pawn, cached, checkReachability: true);
                    return stairs?.Counterpart != null ? cached : null;
                }
                return null;
            }
            if (verdictCache.Count > 2048)
            {
                verdictCache.Clear();
            }

            Map found = null;
            // Quantity-aware pin: a level keeps only as much of a material as its
            // blueprints still need; surplus stacks export normally.
            bool exportBlocked = !CrossLevelDemand.ExportAllowed(map, t);
            if (!exportBlocked)
            {
                StoragePriority current = StoreUtility.CurrentStoragePriorityOf(t);
                if (Check(pawn, t, comp.upperMap, current, ref stairs))
                {
                    found = comp.upperMap;
                }
                else if (Check(pawn, t, comp.lowerMap, current, ref stairs))
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
                mapId = found?.uniqueID ?? -1
            };
            return found;
        }

        private static bool DemandCheck(Pawn pawn, Thing t, Map target, ref Building_ABStairs stairs)
        {
            if (target == null || target.Disposed || !CrossLevelDemand.Demands(target, t.def))
            {
                return false;
            }
            Building_ABStairs s = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            if (s?.Counterpart == null)
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

        private static bool Check(Pawn pawn, Thing t, Map target, StoragePriority current, ref Building_ABStairs stairs)
        {
            if (target == null || target.Disposed)
            {
                return false;
            }
            Building_ABStairs s = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            Building_ABStairs exit = s?.Counterpart;
            if (exit == null)
            {
                return false;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool better;
            try
            {
                better = StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, target, current, pawn.Faction,
                    out IntVec3 _, needAccurateResult: false);
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
            }
            if (better)
            {
                stairs = s;
                return true;
            }
            return false;
        }
    }
}
