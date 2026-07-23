using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Forced cross-level construction with materials in hand (user report
    /// 2026-07-23): right-clicking a blueprint or frame on another level only
    /// offered the vanilla construct order, which the wrapped generator
    /// evaluates ON the target level - no materials there means a disabled
    /// "need materials" option even when the pawn is standing next to a full
    /// stockpile. The two levels' storage never reads as connected.
    ///
    /// This adds a purpose-built float menu option when the clicked
    /// constructible still needs a material the TARGET level lacks but the
    /// pawn's OWN level can supply: the pawn picks up a load, carries it
    /// through the stairs, drops it at the site, and immediately runs the
    /// vanilla deliver-resources giver against the clicked thing (now
    /// satisfiable locally) - so one order does the whole trip and ends in
    /// actual construction. Follow-up loads flow through the construction
    /// supply giver and demand hauling as usual.
    /// </summary>
    internal static class ABConstructSupply
    {
        /// <summary>Append the option to a cross-level float menu when it
        /// applies. Called from CrossLevelOrders.BuildOptions after the
        /// vanilla options are generated and wrapped; only for pawns that are
        /// not already on the target level.</summary>
        internal static void AddOption(List<FloatMenuOption> options, Pawn pawn, Map targetMap,
            Map cur, Vector3 clickPos)
        {
            try
            {
                if (options == null || !ABGuard.On(ABGuard.Logistics))
                {
                    return;
                }
                ABSettings settings = ABMod.Settings;
                if (settings == null || !settings.crossLevelSupply || !settings.supplyConstruction)
                {
                    return;
                }
                if (pawn == null || !pawn.Spawned || pawn.Map == targetMap || pawn.Downed)
                {
                    return;
                }
                // Same click transform the option generator used: through open
                // air the click aims at the level below the viewed one.
                Vector3 destPos = cur != targetMap && cur.Levels()?.lowerMap == targetMap
                    ? LevelRenderer.ScreenToBelowPos(clickPos)
                    : clickPos;
                IntVec3 cell = destPos.ToIntVec3();
                if (!cell.InBounds(targetMap))
                {
                    return;
                }
                Thing constructible = ConstructibleAt(targetMap, cell);
                if (constructible == null)
                {
                    return;
                }
                IConstructible ic = (IConstructible)constructible;
                Thing stack = null;
                int needed = 0;
                List<ThingDefCountClass> cost = ic.TotalMaterialCost();
                for (int i = 0; i < cost.Count; i++)
                {
                    ThingDef def = cost[i].thingDef;
                    if (def == null)
                    {
                        continue;
                    }
                    int remaining = ic.ThingCountNeeded(def);
                    if (remaining <= 0 || TargetLevelHas(targetMap, def))
                    {
                        // Satisfied, or the target level can feed the vanilla
                        // deliver giver itself - the wrapped option covers it.
                        continue;
                    }
                    stack = LocalStackOf(pawn, def);
                    if (stack != null)
                    {
                        needed = remaining;
                        break;
                    }
                }
                if (stack == null)
                {
                    return;
                }
                Thing target = constructible;
                Thing carry = stack;
                int count = needed;
                Map dest = targetMap;
                options.Add(new FloatMenuOption(
                    "AB_BringMaterialsAndBuild".Translate(carry.def.label, target.LabelShort),
                    delegate { StartSupplyOrder(pawn, dest, target, carry, count); },
                    MenuOptionPriority.High));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "construct supply option");
            }
        }

        /// <summary>The player's blueprint or frame at the cell, if any.
        /// Install/reinstall blueprints carry their own thing and need no
        /// materials, so they never qualify.</summary>
        private static Thing ConstructibleAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Faction == Faction.OfPlayer && !(t is Blueprint_Install) && t is IConstructible)
                {
                    return t;
                }
            }
            return null;
        }

        private static bool TargetLevelHas(Map map, ThingDef def)
        {
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].Spawned && !things[i].IsForbidden(Faction.OfPlayer))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A stack of the material on the pawn's own level it could
        /// actually pick up, respecting the level's own construction needs.</summary>
        private static Thing LocalStackOf(Pawn pawn, ThingDef def)
        {
            List<Thing> things = pawn.Map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Spawned && !t.IsForbidden(pawn)
                    && CrossLevelDemand.ExportAllowed(pawn.Map, t)
                    && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced: true))
                {
                    return t;
                }
            }
            return null;
        }

        private static void StartSupplyOrder(Pawn pawn, Map targetMap, Thing constructible,
            Thing stack, int needed)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead
                    || stack == null || !stack.Spawned || stack.Map != pawn.Map)
                {
                    return;
                }
                if (!CrossLevelWork.TryResolveStairs(pawn, targetMap, out Building_ABStairs stairs,
                    out Building_ABStairs exit))
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, stack, stairs);
                job.targetC = exit;
                job.count = Mathf.Min(needed, Mathf.Min(stack.stackCount,
                    pawn.carryTracker.MaxStackSpaceEver(stack.def)));
                job.playerForced = true;
                ABPendingOrders.Set(pawn, targetMap, delegate { FinishOnSite(pawn, constructible); });
                pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "construct supply order");
            }
        }

        /// <summary>Arrival continuation: land the cargo (the vanilla deliver
        /// giver only sees spawned stacks; the ordered job also clears the
        /// generic store-cargo job the transfer queued) and run the forced
        /// deliver-resources giver against the clicked constructible - the
        /// pawn hauls its own dropped load to the site and builds.</summary>
        private static void FinishOnSite(Pawn pawn, Thing constructible)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead
                    || constructible == null || constructible.Destroyed || !constructible.Spawned
                    || constructible.Map != pawn.Map)
                {
                    return;
                }
                if (pawn.carryTracker?.CarriedThing != null)
                {
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing _);
                }
                // Blueprints may have turned into frames mid-climb; re-resolve
                // the giver by what is actually standing there now.
                WorkGiverDef giverDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail(
                    constructible is Frame
                        ? "ConstructDeliverResourcesToFrames"
                        : "ConstructDeliverResourcesToBlueprints");
                WorkGiver_Scanner scanner = giverDef?.Worker as WorkGiver_Scanner;
                Job job = scanner?.JobOnThing(pawn, constructible, forced: true);
                if (job != null)
                {
                    job.playerForced = true;
                    pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "construct supply arrival");
            }
        }
    }
}
