using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Walk to the stairwell, climb for a moment, then transfer to the linked
    /// stairwell on the other level. On any failure the pawn is put back safely.
    /// </summary>
    public class JobDriver_UseStairs : JobDriver
    {
        private const int ClimbTicks = 90;

        private Building_ABStairs Stairs => job.GetTarget(TargetIndex.A).Thing as Building_ABStairs;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // Stairs are shared infrastructure; any number of pawns may use them.
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Stairs == null || Stairs.Counterpart == null);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(ClimbTicks, TargetIndex.A);
            climb.WithProgressBarToilDelay(TargetIndex.A);
            yield return climb;
            Toil transfer = ToilMaker.MakeToil("AB_Transfer");
            transfer.initAction = DoTransfer;
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }

        private void DoTransfer()
        {
            StairTransfer.Transfer(pawn, Stairs);
        }
    }

    /// <summary>What should happen to carried pawn cargo after a transfer:
    /// continue as a rescue, as a capture, or infer from context.</summary>
    public enum CarriedIntent
    {
        Auto,
        Rescue,
        Capture
    }

    /// <summary>Shared pawn transfer through a linked stairwell, with carried
    /// things riding along and a guarded recovery respawn on failure. Used by the
    /// use-stairs job and the cross-level hauling, rescue, and capture jobs.</summary>
    internal static class StairTransfer
    {
        public static void Transfer(Pawn p, Building_ABStairs stairs, CarriedIntent intent = CarriedIntent.Auto)
        {
            Building_ABStairs dest = stairs?.Counterpart;
            if (p == null || dest == null || !dest.Spawned)
            {
                return;
            }
            Map sourceMap = stairs.Map;
            IntVec3 sourcePos = stairs.Position;
            Thing carried = null;
            try
            {
                Map targetMap = dest.Map;
                IntVec3 landing = dest.Position;
                bool drafted = p.Drafted;
                // Despawning a pawn ends its job, and job cleanup DROPS the carried
                // thing at the pawn's feet. Detach it first so nothing can drop, and
                // hand it back after the transfer.
                carried = p.carryTracker?.CarriedThing;
                if (carried != null)
                {
                    p.carryTracker.innerContainer.Remove(carried);
                }
                p.DeSpawn();
                IntVec3 cell = landing.Standable(targetMap) ? landing : CellFinder.StandableCellNear(landing, targetMap, 4f);
                if (!cell.IsValid)
                {
                    cell = landing;
                }
                GenSpawn.Spawn(p, cell, targetMap);
                if (carried != null && !carried.Destroyed)
                {
                    if (p.carryTracker == null || !p.carryTracker.TryStartCarry(carried))
                    {
                        GenPlace.TryPlaceThing(carried, cell, targetMap, ThingPlaceMode.Near);
                    }
                    carried = null;
                }
                if (drafted && p.drafter != null)
                {
                    p.drafter.Drafted = true;
                }
                FinishCarriedDelivery(p, intent);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "stair transfer");
                // fallthrough to recovery below
                if (!p.Spawned && !p.Destroyed && !p.Dead)
                {
                    GenSpawn.Spawn(p, sourcePos, sourceMap);
                }
                if (carried != null && !carried.Destroyed && carried.holdingOwner == null && !carried.Spawned)
                {
                    GenPlace.TryPlaceThing(carried, sourcePos, sourceMap, ThingPlaceMode.Near);
                }
            }
        }

        /// <summary>Deterministically finish a cargo delivery after arrival.
        /// Vanilla's carried-thing storing lives inside JobGiver_Work's scanner
        /// loop, which is gated by WorkGiver_HaulGeneral.ShouldSkip - and that
        /// checks the map's haulables LISTER, which never contains carried
        /// (unspawned) things. On a level with no other haul work (a fresh
        /// rooftop taking its first chunk dump) the whole giver is skipped and
        /// the cargo is never stored: the pawn wanders off with it and drops it
        /// at random. So we queue the store job ourselves; if no storage accepts
        /// it anymore (filled up mid-carry), drop it at the exit so it lands as
        /// a normal local haulable instead of riding around in someone's arms.</summary>
        private static void FinishCarriedDelivery(Pawn p, CarriedIntent intent)
        {
            try
            {
                Thing carried = p.carryTracker?.CarriedThing;
                if (carried == null || !p.IsColonistPlayerControlled || p.Drafted || p.GetLord() != null)
                {
                    return;
                }
                if (carried is Pawn victim)
                {
                    // Pawn cargo: land the victim first so vanilla mechanics
                    // apply, then continue the errand deterministically.
                    if (!p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out Thing _)
                        || !victim.Spawned || victim.Dead)
                    {
                        return;
                    }
                    Building_Bed bed = null;
                    JobDef continuation = null;
                    if (intent == CarriedIntent.Capture)
                    {
                        bed = RestUtility.FindBedFor(victim, p, checkSocialProperness: false,
                            ignoreOtherReservations: false, GuestStatus.Prisoner);
                        continuation = JobDefOf.Capture;
                    }
                    else if (intent == CarriedIntent.Rescue
                        || (victim.Downed && victim.Faction == p.Faction))
                    {
                        bed = RestUtility.FindBedFor(victim, p, checkSocialProperness: false);
                        continuation = JobDefOf.Rescue;
                    }
                    if (bed != null && continuation != null)
                    {
                        Job cont = JobMaker.MakeJob(continuation, victim, bed);
                        cont.count = 1;
                        p.jobs?.jobQueue?.EnqueueFirst(cont, JobTag.Misc);
                    }
                    return;
                }
                Job store = HaulAIUtility.HaulToStorageJob(p, carried, forced: false);
                if (store != null)
                {
                    p.jobs?.jobQueue?.EnqueueFirst(store, JobTag.Misc);
                }
                else
                {
                    p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out Thing _);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "carried delivery finish");
            }
        }
    }
}
