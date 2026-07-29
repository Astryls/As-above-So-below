using System;
using System.Collections.Generic;
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

        /// <summary>
        /// One option PER far end, not one option total.
        ///
        /// The original override was GetSingleOptionFor, which structurally cannot offer
        /// two directions - and the elevator has two or more ends. Its "best counterpart"
        /// fallback returned the FIRST end, and TryEstablish adds ends bottom-up, so a
        /// surface elevator's first end is always the BASEMENT: the float menu offered
        /// "go down" and nothing else. Reported as "the elevator only works going down" -
        /// the up transit was fine; the player was never given a way to order it.
        /// </summary>
        public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            try
            {
                if (!ABGuard.On(ABGuard.Movement))
                {
                    return options;
                }
                Building_ABStairs2 stairs = clickedThing as Building_ABStairs2;
                if (stairs == null || !stairs.Spawned)
                {
                    return options;
                }
                Pawn pawn = context.FirstSelectedPawn;
                if (pawn == null || pawn.Map != stairs.Map)
                {
                    return options;
                }
                ABBandMap bands = ABBands.CompOf(stairs.Map);
                if (bands == null || !bands.Banded)
                {
                    return options;
                }
                bool reachable = pawn.CanReach(stairs, PathEndMode.OnCell, Danger.Deadly);
                int myBand = bands.BandOf(stairs.Position);
                bool multi = stairs.Counterparts.Count > 1;

                for (int i = 0; i < stairs.Counterparts.Count; i++)
                {
                    Building_ABStairs2 far = stairs.Counterparts[i];
                    if (far == null || !far.Spawned)
                    {
                        continue;
                    }
                    int delta = bands.BandOf(far.Position) - myBand;
                    string label = delta > 0 ? "AB_GoUp".Translate() : "AB_GoDown".Translate();
                    if (multi)
                    {
                        // Two ends can share a direction (a sky elevator goes down to the
                        // surface AND down to the basement) - the level disambiguates.
                        label = "AB_GoToLevel".Translate(label,
                            ABBands.LevelOf(stairs.Map, far.Position));
                    }
                    if (!reachable)
                    {
                        options.Add(new FloatMenuOption(label + " (" + "NoPath".Translate() + ")", null));
                        continue;
                    }
                    IntVec3 dest = far.Position;
                    options.Add(FloatMenuUtility.DecoratePrioritizedTask(
                        new FloatMenuOption(label, delegate
                        {
                            Job job = JobMaker.MakeJob(JobDefOf.Goto, dest);
                            job.locomotionUrgency = LocomotionUrgency.Jog;
                            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                        }), pawn, dest));
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 stairs float menu");
            }
            return options;
        }
    }
}
