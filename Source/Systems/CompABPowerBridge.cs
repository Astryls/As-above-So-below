using System;
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
                }
            }
            else if (delta < -0.5f)
            {
                float amount = Mathf.Min(-delta, StoredEnergy, other.Props.storedEnergyMax - other.StoredEnergy);
                if (amount > 0f)
                {
                    DrawPower(amount);
                    other.AddEnergy(amount);
                }
            }
        }
    }
}
