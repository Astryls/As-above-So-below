using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Pick up the item, walk to the stairwell, climb, and carry it through to the
    /// linked level. The job ends after the transfer; vanilla's carried-thing
    /// handling immediately finds the destination storage on the new level.
    /// </summary>
    public class JobDriver_HaulAcrossLevels : JobDriver
    {
        private const int ClimbTicks = Building_ABStairs.BaseClimbTicks;

        private Building_ABStairs Stairs => job.GetTarget(TargetIndex.B).Thing as Building_ABStairs;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (ABGiddyUpCompat.BlockForMount(pawn))
            {
                return false;
            }
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDespawnedOrNull(TargetIndex.B);
            this.FailOn(() => Stairs == null || !Stairs.HasAnyLink);
            this.FailOnForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(Stairs?.ClimbTicksFor(pawn) ?? ClimbTicks, TargetIndex.B);
            climb.WithProgressBarToilDelay(TargetIndex.B);
            climb.AddPreInitAction(delegate { ClimbAnimation.StartClimb(pawn, Stairs); });
            climb.AddFinishAction(delegate { ClimbAnimation.Stop(pawn); });
            yield return climb;
            Toil transfer = ToilMaker.MakeToil("AB_HaulTransfer");
            transfer.initAction = delegate
            {
                Building_ABStairs dest = job.GetTarget(TargetIndex.C).Thing as Building_ABStairs;
                StairTransfer.Transfer(pawn, Stairs, CarriedIntent.Auto, dest);
            };
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }
    }
}
