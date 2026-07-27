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

        private const int MaxRemotePawnsPerMap = 15;

        static ABReverseCompat()
        {
            try
            {
                Active = ABCompat.Detect("brrainz.reversecommands", "Reverse Commands");
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
                ABSettings settings = ABMod.Settings;
                if (settings == null || !settings.crossLevelOrders)
                {
                    return;
                }
                // RC's reversed menu only lists current-map colonists; its own GetOptions
                // calls for those are already made cross-level aware by our GetOptions
                // prefix. Here we add colonists on the OTHER levels of the column, routed
                // to whichever level the click is aimed at (up, down, or acting directly).
                Vector3 clickPos = UI.MouseMapPosition();
                Map targetMap = CrossLevelOrders.ResolveTargetMap(cur, clickPos, out _);
                string goHere = "GoHere".Translate();
                AddColumnColonists(__result, cur, comp.upperMap, clickPos, targetMap, goHere);
                AddColumnColonists(__result, cur, comp.lowerMap, clickPos, targetMap, goHere);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "reverse commands cross level");
            }
        }

        /// <summary>Adds colonists on a linked level to Reverse Commands' reversed menu,
        /// each with options routed to whichever level the click targets - via
        /// CrossLevelOrders.BuildOptions, which acts directly when the colonist is already
        /// on the target level and otherwise routes through the stairs (and replays the
        /// order on arrival). RC groups by label and opens a per-pawn float sub-menu, so
        /// these just slot into the same dictionary.</summary>
        private static void AddColumnColonists(Dictionary<string, Dictionary<Pawn, FloatMenuOption>> result,
            Map cur, Map other, Vector3 clickPos, Map targetMap, string goHere)
        {
            if (other == null || other.Disposed)
            {
                return;
            }
            List<Pawn> colonists = other.mapPawns.FreeColonists;
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
                List<FloatMenuOption> options = CrossLevelOrders.BuildOptions(pawn, clickPos, cur, targetMap, out _);
                if (options == null || options.Count == 0)
                {
                    continue;
                }
                // RC's sub-menu sorts pawns by cached path; seed the cache so a colonist
                // on another map cannot NRE it. Reflection: no typeref to their assembly.
                pathInfoAddInfo?.Invoke(null, new object[] { pawn, clickPos.ToIntVec3() });
                for (int j = 0; j < options.Count; j++)
                {
                    FloatMenuOption option = options[j];
                    if (option == null || option.Disabled || option.Label == goHere)
                    {
                        continue;
                    }
                    if (!result.TryGetValue(option.Label, out Dictionary<Pawn, FloatMenuOption> perPawn))
                    {
                        perPawn = new Dictionary<Pawn, FloatMenuOption>();
                        result[option.Label] = perPawn;
                    }
                    perPawn[pawn] = option;
                }
            }
        }
    }
}
