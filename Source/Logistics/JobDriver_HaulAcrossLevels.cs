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

        /// <summary>Multi-hop chain haul (AB_HaulChainAcrossLevels): the item is
        /// carried one gap at a time and, on each landing, either stored (first
        /// accepting level) or handed to the next hop. The plain AB_HaulAcrossLevels
        /// job (Chain == false) is the unchanged single-hop haul.</summary>
        private bool Chain => job.def == ABDefOf.AB_HaulChainAcrossLevels;

        /// <summary>A chain continuation hop arrives already holding the item
        /// (StairTransfer preserves the carry), so its pickup toils are skipped.</summary>
        private bool AlreadyCarryingTarget
        {
            get
            {
                Thing t = job.GetTarget(TargetIndex.A).Thing;
                return t != null && pawn.carryTracker?.CarriedThing == t;
            }
        }

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
            if (!Chain)
            {
                // A carried chain-continuation item is off the map and already
                // committed; forbidden-status only gates the initial pickup.
                this.FailOnForbidden(TargetIndex.A);
            }

            Toil gotoStairs = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            // Continuation hops land already holding the item - skip the pickup.
            yield return Toils_Jump.JumpIf(gotoStairs, () => AlreadyCarryingTarget);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);

            yield return gotoStairs;
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
