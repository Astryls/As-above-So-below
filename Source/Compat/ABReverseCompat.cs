using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Reverse Commands (brrainz.reversecommands) soft compat: their reversed
    /// float menu lists only current-map colonists. This adds colonists from
    /// directly linked levels: each candidate is virtually placed at their
    /// stairwell's exit on the viewed map, the vanilla float options are
    /// gathered exactly like Reverse Commands does for local pawns, and every
    /// option's action is wrapped to route the pawn through the stairs first;
    /// the original order replays automatically after the transfer via
    /// ABPendingOrders (job queues do not survive the despawn).
    ///
    /// Zero compile-time references to their assembly: the patch target and the
    /// PathInfo seeding (their pick menu sorts by cached path and would NRE on
    /// unseeded pawns) both resolve by reflection, so no typeref exists for the
    /// debug-menu scan to trip over. Everything is inert when the mod is absent.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABReverseCompat
    {
        public static readonly bool Active;

        private static readonly MethodInfo pathInfoAddInfo;

        private static readonly AccessTools.FieldRef<Selector, List<object>> SelectedRef =
            AccessTools.FieldRefAccess<Selector, List<object>>("selected");

        private const int MaxRemotePawnsPerMap = 15;

        static ABReverseCompat()
        {
            try
            {
                Active = ModsConfig.IsActive("brrainz.reversecommands");
                if (!Active)
                {
                    return;
                }
                Type tools = AccessTools.TypeByName("ReverseCommands.Tools");
                MethodInfo target = tools != null ? AccessTools.Method(tools, "GetPawnActions") : null;
                Type pathInfo = AccessTools.TypeByName("ReverseCommands.PathInfo");
                pathInfoAddInfo = pathInfo != null ? AccessTools.Method(pathInfo, "AddInfo") : null;
                if (target == null)
                {
                    Log.Warning(ABLog.Tag + " Reverse Commands detected but its Tools.GetPawnActions was not found; cross level support disabled.");
                    Active = false;
                    return;
                }
                HarmonyBoot.Harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(ABReverseCompat), nameof(GetPawnActionsPostfix)));
                ABLog.Dev("Reverse Commands detected, cross level pawn actions enabled.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Reverse Commands compat setup failed: " + e.Message);
                Active = false;
            }
        }

        /// <summary>Signature uses vanilla types only. Appends linked-level
        /// colonists' options into Reverse Commands' label dictionary.</summary>
        private static void GetPawnActionsPostfix(Dictionary<string, Dictionary<Pawn, FloatMenuOption>> __result)
        {
            if (!ABGuard.On(ABGuard.Logistics) || __result == null)
            {
                return;
            }
            try
            {
                Map cur = Find.CurrentMap;
                LevelComp comp = cur?.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                // Same early-out as Reverse Commands: drafted selection means no menu.
                Vector3 clickPos = UI.MouseMapPosition();
                string goHere = "GoHere".Translate();
                AddRemotePawns(__result, cur, comp.upperMap, clickPos, goHere);
                AddRemotePawns(__result, cur, comp.lowerMap, clickPos, goHere);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "reverse commands cross level");
            }
        }

        private static void AddRemotePawns(Dictionary<string, Dictionary<Pawn, FloatMenuOption>> result,
            Map cur, Map remote, Vector3 clickPos, string goHere)
        {
            if (remote == null || remote.Disposed)
            {
                return;
            }
            List<Pawn> colonists = remote.mapPawns.FreeColonists;
            int examined = 0;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn pawn = colonists[i];
                if (!pawn.IsColonistPlayerControlled || pawn.Dead || !pawn.Spawned
                    || pawn.Downed || pawn.Drafted || pawn.GetLord() != null)
                {
                    continue;
                }
                if (++examined > MaxRemotePawnsPerMap)
                {
                    return;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(pawn, cur, checkReachability: true);
                Building_ABStairs exit = stairs?.CounterpartTowards(cur);
                if (exit == null)
                {
                    continue;
                }
                List<FloatMenuOption> options = GatherOptionsVirtually(pawn, cur, exit.Position, clickPos);
                if (options == null || options.Count == 0)
                {
                    continue;
                }
                // Their pick menu sorts by cached path; seed the cache so remote
                // pawns cannot NRE it. Reflection: no typeref to their assembly.
                pathInfoAddInfo?.Invoke(null, new object[] { pawn, clickPos.ToIntVec3() });
                Building_ABStairs entry = stairs;
                for (int j = 0; j < options.Count; j++)
                {
                    FloatMenuOption option = options[j];
                    if (option == null || option.Label == goHere)
                    {
                        continue;
                    }
                    Action original = option.action;
                    if (original == null)
                    {
                        continue;
                    }
                    Pawn p = pawn;
                    option.action = delegate
                    {
                        RouteThenRun(p, cur, entry, original);
                    };
                    if (!result.TryGetValue(option.Label, out Dictionary<Pawn, FloatMenuOption> perPawn))
                    {
                        perPawn = new Dictionary<Pawn, FloatMenuOption>();
                        result[option.Label] = perPawn;
                    }
                    perPawn[pawn] = option;
                }
            }
        }

        /// <summary>Vanilla float options for the pawn as if they stood at the
        /// stairwell exit on the viewed map: reachability and validity evaluate
        /// where the pawn will actually arrive. Mirrors Reverse Commands' own
        /// selector juggling so providers behave identically.</summary>
        private static List<FloatMenuOption> GatherOptionsVirtually(Pawn pawn, Map cur, IntVec3 exitCell, Vector3 clickPos)
        {
            if (!ABVirtualPosition.TrySwap(pawn, cur, exitCell, out ABVirtualPosition.Token token))
            {
                return null;
            }
            Selector selector = Find.Selector;
            List<object> savedSelection = SelectedRef(selector);
            SelectedRef(selector) = new List<object> { pawn };
            try
            {
                return FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, clickPos, out FloatMenuContext _);
            }
            finally
            {
                SelectedRef(selector) = savedSelection;
                ABVirtualPosition.Restore(pawn, token);
            }
        }

        private static void RouteThenRun(Pawn pawn, Map targetMap, Building_ABStairs stairs, Action original)
        {
            try
            {
                if (pawn == null || original == null || !pawn.Spawned || pawn.Dead)
                {
                    return;
                }
                if (pawn.Map == targetMap)
                {
                    // Already there (edge: they migrated meanwhile).
                    original();
                    return;
                }
                if (stairs == null || !stairs.Spawned)
                {
                    return;
                }
                Building_ABStairs exit = stairs.CounterpartTowards(targetMap);
                if (exit == null)
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
                job.targetC = exit;
                ABPendingOrders.Set(pawn, targetMap, original);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "reverse commands routing");
            }
        }
    }
}
