using System;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Stairwells transmit power and bridge the two levels' power nets. Every 30
    /// ticks the pair's master side (lower map id) balances the nets: the deficit
    /// side outputs up to 90% of the other side's surplus, mirrored as a draw on
    /// the surplus side. Damped to avoid flicker; both outputs decay to zero when
    /// unlinked or balanced. Kill switch: power.
    /// </summary>
    public class CompABPowerBridge : CompPowerPlant
    {
        private const int UpdateInterval = 30;

        private const float TransferDamping = 0.9f;

        protected override float DesiredPowerOutput => PowerOutput;

        public override void CompTick()
        {
            base.CompTick();
            if (!ABGuard.On(ABGuard.Power) || parent.IsHashIntervalTick(UpdateInterval) == false)
            {
                return;
            }
            try
            {
                BalancePair();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Power, e, "power bridge");
                PowerOutput = 0f;
            }
        }

        private void BalancePair()
        {
            Building_ABStairs stairs = parent as Building_ABStairs;
            Building_ABStairs counterpart = stairs?.Counterpart;
            CompABPowerBridge other = counterpart?.GetComp<CompABPowerBridge>();
            if (other == null)
            {
                PowerOutput = 0f;
                return;
            }
            // One side runs the math for the pair.
            if (stairs.Map.uniqueID > counterpart.Map.uniqueID)
            {
                return;
            }
            PowerNet mine = PowerNet;
            PowerNet theirs = other.PowerNet;
            if (mine == null || theirs == null || mine == theirs)
            {
                PowerOutput = 0f;
                other.PowerOutput = 0f;
                return;
            }
            float myGain = mine.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick;
            float theirGain = theirs.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick;
            // Exclude our own current contribution so the calculation converges
            // instead of feeding back.
            myGain -= PowerOutput;
            theirGain -= other.PowerOutput;
            float transfer = 0f;
            if (myGain < 0f && theirGain > 0f)
            {
                transfer = Math.Min(-myGain, theirGain) * TransferDamping;
                PowerOutput = transfer;
                other.PowerOutput = -transfer;
            }
            else if (theirGain < 0f && myGain > 0f)
            {
                transfer = Math.Min(-theirGain, myGain) * TransferDamping;
                PowerOutput = -transfer;
                other.PowerOutput = transfer;
            }
            else
            {
                PowerOutput = 0f;
                other.PowerOutput = 0f;
            }
        }
    }
}
