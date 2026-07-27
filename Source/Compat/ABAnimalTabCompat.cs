using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Fluffy's Animal Tab (Fluffy.AnimalTab) soft compat. That mod replaces the
    /// Animals window with its own MainTabWindow_Animals whose pawn recache reads
    /// Find.CurrentMap only; our patch on the VANILLA Animals window never fires
    /// for it, so other levels' animals would silently vanish from the tab.
    /// Postfix its RecachePawns (resolved by name, zero typerefs, inert when the
    /// mod is absent) and append the column's other levels, running the extras
    /// through its own filter workers so the filter toggle stays truthful.
    /// Fails open: on any error the compat marks itself broken and the tab shows
    /// exactly what it would without us.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABAnimalTabCompat
    {
        private static readonly AccessTools.FieldRef<object, IEnumerable<Pawn>> AllPawnsRef;
        private static readonly AccessTools.FieldRef<object, IEnumerable<Pawn>> FilteredPawnsRef;
        private static readonly PropertyInfo FiltersProp;
        private static readonly MethodInfo AllowsMethod;
        private static bool broken;

        static ABAnimalTabCompat()
        {
            try
            {
                if (!ABCompat.Detect("Fluffy.AnimalTab", "Animal Tab"))
                {
                    return;
                }
                Type window = AccessTools.TypeByName("AnimalTab.MainTabWindow_Animals");
                MethodInfo recache = window != null ? AccessTools.Method(window, "RecachePawns") : null;
                FieldInfo all = window != null ? AccessTools.Field(window, "_allPawns") : null;
                FieldInfo filtered = window != null ? AccessTools.Field(window, "_filteredPawns") : null;
                if (recache == null || all == null || filtered == null)
                {
                    Log.Warning(ABLog.Tag + " Animal Tab detected but its window internals were not found; other levels' animals will not appear in its tab.");
                    return;
                }
                AllPawnsRef = AccessTools.FieldRefAccess<IEnumerable<Pawn>>(window, "_allPawns");
                FilteredPawnsRef = AccessTools.FieldRefAccess<IEnumerable<Pawn>>(window, "_filteredPawns");
                FiltersProp = AccessTools.Property(window, "Filters");
                Type worker = AccessTools.TypeByName("AnimalTab.FilterWorker");
                AllowsMethod = worker != null ? AccessTools.Method(worker, "Allows") : null;
                HarmonyBoot.Harmony.Patch(recache,
                    postfix: new HarmonyMethod(typeof(ABAnimalTabCompat), nameof(RecachePostfix)));
                ABLog.Dev("Animal Tab detected, cross level animals enabled in its tab.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Animal Tab compat setup failed: " + e.Message);
            }
        }

        private static void RecachePostfix(object __instance)
        {
            if (broken || !ABGuard.On(ABGuard.Ui))
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
                List<Pawn> extra = new List<Pawn>();
                // Level keys capped to {1,0,-1}; walk high->low, no LINQ alloc.
                for (int lvl = 1; lvl >= -1; lvl--)
                {
                    if (!controller.MapByLevel.TryGetValue(lvl, out Map m) || m == null || m == cur || m.Disposed)
                    {
                        continue;
                    }
                    List<Pawn> faction = m.mapPawns.PawnsInFaction(Faction.OfPlayer);
                    for (int i = 0; i < faction.Count; i++)
                    {
                        if (faction[i].RaceProps.Animal)
                        {
                            extra.Add(faction[i]);
                        }
                    }
                }
                if (extra.Count == 0)
                {
                    return;
                }
                IEnumerable<Pawn> all = AllPawnsRef(__instance);
                if (all != null)
                {
                    AllPawnsRef(__instance) = all.Concat(extra);
                }
                IEnumerable<Pawn> filtered = FilteredPawnsRef(__instance);
                if (filtered != null)
                {
                    FilteredPawnsRef(__instance) = filtered.Concat(extra.Where(PassesFilters));
                }
            }
            catch (Exception e)
            {
                broken = true;
                Log.Warning(ABLog.Tag + " Animal Tab cross level append failed and shut itself down: " + e.Message);
            }
        }

        /// <summary>Runs their filter workers on an appended pawn. Fails open per
        /// pawn: an unreadable filter shows the animal instead of hiding it.</summary>
        private static bool PassesFilters(Pawn p)
        {
            try
            {
                if (FiltersProp == null || AllowsMethod == null)
                {
                    return true;
                }
                IEnumerable filters = FiltersProp.GetValue(null) as IEnumerable;
                if (filters == null)
                {
                    return true;
                }
                object[] args = { p };
                foreach (object f in filters)
                {
                    if (f != null && AllowsMethod.Invoke(f, args) is bool allowed && !allowed)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
