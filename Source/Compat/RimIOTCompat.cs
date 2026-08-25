using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// RimIOT's LOGISTIC MATRIX, MADE CROSS-LEVEL (§62 Data column).
    ///
    /// RimIOT rebuilds every StorageNetwork from scratch whenever topology changes:
    /// `MapComponent_NetworkManager.RebuildAllNetworks` BFS-floods over registered node
    /// buildings via `GenAdj.CellsAdjacent8Way`, then derives per-network caches. The flood
    /// is inline, so there is no neighbour funnel to widen; instead the rebuild is left
    /// alone and its RESULT is merged afterwards - the Dubwise recipe, adapted to a host
    /// whose networks carry far more derived state.
    ///
    /// ⚠ MERGE-BY-REMOVAL, MEMBER BY MEMBER. The loser's cables/inputConnectors/interfaces/
    /// connectedContainers move into the winner, the component's private routing dicts are
    /// repointed, the loser leaves the AllNetworks list, and the winner's caches are
    /// rebuilt through RimIOT's OWN public surface: StorageNetwork.RebuildInterfaceCache +
    /// RecalcTopologyHash, and RimIOTApi.TriggerRebalance (which redoes container
    /// summaries, the accepted-defs index and the rebalance queue exactly the way the host
    /// does it).
    ///
    /// ⚠ THE 500-TICK AUDITS ARE MEMBER-BASED, NOT SPATIAL - checked in the 1.6 decompile.
    /// `VerifyTopologyHash` XORs connectedContainers ids and compares to the stored hash;
    /// `PerformFullAudit` verifies items against member containers. Neither re-floods the
    /// map, so a merged network passes every audit once RecalcTopologyHash has stamped it.
    /// A spatially-derived hash would have made this whole approach churn (rebuild, merge,
    /// hash mismatch, rebuild forever) - if RimIOT ever changes the hash to spatial, the
    /// counters below are what will say so.
    ///
    /// ⚠ HashSet&lt;T&gt; DOES NOT IMPLEMENT NON-GENERIC ICollection. Every member move uses
    /// the resolve-Add-by-shape recipe; only IEnumerable is safe to cast to.
    /// </summary>
    [HarmonyPatch]
    public static class RimIOTCompat
    {
        private static Type manager;
        private static Type networkType;
        private static Type apiType;
        private static MethodInfo getNetworkFor;      // manager.GetNetworkFor(Building)
        private static PropertyInfo allNetworks;      // manager.AllNetworks (live List)
        private static FieldInfo buildingToNetwork;   // manager private Dictionary
        private static FieldInfo containerToNetwork;  // manager private Dictionary
        private static FieldInfo fCables;             // StorageNetwork.cables (HashSet<Building>)
        private static FieldInfo fInputs;             // StorageNetwork.inputConnectors
        private static FieldInfo fInterfaces;         // StorageNetwork.interfaces
        private static FieldInfo fContainers;         // StorageNetwork.connectedContainers
        private static MethodInfo rebuildIfaceCache;  // StorageNetwork.RebuildInterfaceCache()
        private static MethodInfo recalcHash;         // StorageNetwork.RecalcTopologyHash()
        private static MethodInfo apiTriggerRebalance; // RimIOTApi.TriggerRebalance(net, map)

        private static int rebuilds;
        private static int merges;
        private static string skip = "(none)";

        public static string CounterReport()
        {
            if (manager == null)
            {
                return "    RimIOT: (not installed)";
            }
            return "    RimIOT: rebuilds=" + rebuilds + " merges=" + merges + " | skip: " + skip;
        }

        private static bool Prepare()
        {
            manager = AccessTools.TypeByName("RimIOT.MapComponent_NetworkManager");
            networkType = AccessTools.TypeByName("RimIOT.StorageNetwork");
            apiType = AccessTools.TypeByName("RimIOT.RimIOTApi");
            if (manager == null || networkType == null)
            {
                return false;
            }
            getNetworkFor = AccessTools.Method(manager, "GetNetworkFor");
            allNetworks = AccessTools.Property(manager, "AllNetworks");
            buildingToNetwork = AccessTools.Field(manager, "buildingToNetwork");
            containerToNetwork = AccessTools.Field(manager, "containerToNetwork");
            fCables = AccessTools.Field(networkType, "cables");
            fInputs = AccessTools.Field(networkType, "inputConnectors");
            fInterfaces = AccessTools.Field(networkType, "interfaces");
            fContainers = AccessTools.Field(networkType, "connectedContainers");
            rebuildIfaceCache = AccessTools.Method(networkType, "RebuildInterfaceCache");
            recalcHash = AccessTools.Method(networkType, "RecalcTopologyHash");
            apiTriggerRebalance = apiType != null
                ? AccessTools.Method(apiType, "TriggerRebalance")
                : null;
            if (getNetworkFor == null || allNetworks == null || buildingToNetwork == null
                || containerToNetwork == null || fCables == null || fInputs == null
                || fInterfaces == null || fContainers == null || rebuildIfaceCache == null
                || recalcHash == null)
            {
                Log.Warning(ABLog.Tag + " RimIOT is present but its network internals did not "
                    + "resolve; cross-level logistic matrix is disabled.");
                manager = null;
                return false;
            }
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(manager, "RebuildAllNetworks");
        }

        private static void Postfix(object __instance)
        {
            if (manager == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                Map map = (__instance as MapComponent)?.map;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                rebuilds++;
                ABNetwork net = ABColumnNetworks.ById("RimIOT.Network");
                if (net?.carrier == null)
                {
                    skip = "no carrier generated";
                    return;
                }
                List<Thing> carriers = map.listerThings.ThingsOfDef(net.carrier);
                if (carriers.Count == 0)
                {
                    // Rule 31: a silent return here reads identically to a broken merge.
                    skip = "no RimIOT carrier on this map (no data column, or its toggle is off)";
                    return;
                }
                List<Thing> partners = new List<Thing>();
                for (int i = 0; i < carriers.Count; i++)
                {
                    if (!(carriers[i] is Building anchor))
                    {
                        continue;
                    }
                    partners.Clear();
                    ABColumnLink.AppendPartners(carriers[i], partners);
                    for (int j = 0; j < partners.Count; j++)
                    {
                        if (partners[j] is Building far)
                        {
                            Join(__instance, map, anchor, far);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "RimIOT matrix merge");
            }
        }

        private static void Join(object holder, Map map, Building anchor, Building far)
        {
            object keep = getNetworkFor.Invoke(holder, new object[] { anchor });
            object drop = getNetworkFor.Invoke(holder, new object[] { far });
            if (keep == null || drop == null)
            {
                skip = "network was null (keep=" + (keep != null) + " drop=" + (drop != null) + ")";
                return;
            }
            if (ReferenceEquals(keep, drop))
            {
                skip = "already the same network - nothing to merge";
                return;
            }

            IDictionary b2n = buildingToNetwork.GetValue(holder) as IDictionary;
            IDictionary c2n = containerToNetwork.GetValue(holder) as IDictionary;
            if (b2n == null || c2n == null)
            {
                skip = "routing dictionaries did not resolve";
                return;
            }

            // Members first: every building set of the loser moves to the winner, and the
            // component's routing dict follows so GetNetworkFor answers the merged net for
            // the NEXT pair in a taller stack.
            MoveBuildings(fCables, keep, drop, b2n);
            MoveBuildings(fInputs, keep, drop, b2n);
            MoveBuildings(fInterfaces, keep, drop, b2n);

            object keepContainers = fContainers.GetValue(keep);
            MethodInfo addContainer = AddMethodFor(keepContainers);
            if (fContainers.GetValue(drop) is IEnumerable dropContainers && addContainer != null)
            {
                foreach (object c in dropContainers)
                {
                    addContainer.Invoke(keepContainers, new[] { c });
                    c2n[c] = keep;
                }
            }

            if (allNetworks.GetValue(holder) is IList list)
            {
                list.Remove(drop);
            }

            // Winner's caches, rebuilt through the host's own surface.
            rebuildIfaceCache.Invoke(keep, null);
            apiTriggerRebalance?.Invoke(null, new[] { keep, (object)map });
            recalcHash.Invoke(keep, null);
            merges++;
        }

        private static void MoveBuildings(FieldInfo setField, object keep, object drop, IDictionary b2n)
        {
            object keepSet = setField.GetValue(keep);
            MethodInfo add = AddMethodFor(keepSet);
            if (add == null || !(setField.GetValue(drop) is IEnumerable dropSet))
            {
                skip = "member set on " + setField.Name + " was not movable";
                return;
            }
            foreach (object b in dropSet)
            {
                add.Invoke(keepSet, new[] { b });
                b2n[b] = keep;
            }
        }

        private static readonly Dictionary<Type, MethodInfo> addCache = new Dictionary<Type, MethodInfo>();

        private static MethodInfo AddMethodFor(object collection)
        {
            if (collection == null)
            {
                return null;
            }
            Type t = collection.GetType();
            if (addCache.TryGetValue(t, out MethodInfo cached))
            {
                return cached;
            }
            MethodInfo[] all = t.GetMethods();
            MethodInfo found = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name == "Add" && all[i].GetParameters().Length == 1)
                {
                    found = all[i];
                    break;
                }
            }
            addCache[t] = found;
            return found;
        }
    }
}
