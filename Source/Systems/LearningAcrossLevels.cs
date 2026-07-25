using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Children's schooling across levels (parity P3 #14, Biotech,
    /// 2026-07-25). Lessontaking resolves through SchoolUtility.FindTeacher +
    /// ClosestSchoolDesk, both scoped to the child's OWN map - a classroom
    /// one level away simply does not exist for JobGiver_Learn, so kids fill
    /// their learning need with floor-play while the school desks idle.
    ///
    /// House needs-bridge shape: when the vanilla giver returns nothing, the
    /// child still wants learning, and lessontaking is among its active
    /// desires, probe linked levels with the lessontaking worker's OWN CanDo
    /// (teacher present, desk reachable, spot reservable - all evaluated
    /// under a virtual position swap); on a hit, take the stairs and let the
    /// giver re-fire on arrival. Other learning desires (play, skydreaming,
    /// nature running) are ambient and stay local by design.
    /// Kill switch: logistics; setting: crossLevelNeeds.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Learn), "TryGiveJob")]
    internal static class Patch_Learn_CrossLevel
    {
        private static readonly Dictionary<int, int> cooldown = new Dictionary<int, int>();

        private static LearningDesireDef lessontaking;

        private static bool resolved;

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !ModsConfig.BiotechActive || !ABGuard.On(ABGuard.Logistics))
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
                if (!NeedsCross.EligibleColonist(pawn) || pawn.learning == null
                    || pawn.needs?.learning == null
                    || pawn.needs.learning.CurLevelPercentage >= 0.95f)
                {
                    return;
                }
                if (!resolved)
                {
                    resolved = true;
                    lessontaking = DefDatabase<LearningDesireDef>.GetNamedSilentFail("Lessontaking");
                }
                if (lessontaking?.Worker == null
                    || !pawn.learning.ActiveLearningDesires.Contains(lessontaking))
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                if (NeedsCross.OnCooldown(cooldown, pawn))
                {
                    return;
                }
                if (TrySchoolTowards(pawn, comp.upperMap, out Job job)
                    || TrySchoolTowards(pawn, comp.lowerMap, out job))
                {
                    __result = job;
                    return;
                }
                NeedsCross.Charge(cooldown, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level lessontaking");
            }
        }

        private static bool TrySchoolTowards(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            Thing desk = null;
            if (!ABVirtualPosition.WithPawnAt(pawn, target, exit.Position, delegate
            {
                if (!lessontaking.Worker.CanDo(pawn))
                {
                    return false;
                }
                Pawn teacher = SchoolUtility.FindTeacher(pawn);
                desk = teacher == null ? null : SchoolUtility.ClosestSchoolDesk(pawn, teacher);
                return desk != null;
            }))
            {
                return false;
            }
            StairRouter.Reroute(pawn, target, StairRouter.DestHint(desk, target), ref stairs, ref exit);
            job = CrossLevelWork.MakeStairsJob(stairs, exit);
            return true;
        }
    }
}
