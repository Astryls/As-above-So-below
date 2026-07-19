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
        private static void Postfix(ref IEnumerable<Pawn> __result)
        {
            if (!ABGuard.On(ABGuard.Ui))
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
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel.OrderByDescending(k => k.Key))
                {
                    Map m = kvp.Value;
                    if (m == null || m == cur || m.Disposed)
                    {
                        continue;
                    }
                    // Maps are disjoint, so no deduplication is needed.
                    list.AddRange(m.mapPawns.FreeColonists);
                }
                __result = list;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "pawn table augmentation");
            }
        }
    }
}
