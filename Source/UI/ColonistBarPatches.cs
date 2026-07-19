using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Colonist bar level ordering (T10 #6). Vanilla already groups the bar per
    /// map with separators, but orders the groups by map creation id, so the
    /// basement can appear before the sky depending on build order. Reorder the
    /// groups so each column reads top to bottom (sky, surface, basement) and
    /// columns cluster together; caravans stay at the end untouched. Fails open
    /// to vanilla ordering.
    /// </summary>
    [HarmonyPatch(typeof(ColonistBar), "CheckRecacheEntries")]
    internal static class Patch_ColonistBar_LevelOrder
    {
        private static readonly AccessTools.FieldRef<ColonistBar, List<ColonistBar.Entry>> EntriesRef =
            AccessTools.FieldRefAccess<ColonistBar, List<ColonistBar.Entry>>("cachedEntries");

        private static readonly AccessTools.FieldRef<ColonistBar, List<Vector2>> DrawLocsRef =
            AccessTools.FieldRefAccess<ColonistBar, List<Vector2>>("cachedDrawLocs");

        private static readonly AccessTools.FieldRef<ColonistBar, float> ScaleRef =
            AccessTools.FieldRefAccess<ColonistBar, float>("cachedScale");

        private static readonly AccessTools.FieldRef<ColonistBar, ColonistBarDrawLocsFinder> FinderRef =
            AccessTools.FieldRefAccess<ColonistBar, ColonistBarDrawLocsFinder>("drawLocsFinder");

        private static readonly AccessTools.FieldRef<ColonistBar, ColonistBarColonistDrawer> DrawerRef =
            AccessTools.FieldRefAccess<ColonistBar, ColonistBarColonistDrawer>("drawer");

        private static void Postfix(ColonistBar __instance)
        {
            if (!ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                List<ColonistBar.Entry> entries = EntriesRef(__instance);
                if (entries == null || entries.Count == 0)
                {
                    return;
                }
                // Fast out: no multi-level column anywhere.
                bool anyColumn = false;
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    LevelComp c = maps[i].Levels();
                    if (c != null && c.level == 0 && c.MapByLevel.Count > 1)
                    {
                        anyColumn = true;
                        break;
                    }
                }
                if (!anyColumn)
                {
                    return;
                }
                // Collect groups in original order.
                List<int> groupOrder = new List<int>();
                Dictionary<int, List<ColonistBar.Entry>> byGroup = new Dictionary<int, List<ColonistBar.Entry>>();
                Dictionary<int, Map> groupMap = new Dictionary<int, Map>();
                for (int i = 0; i < entries.Count; i++)
                {
                    ColonistBar.Entry e = entries[i];
                    if (!byGroup.TryGetValue(e.group, out List<ColonistBar.Entry> list))
                    {
                        list = new List<ColonistBar.Entry>();
                        byGroup[e.group] = list;
                        groupOrder.Add(e.group);
                        groupMap[e.group] = e.map;
                    }
                    list.Add(e);
                }
                // Sort keys: caravans (map null) last in original order; maps
                // cluster by column ground id, then descending level within it.
                List<int> sorted = new List<int>(groupOrder);
                sorted.Sort((a, b) =>
                {
                    Map ma = groupMap[a];
                    Map mb = groupMap[b];
                    long ka = KeyFor(ma, groupOrder.IndexOf(a));
                    long kb = KeyFor(mb, groupOrder.IndexOf(b));
                    return ka.CompareTo(kb);
                });
                bool changed = false;
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (sorted[i] != groupOrder[i])
                    {
                        changed = true;
                        break;
                    }
                }
                if (!changed)
                {
                    return;
                }
                List<ColonistBar.Entry> rebuilt = new List<ColonistBar.Entry>(entries.Count);
                for (int g = 0; g < sorted.Count; g++)
                {
                    List<ColonistBar.Entry> list = byGroup[sorted[g]];
                    for (int i = 0; i < list.Count; i++)
                    {
                        rebuilt.Add(new ColonistBar.Entry(list[i].pawn, list[i].map, g));
                    }
                }
                entries.Clear();
                entries.AddRange(rebuilt);
                DrawerRef(__instance).Notify_RecachedEntries();
                FinderRef(__instance).CalculateDrawLocs(DrawLocsRef(__instance), out float scale, sorted.Count);
                ScaleRef(__instance) = scale;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "colonist bar level order");
            }
        }

        private static long KeyFor(Map map, int originalIndex)
        {
            if (map == null)
            {
                // Caravans: after every map, stable.
                return ((long)int.MaxValue << 16) + originalIndex;
            }
            LevelComp comp = map.Levels();
            Map ground = map.GroundMap() ?? map;
            int level = comp?.level ?? 0;
            // Column clusters by ground id; sky (1) before surface (0) before
            // basement (-1) within the cluster.
            return ((long)ground.uniqueID << 16) + (1 - level);
        }
    }
}
