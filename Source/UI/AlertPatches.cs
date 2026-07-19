using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Alerts audit (T10 #7). The one vanilla alert that reads wrong with
    /// stacked levels is "Need colonist beds": it counts colonists versus beds
    /// PER MAP, so bedrooms built on the sky level leave the surface map
    /// reporting a deficit forever (and pocket-level colonists are never
    /// counted at all). Aggregate the column: pocket levels report false, and
    /// the ground map sums the vanilla per-map bed arithmetic across its
    /// column. Cross-level couples are approximated as two singles by the
    /// per-map pairing, which slightly overstates need; acceptable and safe.
    /// Other stock alerts already iterate all maps or read colony-wide state.
    /// </summary>
    [HarmonyPatch(typeof(Alert_NeedColonistBeds), "NeedColonistBeds")]
    internal static class Patch_Alert_NeedColonistBeds_Column
    {
        private static void Postfix(Map map, ref bool __result)
        {
            if (!ABGuard.On(ABGuard.Ui) || map == null)
            {
                return;
            }
            try
            {
                LevelComp comp = map.Levels();
                if (comp == null)
                {
                    return;
                }
                if (comp.level != 0)
                {
                    // Counted by the column's ground map.
                    __result = false;
                    return;
                }
                LevelComp controller = map.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                int singles = 0;
                int doubles = 0;
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    Alert_NeedColonistBeds.AvailableColonistBeds(m, includeBabies: false,
                        out int s, out int d, out int _);
                    singles += s;
                    doubles += d;
                }
                __result = singles < 0 || doubles < 0;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "colonist beds alert aggregation");
            }
        }
    }
}
