using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Stairwells bridge power between levels using the shared-tank model: each
    /// side is a small lossless battery, vanilla nets charge it with surplus and
    /// feed consumers from it (including switching them back on), and this comp
    /// equalizes the pair's stored energy every 30 ticks, master side only.
    /// A fake power plant does not work here: UpdateDesiredPowerOutput zeroes
    /// output while PowerOn is false, and a starving net hides its deficit by
    /// switching consumers off. Batteries dodge both mechanisms entirely.
    /// Kill switch: power.
    /// </summary>
    public class CompABPowerBridge : CompPowerBattery
    {
        private const int UpdateInterval = 30;

        /// <summary>True on the side that received energy in the last equalization.
        /// Only the receiving side forwards charge into its local battery bank;
        /// the producing side must never churn energy in circles.</summary>
        private bool lastFlowIn;

        public override void CompTick()
        {
            base.CompTick();
            if (!ABGuard.On(ABGuard.Power) || !parent.IsHashIntervalTick(UpdateInterval))
            {
                return;
            }
            try
            {
                Equalize();
                if (lastFlowIn)
                {
                    ForwardToLocalBank();
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Power, e, "power bridge");
            }
        }

        private void Equalize()
        {
            Building_ABStairs stairs = parent as Building_ABStairs;
            Building_ABStairs counterpart = stairs?.Counterpart;
            CompABPowerBridge other = counterpart?.GetComp<CompABPowerBridge>();
            if (other == null)
            {
                return;
            }
            // One side runs the math for the pair.
            if (stairs.Map.uniqueID > counterpart.Map.uniqueID)
            {
                return;
            }
            float total = StoredEnergy + other.StoredEnergy;
            float target = total * 0.5f;
            float delta = target - StoredEnergy;
            if (delta > 0.5f)
            {
                float amount = Mathf.Min(delta, other.StoredEnergy, Props.storedEnergyMax - StoredEnergy);
                if (amount > 0f)
                {
                    other.DrawPower(amount);
                    AddEnergy(amount);
                    lastFlowIn = true;
                    other.lastFlowIn = false;
                }
            }
            else if (delta < -0.5f)
            {
                float amount = Mathf.Min(-delta, StoredEnergy, other.Props.storedEnergyMax - other.StoredEnergy);
                if (amount > 0f)
                {
                    DrawPower(amount);
                    other.AddEnergy(amount);
                    lastFlowIn = false;
                    other.lastFlowIn = true;
                }
            }
        }

        /// <summary>Vanilla nets never move charge battery to battery, so a bank on
        /// the receiving level would stay empty forever. The receiving bridge side
        /// pushes its charge into the local net's ordinary batteries at their own
        /// charge efficiency (bridge batteries excluded).</summary>
        private void ForwardToLocalBank()
        {
            PowerNet net = PowerNet;
            if (net == null)
            {
                return;
            }
            float available = StoredEnergy;
            if (available <= 0.5f)
            {
                return;
            }
            List<CompPowerBattery> batteries = net.batteryComps;
            for (int i = 0; i < batteries.Count; i++)
            {
                CompPowerBattery battery = batteries[i];
                if (battery == this || battery is CompABPowerBridge)
                {
                    continue;
                }
                float space = battery.Props.storedEnergyMax - battery.StoredEnergy;
                if (space <= 0f)
                {
                    continue;
                }
                float efficiency = Mathf.Max(battery.Props.efficiency, 0.01f);
                float draw = Mathf.Min(available, space / efficiency);
                if (draw <= 0f)
                {
                    continue;
                }
                DrawPower(draw);
                battery.AddEnergy(draw * efficiency);
                available -= draw;
                if (available <= 0.5f)
                {
                    break;
                }
            }
        }
    }
}
