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
    /// <summary>Cross-level work, two tiers. Tier 1 (idle): the vanilla scan
    /// found nothing on the pawn's map, so look for any work on linked levels.
    /// Tier 2 (priority-aware): the scan DID find a job, but only at a rank
    /// the pawn considers low - probe linked levels for strictly better-ranked
    /// work (a warden set to priority 1 leaves priority-2 mining to go convert
    /// the prisoner in the basement).</summary>
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    internal static class Patch_JobGiverWork_CrossLevel
    {
        private static void Postfix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
        {
            if (!LevelCensus.AnyLevelColumns || CrossLevelWork.VirtualScanActive
                || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelWork)
            {
                return;
            }
            if (pawn == null || !pawn.Spawned || pawn.Map == null
                || !pawn.IsColonistPlayerControlled || pawn.Drafted || pawn.Downed)
            {
                return;
            }
            if (pawn.GetLord() != null)
            {
                // Never pull pawns out of gatherings, caravans, or rituals.
                return;
            }
            try
            {
                ThinkResult? result;
                if (__result.Job != null)
                {
                    // A local job exists. Emergency results (firefight, rescue,
                    // urgent tend) always stand; otherwise ask whether a linked
                    // level holds strictly better-ranked work.
                    if (__instance.emergency || !settings.priorityCrossLevelWork)
                    {
                        return;
                    }
                    result = CrossLevelWork.TryMigrateForBetterWork(__instance, pawn, __result);
                }
                else
                {
                    // The think tree runs an emergency JobGiver_Work (rescue, tend,
                    // firefight - vanilla caches those givers into a list the normal
                    // pass never scans) BEFORE the normal one. The emergency instance
                    // migrates through its own pre-checked, separately-cooled path so
                    // doctors cross levels for downed pawns without empty emergency
                    // scans starving the real work scanner.
                    result = __instance.emergency
                        ? CrossLevelWork.TryMigrateForEmergencyWork(__instance, pawn)
                        : CrossLevelWork.TryMigrateForWork(__instance, pawn);
                }
                if (result.HasValue)
                {
                    __result = result.Value;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level work");
            }
        }
    }

    /// <summary>Drafted colonists get go up / go down gizmos that send them through
    /// the nearest linked stairwell.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_Pawn_LevelGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result)
            {
                yield return g;
            }
            List<Gizmo> extras = null;
            try
            {
                extras = Build(__instance);
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Pawn level gizmos failed: " + e, 762195845);
            }
            if (extras == null)
            {
                yield break;
            }
            for (int i = 0; i < extras.Count; i++)
            {
                yield return extras[i];
            }
        }

        private static List<Gizmo> Build(Pawn pawn)
        {
            if (!LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Movement) || !pawn.Spawned)
            {
                return null;
            }
            bool draftedColonist = pawn.Drafted && pawn.IsColonistPlayerControlled;
            // Obedient pets get the same send up / send down orders (T7 #6).
            bool obedientPet = !draftedColonist
                && pawn.RaceProps.Animal && pawn.Faction == Faction.OfPlayer && !pawn.Downed
                && pawn.training != null && pawn.training.HasLearned(TrainableDefOf.Obedience)
                && !AnimalPenUtility.NeedsToBeManagedByRope(pawn);
            if (!draftedColonist && !obedientPet)
            {
                return null;
            }
            LevelComp comp = pawn.Map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            List<Gizmo> list = new List<Gizmo>();
            AddOption(list, pawn, comp.upperMap, up: true);
            AddOption(list, pawn, comp.lowerMap, up: false);
            return list;
        }

        private static void AddOption(List<Gizmo> list, Pawn pawn, Map target, bool up)
        {
            if (target == null || target.Disposed)
            {
                return;
            }
            // No reachability check here: this runs per GUI pass. Pathing resolves
            // when the job starts and fails with the vanilla message if blocked.
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: false);
            Command_Action cmd = new Command_Action
            {
                defaultLabel = (up ? "AB_GoUp" : "AB_GoDown").Translate(),
                defaultDesc = (up ? "AB_GoUpDesc" : "AB_GoDownDesc").Translate(),
                icon = up ? ABIcons.UpStairs : ABIcons.DownStairs
            };
            if (stairs == null)
            {
                cmd.Disable("AB_NoStairs".Translate());
            }
            else
            {
                Building_ABStairs chosen = stairs;
                Map chosenTarget = target;
                cmd.action = delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, chosen);
                    job.targetC = chosen.CounterpartTowards(chosenTarget);
                    if (pawn.Drafted)
                    {
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                    }
                    else
                    {
                        // Animals have no ordered-job UI semantics; force it.
                        pawn.jobs.StartJob(job, JobCondition.InterruptForced);
                    }
                };
            }
            list.Add(cmd);
        }
    }
}
