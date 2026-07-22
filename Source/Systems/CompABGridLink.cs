using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Direct cross-level power link (replaces the old shared-tank battery
    /// model). The vertical conduit joins the local net as an ordinary
    /// transmitting power trader; the master side (lower map uniqueID) reads
    /// both nets every interval and mirrors a single signed flow through the
    /// pair: PowerOutput is set positive on the receiving side and negative on
    /// the sending side, so vanilla's own PowerNetTick does all distribution,
    /// battery charging, brownout, and re-enable logic. No storage, no
    /// efficiency loss, no player-built battery required on either level.
    ///
    /// Flow model per direction:
    ///  - deficit = unmet live draw plus off-but-wanting consumers (vanilla
    ///    hides starved demand by flicking consumers off; counting wanting-on
    ///    comps lets devices power back up through the shaft).
    ///  - Deficit may be served from the far side's generation surplus AND its
    ///    stored battery energy (batteries drain naturally through the negative
    ///    gain vanilla sees on the sending net).
    ///  - Leftover generation surplus (never battery charge - no battery to
    ///    battery churn) additionally charges the far side's batteries.
    /// Both directions are computed and netted into one signed transfer.
    /// Multiple conduits between the same pair of nets converge because each
    /// master subtracts the flow other links already carry.
    /// Kill switch: power. Fail state is zero flow, vanilla untouched.
    /// </summary>
    public class CompABGridLink : CompPowerTrader
    {
        private const int UpdateInterval = 15;

        /// <summary>Ignore sub-watt noise so idle pairs stay at exactly zero.</summary>
        private const float MinFlow = 1f;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // A link is never flickable or breakdownable; it is always "on" and
            // idles at zero output until the master computes a flow.
            PowerOn = true;
        }

        /// <summary>The link reads as a conduit, not a producer or consumer.</summary>
        public override string CompInspectStringExtra()
        {
            float w = PowerOutput;
            if (w > MinFlow)
            {
                return "AB_GridLinkReceiving".Translate(w.ToString("F0"));
            }
            if (w < -MinFlow)
            {
                return "AB_GridLinkSending".Translate((-w).ToString("F0"));
            }
            return "AB_GridLinkIdle".Translate();
        }

        public override void CompTick()
        {
            base.CompTick();
            if (!parent.IsHashIntervalTick(UpdateInterval))
            {
                return;
            }
            if (!ABGuard.On(ABGuard.Power))
            {
                if (PowerOutput != 0f)
                {
                    PowerOutput = 0f;
                }
                return;
            }
            try
            {
                Recompute();
            }
            catch (Exception e)
            {
                PowerOutput = 0f;
                ABGuard.Disable(ABGuard.Power, e, "direct power link");
            }
        }

        private void Recompute()
        {
            Building_ABStairs stairs = parent as Building_ABStairs;
            CompABGridLink other = stairs?.Counterpart?.GetComp<CompABGridLink>();
            if (other == null || other.parent.Map == null)
            {
                PowerOutput = 0f;
                return;
            }
            // One side runs the math for the pair.
            if (parent.Map.uniqueID > other.parent.Map.uniqueID)
            {
                return;
            }
            PowerNet netA = PowerNet;
            PowerNet netB = other.PowerNet;
            if (netA == null || netB == null || netA == netB)
            {
                PowerOutput = 0f;
                other.PowerOutput = 0f;
                return;
            }
            Measure(netA, this, other, out float gainA, out float wantA, out float otherLinksA);
            Measure(netB, this, other, out float gainB, out float wantB, out float otherLinksB);
            float storedA = netA.CurrentStoredEnergy();
            float storedB = netB.CurrentStoredEnergy();
            // Demand already served by parallel AB links reduces what this pair
            // must carry; a negative otherLinks total is flow they export.
            float deficitA = Mathf.Max(0f, Mathf.Max(0f, -gainA) + wantA - Mathf.Max(0f, otherLinksA));
            float deficitB = Mathf.Max(0f, Mathf.Max(0f, -gainB) + wantB - Mathf.Max(0f, otherLinksB));
            float toB = FlowToward(deficitB, gainA, storedA, netB);
            float toA = FlowToward(deficitA, gainB, storedB, netA);
            float signed = toB - toA;
            if (Mathf.Abs(signed) < MinFlow)
            {
                signed = 0f;
            }
            // Negative on the sender, positive on the receiver.
            PowerOutput = -signed;
            other.PowerOutput = signed;
            if (!other.PowerOn)
            {
                other.PowerOn = true;
            }
            if (!PowerOn)
            {
                PowerOn = true;
            }
        }

        /// <summary>Watts to push toward a net with the given deficit, from a
        /// side with the given generation surplus and stored energy. Stored
        /// energy serves deficits only; leftover generation also charges the
        /// receiving side's batteries so a batteryless generator level can top
        /// up banks anywhere in the column.</summary>
        private static float FlowToward(float deficit, float senderGain, float senderStored, PowerNet receiver)
        {
            float genSurplus = Mathf.Max(0f, senderGain);
            // Stored Wd stretched over the update window, expressed in watts.
            float reserve = senderStored > 1f ? senderStored * (60000f / UpdateInterval) : 0f;
            float serve = Mathf.Min(deficit, genSurplus + reserve);
            float genLeft = Mathf.Max(0f, genSurplus - serve);
            if (genLeft > MinFlow && BatteryRoom(receiver) > 1f)
            {
                serve += genLeft;
            }
            return serve;
        }

        private static float BatteryRoom(PowerNet net)
        {
            float room = 0f;
            for (int i = 0; i < net.batteryComps.Count; i++)
            {
                CompPowerBattery b = net.batteryComps[i];
                room += b.Props.storedEnergyMax - b.StoredEnergy;
            }
            return room;
        }

        /// <summary>Reads a net's live gain (watts, all AB links excluded), the
        /// unserved demand of consumers that want power but are off, and the
        /// summed output of OTHER AB links on the net (positive = inflow other
        /// pairs already deliver).</summary>
        private static void Measure(PowerNet net, CompABGridLink self, CompABGridLink counterpart,
            out float gain, out float offWanting, out float otherLinks)
        {
            gain = 0f;
            offWanting = 0f;
            otherLinks = 0f;
            for (int i = 0; i < net.powerComps.Count; i++)
            {
                CompPowerTrader comp = net.powerComps[i];
                if (comp is CompABGridLink link)
                {
                    if (link != self && link != counterpart && link.PowerOn)
                    {
                        otherLinks += link.PowerOutput;
                    }
                    continue;
                }
                if (comp.PowerOn)
                {
                    gain += comp.PowerOutput;
                }
                else
                {
                    float draw = comp.Props.PowerConsumption;
                    if (draw > 0f && FlickUtility.WantsToBeOn(comp.parent) && !comp.parent.IsBrokenDown())
                    {
                        offWanting += draw;
                    }
                }
            }
        }
    }
}
