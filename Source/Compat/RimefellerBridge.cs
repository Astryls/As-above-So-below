using System.Collections.Generic;
using Rimefeller;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bridges Rimefeller pipeline networks between levels. The vertical chem
    /// pipe carries two small hidden Rimefeller storage tanks (oil and fuel),
    /// so each side's net always has at least one usable tank: wells and
    /// crackers pump into the link's tanks through Rimefeller's own logic, and
    /// this bridge levels oil and fuel fill fractions across the pair through
    /// Rimefeller's public Pull and Push APIs. Consumers on a level with no
    /// player-built tank run straight off the shaft. Nets are resolved through
    /// the link's own tank comps, so no cell scanning is needed.
    ///
    /// HARD SOFT-COMPAT RULE (learned from a live TypeLoadException): foreign
    /// types must never appear in ANY method signature (parameters or return
    /// type) or class-level member here - locals inside method bodies only.
    /// Assembly-wide attribute scans (LudeonTK's debug menu setup reflects over
    /// every method in every assembly) resolve signature types even for methods
    /// that are never called, which hard-crashes the scan when Rimefeller is
    /// absent. Method BODIES are safe: they are JIT compiled only on first
    /// invocation, and every call site is gated by ABPipeCompat's detection
    /// check. That is why everything below lives inside clean-signature methods.
    /// </summary>
    public static class RimefellerBridge
    {
        private const float Damping = 0.5f;

        private const float MinTransfer = 0.5f;

        public static void BridgePair(Building_ABStairs a, Building_ABStairs b)
        {
            PipelineNet netA = NetOf(a);
            PipelineNet netB = NetOf(b);
            if (netA == null || netB == null || netA == netB)
            {
                return;
            }
            EqualizeOil(netA, netB);
            EqualizeFuel(netA, netB);
        }

        private static PipelineNet NetOf(Building_ABStairs link)
        {
            foreach (CompStorageTank tank in link.GetComps<CompStorageTank>())
            {
                PipelineNet net = tank.pipeNet;
                if (net != null)
                {
                    return net;
                }
            }
            return null;
        }

        private static void EqualizeOil(PipelineNet netA, PipelineNet netB)
        {
            SumTanks(netA.OilStorage, out float storedA, out float capA);
            SumTanks(netB.OilStorage, out float storedB, out float capB);
            if (capA <= 0f || capB <= 0f)
            {
                return;
            }
            float move = (storedA * capB - storedB * capA) / (capA + capB) * Damping;
            if (move > MinTransfer)
            {
                MoveOil(netA, netB, move);
            }
            else if (move < -MinTransfer)
            {
                MoveOil(netB, netA, -move);
            }
        }

        private static void MoveOil(PipelineNet from, PipelineNet to, float amount)
        {
            if (!from.PullOil(amount))
            {
                return;
            }
            double leftover = to.PushCrude(amount);
            if (leftover > 0.001)
            {
                from.PushCrude(leftover);
            }
        }

        private static void EqualizeFuel(PipelineNet netA, PipelineNet netB)
        {
            SumTanks(netA.FuelStorage, out float storedA, out float capA);
            SumTanks(netB.FuelStorage, out float storedB, out float capB);
            if (capA <= 0f || capB <= 0f)
            {
                return;
            }
            float move = (storedA * capB - storedB * capA) / (capA + capB) * Damping;
            if (move > MinTransfer)
            {
                MoveFuel(netA, netB, move);
            }
            else if (move < -MinTransfer)
            {
                MoveFuel(netB, netA, -move);
            }
        }

        private static void MoveFuel(PipelineNet from, PipelineNet to, float amount)
        {
            if (!from.PullFuel(amount))
            {
                return;
            }
            float leftover = to.PushFuel(amount);
            if (leftover > 0.001f)
            {
                from.PushFuel(leftover);
            }
        }

        private static void SumTanks(List<CompStorageTank> tanks, out float stored, out float cap)
        {
            stored = 0f;
            cap = 0f;
            if (tanks == null)
            {
                return;
            }
            for (int i = 0; i < tanks.Count; i++)
            {
                CompStorageTank t = tanks[i];
                if (t == null || t.DrainTank)
                {
                    continue;
                }
                stored += (float)t.Storage;
                cap += (float)t.Props.StorageCap;
            }
        }
    }
}
