using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// MULTI-HOP (2+ level) cross-level hauling. The single-hop givers only ever
    /// look at directly-adjacent levels (CrossLevelHaul.TargetLevelFor checks
    /// comp.upperMap / comp.lowerMap), so a stack whose only accepting storage
    /// is two levels away - a designated basement chunk and a chunk stockpile on
    /// the sky, with a bare surface in between - was never hauled at all (user
    /// report 2026-07-26 "pawns do not haul across 2 levels, they do haul from
    /// basement to ground level").
    ///
    /// This adds a GREEDY chain: the item is carried one gap at a time toward the
    /// nearest level that accepts it and stored at the FIRST such level (user
    /// directive: "stop at first level that accepts it"). Because each stair
    /// transfer despawns the pawn - which ends its job - a single job cannot span
    /// multiple gaps; instead every hop is its own AB_HaulChainAcrossLevels job,
    /// and StairTransfer's ABPendingOrders callback (OnArrive) decides per landing
    /// whether to store here or take the next hop. The item rides in the pawn's
    /// hands the whole way (StairTransfer preserves the carry), so no intermediate
    /// drop / re-designation is needed and the vanilla haulability gate is honored
    /// at the source only.
    /// </summary>
    public static class CrossLevelHaulChain
    {
        /// <summary>Source-side gate for STARTING a far chain: same guard +
        /// vanilla haulability parity the adjacent verdict uses. The in-flight
        /// continuation (OnArrive) does NOT re-gate - the haul is already
        /// committed and the item is in hand.</summary>
        private static bool CanStartFor(Pawn pawn, Thing item)
        {
            if (!ABGuard.On(ABGuard.Logistics) || pawn == null || item == null
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling)
            {
                return false;
            }
            // NOTE: intentionally NOT gated on VirtualScanActive. TryStartFarHaul
            // does no virtual position swaps of its own (coarse LevelAcceptsItem
            // + reachability + a same-map best-local read), so it is safe to
            // EVALUATE inside another giver's virtual scan - which is exactly how
            // the fetch giver discovers that an off-level item is destined 2+
            // gaps away and sends an idle pawn to go start the push.
            if (!item.Spawned || item.Map != pawn.Map || item.IsForbidden(pawn)
                || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, item, forced: false))
            {
                return false;
            }
            // Vanilla haulability parity (see CrossLevelHaul.TargetLevelFor):
            // a non-alwaysHaulable thing (stone / slag chunk) only auto-hauls
            // when designated (Orders -> Haul) or already in storage.
            if (!item.def.alwaysHaulable
                && item.Map.designationManager.DesignationOn(item, DesignationDefOf.Haul) == null
                && !item.IsInValidStorage())
            {
                return false;
            }
            return true;
        }

        /// <summary>WorkGiver entry: is there a NON-adjacent (2+ gap) level whose
        /// storage strictly beats the item's best local option, with a routable
        /// first hop toward it? Adjacent destinations are owned by the single-hop
        /// TargetLevelFor and deliberately excluded here.</summary>
        public static bool TryStartFarHaul(Pawn pawn, Thing item,
            out Building_ABStairs entry, out Building_ABStairs exit)
        {
            entry = null;
            exit = null;
            if (!CanStartFor(pawn, item))
            {
                return false;
            }
            LevelComp comp = pawn.Map.Levels();
            if (comp == null)
            {
                return false;
            }
            // Strictly better than the best storage the item can reach on its own
            // level (mirrors the adjacent beatsLocal doctrine; for a loose chunk
            // with no local storage this is Unstored, so any real storage counts).
            int min = (int)CrossLevelHaul.BestLocalPriority(pawn, item, pawn.Map,
                StoreUtility.CurrentStoragePriorityOf(item));

            Map up2 = comp.upperMap?.Levels()?.upperMap;
            Map down2 = comp.lowerMap?.Levels()?.lowerMap;
            if (LevelAcceptsItem(up2, item, min) && FirstHop(pawn, 1, out entry, out exit))
            {
                return true;
            }
            if (LevelAcceptsItem(down2, item, min) && FirstHop(pawn, -1, out entry, out exit))
            {
                return true;
            }
            return false;
        }

        /// <summary>Runs on each landing of a chain hop (via ABPendingOrders,
        /// after StairTransfer). Stores the item if THIS level accepts it (first
        /// accepting level wins); otherwise sets it down here and - if a linked
        /// level still accepts it - (re-)designates it to haul, so the ordinary
        /// single-hop / far givers carry it onward. Every remaining leg is then a
        /// normal, well-tested haul; no carried item is threaded through a
        /// re-entrant follow-up job (the source of the "stops at the top of the
        /// first flight and never continues" bug). If nowhere accepts it, it just
        /// stays put (self-heals when storage frees up).</summary>
        public static void OnArrive(Pawn pawn, Thing item)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || item == null)
                {
                    return;
                }
                Thing carried = pawn.carryTracker?.CarriedThing;
                if (carried == null || carried != item)
                {
                    return;
                }
                // 1) This level accepts it? Store here (precise, reachable).
                if (TryStoreHere(pawn, carried))
                {
                    return;
                }
                // 2) Set it down here and hand it back to the normal givers.
                bool more = AnyLinkedLevelAccepts(pawn, carried);
                if (!pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near,
                        out Thing dropped) || dropped == null || !dropped.Spawned)
                {
                    return;
                }
                // Non-alwaysHaulable things (chunks) need a Haul designation to be
                // picked up again - the origin designation was on the level it left,
                // so re-stamp it here. alwaysHaulable things need no designation.
                if (more && !dropped.def.alwaysHaulable
                    && dropped.Map.designationManager.DesignationOn(dropped, DesignationDefOf.Haul) == null)
                {
                    dropped.Map.designationManager.AddDesignation(
                        new Designation(dropped, DesignationDefOf.Haul));
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level haul chain arrive");
                try
                {
                    if (pawn?.carryTracker?.CarriedThing != null)
                    {
                        pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                    }
                }
                catch
                {
                    // last-resort: never rethrow out of the transfer callback.
                }
            }
        }

        /// <summary>Does any DIRECTLY-linked level (or one two gaps away) have
        /// accepting storage for the item? Gate for re-designating an
        /// intermediate drop so it is not left with a stray Haul designation and
        /// nowhere to go.</summary>
        private static bool AnyLinkedLevelAccepts(Pawn pawn, Thing item)
        {
            LevelComp comp = pawn.Map.Levels();
            if (comp == null)
            {
                return false;
            }
            Map up1 = comp.upperMap;
            Map down1 = comp.lowerMap;
            return LevelAcceptsItem(up1, item, 0)
                || LevelAcceptsItem(down1, item, 0)
                || LevelAcceptsItem(up1?.Levels()?.upperMap, item, 0)
                || LevelAcceptsItem(down1?.Levels()?.lowerMap, item, 0);
        }

        /// <summary>Store the carried item on the pawn's CURRENT level if that
        /// level has accepting, reachable storage. Vanilla builds the deposit
        /// job; a pawn already carrying the thing just walks it to the cell.</summary>
        private static bool TryStoreHere(Pawn pawn, Thing item)
        {
            Job store = HaulAIUtility.HaulToStorageJob(pawn, item, forced: false);
            if (store == null)
            {
                return false;
            }
            pawn.jobs?.TryTakeOrderedJob(store, JobTag.Misc);
            return true;
        }

        /// <summary>Coarse "does this level have accepting storage for the item at
        /// a strictly-higher priority than minPriority" - a per-cell vanilla
        /// blocker check, no pawn/virtual-position needed. Reachability is
        /// validated at execution (the hop + the store job); an unreachable
        /// accepting level self-heals into a drop.</summary>
        private static bool LevelAcceptsItem(Map level, Thing item, int minPriority)
        {
            if (level == null || level.Disposed)
            {
                return false;
            }
            List<SlotGroup> groups = level.haulDestinationManager?.AllGroupsListForReading;
            if (groups == null)
            {
                return false;
            }
            for (int i = 0; i < groups.Count; i++)
            {
                SlotGroup g = groups[i];
                if (g?.Settings == null || (int)g.Settings.Priority <= minPriority
                    || !g.Settings.AllowedToAccept(item))
                {
                    continue;
                }
                List<IntVec3> cells = g.CellsList;
                for (int c = 0; c < cells.Count; c++)
                {
                    if (CrossLevelHaul.CellCapacity(item, level, cells[c]) > 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>First usable (stairs, exit) pair on the pawn's map toward the
        /// adjacent level in the given direction. The chain re-resolves the next
        /// hop live at each landing, so nearest-island-first is enough here.</summary>
        private static bool FirstHop(Pawn pawn, int dir,
            out Building_ABStairs entry, out Building_ABStairs exit)
        {
            entry = null;
            exit = null;
            LevelComp comp = pawn.Map.Levels();
            Map adj = dir > 0 ? comp?.upperMap : comp?.lowerMap;
            if (adj == null || adj.Disposed)
            {
                return false;
            }
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, adj);
            if (pairs.Count == 0)
            {
                return false;
            }
            entry = pairs[0].stairs;
            exit = pairs[0].exit;
            return entry != null && exit != null;
        }
    }
}
