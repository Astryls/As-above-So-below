using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Work, Schedule, and every other pawn table that chains the base getter now
    /// list colonists from every level in the current map's column, so the colony
    /// reads as one base regardless of which level is being viewed. Assign already
    /// spans maps in vanilla via PawnsFinder. Rows for other levels are appended
    /// after the current map's pawns, upper level first.
    /// </summary>
    [HarmonyPatch(typeof(MainTabWindow_PawnTable), "Pawns", MethodType.Getter)]
    internal static class Patch_PawnTables_AllLevels
    {
        private static void Postfix(MainTabWindow_PawnTable __instance, ref IEnumerable<Pawn> __result)
        {
            if (!LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                Map cur = Find.CurrentMap;
                LevelComp controller = cur?.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                // Ghouls appear only where vanilla shows them: the Schedule tab
                // concats its own map's controllable subhumans, so only there do
                // we append the other levels' (T7 #5). Appending them in every
                // base-chained table would ghost them into the Work tab.
                bool includeSubhumans = __instance is MainTabWindow_Schedule;
                List<Pawn> list = new List<Pawn>(__result);
                // MapByLevel keys are capped to {1 sky, 0 ground, -1 basement};
                // walk them high->low without a LINQ OrderByDescending allocation
                // (this postfix runs on every pawn-table rebuild).
                for (int lvl = 1; lvl >= -1; lvl--)
                {
                    if (!controller.MapByLevel.TryGetValue(lvl, out Map m) || m == null || m == cur || m.Disposed)
                    {
                        continue;
                    }
                    // Maps are disjoint, so no deduplication is needed.
                    list.AddRange(m.mapPawns.FreeColonists);
                    if (includeSubhumans)
                    {
                        list.AddRange(m.mapPawns.SpawnedColonySubhumansPlayerControlled);
                    }
                }
                __result = list;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "pawn table augmentation");
            }
        }
    }

    /// <summary>The Animals tab overrides Pawns without chaining the base getter,
    /// so the colony-wide augmentation above never reaches it. Append the other
    /// levels' colony animals here so pets on the sky level stay manageable
    /// (T7 #6).</summary>
    [HarmonyPatch(typeof(MainTabWindow_Animals), "Pawns", MethodType.Getter)]
    internal static class Patch_AnimalsTable_AllLevels
    {
        private static void Postfix(ref IEnumerable<Pawn> __result)
        {
            if (!LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                Map cur = Find.CurrentMap;
                LevelComp controller = cur?.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                List<Pawn> list = new List<Pawn>(__result);
                // Level keys capped to {1,0,-1}; walk high->low, no LINQ alloc.
                for (int lvl = 1; lvl >= -1; lvl--)
                {
                    if (!controller.MapByLevel.TryGetValue(lvl, out Map m) || m == null || m == cur || m.Disposed)
                    {
                        continue;
                    }
                    list.AddRange(m.mapPawns.ColonyAnimals);
                }
                __result = list;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "animals table augmentation");
            }
        }
    }
}
