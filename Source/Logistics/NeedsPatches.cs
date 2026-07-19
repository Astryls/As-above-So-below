using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A tired colonist whose owned bed sits anywhere in this column takes the
    /// stairs toward it instead of sleeping on the ground. Routing is one hop at
    /// a time: a sky pawn with a basement bed descends to the ground, where this
    /// giver fires again for the second hop (T7 #3). On arrival the vanilla rest
    /// job finds the bed normally. Hunger and recreation resolve through the
    /// idle-return drift; rest needs the redirect because vanilla happily sleeps
    /// on any floor.
    /// </summary>
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
                if (pawn == null || !pawn.Spawned || !pawn.IsColonistPlayerControlled
                    || pawn.Drafted || pawn.Downed || pawn.GetLord() != null)
                {
                    return;
                }
                Building_Bed bed = pawn.ownership?.OwnedBed;
                if (bed == null || !bed.Spawned || bed.Map == pawn.Map)
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null)
                {
                    return;
                }
                // Same column only; route one hop toward the bed's level.
                Map pawnGround = pawn.Map.GroundMap();
                if (pawnGround == null || pawnGround != bed.Map.GroundMap())
                {
                    return;
                }
                Map nextMap = bed.Map.Level() > comp.level ? comp.upperMap : comp.lowerMap;
                if (nextMap == null || nextMap.Disposed)
                {
                    return;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, nextMap, checkReachability: true);
                if (stairs?.Counterpart == null)
                {
                    return;
                }
                __result = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level rest");
            }
        }
    }
}
