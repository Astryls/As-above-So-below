using System;
using DubsBadHygiene;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Cross-level bridging for Dubs Bad Hygiene air conditioning (cooling), the
    /// DBH counterpart to the Vanilla Temperature Expanded AC that already
    /// crosses levels through the VEF duct. DBH cooling rides its OWN pipe net
    /// (PipeType.Air), separate from water and from VEF's PipeSystem: each Air
    /// net's room units cool their rooms by exactly pipeNet.CoolingCap, which DBH
    /// recomputes as Clamp01(outdoorCapacity / indoorCapacity) EVERY tick in
    /// PlumbingNet.TickAircon. Because it is recomputed every tick (unlike the
    /// persistent water storage the water bridge equalizes), the bridge cannot
    /// run on the 100-tick pipe cadence - it runs as a postfix on
    /// HygienePipeMapComp.MapComponentTick (see DBHAirconPatch), right after DBH
    /// finishes ticking that map's nets.
    ///
    /// For each air-duct riser pair, the two levels' outdoor (compressor) and
    /// indoor (room-unit) capacities are POOLED and this map's net CoolingCap is
    /// rewritten from the pooled ratio. Only THIS map's net is written; the
    /// linked map rewrites its own net on its own tick, so nothing clobbers the
    /// pooled value later in the same tick. Net effect: an outdoor unit on one
    /// level cools rooms on the linked level, and the compressor works harder
    /// (rejects more heat) to match the extra demand - exactly DBH's own model,
    /// extended across the shaft.
    ///
    /// HARD SOFT-COMPAT RULE (shared with DBHWaterBridge/VEFPipeBridge): foreign
    /// types must never appear in ANY method signature or field here - locals
    /// inside plain method bodies only. Assembly-wide attribute scans resolve
    /// signature types even for never-called methods, which hard-crashes the scan
    /// when DBH is absent. Method BODIES JIT only on first invocation, and every
    /// entry point is gated by DBHAirconPatch (patch applied only when DBH is
    /// active), so the bodies below are safe.
    /// </summary>
    public static class DBHAirconBridge
    {
        /// <summary>Called once per map per tick from the DBH net-tick postfix.
        /// Pools every air-duct riser pair anchored on this map and rewrites this
        /// map's air nets' CoolingCap.</summary>
        public static void PoolForMap(Map map)
        {
            if (!ABGuard.On(ABGuard.Pipes))
            {
                return;
            }
            ABSettings s = ABMod.Settings;
            if (s == null || !s.crossLevelPipes || map == null || map.Disposed)
            {
                return;
            }
            LevelComp comp = map.Levels();
            if (comp == null)
            {
                return;
            }
            System.Collections.Generic.List<Building_ABStairs> stairs = comp.Stairs;
            if (stairs == null || stairs.Count == 0)
            {
                return;
            }
            try
            {
                for (int i = 0; i < stairs.Count; i++)
                {
                    Building_ABStairs a = stairs[i];
                    if (a == null || !a.Spawned || a.Ext == null || !a.Ext.bridgeAircon)
                    {
                        continue;
                    }
                    BridgePair(a, a.Counterpart);
                    // Elevator middle cars never carry ducts, but a utility riser
                    // could in principle hold a second link; cover it for parity.
                    BridgePair(a, a.SecondCounterpart);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Pipes, e, "DBH aircon cross-level bridge");
            }
        }

        private static void BridgePair(Building_ABStairs a, Building_ABStairs b)
        {
            if (b == null || !b.Spawned || b.Map == null || b.Map.Disposed || a.Map == b.Map)
            {
                return;
            }
            HygienePipeMapComp compA = a.Map.GetComponent<HygienePipeMapComp>();
            HygienePipeMapComp compB = b.Map.GetComponent<HygienePipeMapComp>();
            if (compA == null || compB == null)
            {
                return;
            }
            int air = (int)PipeType.Air;

            // The riser IS an air pipe, so exactly one air net contains its cell.
            PlumbingNet netA = null;
            PlumbingNet[] netsA = compA.PipeNets;
            for (int i = 0; i < netsA.Length; i++)
            {
                PlumbingNet n = netsA[i];
                if (n != null && n.NetType == air && n.cells.Contains(a.Position))
                {
                    netA = n;
                    break;
                }
            }
            if (netA == null)
            {
                return;
            }
            PlumbingNet netB = null;
            PlumbingNet[] netsB = compB.PipeNets;
            for (int i = 0; i < netsB.Length; i++)
            {
                PlumbingNet n = netsB[i];
                if (n != null && n.NetType == air && n.cells.Contains(b.Position))
                {
                    netB = n;
                    break;
                }
            }
            if (netB == null || netB == netA)
            {
                return;
            }

            // Pool capacity PER-MAP-LOCAL: filter each unit by parent.Map so that
            // if DBH's own cross-map "internet" merge has folded both levels'
            // units into one net's lists, we never double-count.
            float outdoor = 0f;
            float indoor = 0f;
            SumInto(netA, a.Map, ref outdoor, ref indoor);
            SumInto(netB, b.Map, ref outdoor, ref indoor);
            if (indoor <= 0f)
            {
                // No indoor demand anywhere on the pooled system: leave DBH's own
                // per-net value untouched.
                return;
            }
            // DBH's own TickAircon formula (Clamp01(1 - (indoor-outdoor)/indoor)),
            // pooled across both levels. Written on this map's net only.
            netA.CoolingCap = Mathf.Clamp01(outdoor / indoor);
        }

        // object-typed signature (cast on entry): a PlumbingNet parameter would
        // put a foreign type in a method signature and crash the assembly-wide
        // attribute scan when DBH is absent. See the class remarks.
        private static void SumInto(object netObj, Map onlyMap, ref float outdoor, ref float indoor)
        {
            PlumbingNet net = (PlumbingNet)netObj;
            System.Collections.Generic.List<CompAirconBaseUnit> outs = net.Aircons;
            for (int i = 0; i < outs.Count; i++)
            {
                CompAirconBaseUnit u = outs[i];
                if (u != null && u.parent != null && u.parent.Map == onlyMap)
                {
                    outdoor += u.Capacity;
                }
            }
            System.Collections.Generic.List<CompAirconIndoorUnit> ins = net.Airvents;
            for (int i = 0; i < ins.Count; i++)
            {
                CompAirconIndoorUnit u = ins[i];
                if (u != null && u.parent != null && u.parent.Map == onlyMap)
                {
                    indoor += u.Capacity;
                }
            }
        }
    }
}
