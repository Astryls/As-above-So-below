using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Neutral dispatcher for pipe network bridging. Detects Dubs Bad Hygiene and
    /// the Vanilla Expanded Framework pipe system at startup; the typed bridge
    /// classes are only ever called (and therefore only ever JIT compiled) when
    /// the corresponding mod is active, so missing assemblies can never crash.
    /// Driven from the ground map's LevelComp every 250 ticks; every stairwell
    /// pair has one end on the ground map under the three-level cap.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABPipeCompat
    {
        public static readonly bool DbhActive;

        public static readonly bool VefActive;

        static ABPipeCompat()
        {
            DbhActive = ModsConfig.IsActive("Dubwise.DubsBadHygiene");
            VefActive = ModsConfig.IsActive("OskarPotocki.VanillaFactionsExpanded.Core");
            if (DbhActive)
            {
                ABLog.Dev("Dubs Bad Hygiene detected, water bridging enabled.");
            }
            if (VefActive)
            {
                ABLog.Dev("VEF pipe system detected, resource bridging enabled.");
            }
        }

        private static void BridgeLink(Building_ABStairs a, Building_ABStairs b)
        {
            if (b == null || !b.Spawned || b.Map == null || b.Map.Disposed)
            {
                return;
            }
            if (DbhActive)
            {
                DBHWaterBridge.BridgePair(a, b);
            }
            if (VefActive)
            {
                VEFPipeBridge.BridgePair(a, b);
            }
        }

        public static void TickGroundPairs(LevelComp groundComp)
        {
            if (!DbhActive && !VefActive)
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelPipes)
            {
                return;
            }
            List<Building_ABStairs> stairs = groundComp.Stairs;
            for (int i = 0; i < stairs.Count; i++)
            {
                Building_ABStairs a = stairs[i];
                if (a == null || !a.Spawned)
                {
                    continue;
                }
                BridgeLink(a, a.Counterpart);
                // Elevator middle cars hold a second link (down).
                BridgeLink(a, a.SecondCounterpart);
            }
        }
    }
}
