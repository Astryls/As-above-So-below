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
    /// Non-Ideology gatherings across levels (parity audit P2): parties,
    /// marriage ceremonies, and speeches run on LordJob_VoluntarilyJoinable
    /// lords that only pawns on the SAME map ever consider joining
    /// (ThinkNode_JoinVoluntarilyJoinableLord scans pawn.Map.lordManager).
    /// Ideology rituals have their own attendance module; this covers the
    /// rest: when the join node finds nothing locally and a joinable
    /// gathering with positive priority for this pawn runs on a linked
    /// level, walk over - the vanilla node joins (or declines) properly on
    /// arrival. Rides the rituals toggle; fails open.
    /// </summary>
    [HarmonyPatch(typeof(ThinkNode_JoinVoluntarilyJoinableLord), nameof(ThinkNode_JoinVoluntarilyJoinableLord.TryIssueJobPackage))]
    internal static class Patch_JoinGathering_CrossLevel
    {
        private const int FailCooldownTicks = 600;

        private static readonly ABPawnCooldown cooldown = new ABPawnCooldown();

        private static void Postfix(Pawn pawn, ThinkNode __instance, ref ThinkResult __result)
        {
            if (__result.Job != null || !LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelRituals)
            {
                return;
            }
            try
            {
                if (!NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!cooldown.Ready(pawn, now))
                {
                    return;
                }
                cooldown.ChargeUntil(pawn, now + FailCooldownTicks);
                Job job = TryToward(pawn, comp.upperMap) ?? TryToward(pawn, comp.lowerMap);
                if (job != null)
                {
                    __result = new ThinkResult(job, __instance, JobTag.Misc);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level gathering join");
            }
        }

        private static Job TryToward(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            List<Lord> lords = target.lordManager.lords;
            for (int i = 0; i < lords.Count; i++)
            {
                LordJob lordJob = lords[i]?.LordJob;
                // Rituals have the dedicated attendance module; never
                // double-handle them here.
                if (lordJob is LordJob_Ritual
                    || !(lordJob is LordJob_VoluntarilyJoinable joinable))
                {
                    continue;
                }
                float priority;
                try
                {
                    priority = joinable.VoluntaryJoinPriorityFor(pawn);
                }
                catch
                {
                    continue;
                }
                if (priority <= 0f)
                {
                    continue;
                }
                if (CrossLevelWork.TryStairsJobToward(pawn, target, IntVec3.Invalid, out Job job))
                {
                    ABLog.Dev(pawn.LabelShort + " heads to a gathering on level " + target.Level() + ".");
                    return job;
                }
            }
            return null;
        }
    }
}
