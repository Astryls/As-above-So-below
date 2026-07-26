using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// One-big-map storage-priority parity (user request 2026-07-25: "hauling
    /// should respect levels like critical across map levels"). Fires ABOVE
    /// vanilla HaulGeneral (priorityInType 20 &gt; 15) but ONLY when a linked
    /// level offers storage of a STRICTLY HIGHER priority than the best the item
    /// could reach on its own level - a Critical stockpile below vs a Normal one
    /// here, or remote storage for an item with nowhere local to go. That pulls
    /// the stack across at full hauling urgency, exactly as a same-map move to
    /// that tier would, instead of leaving high-priority stockpiles on other
    /// levels starved behind every local haul (the parity break the user hit:
    /// a Critical basement larder that never filled while any surface hauling
    /// remained).
    ///
    /// Equal-tier (or lower) cross-level moves are NOT hauled at all: a
    /// same-priority stockpile on another level is never a reason to walk the
    /// stairs (user directive 2026-07-26). TargetLevelFor discards them at the
    /// source, so neither this giver nor the low-priority ones ferry a stack
    /// between two same-priority stores on different levels. Only explicit
    /// player intent (Allow Tool Haul Urgently) still crosses on equal tier.
    /// The verdict is monotone - each elevated move strictly raises the stack's
    /// stored tier, bounded by Critical - so there is no oscillation.
    ///
    /// Handles both single and bulk pawns via the shared job builder, which
    /// auto-selects a Pick Up And Haul / Hauler's Dream bulk load when the pawn
    /// carries the inventory-haul comp, so the low-priority single/bulk split
    /// does not need to be mirrored here.
    /// </summary>
    public class WorkGiver_ABHaulToBetterLevelStorage : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ABGuard.On(ABGuard.Logistics)
                // Never issue a cross-level job while probing another map: the
                // verdict would reference the virtual position.
                || CrossLevelWork.VirtualScanActive
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !pawn.Map.ConnectedToOtherLevel()
                // Battery-driven workers (Misc. Robots, mechs) stay near home
                // when low; their recharge AI will want them shortly.
                || CrossLevelWork.LowPowerWorker(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _,
                       ignorePins: false, out int _, out bool _, out bool beatsLocal) != null
                   && beatsLocal;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            Map target = CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs stairs,
                ignorePins: false, out int allowedCount, out bool _, out bool beatsLocal);
            if (target == null || stairs == null || !beatsLocal)
            {
                return null;
            }
            // beatsLocal is storage-only (never a demand verdict), so no
            // in-flight ledger claim is needed. allowedCount clamps the carry
            // to what the destination can absorb (no-space parity).
            return CrossLevelHaulJob.Build(pawn, t, target, stairs, allowedCount: allowedCount);
        }
    }
}
