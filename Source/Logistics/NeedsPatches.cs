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
}
