using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Allow Tool priority-hauling parity across levels. Allow Tool's own
    /// WorkGiver_HaulUrgently stores same-map only, so an urgently-designated
    /// stack whose better storage sits on a linked level would drop out of the
    /// urgent lane. This giver carries those stacks across FIRST - a high
    /// priorityInType puts it ahead of ordinary hauling within the Hauling work
    /// type, the closest match to Allow Tool's above-Hauling urgent work type
    /// without adding a work type that would break when Allow Tool is absent.
    /// When a bulk bridge (PUAH / Hauler's Dream) is active the trip carries a
    /// whole load of urgent cargo; otherwise it is a single carry. Inert unless
    /// Allow Tool is installed.
    /// </summary>
    public class WorkGiver_ABHaulUrgentlyAcrossLevels : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HaulableEver);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ABGuard.On(ABGuard.Logistics)
                || CrossLevelWork.VirtualScanActive
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !ABAllowToolCompat.Active
                || !pawn.Map.ConnectedToOtherLevel()
                || CrossLevelWork.LowPowerWorker(pawn)
                || !ABAllowToolCompat.AnyUrgent(pawn.Map);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return ABAllowToolCompat.IsUrgent(t)
                && CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!ABAllowToolCompat.IsUrgent(t))
            {
                return null;
            }
            Map target = CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs stairs);
            if (target == null || stairs == null)
            {
                return null;
            }
            // An urgent trip carries only urgent cargo.
            return CrossLevelHaulJob.Build(pawn, t, target, stairs, ABAllowToolCompat.IsUrgent);
        }
    }
}
