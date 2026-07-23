using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Achtung! (brrainz.achtung) soft compat: cross-level formation drags.
    /// Achtung's drafted position dragging computes and validates every
    /// formation slot against the PAWN's own map (Colonist.UpdateOrderPos uses
    /// pawn.Map for standability/reachability and Tools.OrderTo issues the
    /// Goto there), so dragging a line while viewing a different level either
    /// silently drops pawns from the formation or - worse, since our columns
    /// share plumb coordinates - walks them to the same cell on the WRONG
    /// level.
    ///
    /// Fix: prefix AchtungMod.Colonist.OrderTo. When the ordered pawn stands
    /// on a directly linked level of the viewed map, skip Achtung's local
    /// order and route the pawn through the stairs instead, replaying a
    /// drafted goto to its formation slot on arrival (RouteThenRun +
    /// ABPendingOrders, the same machinery as our right-click cross-level
    /// orders). Same-map pawns keep Achtung's unmodified path, so mixed
    /// selections converge onto one formation line. Repeated drag updates for
    /// the same slot cell are deduplicated per pawn.
    ///
    /// Achtung's forced-work system stays map-local (out of scope). Resolved
    /// by name at startup, foreign types never appear in signatures, fails
    /// open.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABAchtungCompat
    {
        private static bool active;

        private static FieldInfo pawnField;

        /// <summary>Last slot cell routed per pawn: drag streams call OrderTo
        /// every mouse move; only a changed slot re-issues the stairs trip.</summary>
        private static readonly Dictionary<int, IntVec3> lastCells = new Dictionary<int, IntVec3>();

        static ABAchtungCompat()
        {
            try
            {
                if (!ABDetect.Active("brrainz.achtung"))
                {
                    return;
                }
                Type colonistType = AccessTools.TypeByName("AchtungMod.Colonist");
                pawnField = colonistType != null ? AccessTools.Field(colonistType, "pawn") : null;
                MethodInfo orderTo = colonistType != null
                    ? AccessTools.Method(colonistType, "OrderTo", new[] { typeof(Vector3) })
                    : null;
                if (pawnField == null || orderTo == null)
                {
                    Log.Warning(ABLog.Tag + " Achtung detected but its drag-order internals were not found; cross-level formation drags are off.");
                    return;
                }
                HarmonyBoot.Harmony.Patch(orderTo,
                    prefix: new HarmonyMethod(typeof(ABAchtungCompat), nameof(OrderToPrefix)));
                active = true;
                ABLog.Dev("Achtung detected, cross-level formation drag routing active.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Achtung compat setup failed: " + e.Message);
            }
        }

        private static bool OrderToPrefix(object __instance, Vector3 pos)
        {
            if (!active || !ABGuard.On(ABGuard.Movement))
            {
                return true;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return true;
            }
            try
            {
                Pawn pawn = pawnField.GetValue(__instance) as Pawn;
                Map cur = Find.CurrentMap;
                if (pawn == null || cur == null || !pawn.Spawned || pawn.Map == null
                    || pawn.Map == cur)
                {
                    // Same map (or invalid): Achtung's own path.
                    return true;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (cur != comp.upperMap && cur != comp.lowerMap))
                {
                    // Not a directly linked level (unrelated map, or two levels
                    // away): leave Achtung alone rather than half-routing.
                    return true;
                }
                IntVec3 cell = pos.ToIntVec3();
                if (!cell.InBounds(cur))
                {
                    return false;
                }
                if (!cell.Standable(cur))
                {
                    cell = CellFinder.StandableCellNear(cell, cur, 2.9f);
                    if (!cell.IsValid)
                    {
                        return false;
                    }
                }
                if (lastCells.Count > 128)
                {
                    lastCells.Clear();
                }
                if (lastCells.TryGetValue(pawn.thingIDNumber, out IntVec3 last) && last == cell)
                {
                    // Same slot as the trip already underway.
                    return false;
                }
                lastCells[pawn.thingIDNumber] = cell;
                if (!CrossLevelWork.TryResolveStairs(pawn, cur, out Building_ABStairs entry,
                    out Building_ABStairs _))
                {
                    return false;
                }
                Map dest = cur;
                IntVec3 slot = cell;
                CrossLevelOrders.RouteThenRun(pawn, dest, entry, delegate
                {
                    IssueGoto(pawn, dest, slot);
                });
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "achtung cross level drag");
                return true;
            }
        }

        /// <summary>Arrival replay: the drafted goto to the formation slot,
        /// mirroring Achtung's Tools.OrderTo essentials (playerForced, no pawn
        /// collision). Map state may have changed mid-climb, so the slot is
        /// re-anchored to a standable cell.</summary>
        private static void IssueGoto(Pawn pawn, Map map, IntVec3 cell)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map != map)
                {
                    return;
                }
                IntVec3 target = cell.Standable(map)
                    ? cell
                    : CellFinder.StandableCellNear(cell, map, 2.9f);
                if (!target.IsValid)
                {
                    return;
                }
                Job job = JobMaker.MakeJob(JobDefOf.Goto, target);
                job.playerForced = true;
                job.collideWithPawns = false;
                if (pawn.jobs != null && pawn.jobs.IsCurrentJobPlayerInterruptible())
                {
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "achtung arrival goto");
            }
        }
    }
}
