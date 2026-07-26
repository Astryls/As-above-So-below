using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bulk cross-level haul (Pick Up And Haul / Hauler's Dream present): scoop
    /// the queued stacks into inventory until near capacity, walk to the
    /// stairwell, climb, and transfer to the linked level. Inventory rides the
    /// despawn automatically; on arrival the host mod's own unloader stores
    /// everything, so one trip moves a whole load instead of one stack.
    /// Falls back to nothing gracefully - if the pawn is already too encumbered
    /// to lift anything, the job just ends.
    /// </summary>
    public class JobDriver_ABBulkHaulAcrossLevels : JobDriver
    {
        private const int ClimbTicks = Building_ABStairs.BaseClimbTicks;

        /// <summary>Matches CrossLevelHaulJob's gather cap so the driver stops
        /// scooping at the same headroom the giver planned for.</summary>
        private const float GatherEncumbranceCap = 0.8f;

        private bool hauledAnything;

        /// <summary>Whether the MOST RECENT pickup toil actually loaded
        /// something. Once a stack is too heavy to add (inventory near
        /// capacity), continuing to walk the rest of the queue lifts nothing
        /// and just wastes a trip, so gathering stops the moment a visit comes
        /// up empty instead of trudging to every queued stack.</summary>
        private bool lastPickupOk;

        private Building_ABStairs Stairs => job.GetTarget(TargetIndex.A).Thing as Building_ABStairs;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (ABGiddyUpCompat.BlockForMount(pawn))
            {
                return false;
            }
            // Best-effort: reserve as many cargo stacks as we can; the ones we
            // miss are simply skipped at pickup time. Stairs are shared.
            if (job.targetQueueB != null)
            {
                pawn.ReserveAsManyAsPossible(job.targetQueueB, job);
            }
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Stairs == null || !Stairs.HasAnyLink);

            Toil next = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
            Toil gotoItem = Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch);
            gotoItem.FailOnDespawnedNullOrForbidden(TargetIndex.B);
            Toil pickup = MakePickupToil();
            Toil travel = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil climb = MakeClimbToil();
            Toil transfer = MakeTransferToil();
            Toil done = ToilMaker.MakeToil("AB_BulkHaulDone");
            done.initAction = delegate { };
            done.defaultCompleteMode = ToilCompleteMode.Instant;

            yield return next;
            yield return gotoItem;
            yield return pickup;
            // Keep gathering same-destination cargo until the queue is empty,
            // the pawn is near capacity, OR the last visited stack could not be
            // lifted (lastPickupOk) - the latter stops the pawn from walking to
            // every remaining stack when its inventory is already full of heavy
            // cargo it cannot add to.
            yield return Toils_Jump.JumpIf(next, () => lastPickupOk
                && !job.targetQueueB.NullOrEmpty()
                && MassUtility.EncumbrancePercent(pawn) < GatherEncumbranceCap);
            // Picked up nothing (already loaded down): bail cleanly, no trip.
            yield return Toils_Jump.JumpIf(done, () => !hauledAnything);
            yield return travel;
            yield return climb;
            yield return transfer;
            yield return done;
        }

        private Toil MakePickupToil()
        {
            Toil toil = ToilMaker.MakeToil("AB_BulkHaulPickup");
            toil.initAction = delegate
            {
                lastPickupOk = false;
                Thing t = job.GetTarget(TargetIndex.B).Thing;
                if (t == null || !t.Spawned || pawn.inventory == null)
                {
                    return;
                }
                int room = MassUtility.CountToPickUpUntilOverEncumbered(pawn, t);
                int count = Mathf.Min(t.stackCount, room);
                if (count <= 0)
                {
                    return;
                }
                Thing split = t.SplitOff(count);
                if (split == null)
                {
                    return;
                }
                ThingOwner inv = pawn.inventory.innerContainer;
                bool canMerge = ContainsDef(inv, split.def);
                if (!inv.TryAdd(split, canMerge))
                {
                    if (!split.Destroyed && !split.Spawned)
                    {
                        GenPlace.TryPlaceThing(split, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                    }
                    return;
                }
                // Register whatever stack now holds this def so the host mod's
                // unloader will store it (a merge can destroy the split Thing).
                Thing reg = split.Destroyed ? FirstOfDef(inv, split.def) : split;
                ABInventoryHaulBridge.Register(pawn, reg);
                hauledAnything = true;
                lastPickupOk = true;
            };
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            return toil;
        }

        private Toil MakeClimbToil()
        {
            Toil climb = Toils_General.Wait(Stairs?.ClimbTicksFor(pawn) ?? ClimbTicks, TargetIndex.A);
            climb.WithProgressBarToilDelay(TargetIndex.A);
            climb.AddPreInitAction(delegate { ClimbAnimation.StartClimb(pawn, Stairs); });
            climb.AddFinishAction(delegate { ClimbAnimation.Stop(pawn); });
            return climb;
        }

        private Toil MakeTransferToil()
        {
            Toil transfer = ToilMaker.MakeToil("AB_BulkHaulTransfer");
            transfer.initAction = delegate
            {
                Building_ABStairs entry = Stairs;
                Building_ABStairs dest = job.GetTarget(TargetIndex.C).Thing as Building_ABStairs;
                StairTransfer.Transfer(pawn, entry, CarriedIntent.Auto, dest);
                // Pawn now stands on the destination level with the load in
                // inventory - ask the host mod to store it there right away.
                ABInventoryHaulBridge.RequestUnload(pawn);
            };
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            return transfer;
        }

        private static bool ContainsDef(ThingOwner owner, ThingDef def)
        {
            for (int i = 0; i < owner.Count; i++)
            {
                if (owner[i]?.def == def)
                {
                    return true;
                }
            }
            return false;
        }

        private static Thing FirstOfDef(ThingOwner owner, ThingDef def)
        {
            for (int i = 0; i < owner.Count; i++)
            {
                Thing t = owner[i];
                if (t?.def == def)
                {
                    return t;
                }
            }
            return null;
        }
    }
}
