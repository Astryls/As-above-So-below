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

        /// <summary>Multi-hop chain haul (AB_HaulChainAcrossLevels): this hop just
        /// carries the item ONE gap toward a far destination; on landing it is
        /// stored (if this level accepts) or set down and re-designated so the
        /// ordinary single-hop / far givers carry it the rest of the way - each
        /// leg is then a normal, well-tested haul rather than a re-entrant
        /// carried-item continuation. The plain AB_HaulAcrossLevels job
        /// (Chain == false) is the unchanged single-hop haul.</summary>
        private bool Chain => job.def == ABDefOf.AB_HaulChainAcrossLevels;

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
                if (Chain)
                {
                    // On landing, decide store-here vs next hop (greedy, stops at
                    // the first accepting level). Set BEFORE the transfer so it is
                    // armed when StairTransfer runs the pending-order callback.
                    Thing item = job.GetTarget(TargetIndex.A).Thing;
                    Map landMap = dest?.Map;
                    if (item != null && landMap != null)
                    {
                        ABPendingOrders.Set(pawn, landMap,
                            delegate { CrossLevelHaulChain.OnArrive(pawn, item); });
                    }
                }
                StairTransfer.Transfer(pawn, Stairs, CarriedIntent.Auto, dest);
            };
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }
    }
}
