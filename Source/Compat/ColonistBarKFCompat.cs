using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Colonist Bar KF (Mlie.ColonistBarKF) soft compat: KF replaces the vanilla colonist
    /// bar with its own ColonistBar_KF + EntryKF list, so our vanilla merge patch never
    /// touches it. KF builds one bar group per map (groupInt++ per map). We postfix its
    /// entry recache (ColBarHelper_KF.CheckRecacheEntries) and, when oneColonistBar is on,
    /// rebuild its cached entry list so every map of one column shares a single group -
    /// the column's sky/surface/basement render as one contiguous block with no separator.
    ///
    /// EntryKF.group is readonly and KF's draw-loc finder indexes per-group arrays by the
    /// group value, so groups must be renumbered contiguously (0..N-1) with entries sorted
    /// so each group is one run. We therefore REBUILD the list via EntryKF's constructor
    /// rather than mutate in place, drop the per-map placeholder frames inside a merged
    /// column, and re-run KF's CalculateDrawLocs. Everything is reflection-only (no compile
    /// -time ref to KF), dirty-frame gated (a prefix captures EntriesDirty), and fails open:
    /// any missing member or throw disables the patch and leaves KF's own bar intact.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ColonistBarKFCompat
    {
        private static readonly bool ready;

        private static readonly FieldInfo cachedEntriesF;
        private static readonly FieldInfo entriesDirtyF;
        private static readonly FieldInfo cachedScaleF;
        private static readonly PropertyInfo drawLocsP;
        private static readonly ConstructorInfo entryCtor;
        private static readonly FieldInfo entryPawnF;
        private static readonly FieldInfo entryMapF;
        private static readonly FieldInfo entryGroupF;
        private static readonly FieldInfo drawLocsFinderF;
        private static readonly MethodInfo calcDrawLocsM;
        private static readonly FieldInfo drawerF;
        private static readonly MethodInfo notifyRecachedM;

        static ColonistBarKFCompat()
        {
            try
            {
                if (!ABDetect.Active("Mlie.ColonistBarKF"))
                {
                    return;
                }
                Type helperType = AccessTools.TypeByName("ColonistBarKF.Bar.ColBarHelper_KF");
                Type entryType = AccessTools.TypeByName("ColonistBarKF.EntryKF");
                Type barType = AccessTools.TypeByName("ColonistBarKF.Bar.ColonistBar_KF");
                Type finderType = AccessTools.TypeByName("ColonistBarKF.Bar.ColonistBarDrawLocsFinder_Kf");
                Type drawerType = AccessTools.TypeByName("ColonistBarKF.Bar.ColonistBarColonistDrawer_KF");
                if (helperType == null || entryType == null || barType == null || finderType == null)
                {
                    Log.Warning(ABLog.Tag + " Colonist Bar KF detected but its bar types were not found; column merge disabled for it.");
                    return;
                }

                cachedEntriesF = AccessTools.Field(helperType, "cachedEntries");
                entriesDirtyF = AccessTools.Field(helperType, "EntriesDirty");
                cachedScaleF = AccessTools.Field(helperType, "CachedScale");
                drawLocsP = AccessTools.Property(helperType, "DrawLocs");
                entryCtor = AccessTools.Constructor(entryType, new[] { typeof(Pawn), typeof(Map), typeof(int) });
                entryPawnF = AccessTools.Field(entryType, "pawn");
                entryMapF = AccessTools.Field(entryType, "map");
                entryGroupF = AccessTools.Field(entryType, "group");
                drawLocsFinderF = AccessTools.Field(barType, "DrawLocsFinder");
                drawerF = AccessTools.Field(barType, "Drawer");
                calcDrawLocsM = AccessTools.Method(finderType, "CalculateDrawLocs",
                    new[] { typeof(List<Vector2>), typeof(float).MakeByRefType() });
                notifyRecachedM = drawerType != null ? AccessTools.Method(drawerType, "Notify_RecachedEntries") : null;

                MethodInfo target = AccessTools.Method(helperType, "CheckRecacheEntries");
                if (cachedEntriesF == null || entriesDirtyF == null || cachedScaleF == null || drawLocsP == null
                    || entryCtor == null || entryPawnF == null || entryMapF == null || entryGroupF == null
                    || drawLocsFinderF == null || calcDrawLocsM == null || target == null)
                {
                    Log.Warning(ABLog.Tag + " Colonist Bar KF internals did not match; column merge disabled for it.");
                    return;
                }

                HarmonyBoot.Harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(ColonistBarKFCompat), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(ColonistBarKFCompat), nameof(Postfix)));
                ready = true;
                ABLog.Dev("Colonist Bar KF detected, column merge enabled.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Colonist Bar KF compat setup failed: " + e.Message);
            }
        }

        private static void Prefix(object __instance, out bool __state)
        {
            __state = false;
            if (ready)
            {
                try
                {
                    __state = (bool)entriesDirtyF.GetValue(__instance);
                }
                catch
                {
                    __state = false;
                }
            }
        }

        private static void Postfix(object __instance, bool __state)
        {
            if (!ready || !__state || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.oneColonistBar || LtoActive)
            {
                return;
            }
            try
            {
                Merge(__instance);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "colonist bar KF merge");
            }
        }

        private static void Merge(object helper)
        {
            IList entries = cachedEntriesF.GetValue(helper) as IList;
            int n = entries?.Count ?? 0;
            if (n == 0)
            {
                return;
            }

            // Snapshot the entries.
            Pawn[] pawns = new Pawn[n];
            Map[] maps = new Map[n];
            int[] groups = new int[n];
            for (int i = 0; i < n; i++)
            {
                object e = entries[i];
                pawns[i] = entryPawnF.GetValue(e) as Pawn;
                maps[i] = entryMapF.GetValue(e) as Map;
                groups[i] = (int)entryGroupF.GetValue(e);
            }

            // Distinct groups in order, with their map and first index.
            List<int> groupOrder = new List<int>();
            Dictionary<int, Map> groupMap = new Dictionary<int, Map>();
            Dictionary<int, int> groupFirst = new Dictionary<int, int>();
            Dictionary<int, List<int>> entriesByGroup = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int g = groups[i];
                if (!entriesByGroup.TryGetValue(g, out List<int> idxs))
                {
                    idxs = new List<int>();
                    entriesByGroup[g] = idxs;
                    groupOrder.Add(g);
                    groupMap[g] = maps[i];
                    groupFirst[g] = i;
                }
                idxs.Add(i);
            }

            // Column key per group (ground uniqueID = shared across a column's maps; caravans
            // and standalone maps unique), and how many groups share each column key.
            Dictionary<int, long> colKey = new Dictionary<int, long>(groupOrder.Count);
            Dictionary<long, int> colCount = new Dictionary<long, int>();
            Dictionary<long, int> colAnchor = new Dictionary<long, int>();
            for (int i = 0; i < groupOrder.Count; i++)
            {
                int g = groupOrder[i];
                long ck = ColumnKey(groupMap[g], g);
                colKey[g] = ck;
                colCount[ck] = colCount.TryGetValue(ck, out int c) ? c + 1 : 1;
                if (!colAnchor.TryGetValue(ck, out int a) || groupFirst[g] < a)
                {
                    colAnchor[ck] = groupFirst[g];
                }
            }

            bool anyMerge = false;
            foreach (int v in colCount.Values)
            {
                if (v > 1)
                {
                    anyMerge = true;
                    break;
                }
            }
            if (!anyMerge)
            {
                return;
            }

            // Cluster groups by column (kept at the column's first position) and, within a
            // column, order sky -> surface -> basement.
            List<int> sorted = new List<int>(groupOrder);
            sorted.Sort((a, b) =>
            {
                int aa = colAnchor[colKey[a]];
                int ab = colAnchor[colKey[b]];
                if (aa != ab)
                {
                    return aa.CompareTo(ab);
                }
                return LevelOrder(groupMap[a]).CompareTo(LevelOrder(groupMap[b]));
            });

            // Rebuild, dropping placeholder (pawn == null) frames inside a merged column so a
            // freshly opened empty level does not widen KF's bar. Renumber merged group ids
            // over the SURVIVORS so KF's per-group arrays (indexed by group value) stay
            // contiguous and valid; a column's survivors are contiguous after the sort.
            List<object> rebuilt = new List<object>(n);
            int dg = -1;
            long lastCk = long.MinValue;
            for (int i = 0; i < sorted.Count; i++)
            {
                int g = sorted[i];
                long ck = colKey[g];
                bool merged = colCount[ck] > 1;
                List<int> idxs = entriesByGroup[g];
                for (int j = 0; j < idxs.Count; j++)
                {
                    int idx = idxs[j];
                    if (merged && pawns[idx] == null)
                    {
                        continue;
                    }
                    if (dg < 0 || ck != lastCk)
                    {
                        dg++;
                        lastCk = ck;
                    }
                    rebuilt.Add(entryCtor.Invoke(new object[] { pawns[idx], maps[idx], dg }));
                }
            }

            entries.Clear();
            for (int i = 0; i < rebuilt.Count; i++)
            {
                entries.Add(rebuilt[i]);
            }

            // Recompute KF's draw locations + scale for the merged layout.
            object finder = drawLocsFinderF.GetValue(null);
            object drawLocs = drawLocsP.GetValue(helper);
            object[] args = { drawLocs, 0f };
            calcDrawLocsM.Invoke(finder, args);
            cachedScaleF.SetValue(helper, (float)args[1]);

            object drawer = drawerF?.GetValue(null);
            if (drawer != null)
            {
                notifyRecachedM?.Invoke(drawer, null);
            }
        }

        private static long ColumnKey(Map map, int originalGroup)
        {
            if (map == null)
            {
                return ((long)originalGroup << 1) | 1L;
            }
            Map ground = map.GroundMap() ?? map;
            return (long)ground.uniqueID << 1;
        }

        private static int LevelOrder(Map map)
        {
            return map == null ? 0 : (1 - map.Level());
        }

        private static int ltoActive = -1;

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
