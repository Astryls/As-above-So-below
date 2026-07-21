using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Neutral dispatcher for cross-level network bridging. Detects Dubs Bad
    /// Hygiene, the Vanilla Expanded Framework pipe system (which carries
    /// Vanilla Pipes Expanded, Helixien gas, and Vanilla Temperature Expanded),
    /// and Rimefeller at startup; the typed bridge classes are only ever called
    /// (and therefore only ever JIT compiled) when the corresponding mod is
    /// active, so missing assemblies can never crash. Vanilla power bridges
    /// itself through CompABGridLink on the vertical conduit - Rimatomics
    /// electricity rides vanilla power nets, so its grid crosses levels through
    /// the same conduit with no extra code.
    /// Driven from the ground map's LevelComp every 100 ticks (matching the
    /// VEF pipe net tick, so each direct injection covers exactly one net
    /// tick); every link pair has one end on the ground map under the
    /// three-level cap. Bridging is carried by the dedicated vertical utility
    /// links only - stairways no longer tick resources across.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABPipeCompat
    {
        public static readonly bool DbhActive;

        public static readonly bool VefActive;

        public static readonly bool RimefellerActive;

        static ABPipeCompat()
        {
            // ABDetect: postfix-insensitive so local copies of these mods
            // (effective id "..._steam" splits) still enable the bridges.
            DbhActive = ABDetect.Active("Dubwise.DubsBadHygiene");
            VefActive = ABDetect.Active("OskarPotocki.VanillaFactionsExpanded.Core");
            RimefellerActive = ABDetect.Active("Dubwise.Rimefeller");
            if (DbhActive)
            {
                ABLog.Dev("Dubs Bad Hygiene detected, direct water bridging enabled.");
            }
            if (VefActive)
            {
                ABLog.Dev("VEF pipe system detected, direct resource bridging enabled (VPE, Helixien, VTE).");
            }
            if (RimefellerActive)
            {
                ABLog.Dev("Rimefeller detected, oil and fuel bridging enabled.");
            }
        }

        private static readonly List<Building_ABStairs> columnStairs = new List<Building_ABStairs>();

        private static void BridgeLink(Building_ABStairs a, Building_ABStairs b)
        {
            if (b == null || !b.Spawned || b.Map == null || b.Map.Disposed)
            {
                return;
            }
            // Bridge each pair exactly once, from its lower end (b sits above a).
            // The column is enumerated whole, so both ends are visited; the
            // level guard stops a +1<->+2 pair injecting twice.
            if (b.Map.Level() <= a.Map.Level())
            {
                return;
            }
            // Per-def capability routing: each vertical utility link bridges
            // exactly what its extension declares. Stairs declare nothing.
            ABStairsExtension ext = a.Ext;
            if (ext == null)
            {
                return;
            }
            if (DbhActive && ext.bridgeWater)
            {
                DBHWaterBridge.BridgePair(a, b);
            }
            if (VefActive && ext.bridgeVef)
            {
                VEFPipeBridge.BridgePair(a, b);
            }
            if (RimefellerActive && ext.bridgeChem)
            {
                RimefellerBridge.BridgePair(a, b);
            }
        }

        public static void TickGroundPairs(LevelComp groundComp)
        {
            if (!DbhActive && !VefActive && !RimefellerActive)
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelPipes)
            {
                return;
            }
            groundComp.CollectColumnStairs(columnStairs);
            for (int i = 0; i < columnStairs.Count; i++)
            {
                Building_ABStairs a = columnStairs[i];
                if (a == null || !a.Spawned)
                {
                    continue;
                }
                BridgeLink(a, a.Counterpart);
                // Elevator middle cars hold a second link (down).
                BridgeLink(a, a.SecondCounterpart);
            }
            columnStairs.Clear();
        }
    }
}
