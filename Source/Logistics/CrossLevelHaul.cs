using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Decides whether an item should be carried to a linked level: true when a
    /// storage with better priority than the item's current cell exists there,
    /// evaluated by vanilla StoreUtility while the pawn is virtually placed at a
    /// stairwell exit. Island-aware since 2026-07-24: the storage search runs
    /// from ONE exit per distinct island of the target level (a bridge larder
    /// reachable only through the far staircase used to be invisible because
    /// only the exit nearest the pawn was ever tried), and every route is
    /// STRICT - stairs that cannot region-reach the discovered destination are
    /// never used, so trips cannot strand cargo on the wrong island. Verdicts
    /// are cached per item so idle scan passes stay cheap.
    /// </summary>
    public static class CrossLevelHaul
    {
        private static int VerdictTtlTicks => ABMod.Settings?.jobCacheTtl ?? 600;

        private static readonly Dictionary<int, VerdictEntry> verdictCache = new Dictionary<int, VerdictEntry>();

        private struct VerdictEntry
        {
            public int tick;
            public int mapId;
            /// <summary>Store cell (or demand island anchor) discovered when the
            /// verdict was made, so the cached path can still route strictly
            /// toward the goal. Explicitly Invalid when unknown (default
            /// IntVec3 is a real cell).</summary>
            public IntVec3 cell;
        }

        /// <summary>Storage settings changed somewhere: every cached verdict
        /// downstream of storage priorities is suspect. Cheap full reset.</summary>
        public static void ClearVerdicts()
        {
            verdictCache.Clear();
        }

        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs)
        {
            return TargetLevelFor(pawn, t, out stairs, ignorePins: false);
        }

        /// <summary>ignorePins is the explicit-player-intent variant (Allow
        /// Tool's Haul Urgently designation): both export pins are bypassed -
        /// the player pointed at the stack and said MOVE - and the verdict
        /// cache is skipped in BOTH directions so pin-free verdicts never
        /// poison the autonomous flows' cached answers.</summary>
        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs, bool ignorePins)
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
            // A minified thing an install blueprint (any map - vanilla's lookup
            // walks them all) is waiting for never storage-migrates: the
            // construction ferry owns it, and a storage verdict could drag it
            // AWAY from the install level. Player-explicit urgent designations
            // (ignorePins) still win.
            if (!ignorePins && t is MinifiedThing
                && InstallBlueprintUtility.ExistingBlueprintFor(t) != null)
            {
                return null;
            }

            int now = Find.TickManager.TicksGame;
            if (!ignorePins)
            {
                if (verdictCache.TryGetValue(t.thingIDNumber, out VerdictEntry entry) && now - entry.tick < VerdictTtlTicks)
                {
                    if (entry.mapId == -1)
                    {
                        return null;
                    }
                    Map cached = FindLinked(comp, entry.mapId);
                    if (cached != null && TryRouteCached(pawn, cached, entry.cell, out stairs))
                    {
                        return cached;
                    }
                    // Stale verdict (map gone, stairs gone, or islands changed so
                    // the goal is no longer strictly routable): recompute now.
                    verdictCache.Remove(t.thingIDNumber);
                    stairs = null;
                }
                if (verdictCache.Count > 2048)
                {
                    verdictCache.Clear();
                }
            }

            Map found = null;
            IntVec3 foundCell = IntVec3.Invalid;
            // Two gates (2026-07-24 relay fix): STORAGE moves respect the full
            // export policy including the import pin; DEMAND moves only the
            // native construction pin - a stack that just landed on an
            // interchange level must be liftable onward toward the level that
            // wants it immediately, or every two-hop chain stalls out the pin.
            if (ignorePins || CrossLevelDemand.ExportAllowed(map, t))
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
            }
            if (found == null && (ignorePins || CrossLevelDemand.ExportAllowedForDemand(map, t)))
            {
                // No better storage move: pull materials toward islands whose
                // blueprints, benches, mouths, or relay interchanges still
                // need them. Strictly routed toward the demanding island.
                if (CrossLevelDemand.TryRouteDemand(pawn, comp.upperMap, t, out stairs, out Building_ABStairs exitUp))
                {
                    found = comp.upperMap;
                    foundCell = exitUp.Position;
                }
                else if (CrossLevelDemand.TryRouteDemand(pawn, comp.lowerMap, t, out stairs, out Building_ABStairs exitDown))
                {
                    found = comp.lowerMap;
                    foundCell = exitDown.Position;
                }
            }
            if (!ignorePins)
            {
                verdictCache[t.thingIDNumber] = new VerdictEntry
                {
                    tick = now,
                    mapId = found?.uniqueID ?? -1,
                    cell = foundCell
                };
            }
            return found;
        }

        /// <summary>Re-route a cached verdict. With a known goal cell the route
        /// must be strict; without one (legacy or demand anchor lost) fall back
        /// to the nearest usable stairwell, matching the old behavior.</summary>
        private static bool TryRouteCached(Pawn pawn, Map target, IntVec3 cell, out Building_ABStairs stairs)
        {
            if (cell.IsValid)
            {
                return StairRouter.TryBestToward(pawn, target, cell, requireReach: true,
                    out stairs, out Building_ABStairs _);
            }
            stairs = CrossLevelWork.NearestUsableStairsCached(pawn, target);
            return stairs?.CounterpartTowards(target) != null;
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
            // One storage search per distinct island of the target level: the
            // exit nearest the pawn may belong to an island with no storage
            // while another staircase leads straight to the larder.
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            for (int p = 0; p < pairs.Count; p++)
            {
                Building_ABStairs s = pairs[p].stairs;
                Building_ABStairs exit = pairs[p].exit;
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
                if (!better)
                {
                    continue;
                }
                // Real positions are restored: upgrade to the stair pair that
                // minimizes the whole trip. Strict inside; the discovering pair
                // stays when nothing better strictly routes.
                StairRouter.Reroute(pawn, target, storeCell, ref s, ref exit);
                stairs = s;
                destCell = storeCell;
                return true;
            }
            return false;
        }
    }
}
