using System.Collections.Generic;
using RimWorld;
using UnityEngine;
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
            /// <summary>Clamp for the ferry job's count: residual demand when
            /// the verdict came from the demand path, or the destination
            /// storage's absorbable capacity for storage moves (2026-07-25
            /// log-carousel fix: carrying a full stack toward a sliver of
            /// space stranded the remainder unstored at the stair mouth and
            /// the return haul built a permanent up-down loop). Always > 0 on
            /// a live verdict.</summary>
            public int count;
            /// <summary>True when the verdict came from the demand path (the
            /// caller claims an in-flight ledger entry); false for storage
            /// moves.</summary>
            public bool demand;
            /// <summary>True when this is a STORAGE verdict whose destination
            /// priority strictly beats the best storage the item could reach on
            /// its own level - the elevated (above-vanilla) haul giver fires
            /// only on these. Always false for demand verdicts.</summary>
            public bool beatsLocal;
        }

        /// <summary>Storage settings changed somewhere: every cached verdict
        /// downstream of storage priorities is suspect. Cheap full reset.</summary>
        public static void ClearVerdicts()
        {
            verdictCache.Clear();
        }

        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs)
        {
            return TargetLevelFor(pawn, t, out stairs, ignorePins: false, out int _, out bool _);
        }

        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs, bool ignorePins)
        {
            return TargetLevelFor(pawn, t, out stairs, ignorePins, out int _, out bool _);
        }

        /// <summary>ignorePins is the explicit-player-intent variant (Allow
        /// Tool's Haul Urgently designation): both export pins are bypassed -
        /// the player pointed at the stack and said MOVE - and the verdict
        /// cache is skipped in BOTH directions so pin-free verdicts never
        /// poison the autonomous flows' cached answers.</summary>
        /// <summary>Count-aware variant: allowedCount is the clamp for the
        /// ferry job's count - residual demand (net of other pawns' en-route
        /// cargo) when demand is true, or the destination storage's
        /// absorbable capacity when demand is false. Always > 0 on a
        /// non-null verdict; callers clamp job counts to it so a trip never
        /// carries more than the other level can actually take (vanilla
        /// no-space parity).</summary>
        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs, bool ignorePins, out int allowedCount, out bool demand)
        {
            return TargetLevelFor(pawn, t, out stairs, ignorePins, out allowedCount, out demand, out bool _);
        }

        /// <summary>beatsLocal variant: also reports whether this is a STORAGE
        /// move whose destination priority strictly beats the best storage the
        /// item could reach on its own level. The elevated cross-level haul
        /// giver (priorityInType above vanilla HaulGeneral) fires only on
        /// beatsLocal verdicts, so a higher-tier stockpile on another level - a
        /// Critical larder below, say - pulls the stack across at full hauling
        /// urgency instead of starving behind every local haul, while
        /// equal-tier lateral moves stay on the low-priority givers.</summary>
        public static Map TargetLevelFor(Pawn pawn, Thing t, out Building_ABStairs stairs, bool ignorePins, out int allowedCount, out bool demand, out bool beatsLocal)
        {
            stairs = null;
            allowedCount = 0;
            demand = false;
            beatsLocal = false;
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
                        allowedCount = entry.count;
                        demand = entry.demand;
                        beatsLocal = entry.beatsLocal;
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
                // Best tier the item could reach on its OWN level right now
                // (its current cell, or a strictly-better local stockpile).
                StoragePriority bestLocal = BestLocalPriority(pawn, t, map, current);
                int cap = 0;
                StoragePriority destPrio = StoragePriority.Unstored;
                if (Check(pawn, t, comp.upperMap, current, ref stairs, ref foundCell, ref cap, out destPrio))
                {
                    found = comp.upperMap;
                    allowedCount = cap;
                }
                else if (Check(pawn, t, comp.lowerMap, current, ref stairs, ref foundCell, ref cap, out destPrio))
                {
                    found = comp.lowerMap;
                    allowedCount = cap;
                }
                if (found != null)
                {
                    // One-big-map parity: only a STRICTLY higher tier than the
                    // best local option elevates the haul above vanilla. Equal
                    // tiers stay on the low-priority givers (no stair thrash),
                    // and the elevation is monotone (bounded by Critical) so it
                    // cannot oscillate.
                    beatsLocal = (int)destPrio > (int)bestLocal;
                }
            }
            if (found == null && (ignorePins || CrossLevelDemand.ExportAllowedForDemand(map, t)))
            {
                // No better storage move: pull materials toward islands whose
                // blueprints, benches, mouths, or relay interchanges still
                // need them. Strictly routed toward the demanding island,
                // count-aware so callers clamp to the residual want.
                if (CrossLevelDemand.TryRouteDemand(pawn, comp.upperMap, t, out stairs, out Building_ABStairs exitUp, out int wantUp))
                {
                    found = comp.upperMap;
                    foundCell = exitUp.Position;
                    allowedCount = wantUp;
                    demand = true;
                }
                else if (CrossLevelDemand.TryRouteDemand(pawn, comp.lowerMap, t, out stairs, out Building_ABStairs exitDown, out int wantDown))
                {
                    found = comp.lowerMap;
                    foundCell = exitDown.Position;
                    allowedCount = wantDown;
                    demand = true;
                }
            }
            if (!ignorePins)
            {
                verdictCache[t.thingIDNumber] = new VerdictEntry
                {
                    tick = now,
                    mapId = found?.uniqueID ?? -1,
                    cell = foundCell,
                    count = allowedCount,
                    demand = demand,
                    beatsLocal = beatsLocal
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

        private static bool Check(Pawn pawn, Thing t, Map target, StoragePriority current, ref Building_ABStairs stairs, ref IntVec3 destCell, ref int capacity, out StoragePriority destPriority)
        {
            destPriority = StoragePriority.Unstored;
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
                int cap = 0;
                StoragePriority foundPrio = StoragePriority.Unstored;
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
                    if (better)
                    {
                        // Destination tier (for the beats-local elevation test)
                        // and capacity BEFORE the container position overwrites
                        // storeCell (an invalid cell is what identifies the
                        // container path). Reads only target-map grids, so
                        // running under the virtual swap is safe.
                        foundPrio = haulDest.GetStoreSettings().Priority;
                        cap = AbsorbCapacity(t, target, storeCell, haulDest);
                    }
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
                // No-space parity (2026-07-25): the trip may only carry what
                // the discovered destination can actually absorb. A full
                // stack chasing a sliver of space stranded the remainder
                // unstored at the stair mouth, and the return haul toward its
                // old storage built the endless up-down log carousel.
                if (cap <= 0)
                {
                    continue;
                }
                // Real positions are restored: upgrade to the stair pair that
                // minimizes the whole trip. Strict inside; the discovering pair
                // stays when nothing better strictly routes.
                StairRouter.Reroute(pawn, target, storeCell, ref s, ref exit);
                stairs = s;
                destCell = storeCell;
                capacity = cap;
                destPriority = foundPrio;
                return true;
            }
            return false;
        }

        /// <summary>Highest storage priority the item could reach on its OWN
        /// level right now: its current cell's tier, or a strictly-better local
        /// stockpile if one exists. The elevated cross-level giver fires only
        /// when another level beats THIS, so equal-tier lateral moves stay
        /// deprioritized (no stair thrash) while a genuinely higher tier - a
        /// Critical stockpile below - pulls the item across at full urgency.
        /// Runs on the pawn's real map (no virtual swap), reading real
        /// positions.</summary>
        private static StoragePriority BestLocalPriority(Pawn pawn, Thing t, Map map, StoragePriority current)
        {
            if (StoreUtility.TryFindBestBetterStorageFor(t, pawn, map, current, pawn.Faction,
                    out IntVec3 _, out IHaulDestination localDest, needAccurateResult: false)
                && localDest != null)
            {
                return localDest.GetStoreSettings().Priority;
            }
            return current;
        }

        /// <summary>How many items of t the discovered destination can absorb
        /// right now. Mirrors vanilla's storage-capacity semantics: container
        /// destinations report their own acceptance; cell destinations sum the
        /// slot group's free space (empty cells and same-def partial stacks),
        /// capped at one stack - a single trip never carries more. Coarse on
        /// purpose: reservation races self-heal via the vanilla store leg on
        /// arrival, exactly like two local haulers racing one cell.</summary>
        private static int AbsorbCapacity(Thing t, Map target, IntVec3 storeCell, IHaulDestination haulDest)
        {
            int max = t.def.stackLimit;
            // Container destination (grave, casket, Deep-Storage style):
            // resolved before storeCell is overwritten with its position.
            if (!storeCell.IsValid)
            {
                if (haulDest is Thing destThing)
                {
                    ThingOwner inner = ThingOwnerUtility.TryGetInnerInteractableThingOwner(destThing);
                    if (inner != null)
                    {
                        return Mathf.Min(inner.GetCountCanAccept(t), max);
                    }
                }
                // Unknown destination shape: fail open with the old behavior.
                return max;
            }
            SlotGroup group = target.haulDestinationManager.SlotGroupAt(storeCell);
            if (group == null)
            {
                return CellCapacity(t, target, storeCell);
            }
            int sum = 0;
            List<IntVec3> cells = group.CellsList;
            for (int i = 0; i < cells.Count; i++)
            {
                sum += CellCapacity(t, target, cells[i]);
                if (sum >= max)
                {
                    return max;
                }
            }
            return sum;
        }

        /// <summary>Free space for t in one storage cell, mirroring vanilla's
        /// NoStorageBlockersIn: a non-stackable item or a blocking building
        /// zeroes the cell; a same-def partial stack leaves its remainder.</summary>
        private static int CellCapacity(Thing t, Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return 0;
            }
            int cap = t.def.stackLimit;
            List<Thing> list = map.thingGrid.ThingsListAt(c);
            for (int i = 0; i < list.Count; i++)
            {
                Thing other = list[i];
                if (other.def.EverStorable(false))
                {
                    if (!other.CanStackWith(t))
                    {
                        return 0;
                    }
                    cap = Mathf.Min(cap, Mathf.Max(t.def.stackLimit - other.stackCount, 0));
                }
                else if ((other.def.entityDefToBuild != null
                        && other.def.entityDefToBuild.passability != Traversability.Standable)
                    || (other.def.surfaceType == SurfaceType.None
                        && other.def.passability != Traversability.Standable))
                {
                    return 0;
                }
            }
            return cap;
        }
    }
}
