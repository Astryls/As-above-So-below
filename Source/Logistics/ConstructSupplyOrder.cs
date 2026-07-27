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
                    // Not a build site: maybe a dry campfire/generator/turret
                    // (user report 2026-07-23: "need wood" with wood one level
                    // away - vanilla's fuel search never leaves the map).
                    AddRefuelOption(options, pawn, targetMap, cell);
                    return;
                }
                // Install blueprints (user report 2026-07-23, run #71): the
                // vanilla install giver dies on CanReach(mini) when the
                // minified thing sits on another level ("No path"). Offer the
                // carry-and-install trip when the mini is a loose minified
                // item on the pawn's own level. Reinstalling a still-built
                // building stays vanilla: it must be uninstalled on its own
                // level first, and the uninstall designation already migrates
                // workers there.
                if (constructible is Blueprint_Install install)
                {
                    Thing mini = install.MiniToInstallOrBuildingToReinstall;
                    if (!(mini is MinifiedThing) || !mini.Spawned || mini.Map != pawn.Map
                        || mini.IsForbidden(pawn)
                        || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, mini, forced: true)
                        // No stairwell exit reaches the install site: the trip
                        // is undeliverable; leave the vanilla disabled row.
                        || !CrossLevelWork.TryResolveStairsStrict(pawn, targetMap, install.Position,
                            out Building_ABStairs _, out Building_ABStairs _))
                    {
                        return;
                    }
                    Thing carryMini = mini;
                    Thing installTarget = install;
                    Map installDest = targetMap;
                    AddNative(options, install,
                        "AB_BringAndInstall".Translate(carryMini.LabelShort),
                        delegate { StartSupplyOrder(pawn, installDest, installTarget, carryMini, 1); });
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
                // Undeliverable sites get no option (2026-07-24): when no
                // stairwell exit region-reaches the constructible, the vanilla
                // disabled row stays - exactly like a walled-off blueprint.
                if (!CrossLevelWork.TryResolveStairsStrict(pawn, targetMap, constructible.Position,
                    out Building_ABStairs _, out Building_ABStairs _))
                {
                    return;
                }
                Thing target = constructible;
                Thing carry = stack;
                int count = needed;
                Map dest = targetMap;
                AddNative(options, constructible,
                    "AB_BringMaterialsAndBuild".Translate(carry.def.label, target.LabelShort),
                    delegate { StartSupplyOrder(pawn, dest, target, carry, count); });
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "construct supply option");
            }
        }

        /// <summary>The "Bring {fuel} and refuel {thing}" branch: the clicked
        /// player building wants fuel, the target level has NONE of its
        /// accepted fuels, and the pawn's level can spare one. One order
        /// carries a load over and runs the forced vanilla refuel giver on
        /// arrival.</summary>
        private static void AddRefuelOption(List<FloatMenuOption> options, Pawn pawn, Map targetMap, IntVec3 cell)
        {
            List<Thing> things = targetMap.thingGrid.ThingsListAt(cell);
            Thing refuelable = null;
            CompRefuelable comp = null;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].Faction != Faction.OfPlayer)
                {
                    continue;
                }
                comp = things[i].TryGetComp<CompRefuelable>();
                if (comp != null)
                {
                    refuelable = things[i];
                    break;
                }
            }
            if (refuelable == null)
            {
                return;
            }
            int needed = Mathf.CeilToInt(comp.TargetFuelLevel - comp.Fuel);
            if (needed <= 0)
            {
                return;
            }
            // Any accepted fuel already on the target level: vanilla's own
            // (wrapped) refuel option works, ours would be noise.
            foreach (ThingDef def in comp.Props.fuelFilter.AllowedThingDefs)
            {
                if (TargetLevelHas(targetMap, def))
                {
                    return;
                }
            }
            Thing stack = null;
            foreach (ThingDef def in comp.Props.fuelFilter.AllowedThingDefs)
            {
                stack = LocalStackOf(pawn, def);
                if (stack != null)
                {
                    break;
                }
            }
            if (stack == null)
            {
                return;
            }
            if (!CrossLevelWork.TryResolveStairsStrict(pawn, targetMap, refuelable.Position,
                out Building_ABStairs _, out Building_ABStairs _))
            {
                return;
            }
            Thing carry = stack;
            Thing target = refuelable;
            Map dest = targetMap;
            int count = needed;
            AddNative(options, refuelable,
                "AB_BringAndRefuel".Translate(carry.def.label, target.LabelShort),
                delegate { StartSupplyOrder(pawn, dest, target, carry, count); });
        }

        /// <summary>NATIVE FLOAT MENU POLICY (user-directed 2026-07-23): our
        /// order must read as base game, not mod. The vanilla generator left a
        /// DISABLED row for this target ("Cannot refuel campfire: Need wood")
        /// carrying the target's icon and its natural menu slot - remove that
        /// row, inherit its orderInPriority, show the same thing icon, and use
        /// Default priority so ours sorts exactly where the enabled vanilla
        /// order would have been.</summary>
        private static void AddNative(List<FloatMenuOption> options, Thing target, string label, Action action)
        {
            FloatMenuOption ours = new FloatMenuOption(label, action, MenuOptionPriority.Default,
                null, target);
            ours.iconThing = target;
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption o = options[i];
                if (o != null && o.Disabled
                    && (o.revalidateClickTarget == target || o.iconThing == target))
                {
                    ours.orderInPriority = o.orderInPriority;
                    options.RemoveAt(i);
                    break;
                }
            }
            options.Add(ours);
        }

        /// <summary>The player's blueprint, frame, or install blueprint at
        /// the cell, if any. Install blueprints get the carry-and-install
        /// branch; material blueprints and frames the carry-and-build one.</summary>
        private static Thing ConstructibleAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t.Faction == Faction.OfPlayer && t is IConstructible)
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>Nearest blueprint or frame on the map still needing the
        /// def - the automatic supply giver's direct-to-site leg resolves its
        /// delivery target with this at scan time. Distance measured from the
        /// stairwell exit the load will arrive at; capped scan.</summary>
        internal static Thing FindSiteNeeding(Map map, ThingDef def, IntVec3 near)
        {
            if (map == null || def == null)
            {
                return null;
            }
            Thing best = null;
            float bestDist = float.MaxValue;
            int examined = 0;
            ScanSites(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint),
                def, near, ref best, ref bestDist, ref examined);
            ScanSites(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame),
                def, near, ref best, ref bestDist, ref examined);
            return best;
        }

        private static void ScanSites(List<Thing> list, ThingDef def, IntVec3 near,
            ref Thing best, ref float bestDist, ref int examined)
        {
            for (int i = 0; i < list.Count && examined < 80; i++)
            {
                Thing t = list[i];
                if (t.Faction != Faction.OfPlayer || !t.Spawned || !(t is IConstructible ic))
                {
                    continue;
                }
                examined++;
                if (ic.ThingCountNeeded(def) <= 0)
                {
                    continue;
                }
                float d = (t.Position - near).LengthHorizontalSquared;
                if (d < bestDist)
                {
                    best = t;
                    bestDist = d;
                }
            }
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
                // Strict toward the clicked site: the option was only offered
                // when a reaching stairwell existed; if that changed since the
                // click, silently refuse rather than strand the cargo.
                if (!CrossLevelWork.TryResolveStairsStrict(pawn, targetMap, constructible.Position,
                    out Building_ABStairs stairs, out Building_ABStairs exit))
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, stack, stairs);
                job.targetC = exit;
                job.count = Mathf.Min(needed, Mathf.Min(stack.stackCount,
                    pawn.carryTracker.MaxStackSpaceEver(stack.def)));
                job.playerForced = true;
                ABPendingOrders.Set(pawn, targetMap, delegate { FinishOnSite(pawn, constructible, allowRetry: true); });
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
        /// <summary>One-shot retry queue (user report 2026-07-23: pawn climbed
        /// the stairs with the wood and just stood there): the arrival
        /// continuation runs INSIDE the transfer toil, and any transfer-time
        /// ordering quirk (reservations mid-cleanup, region updates from the
        /// fresh spawn) can null the giver's job resolution. A single delayed
        /// re-attempt a few ticks later, with the pawn fully settled, makes
        /// the chain seamless without polling.</summary>
        private static readonly List<(Pawn pawn, Thing target, int tick)> retries =
            new List<(Pawn pawn, Thing target, int tick)>();

        /// <summary>Called from ABGameComp; no-op unless a retry is queued.</summary>
        [ABGameTick(30)]
        internal static void Tick()
        {
            if (retries.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = retries.Count - 1; i >= 0; i--)
            {
                (Pawn pawn, Thing target, int tick) r = retries[i];
                if (now < r.tick)
                {
                    continue;
                }
                retries.RemoveAt(i);
                FinishOnSite(r.pawn, r.target, allowRetry: false);
            }
        }

        private static void ScheduleRetry(Pawn pawn, Thing target)
        {
            if (retries.Count > 32)
            {
                retries.Clear();
            }
            retries.Add((pawn, target, Find.TickManager.TicksGame + 15));
        }

        internal static void FinishOnSite(Pawn pawn, Thing constructible, bool allowRetry)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead
                    || constructible == null || constructible.Destroyed || !constructible.Spawned
                    || constructible.Map != pawn.Map)
                {
                    return;
                }
                Thing dropped = null;
                if (pawn.carryTracker?.CarriedThing != null)
                {
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out dropped);
                }
                // Re-resolve the giver by what is actually standing there now:
                // blueprints may have turned into frames mid-climb, and the
                // refuel branch shares this continuation.
                string giverName;
                if (constructible is IConstructible)
                {
                    giverName = constructible is Frame
                        ? "ConstructDeliverResourcesToFrames"
                        : "ConstructDeliverResourcesToBlueprints";
                }
                else if (constructible.TryGetComp<CompRefuelable>() != null)
                {
                    giverName = "Refuel";
                }
                else
                {
                    return;
                }
                WorkGiverDef giverDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail(giverName);
                WorkGiver_Scanner scanner = giverDef?.Worker as WorkGiver_Scanner;
                Job job = scanner?.JobOnThing(pawn, constructible, forced: true);
                // Refuel knows its fuel: the load the pawn just carried over.
                // If the giver's own resolution balks, feed it directly.
                if (job == null && giverName == "Refuel" && dropped != null && dropped.Spawned
                    && !dropped.Destroyed)
                {
                    CompRefuelable comp = constructible.TryGetComp<CompRefuelable>();
                    if (comp != null && comp.Fuel < comp.TargetFuelLevel
                        && comp.Props.fuelFilter.Allows(dropped.def))
                    {
                        job = JobMaker.MakeJob(JobDefOf.Refuel, constructible, dropped);
                    }
                }
                if (job != null)
                {
                    job.playerForced = true;
                    pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
                }
                else if (allowRetry)
                {
                    ABLog.Dev("Bring-and-" + giverName + " continuation found no job for "
                        + pawn.LabelShort + " at transfer time; retrying shortly.");
                    ScheduleRetry(pawn, constructible);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "construct supply arrival");
            }
        }
    }
}
