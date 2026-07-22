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
        private static readonly AccessTools.FieldRef<ColonistBar, bool> DirtyRef =
            AccessTools.FieldRefAccess<ColonistBar, bool>("entriesDirty");

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

        /// <summary>CheckRecacheEntries is called every UI frame but early-outs
        /// on a dirty flag; a plain postfix would re-sort and re-allocate every
        /// frame regardless. Mirror the flag so the reorder runs only on frames
        /// where vanilla actually rebuilt the entries.</summary>
        private static void Prefix(ColonistBar __instance, out bool __state)
        {
            __state = DirtyRef(__instance);
        }

        private static void Postfix(ColonistBar __instance, bool __state)
        {
            if (!__state || !ABGuard.On(ABGuard.Ui))
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
                // Defer to [LTO] Colony Groups when it is active: it owns the bar's
                // grouping and forcing ours would fight its UI.
                if (LtoActive)
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
                // Keys precomputed: IndexOf inside a comparator is O(n^2).
                Dictionary<int, long> sortKey = new Dictionary<int, long>(groupOrder.Count);
                for (int i = 0; i < groupOrder.Count; i++)
                {
                    int g = groupOrder[i];
                    sortKey[g] = KeyFor(groupMap[g], i);
                }
                List<int> sorted = new List<int>(groupOrder);
                sorted.Sort((a, b) => sortKey[a].CompareTo(sortKey[b]));

                // Column key per group + how many groups share it. With oneColonistBar on
                // (default) a column's maps share one key (ground uniqueID) and merge into a
                // single contiguous block; caravans and other columns get a unique key. Off
                // = each map its own group (reorder only).
                bool merge = ABMod.Settings?.oneColonistBar ?? true;
                Dictionary<int, long> colKeyOf = new Dictionary<int, long>(sorted.Count);
                Dictionary<long, int> colCount = new Dictionary<long, int>();
                for (int i = 0; i < sorted.Count; i++)
                {
                    long key = merge
                        ? ColumnKey(groupMap[sorted[i]], sorted[i])
                        : (((long)sorted[i] << 1) | 1L);
                    colKeyOf[sorted[i]] = key;
                    colCount[key] = colCount.TryGetValue(key, out int cc) ? cc + 1 : 1;
                }

                // Rebuild, clustered by column. Drop the empty-map placeholder frames inside
                // a MERGED column: otherwise a freshly opened (empty) level adds a slot that
                // widens the bar and shoves it sideways on map creation. Display group ids
                // are renumbered over the SURVIVORS so the finder's per-group arrays stay
                // valid (no gaps). The sort already clusters a column's maps, so a column's
                // survivors are contiguous. Runs only on dirty recache frames.
                List<ColonistBar.Entry> rebuilt = new List<ColonistBar.Entry>(entries.Count);
                int dg = -1;
                long lastKey = long.MinValue;
                for (int g = 0; g < sorted.Count; g++)
                {
                    int og = sorted[g];
                    long key = colKeyOf[og];
                    bool mergedCol = colCount[key] > 1;
                    List<ColonistBar.Entry> list = byGroup[og];
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (mergedCol && list[i].pawn == null)
                        {
                            continue;
                        }
                        if (dg < 0 || key != lastKey)
                        {
                            dg++;
                            lastKey = key;
                        }
                        rebuilt.Add(new ColonistBar.Entry(list[i].pawn, list[i].map, dg));
                    }
                }
                entries.Clear();
                entries.AddRange(rebuilt);
                DrawerRef(__instance).Notify_RecachedEntries();
                FinderRef(__instance).CalculateDrawLocs(DrawLocsRef(__instance), out float scale, dg + 1);
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

        /// <summary>One key per column (shared ground id) so a column's maps merge into a
        /// single bar group; caravans and standalone groups get a unique key. Even keys are
        /// columns, odd keys are per-group, so the two spaces never collide.</summary>
        private static long ColumnKey(Map map, int originalGroup)
        {
            if (map == null)
            {
                return ((long)originalGroup << 1) | 1L;
            }
            Map ground = map.GroundMap() ?? map;
            return (long)ground.uniqueID << 1;
        }

        private static int ltoActive = -1;

        /// <summary>[LTO] Colony Groups replaces the bar's grouping with user-defined
        /// groups; when present we stand down entirely. Cached after first lookup.</summary>
        private static bool LtoActive
        {
            get
            {
                if (ltoActive < 0)
                {
                    ltoActive = ABDetect.Active("DerekBickley.LTOColonyGroupsFinal") ? 1 : 0;
                }
                return ltoActive == 1;
            }
        }
    }
}
