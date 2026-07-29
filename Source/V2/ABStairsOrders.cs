using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 stairwell travel orders.
    ///
    /// Movement between bands is automatic once a destination in another band is chosen -
    /// Pawn_PathFollower.StartPath segments the trip. The gap is that the PLAYER has no way
    /// to express such a destination: only one band is visible at a time, so a
    /// right-click destination is always in the current band, and clicking the stairwell
    /// itself just walks the pawn onto that cell and stops.
    ///
    /// This provider closes that gap by offering the far end as an explicit destination. It
    /// serves DRAFTED pawns as well as undrafted, since ordering soldiers up or down is
    /// exactly when it matters most.
    /// </summary>
    public class FloatMenuOptionProvider_ABStairsTravel : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Movement))
                {
                    return null;
                }
                Building_ABStairs2 stairs = clickedThing as Building_ABStairs2;
                if (stairs == null || !stairs.Spawned)
                {
                    return null;
                }
                Pawn pawn = context.FirstSelectedPawn;
                if (pawn == null || pawn.Map != stairs.Map)
                {
                    return null;
                }
                // Multiple ends (the elevator): prefer the one on the band the player is
                // viewing - they are looking at where they want the pawn to go.
                Building_ABStairs2 far = stairs.BestCounterpartFor(
                    ABBandView.CurrentBand(stairs.Map));
                if (far == null || !far.Spawned)
                {
                    return null;
                }
                ABBandMap bands = ABBands.CompOf(stairs.Map);
                if (bands == null || !bands.Banded)
                {
                    return null;
                }
                int delta = bands.BandOf(far.Position) - bands.BandOf(stairs.Position);
                string label = delta > 0
                    ? "AB_GoUp".Translate()
                    : "AB_GoDown".Translate();

                if (!pawn.CanReach(stairs, PathEndMode.OnCell, Danger.Deadly))
                {
                    return new FloatMenuOption(label + " (" + "NoPath".Translate() + ")", null);
                }
                IntVec3 dest = far.Position;
                return FloatMenuUtility.DecoratePrioritizedTask(
                    new FloatMenuOption(label, delegate
                    {
                        Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
                        job.locomotionUrgency = LocomotionUrgency.Jog;
                        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    }), pawn, dest);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 stairs float menu");
                return null;
            }
        }
    }
}
