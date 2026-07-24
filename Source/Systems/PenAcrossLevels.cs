using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Penned-animal parity (2026-07-24): "take to pen" works across levels.
    /// Vanilla's WorkGiver_TakeToPen searches pen markers on the ANIMAL's map
    /// only, so an unpenned animal on a level with no (accepting) pen stood
    /// around forever even when a pen one level away had space.
    ///
    /// Policy (locked): automatic rehoming picks a cross-level pen ONLY when
    /// no local pen accepts the animal - vanilla stays in charge of every
    /// same-level decision, and settled animals (enclosed current pen) are
    /// never moved by us. Player-forced orders ride the same giver via the
    /// prioritized-work path.
    ///
    /// Flow: the handler ropes the animal, leads it to the stairwell (roped
    /// animals follow their roper natively), both transfer, and the standard
    /// pending-order replay ropes it onward into the destination pen with
    /// vanilla's own RopeToPen job. Pen food/grazing stays per-level.
    /// </summary>
    public class WorkGiver_ABTakeToPenAcrossLevels : WorkGiver_Scanner
    {
        private static readonly ABPawnCooldown probeCooldown = new ABPawnCooldown();

        private static int ProbeCooldownTicks => ABMod.Settings?.jobEmptyScanCooldown ?? 450;

        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Pawn);

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return true;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelWork)
            {
                return true;
            }
            return pawn?.Map == null || !pawn.Map.TryLinkedLevels(out _);
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            try
            {
                if (!(t is Pawn animal) || !animal.IsAnimal || animal.Faction != pawn.Faction
                    || animal.MentalStateDef != null || !animal.Spawned || animal.Map != pawn.Map)
                {
                    return null;
                }
                if (!AnimalPenUtility.NeedsToBeManagedByRope(animal))
                {
                    return null;
                }
                if (animal.roping != null && animal.roping.IsRopedByPawn
                    && animal.roping.RopedByPawn != pawn)
                {
                    return null;
                }
                if (animal.Position.IsForbidden(pawn)
                    || t.Map.designationManager.DesignationOn(t, DesignationDefOf.ReleaseAnimalToWild) != null)
                {
                    return null;
                }
                // Settled in an enclosed pen: never ours to second-guess.
                CompAnimalPenMarker current = AnimalPenUtility.GetCurrentPenOf(animal, allowUnenclosedPens: false);
                if (current != null && current.PenState.Enclosed)
                {
                    return null;
                }
                if (!forced && !probeCooldown.Ready(animal, Find.TickManager.TicksGame))
                {
                    return null;
                }
                if (!pawn.CanReserve(animal, 1, -1, null, forced)
                    || !WorkGiver_InteractAnimal.CanInteractWithAnimal(pawn, animal, out string _,
                        forced, canInteractWhileSleeping: true, ignoreSkillRequirements: true,
                        canInteractWhileRoaming: true))
                {
                    return null;
                }
                // A local pen accepting the animal keeps this vanilla's business.
                if (AnimalPenUtility.GetPenAnimalShouldBeTakenTo(pawn, animal, out string _,
                        forced, canInteractWhileSleeping: true, allowUnenclosedPens: false,
                        ignoreSkillRequirements: true, RopingPriority.Closest) != null)
                {
                    return null;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return null;
                }
                Job job = TryCrossPenJob(pawn, animal, comp.upperMap, forced)
                    ?? TryCrossPenJob(pawn, animal, comp.lowerMap, forced);
                if (job == null && !forced)
                {
                    probeCooldown.ChargeUntil(animal, Find.TickManager.TicksGame + ProbeCooldownTicks);
                }
                return job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level take to pen");
                return null;
            }
        }

        /// <summary>Vanilla's own pen pick, probed with BOTH the handler and the
        /// animal virtually standing at the stairwell exit on the target level -
        /// accept rules, enclosure, and reachability all evaluate exactly as
        /// they will after the real transfer.</summary>
        private static Job TryCrossPenJob(Pawn pawn, Pawn animal, Map target, bool forced)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(pawn, target);
            Building_ABStairs exit = entry?.CounterpartTowards(target);
            if (exit == null)
            {
                return null;
            }
            CompAnimalPenMarker marker = null;
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token pawnToken))
            {
                return null;
            }
            try
            {
                if (ABVirtualPosition.TrySwap(animal, target, exit.Position, out ABVirtualPosition.Token animalToken))
                {
                    try
                    {
                        marker = AnimalPenUtility.GetPenAnimalShouldBeTakenTo(pawn, animal, out string _,
                            forced, canInteractWhileSleeping: true, allowUnenclosedPens: false,
                            ignoreSkillRequirements: true, RopingPriority.Closest);
                    }
                    finally
                    {
                        ABVirtualPosition.Restore(animal, animalToken);
                    }
                }
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, pawnToken);
            }
            if (marker == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_TakeToPenAcrossLevels, animal, entry);
            job.targetC = exit;
            job.count = 1;
            return job;
        }
    }

    /// <summary>
    /// Rope the animal, lead it to the stairs (roped animals follow natively),
    /// transfer both, and hand off to vanilla RopeToPen on the far side via the
    /// standard pending-order replay. Rope state is dropped for the transfer
    /// and re-established by the vanilla job on arrival.
    /// </summary>
    public class JobDriver_ABTakeToPenAcrossLevels : JobDriver
    {
        private Pawn Animal => job.GetTarget(TargetIndex.A).Thing as Pawn;

        private Building_ABStairs Entry => job.GetTarget(TargetIndex.B).Thing as Building_ABStairs;

        private Building_ABStairs FinalExit => job.GetTarget(TargetIndex.C).Thing as Building_ABStairs;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Animal, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnDespawnedOrNull(TargetIndex.B);
            this.FailOn(() => Animal == null || Animal.Dead || Animal.MentalStateDef != null
                || Entry == null || !Entry.HasAnyLink
                || (Animal.roping != null && Animal.roping.IsRopedByPawn
                    && Animal.roping.RopedByPawn != pawn));
            // Door parity: forbid flips abort the trip.
            this.FailOn(() => Entry != null
                && (Entry.EndForbiddenFor(pawn)
                    || (FinalExit != null && FinalExit.EndForbiddenFor(pawn))));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil rope = ToilMaker.MakeToil("AB_RopeAnimal");
            rope.initAction = delegate
            {
                Pawn animal = Animal;
                if (animal != null && (animal.roping == null || !animal.roping.IsRopedByPawn))
                {
                    pawn.roping.RopePawn(animal);
                }
            };
            rope.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return rope;
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(Entry?.ClimbTicksFor(pawn) ?? Building_ABStairs.BaseClimbTicks, TargetIndex.B);
            climb.WithProgressBarToilDelay(TargetIndex.B);
            yield return climb;
            Toil transfer = ToilMaker.MakeToil("AB_TransferWithAnimal");
            transfer.initAction = DoTransfer;
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }

        private void DoTransfer()
        {
            Pawn animal = Animal;
            Building_ABStairs entry = Entry;
            Building_ABStairs final = FinalExit;
            if (animal == null || entry == null || !entry.Spawned)
            {
                return;
            }
            Building_ABStairs dest = StairTransfer.ResolveNextDest(entry, final);
            if (dest == null)
            {
                return;
            }
            Pawn handler = pawn;
            Pawn animalRef = animal;
            // The far side finishes with vanilla's own RopeToPen via the
            // standard idle-gated replay; re-resolution happens there because
            // pen states can change during the climb.
            ABPendingOrders.Set(pawn, dest.Map, delegate
            {
                TryLocalPenJob(handler, animalRef);
            });
            handler.roping?.DropRope(animal);
            StairTransfer.Transfer(animal, entry, CarriedIntent.Auto, dest, final);
            StairTransfer.Transfer(handler, entry, CarriedIntent.Auto, dest, final);
        }

        internal static void TryLocalPenJob(Pawn handler, Pawn animal)
        {
            try
            {
                if (handler == null || animal == null || handler.Dead || animal.Dead
                    || !handler.Spawned || !animal.Spawned || handler.Map != animal.Map)
                {
                    return;
                }
                CompAnimalPenMarker marker = AnimalPenUtility.GetPenAnimalShouldBeTakenTo(
                    handler, animal, out string _, forced: false,
                    canInteractWhileSleeping: true, allowUnenclosedPens: false,
                    ignoreSkillRequirements: true, RopingPriority.Closest);
                if (marker == null)
                {
                    return;
                }
                Job job = WorkGiver_TakeToPen.MakeJob(handler, animal, marker,
                    allowUnenclosedPens: false, RopingPriority.Closest, out string _);
                if (job != null)
                {
                    handler.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level pen handoff");
            }
        }
    }
}
