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
    /// <summary>When the vanilla work scan finds nothing on the pawn's map, look for
    /// work on directly linked levels and send the pawn to the stairs.</summary>
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    internal static class Patch_JobGiverWork_CrossLevel
    {
        private static void Postfix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
        {
            if (CrossLevelWork.VirtualScanActive || __result.Job != null || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            if (__instance.emergency)
            {
                // The think tree runs an emergency JobGiver_Work (firefighting class
                // givers only) BEFORE the normal one. Migrating on it would scan an
                // almost empty work list and burn the per-pawn cooldown every cycle,
                // starving the real scanner. Only the normal instance migrates.
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
                ThinkResult? result = CrossLevelWork.TryMigrateForWork(__instance, pawn);
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
        private static Texture2D upIcon;
        private static Texture2D downIcon;

        private static Texture2D UpIcon =>
            upIcon ?? (upIcon = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsUp")?.uiIcon);

        private static Texture2D DownIcon =>
            downIcon ?? (downIcon = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown")?.uiIcon);

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
            if (!ABGuard.On(ABGuard.Movement) || !pawn.Spawned || !pawn.Drafted
                || !pawn.IsColonistPlayerControlled)
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
                icon = up ? UpIcon : DownIcon
            };
            if (stairs == null)
            {
                cmd.Disable("AB_NoStairs".Translate());
            }
            else
            {
                Building_ABStairs chosen = stairs;
                cmd.action = delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, chosen);
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.DraftedOrder);
                };
            }
            list.Add(cmd);
        }
    }
}
