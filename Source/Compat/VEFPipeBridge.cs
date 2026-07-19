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
    /// This class is only JIT compiled when VEF is active.
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
            for (int i = 0; i < netsA.Count; i++)
            {
                PipeNet netA = netsA[i];
                if (netA?.networkGrid == null || !netA.networkGrid[a.Position])
                {
                    continue;
                }
                PipeNet netB = NetAtOfDef(managerB, b.Position, netA.def);
                if (netB != null && netB != netA)
                {
                    Equalize(netA, netB);
                }
            }
        }

        private static PipeNet NetAtOfDef(PipeNetManager manager, IntVec3 cell, PipeNetDef def)
        {
            List<PipeNet> nets = manager.pipeNets;
            for (int i = 0; i < nets.Count; i++)
            {
                PipeNet net = nets[i];
                if (net?.networkGrid != null && net.def == def && net.networkGrid[cell])
                {
                    return net;
                }
            }
            return null;
        }

        private static void Equalize(PipeNet netA, PipeNet netB)
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
            if (move > MinTransfer)
            {
                Move(netA, netB, move);
            }
            else if (move < -MinTransfer)
            {
                Move(netB, netA, -move);
            }
        }

        private static void Move(PipeNet from, PipeNet to, float amount)
        {
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
