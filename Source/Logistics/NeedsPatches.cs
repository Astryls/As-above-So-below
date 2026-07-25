using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Needs-driven level crossing (T7 #2/#3). Rest: a tired colonist routes one
    /// hop at a time toward their owned bed anywhere in the column (sky pawn
    /// with a basement bed descends to the ground, where the giver fires again),
    /// and a colonist with NO owned bed heads to a directly linked level that
    /// has a free bed instead of floor-sleeping. Food: a hungry colonist whose
    /// level has nothing edible takes the stairs when real food exists on a
    /// linked level (checked with vanilla FoodUtility under a virtual position
    /// swap, so preferences and reachability apply). On arrival the vanilla
    /// givers resolve the actual bed or meal normally. Recreation still resolves
    /// through the idle-return drift. Failed scans charge a short per-pawn
    /// cooldown so needy pawns with no options anywhere stay cheap.
    /// </summary>
    internal static class NeedsCross
    {
        private const int FailCooldownTicks = 300;

        internal static readonly Dictionary<int, int> RestNext = new Dictionary<int, int>();

        internal static readonly Dictionary<int, int> FoodNext = new Dictionary<int, int>();

        internal static readonly Dictionary<int, int> DrugNext = new Dictionary<int, int>();

        internal static readonly Dictionary<int, int> PatientBedNext = new Dictionary<int, int>();

        internal static readonly Dictionary<int, int> DeathrestNext = new Dictionary<int, int>();

        internal static bool EligibleColonist(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.IsColonistPlayerControlled
                && !pawn.Drafted && !pawn.Downed && pawn.GetLord() == null;
        }

        /// <summary>Conservative animal policy (T7 #6): only player pets that are
        /// not pen animals, are not area-restricted to their current map, and are
        /// not busy with a lord may cross levels for food. Pen animals never.</summary>
        internal static bool EligiblePetForFood(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.RaceProps != null
                && pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer
                && !pawn.Downed && pawn.GetLord() == null
                && !AnimalPenUtility.NeedsToBeManagedByRope(pawn)
                && pawn.playerSettings?.AreaRestrictionInPawnCurrentMap == null;
        }

        internal static bool OnCooldown(Dictionary<int, int> table, Pawn pawn)
        {
            return table.TryGetValue(pawn.thingIDNumber, out int next)
                && Find.TickManager.TicksGame < next;
        }

        internal static void Charge(Dictionary<int, int> table, Pawn pawn)
        {
            if (table.Count > 512)
            {
                table.Clear();
            }
            table[pawn.thingIDNumber] = Find.TickManager.TicksGame + FailCooldownTicks;
        }

    }

    [HarmonyPatch(typeof(JobGiver_GetRest), "TryGiveJob")]
    internal static class Patch_GetRest_CrossLevel
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                // Keep any real bed job the vanilla giver produced.
                if (__result != null && __result.targetA.HasThing)
                {
                    return;
                }
                if (!NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                Building_Bed bed = pawn.ownership?.OwnedBed;
                if (bed != null && bed.Spawned && bed.Map != pawn.Map)
                {
                    // Owned bed elsewhere in the column: one hop toward it.
                    Map pawnGround = pawn.Map.GroundMap();
                    if (pawnGround == null || pawnGround != bed.Map.GroundMap())
                    {
                        return;
                    }
                    Map nextMap = bed.Map.Level() > comp.level ? comp.upperMap : comp.lowerMap;
                    // Single hop: aim for the stairwell nearest the owned bed.
                    // Two hops: no meaningful cell on the intermediate level.
                    IntVec3 bedDest = bed.Map == nextMap ? bed.Position : IntVec3.Invalid;
                    if (CrossLevelWork.TryStairsJobToward(pawn, nextMap, bedDest, out Job job))
                    {
                        __result = job;
                    }
                    return;
                }
                if (bed != null)
                {
                    // Owned bed on this map but no bed job: vanilla's problem
                    // (occupied or unreachable), do not second-guess it.
                    return;
                }
                // No owned bed: sleep on a linked level with a free bed rather
                // than on the floor here.
                if (NeedsCross.OnCooldown(NeedsCross.RestNext, pawn))
                {
                    return;
                }
                if (TryFreeBedTowards(pawn, comp.upperMap, out Job j)
                    || TryFreeBedTowards(pawn, comp.lowerMap, out j))
                {
                    __result = j;
                    return;
                }
                NeedsCross.Charge(NeedsCross.RestNext, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level rest");
            }
        }

        private static bool TryFreeBedTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Building_Bed found = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                () => (found = RestUtility.FindBedFor(pawn)) != null))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(found, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }

    [HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
    internal static class Patch_GetFood_CrossLevel
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if ((!NeedsCross.EligibleColonist(pawn) && !NeedsCross.EligiblePetForFood(pawn))
                    || pawn.needs?.food == null)
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                if (__result != null)
                {
                    // Vanilla found LOCAL food. One-big-map parity (user report
                    // 2026-07-24: berries beat the upstairs fridge): when the
                    // local pick is sub-meal, ask whether a linked level's best
                    // food wins vanilla's own optimality contest with the real
                    // stairs travel folded into the distance term - exactly
                    // the comparison a single map would have run. Colonists
                    // only; pets keep the local-first rule.
                    if (NeedsCross.EligibleColonist(pawn))
                    {
                        TryUpgradeLocalPick(pawn, comp, ref __result);
                    }
                    return;
                }
                if (NeedsCross.OnCooldown(NeedsCross.FoodNext, pawn))
                {
                    return;
                }
                if (TryFoodTowards(pawn, comp.upperMap, out Job job)
                    || TryFoodTowards(pawn, comp.lowerMap, out job))
                {
                    if (pawn.RaceProps.Animal)
                    {
                        // Pets get walked home once fed and idle - without this
                        // record the meal trip was one-way and basements filled
                        // with pets one hunger cycle at a time (2026-07-24).
                        CrossLevelAnimals.NotePetFoodTrip(pawn, pawn.Map);
                    }
                    __result = job;
                    return;
                }
                NeedsCross.Charge(NeedsCross.FoodNext, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level food");
            }
        }

        /// <summary>Local food was found but it is sub-meal (raw berries,
        /// kibble, paste): compare vanilla FoodOptimality of the local pick at
        /// its real distance against each linked level's best source at
        /// (distance to stairs + climb + exit to food), and reroute when a
        /// linked level wins. Starving pawns always eat local (desperate mode
        /// has its own scoring); inventory food is never second-guessed.</summary>
        private static void TryUpgradeLocalPick(Pawn pawn, LevelComp comp, ref Job __result)
        {
            if (__result.def != JobDefOf.Ingest)
            {
                return;
            }
            Thing local = __result.targetA.Thing;
            if (local == null || !local.Spawned || local.Map != pawn.Map)
            {
                return; // inventory food or odd job shape: vanilla's business
            }
            if (pawn.needs.food.CurCategory == HungerCategory.Starving)
            {
                return;
            }
            ThingDef localDef = FoodUtility.GetFinalIngestibleDef(local);
            if (localDef?.ingestible == null
                || localDef.ingestible.preferability >= FoodPreferability.MealSimple)
            {
                return; // already a proper meal: no probe, no cost
            }
            if (NeedsCross.OnCooldown(NeedsCross.FoodNext, pawn))
            {
                return;
            }
            float localOpt = FoodUtility.FoodOptimality(pawn, local, localDef,
                (pawn.Position - local.Position).LengthHorizontal);
            Job better = TryBetterFoodTowards(pawn, comp.upperMap, localOpt)
                ?? TryBetterFoodTowards(pawn, comp.lowerMap, localOpt);
            if (better != null)
            {
                __result = better;
            }
            else
            {
                NeedsCross.Charge(NeedsCross.FoodNext, pawn);
            }
        }

        /// <summary>The stairs job toward <paramref name="target"/> when its
        /// best food source beats <paramref name="localOpt"/> at the full
        /// travel distance. Null otherwise.</summary>
        private static Job TryBetterFoodTowards(Pawn pawn, Map target, float localOpt)
        {
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return null;
            }
            Thing source = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                () => FoodUtility.TryFindBestFoodSourceFor(pawn, pawn, desperate: false,
                    out source, out ThingDef _, canRefillDispenser: true, canUseInventory: false)))
            {
                return null;
            }
            ThingDef farDef = FoodUtility.GetFinalIngestibleDef(source);
            if (farDef?.ingestible == null)
            {
                return null;
            }
            // Real travel: walk to the stairs, climb (ticks converted to
            // cells at the pawn's speed), then walk from the exit to the food.
            float ticksPerCell = Mathf.Max(1f, pawn.TicksPerMoveCardinal);
            float travel = (pawn.Position - stairs.Position).LengthHorizontal
                + stairs.ClimbTicksFor(pawn) / ticksPerCell
                + (exit.Position - source.Position).LengthHorizontal;
            float farOpt = FoodUtility.FoodOptimality(pawn, source, farDef, travel);
            if (farOpt <= localOpt + 1f)
            {
                return null; // the local snack legitimately wins, like one map
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(source, target), ref stairs, ref exit);
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }

        private static bool TryFoodTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Thing source = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                () => FoodUtility.TryFindBestFoodSourceFor(pawn, pawn, desperate: false,
                    out source, out ThingDef _, canRefillDispenser: true, canUseInventory: false)))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(source, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }

    /// <summary>Scheduled drugs across levels (parity P2 #10, 2026-07-25).
    /// JobGiver_TakeDrugsForDrugPolicy searches inventory, then the pawn's OWN
    /// map, then pack animals - so a colonist whose scheduled doses sit in a
    /// stockpile one level away silently skips them forever. When the vanilla
    /// giver comes up empty and a linked level holds a valid stack of a due
    /// policy drug (vanilla's own validator: unforbidden, reservable, socially
    /// proper), take the stairs; the giver re-fires on arrival and ingests
    /// normally.</summary>
    [HarmonyPatch(typeof(JobGiver_TakeDrugsForDrugPolicy), "TryGiveJob")]
    internal static class Patch_ScheduledDrugs_CrossLevel
    {
        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if (!NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                DrugPolicy policy = pawn.drugs?.CurrentPolicy;
                if (policy == null)
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                if (NeedsCross.OnCooldown(NeedsCross.DrugNext, pawn))
                {
                    return;
                }
                for (int i = 0; i < policy.Count; i++)
                {
                    ThingDef drug = policy[i].drug;
                    if (drug == null || !pawn.drugs.ShouldTryToTakeScheduledNow(drug))
                    {
                        continue;
                    }
                    if (TryDrugTowards(pawn, comp.upperMap, drug, out Job job)
                        || TryDrugTowards(pawn, comp.lowerMap, drug, out job))
                    {
                        __result = job;
                        return;
                    }
                }
                NeedsCross.Charge(NeedsCross.DrugNext, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level scheduled drugs");
            }
        }

        private static bool TryDrugTowards(Pawn pawn, Map target, ThingDef drug, out Job job)
        {
            job = null;
            if (target == null || target.Disposed
                || target.listerThings.ThingsOfDef(drug).Count == 0)
            {
                return false;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Thing found = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position, delegate
            {
                found = GenClosest.ClosestThingReachable(pawn.Position, target,
                    ThingRequest.ForDef(drug), PathEndMode.ClosestTouch,
                    TraverseParms.For(pawn), 9999f,
                    (Thing x) => x.def.IsDrug && !x.IsForbidden(pawn)
                        && pawn.CanReserve(x, 10, 1) && x.IsSociallyProper(pawn));
                return found != null;
            }))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(found, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }

    /// <summary>Patient self-bedding across levels (parity P2 #11,
    /// 2026-07-25). JobGiver_PatientGoToBed resolves through map-scoped
    /// RestUtility.FindBedFor, so an ambulatory sick colonist on a level with
    /// no free medical bed lies down on the floor instead of walking to the
    /// hospital one level away. The vanilla guard chain is re-run exactly
    /// (urgency, timetable-with-surgery/tend exception, disturbance) before
    /// probing linked levels with the same finder; downed pawns are excluded
    /// (crawling never crosses levels - the rescue bridge owns them).</summary>
    [HarmonyPatch(typeof(JobGiver_PatientGoToBed), "TryGiveJob")]
    internal static class Patch_PatientGoToBed_CrossLevel
    {
        private static void Postfix(JobGiver_PatientGoToBed __instance, Pawn pawn, ref Job __result)
        {
            if (__result != null || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if (!NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                if (__instance.urgentOnly && !HealthAIUtility.ShouldSeekMedicalRestUrgent(pawn))
                {
                    return;
                }
                if (!HealthAIUtility.ShouldSeekMedicalRest(pawn))
                {
                    return;
                }
                if (__instance.respectTimetable && RestUtility.TimetablePreventsLayDown(pawn)
                    && !HealthAIUtility.ShouldHaveSurgeryDoneNow(pawn)
                    && !HealthAIUtility.ShouldBeTendedNowByPlayer(pawn))
                {
                    return;
                }
                if (RestUtility.DisturbancePreventsLyingDown(pawn))
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                if (NeedsCross.OnCooldown(NeedsCross.PatientBedNext, pawn))
                {
                    return;
                }
                if (TryPatientBedTowards(pawn, comp.upperMap, out Job job)
                    || TryPatientBedTowards(pawn, comp.lowerMap, out job))
                {
                    __result = job;
                    return;
                }
                NeedsCross.Charge(NeedsCross.PatientBedNext, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level patient bedding");
            }
        }

        private static bool TryPatientBedTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Thing found = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                () => (found = RestUtility.FindBedFor(pawn, pawn, checkSocialProperness: false)) != null))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(found, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }

    /// <summary>Deathrest across levels (parity P2 #12, Biotech, 2026-07-25).
    /// JobGiver_GetDeathrest never returns null - with no bed on the pawn's
    /// OWN map it deathrests on the bare ground - so a sanguophage whose
    /// casket sits one level away collapsed at the stairs instead of walking
    /// to it. When the vanilla result is the ground-sleep fallback (a cell,
    /// not a bed), route toward the ASSIGNED deathrest casket anywhere in the
    /// column first (one hop at a time, like owned beds), else probe linked
    /// levels with vanilla's own finder; the giver re-fires on arrival.</summary>
    [HarmonyPatch(typeof(JobGiver_GetDeathrest), "TryGiveJob")]
    internal static class Patch_Deathrest_CrossLevel
    {
        private static void Postfix(JobGiver_GetDeathrest __instance, Pawn pawn, ref Job __result)
        {
            if (!ModsConfig.BiotechActive || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            if (__result == null || (__result.targetA.IsValid && __result.targetA.HasThing))
            {
                return; // found a real bed or casket locally: vanilla wins.
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if (!NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                if (pawn.needs == null || !pawn.needs.TryGetNeed(out Need_Deathrest need)
                    || need.CurLevelPercentage > __instance.maxNeedPercent)
                {
                    return;
                }
                if (pawn.InMentalState && !pawn.MentalState.AllowRestingInBed)
                {
                    return;
                }
                if (pawn.roping != null && pawn.roping.IsRoped)
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                // Assigned casket elsewhere in the column: one hop toward it.
                Building_Bed casket = pawn.ownership?.AssignedDeathrestCasket;
                if (casket != null && casket.Spawned && casket.Map != pawn.Map)
                {
                    Map pawnGround = pawn.Map.GroundMap();
                    if (pawnGround != null && pawnGround == casket.Map.GroundMap())
                    {
                        Map nextMap = casket.Map.Level() > comp.level ? comp.upperMap : comp.lowerMap;
                        IntVec3 dest = casket.Map == nextMap ? casket.Position : IntVec3.Invalid;
                        if (CrossLevelWork.TryStairsJobToward(pawn, nextMap, dest, out Job hop))
                        {
                            __result = hop;
                        }
                        return;
                    }
                }
                if (NeedsCross.OnCooldown(NeedsCross.DeathrestNext, pawn))
                {
                    return;
                }
                if (TryDeathrestBedTowards(pawn, comp.upperMap, out Job job)
                    || TryDeathrestBedTowards(pawn, comp.lowerMap, out job))
                {
                    __result = job;
                    return;
                }
                NeedsCross.Charge(NeedsCross.DeathrestNext, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level deathrest");
            }
        }

        private static bool TryDeathrestBedTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Thing found = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                () => (found = RestUtility.FindBedFor(pawn)) != null))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(found, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }
}
