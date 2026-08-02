using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Makes a breaker's switch actually recompute the network it gates.
    ///
    /// ⚠ FLICKING A SWITCH DOES NOT, BY ITSELF, REBUILD ANYTHING. `CompFlickable` sends a
    /// FlickedOn/FlickedOff comp signal and stops there; whether a network notices is
    /// entirely up to whoever owns that network - and the four hosts disagree:
    ///
    ///   vanilla power  - `Building_PowerSwitch` overrides TransmitsPowerNow and calls
    ///                    Notfiy_TransmitterTransmitsPowerNowChanged. Handled by our
    ///                    Building_ABPowerBreaker subclass.
    ///   VEF PipeSystem - `CompPipeValve.ReceiveCompSignal` un/registers the connector.
    ///                    Handled because our breakers carry CompProperties_PipeValve.
    ///   Bad Hygiene    - `CompPipe.ReceiveCompSignal` rebuilds UNCONDITIONALLY. Free.
    ///   Rimefeller,
    ///   Rimatomics     - the same code, but gated on `base.parent is Building_Valve`.
    ///                    Our riser is not one, so NOTHING HAPPENS. This comp is that gap.
    ///
    /// That single `is Building_Valve` difference is why Bad Hygiene water linked correctly
    /// while Rimefeller oil did not: the link logic was identical and correct, and only the
    /// invalidation was missing.
    ///
    /// Harmless on every riser, so it lives on the shared base rather than being sprinkled
    /// across eight defs: junctions have no CompFlickable and therefore never emit the
    /// signal, and PokeRebuild no-ops when no Dubwise host owns the thing.
    /// </summary>
    public class CompABRiserSwitch : ThingComp
    {
        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == "FlickedOn" || signal == "FlickedOff")
            {
                Patch_DubsPipes_ABRiserLink.PokeRebuild(parent);
            }
        }
    }
}
