using System.Collections.Generic;
using PipeSystem;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bridges Vanilla Expanded Framework pipe networks between levels. When pipes
    /// of the same PipeNetDef touch a linked stairwell's cell on both levels, the
    /// stored resource equalizes between the two nets (damped, through PipeSystem's
    /// own public draw and distribute so all their rules apply). Each side needs at
    /// least one storage for resources to flow into.
    ///
    /// HARD SOFT-COMPAT RULE (learned from a live TypeLoadException): foreign
    /// types must never appear in ANY method signature (parameters or return
    /// type) or class-level member here - locals inside method bodies only.
    /// Assembly-wide attribute scans (LudeonTK's debug menu setup reflects over
    /// every method in every assembly) resolve signature types even for methods
    /// that are never called, which hard-crashes the scan when VEF is absent.
    /// Method BODIES are safe: they are JIT compiled only on first invocation,
    /// and every call site is gated by ABPipeCompat's detection check. That is
    /// why everything below lives inside one clean-signature method.
    /// </summary>
    public static class VEFPipeBridge
    {
        private const float Damping = 0.5f;

        private const float MinTransfer = 0.5f;

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
                if (netA?.networkGrid == null || !netA.networkGrid[a.Position])
                {
                    continue;
                }
                // First net of the same def touching the far stairwell.
                PipeNet netB = null;
                for (int j = 0; j < netsB.Count; j++)
                {
                    PipeNet cand = netsB[j];
                    if (cand?.networkGrid != null && cand.def == netA.def && cand.networkGrid[b.Position])
                    {
                        netB = cand;
                        break;
                    }
                }
                if (netB == null || netB == netA)
                {
                    continue;
                }
                float storedA = netA.CurrentStored();
                float storedB = netB.CurrentStored();
                float capA = storedA + netA.AvailableCapacity;
                float capB = storedB + netB.AvailableCapacity;
                if (capA <= 0f || capB <= 0f)
                {
                    continue;
                }
                // Damped equalization toward equal fill fractions.
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
                    continue;
                }
                from.DrawAmongStorage(amount, out float drawn, null, drawFromOverflow: false);
                if (drawn <= 0f)
                {
                    continue;
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
}
