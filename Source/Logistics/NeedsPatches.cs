using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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

        internal static bool TryStairsJob(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            if (stairs?.Counterpart == null)
            {
                return false;
            }
            job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            return true;
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
                    if (nextMap != null && !nextMap.Disposed
                        && NeedsCross.TryStairsJob(pawn, nextMap, out Job job))
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
            if (target == null || target.Disposed)
            {
                return false;
            }
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            Building_ABStairs exit = stairs?.Counterpart;
            if (exit == null)
            {
                return false;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool found;
            try
            {
                found = RestUtility.FindBedFor(pawn) != null;
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
            }
            if (!found)
            {
                return false;
            }
            job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
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
                // Vanilla found something to eat (map or inventory): keep it.
                if (__result != null)
                {
                    return;
                }
                if (!NeedsCross.EligibleColonist(pawn) || pawn.needs?.food == null)
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
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

        private static bool TryFoodTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
            Building_ABStairs exit = stairs?.Counterpart;
            if (exit == null)
            {
                return false;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool found;
            try
            {
                found = FoodUtility.TryFindBestFoodSourceFor(pawn, pawn, desperate: false,
                    out Thing _, out ThingDef _, canRefillDispenser: true, canUseInventory: false);
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
            }
            if (!found)
            {
                return false;
            }
            job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            return true;
        }
    }
}
