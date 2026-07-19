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
    /// This class is only JIT compiled when DBH is active.
    /// </summary>
    public static class DBHWaterBridge
    {
        private const float Damping = 0.5f;

        private const float MinTransfer = 0.05f;

        // No class-level fields may use DBH types: field type resolution can load
        // the assembly before any runtime gate runs. Locals only.
        public static void BridgePair(Building_ABStairs a, Building_ABStairs b)
        {
            HygienePipeMapComp compA = a.Map.GetComponent<HygienePipeMapComp>();
            HygienePipeMapComp compB = b.Map.GetComponent<HygienePipeMapComp>();
            if (compA == null || compB == null)
            {
                return;
            }
            List<PlumbingNet> netsAtA = new List<PlumbingNet>();
            CollectNetsAt(compA, a.Position, netsAtA);
            for (int i = 0; i < netsAtA.Count; i++)
            {
                PlumbingNet netA = netsAtA[i];
                PlumbingNet netB = NetAtOfType(compB, b.Position, netA.NetType);
                if (netB != null && netB != netA)
                {
                    EqualizeWater(netA, netB);
                }
            }
        }

        private static void CollectNetsAt(HygienePipeMapComp comp, IntVec3 cell, List<PlumbingNet> outNets)
        {
            PlumbingNet[] nets = comp.PipeNets;
            for (int i = 0; i < nets.Length; i++)
            {
                if (nets[i].cells.Contains(cell))
                {
                    outNets.Add(nets[i]);
                }
            }
        }

        private static PlumbingNet NetAtOfType(HygienePipeMapComp comp, IntVec3 cell, int netType)
        {
            PlumbingNet[] nets = comp.PipeNets;
            for (int i = 0; i < nets.Length; i++)
            {
                if (nets[i].NetType == netType && nets[i].cells.Contains(cell))
                {
                    return nets[i];
                }
            }
            return null;
        }

        private static void EqualizeWater(PlumbingNet netA, PlumbingNet netB)
        {
            float capA = Capacity(netA);
            float capB = Capacity(netB);
            if (capA <= 0f || capB <= 0f)
            {
                return;
            }
            float storedA = netA.WaterStorage;
            float storedB = netB.WaterStorage;
            float move = (storedA * capB - storedB * capA) / (capA + capB) * Damping;
            if (move > MinTransfer)
            {
                Transfer(netA, netB, move);
            }
            else if (move < -MinTransfer)
            {
                Transfer(netB, netA, -move);
            }
        }

        private static float Capacity(PlumbingNet net)
        {
            float cap = 0f;
            List<CompWaterStorage> towers = net.WaterTowers;
            for (int i = 0; i < towers.Count; i++)
            {
                cap += towers[i].WaterStorage + towers[i].space;
            }
            return cap;
        }

        private static void Transfer(PlumbingNet from, PlumbingNet to, float amount)
        {
            if (!from.PullWater(amount, out ContaminationLevel _))
            {
                return;
            }
            float leftover = to.PushWater(amount);
            if (leftover > 0f)
            {
                from.PushWater(leftover);
            }
        }
    }
}
