using System.Collections.Generic;
using PipeSystem;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// DIRECT cross-level bridging for Vanilla Expanded Framework pipe networks
    /// (covers Vanilla Pipes Expanded resources, Helixien gas, Vanilla
    /// Temperature Expanded AC, and every other PipeNetDef generically). When
    /// nets of the same PipeNetDef touch a linked shaft's cell (or a cardinal
    /// neighbor) on both levels, unmet demand on one net is served straight from
    /// the other net's production surplus and stored resource through
    /// PipeSystem's own ExtraProductionThisTick / ExtraConsumptionThisTick
    /// injection hooks - purpose-built, consumed exactly once per net tick, so
    /// no storage is required on either side and all PipeSystem rules apply.
    /// A damped storage equalization runs as a secondary pass so tank levels
    /// drift together instead of one side hoarding.
    ///
    /// Driven at the same 100-tick cadence PipeNetManager ticks nets, so each
    /// injection covers one net tick. Ordering with their MapComponentTick does
    /// not matter: the Extra fields persist until the net's next tick and are
    /// zeroed on consumption.
    ///
    /// HARD SOFT-COMPAT RULE (learned from a live TypeLoadException): foreign
    /// types must never appear in ANY method signature (parameters or return
    /// type) or class-level member here - locals inside method bodies only.
    /// Assembly-wide attribute scans (LudeonTK's debug menu setup reflects over
    /// every method in every assembly) resolve signature types even for methods
    /// that are never called, which hard-crashes the scan when VEF is absent.
    /// Method BODIES are safe: they are JIT compiled only on first invocation,
    /// and every call site is gated by ABPipeCompat's detection check. That is
    /// why everything below lives inside clean-signature methods.
    /// </summary>
    public static class VEFPipeBridge
    {
        private const float Damping = 0.5f;

        private const float MinTransfer = 0.5f;

        private const float MinDirect = 0.01f;

        public static void BridgePair(Building_ABStairs a, Building_ABStairs b)
        {
            PipeNetManager managerA = a.Map.GetComponent<PipeNetManager>();
            PipeNetManager managerB = b.Map.GetComponent<PipeNetManager>();
            if (managerA == null || managerB == null)
            {
                return;
            }
            List<PipeNet> netsA = managerA.pipeNets;
            List<PipeNet> netsB = managerB.pipeNets;
            for (int i = 0; i < netsA.Count; i++)
            {
                PipeNet netA = netsA[i];
                if (netA?.networkGrid == null || !Touches(netA, a.Position, a.Map))
                {
                    continue;
                }
                // First net of the same def touching the far shaft.
                PipeNet netB = null;
                for (int j = 0; j < netsB.Count; j++)
                {
                    PipeNet cand = netsB[j];
                    if (cand?.networkGrid != null && cand.def == netA.def && Touches(cand, b.Position, b.Map))
                    {
                        netB = cand;
                        break;
                    }
                }
                if (netB == null || netB == netA)
                {
                    continue;
                }
                // Direct demand-serving both ways, then gentle tank leveling.
                DirectFeed(netA, netB);
                DirectFeed(netB, netA);
                EqualizeStorages(netA, netB);
            }
        }

        /// <summary>The shaft counts as touching a net when the net's grid
        /// covers its cell or any cardinal neighbor, so players can butt pipes
        /// against the shaft instead of running them under it.</summary>
        private static bool Touches(PipeNet net, IntVec3 c, Map map)
        {
            if (net.networkGrid[c])
            {
                return true;
            }
            IntVec3[] cardinals = GenAdj.CardinalDirections;
            for (int i = 0; i < cardinals.Length; i++)
            {
                IntVec3 n = c + cardinals[i];
                if (n.InBounds(map) && net.networkGrid[n])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Serves 'to' net's unmet demand (live consumption plus
        /// consumers that are off but could turn on) from 'from' net's
        /// production surplus and stored resource. Injections already promised
        /// by other shafts this window are respected on both sides.</summary>
        private static void DirectFeed(PipeNet from, PipeNet to)
        {
            float offWanting = 0f;
            List<CompResourceTrader> receivers = to.receivers;
            for (int i = 0; i < receivers.Count; i++)
            {
                CompResourceTrader r = receivers[i];
                if (!r.ResourceOn && r.CanBeOn())
                {
                    offWanting += r.Consumption;
                }
            }
            float deficit = to.Consumption + offWanting - to.Production - to.ExtraProductionThisTick;
            if (deficit <= MinDirect)
            {
                return;
            }
            float supply = Mathf.Max(0f, from.Production - from.Consumption - from.ExtraConsumptionThisTick)
                + from.Stored;
            float amount = Mathf.Min(deficit, supply);
            if (amount <= MinDirect)
            {
                return;
            }
            from.ExtraConsumptionThisTick += amount;
            to.ExtraProductionThisTick += amount;
        }

        /// <summary>Damped equalization toward equal fill fractions, tanks on
        /// both sides only; the direct feed above is what keeps storageless
        /// levels running.</summary>
        private static void EqualizeStorages(PipeNet netA, PipeNet netB)
        {
            float storedA = netA.CurrentStored();
            float storedB = netB.CurrentStored();
            float capA = storedA + netA.AvailableCapacity;
            float capB = storedB + netB.AvailableCapacity;
            if (capA <= 0f || capB <= 0f)
            {
                return;
            }
            float move = (storedA * capB - storedB * capA) / (capA + capB) * Damping;
            PipeNet from;
            PipeNet to;
            float amount;
            if (move > MinTransfer)
            {
                from = netA;
                to = netB;
                amount = move;
            }
            else if (move < -MinTransfer)
            {
                from = netB;
                to = netA;
                amount = -move;
            }
            else
            {
                return;
            }
            from.DrawAmongStorage(amount, out float drawn, null, drawFromOverflow: false);
            if (drawn <= 0f)
            {
                return;
            }
            to.DistributeAmongStorage(drawn, out float stored, null, allowOverflow: false);
            float leftover = drawn - stored;
            if (leftover > 0.001f)
            {
                from.DistributeAmongStorage(leftover, out float _, null, allowOverflow: false);
            }
        }
    }
}
