using System.Collections.Generic;
using DubsBadHygiene;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bridges Dubs Bad Hygiene plumbing between levels. When pipes of the same
    /// net type touch a linked stairwell's cell on both levels, the water stored
    /// in the two nets' towers equalizes (damped, through DBH's own public
    /// PullWater and PushWater so all their rules apply). Sewage still needs
    /// per-level handling (a septic tank or outlet on each level).
    ///
    /// HARD SOFT-COMPAT RULE (learned from a live TypeLoadException): foreign
    /// types must never appear in ANY method signature (parameters or return
    /// type) or class-level member here - locals inside method bodies only.
    /// Assembly-wide attribute scans (LudeonTK's debug menu setup reflects over
    /// every method in every assembly) resolve signature types even for methods
    /// that are never called, which hard-crashes the scan when DBH is absent.
    /// Method BODIES are safe: they are JIT compiled only on first invocation,
    /// and every call site is gated by ABPipeCompat's ModsConfig.IsActive check.
    /// That is why everything below lives inside one clean-signature method.
    /// </summary>
    public static class DBHWaterBridge
    {
        private const float Damping = 0.5f;

        private const float MinTransfer = 0.05f;

        public static void BridgePair(Building_ABStairs a, Building_ABStairs b)
        {
            HygienePipeMapComp compA = a.Map.GetComponent<HygienePipeMapComp>();
            HygienePipeMapComp compB = b.Map.GetComponent<HygienePipeMapComp>();
            if (compA == null || compB == null)
            {
                return;
            }
            PlumbingNet[] netsA = compA.PipeNets;
            PlumbingNet[] netsB = compB.PipeNets;
            for (int i = 0; i < netsA.Length; i++)
            {
                PlumbingNet netA = netsA[i];
                if (!netA.cells.Contains(a.Position))
                {
                    continue;
                }
                // First net of the same type touching the far stairwell.
                PlumbingNet netB = null;
                for (int j = 0; j < netsB.Length; j++)
                {
                    if (netsB[j].NetType == netA.NetType && netsB[j].cells.Contains(b.Position))
                    {
                        netB = netsB[j];
                        break;
                    }
                }
                if (netB == null || netB == netA)
                {
                    continue;
                }
                // Capacities from the towers (stored + free space).
                float capA = 0f;
                List<CompWaterStorage> towersA = netA.WaterTowers;
                for (int t = 0; t < towersA.Count; t++)
                {
                    capA += towersA[t].WaterStorage + towersA[t].space;
                }
                float capB = 0f;
                List<CompWaterStorage> towersB = netB.WaterTowers;
                for (int t = 0; t < towersB.Count; t++)
                {
                    capB += towersB[t].WaterStorage + towersB[t].space;
                }
                if (capA <= 0f || capB <= 0f)
                {
                    continue;
                }
                // Damped equalization toward equal fill fractions.
                float move = (netA.WaterStorage * capB - netB.WaterStorage * capA) / (capA + capB) * Damping;
                PlumbingNet from;
                PlumbingNet to;
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
                    continue;
                }
                if (!from.PullWater(amount, out ContaminationLevel _))
                {
                    continue;
                }
                float leftover = to.PushWater(amount);
                if (leftover > 0f)
                {
                    from.PushWater(leftover);
                }
            }
        }
    }
}
