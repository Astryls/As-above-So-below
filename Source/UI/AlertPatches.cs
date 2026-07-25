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

    // ------------------------------------------------------------------
    // Parity scaffold P0 #3 (2026-07-25): building-existence alerts read
    // ONE map's listers, so a colony whose stove / rec room / defenses live
    // on another level of the column nags forever on the ground map. Levels
    // themselves never report (they read non-home inside AlertsReadout via
    // ColumnAsHome); these postfixes fix the GROUND map's verdict by
    // checking the rest of its column before letting the alert fire.
    // Audited as already column-correct, NO patch needed: research bench and
    // fire-in-home-area (both iterate all maps; levels carry their own Home
    // area), and the bill pawn-restriction dropdown (AllMaps_FreeColonists).
    // ------------------------------------------------------------------

    [HarmonyPatch(typeof(Alert_NeedMealSource), "NeedMealSource")]
    internal static class Patch_Alert_NeedMealSource_Column
    {
        private static void Postfix(Map map, ref bool __result)
        {
            if (!__result || !ABGuard.On(ABGuard.Ui) || map == null)
            {
                return;
            }
            try
            {
                LevelComp controller = map.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed || m == map)
                    {
                        continue;
                    }
                    List<Building> buildings = m.listerBuildings.allBuildingsColonist;
                    for (int i = 0; i < buildings.Count; i++)
                    {
                        if (buildings[i].def.building != null && buildings[i].def.building.isMealSource)
                        {
                            // The column cooks one level away.
                            __result = false;
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "column meal-source alert");
            }
        }
    }

    [HarmonyPatch(typeof(Alert_NeedDefenses), "NeedDefenses")]
    internal static class Patch_Alert_NeedDefenses_Column
    {
        private static void Postfix(Map map, ref bool __result)
        {
            if (!__result || !ABGuard.On(ABGuard.Ui) || map == null)
            {
                return;
            }
            try
            {
                LevelComp controller = map.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed || m == map)
                    {
                        continue;
                    }
                    List<Building> buildings = m.listerBuildings.allBuildingsColonist;
                    for (int i = 0; i < buildings.Count; i++)
                    {
                        Building b = buildings[i];
                        if ((b.def.building != null && (b.def.building.IsTurret || b.def.building.isTrap))
                            || b.def == ThingDefOf.Sandbags || b.def == ThingDefOf.Barricade)
                        {
                            // Rooftop turrets and basement traps count for the column.
                            __result = false;
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "column defenses alert");
            }
        }
    }

    [HarmonyPatch(typeof(Alert_NeedJoySources), "NeedJoySource")]
    internal static class Patch_Alert_NeedJoySources_Column
    {
        private static readonly HashSet<JoyKindDef> tmpKinds = new HashSet<JoyKindDef>();

        private static void Postfix(Map map, ref bool __result)
        {
            if (!__result || !ABGuard.On(ABGuard.Ui) || map == null)
            {
                return;
            }
            try
            {
                LevelComp controller = map.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                // TRUE union of recreation kinds across the column (a rec room
                // split over two levels satisfies expectations exactly like one
                // big room would), measured against the ground map's expectation.
                tmpKinds.Clear();
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    List<JoyKindDef> kinds = JoyUtility.JoyKindsOnMapTempList(m);
                    for (int i = 0; i < kinds.Count; i++)
                    {
                        tmpKinds.Add(kinds[i]);
                    }
                    kinds.Clear();
                }
                if (tmpKinds.Count >= ExpectationsUtility.CurrentExpectationFor(map).joyKindsNeeded)
                {
                    __result = false;
                }
                tmpKinds.Clear();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "column joy-sources alert");
            }
        }
    }
}
