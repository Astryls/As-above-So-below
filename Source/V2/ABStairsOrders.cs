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
                    // §85.23: the verb belongs to the LINK KIND. This said "the stairs" for
                    // every def, so an elevator's right-click read "Take the stairs to level X"
                    // (field report). Same kind test ABStairAnim uses: linksAllLevels is the
                    // elevator, "Ladder" in the defName is a ladder, everything else stairs.
                    string key;
                    if (stairs.LinksAllLevels)
                    {
                        key = delta > 0 ? "AB_GoUpElevator" : "AB_GoDownElevator";
                    }
                    else if (stairs.def.defName.IndexOf("Ladder", StringComparison.Ordinal) >= 0)
                    {
                        key = delta > 0 ? "AB_GoUpLadder" : "AB_GoDownLadder";
                    }
                    else
                    {
                        key = delta > 0 ? "AB_GoUp" : "AB_GoDown";
                    }
                    string label = key.Translate();
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
                    // ⚠⚠ AIM AT THE TILE IN FRONT OF THE FAR OPENING, NOT AT THE LINK.
                    // This was `far.Position` - a cell INSIDE the far link's own footprint -
                    // and it quietly undid §85's landing rule: the pawn crossed, was set down
                    // on the front tile by LandingCell, and then this job walked it back ONTO
                    // the staircase and left it standing there. The arrival was never the
                    // bug; the destination was.
                    //
                    // ⚠ THE FALLBACK IS THE OLD BEHAVIOUR VERBATIM, and it is the ELEVATOR's
                    // normal path, not an error case: DisembarkCell returns Invalid for a
                    // non-directional link, so an elevator order still aims at the car and
                    // the pawn arrives inside it (user's call, §85). A stairs/ladder whose
                    // front tile is walled in, out of bounds or across a seam degrades to the
                    // same place it has always gone rather than refusing to travel.
                    //
                    // ⚠ THIS DOES NOT DIVERT THE ROUTE. TryGetTransit scores candidate pairs
                    // partly on the far anchor's distance to the destination, and the front
                    // tile is ONE CELL from the link the player clicked - so the intended
                    // pair still wins by the same margin `far.Position` won by.
                    IntVec3 dest = ABLinkApproach.DisembarkCell(far);
                    if (!dest.IsValid)
                    {
                        dest = far.Position;
                    }
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
