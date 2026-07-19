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
            this.FailOn(() => Stairs == null || !Stairs.HasAnyLink);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(Stairs?.ClimbTicksFor(pawn) ?? ClimbTicks, TargetIndex.A);
            climb.WithProgressBarToilDelay(TargetIndex.A);
            yield return climb;
            Toil transfer = ToilMaker.MakeToil("AB_Transfer");
            transfer.initAction = DoTransfer;
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }

        private void DoTransfer()
        {
            Building_ABStairs entry = Stairs;
            Building_ABStairs final = job.targetC.Thing as Building_ABStairs;
            Building_ABStairs dest = StairTransfer.ResolveNextDest(entry, final);
            if (dest == null)
            {
                return;
            }
            StairTransfer.Transfer(pawn, entry, CarriedIntent.Auto, dest, final);
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
        /// <summary>The next hop's exit for a ride: toward the final destination
        /// when one is set (elevator chains), otherwise whichever single link the
        /// entry has. Null when no hop is possible.</summary>
        public static Building_ABStairs ResolveNextDest(Building_ABStairs entry, Building_ABStairs final)
        {
            if (entry == null)
            {
                return null;
            }
            if (final != null && !final.Destroyed && final.Spawned && final.Map != entry.Map)
            {
                Map cur = entry.Map;
                LevelComp comp = cur.Levels();
                int step = Math.Sign(final.Map.Level() - cur.Level());
                Map nextMap = step > 0 ? comp?.upperMap : comp?.lowerMap;
                return nextMap != null ? entry.CounterpartTowards(nextMap) : null;
            }
            return entry.Counterpart ?? entry.SecondCounterpart;
        }

        public static void Transfer(Pawn p, Building_ABStairs stairs, CarriedIntent intent = CarriedIntent.Auto,
            Building_ABStairs explicitDest = null, Building_ABStairs rideFinal = null)
        {
            Building_ABStairs dest = explicitDest ?? stairs?.Counterpart ?? stairs?.SecondCounterpart;
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
                PullFollowers(p, stairs, sourceMap, dest);
                ContinueRide(p, dest, rideFinal, targetMap);
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

        /// <summary>Elevator chain continuation: when the ride's final destination
        /// lies beyond the map just arrived on, immediately board the arrival car
        /// toward it. The onward job re-runs the short climb and transfers again.</summary>
        private static void ContinueRide(Pawn p, Building_ABStairs arrivalCar, Building_ABStairs rideFinal, Map arrivedOn)
        {
            try
            {
                if (rideFinal == null || rideFinal.Destroyed || !rideFinal.Spawned
                    || rideFinal.Map == arrivedOn || p == null || !p.Spawned || p.Dead)
                {
                    return;
                }
                Building_ABStairs next = ResolveNextDest(arrivalCar, rideFinal);
                if (next == null)
                {
                    return;
                }
                Job onward = JobMaker.MakeJob(ABDefOf.AB_UseStairs, arrivalCar);
                onward.targetC = rideFinal;
                p.jobs?.StartJob(onward, JobCondition.InterruptForced);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "elevator ride continuation");
            }
        }

        private const float FollowPullRadius = 12f;

        /// <summary>Conservative pet follow (T7 #6): when a colonist transfers,
        /// their nearby obedient followers (master set, follow enabled, not a pen
        /// animal) take the same stairs after them. Pets never cross on their own
        /// initiative beyond this and the hungry-food redirect.</summary>
        private static void PullFollowers(Pawn master, Building_ABStairs entry, Map sourceMap, Building_ABStairs dest)
        {
            try
            {
                if (master == null || !master.IsColonistPlayerControlled
                    || sourceMap == null || sourceMap.Disposed
                    || entry == null || !entry.Spawned)
                {
                    return;
                }
                List<Pawn> pawns = sourceMap.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn a = pawns[i];
                    if (!a.RaceProps.Animal || a.Downed || a.Dead)
                    {
                        continue;
                    }
                    Pawn_PlayerSettings ps = a.playerSettings;
                    if (ps == null || ps.Master != master
                        || (!ps.followDrafted && !ps.followFieldwork))
                    {
                        continue;
                    }
                    if (AnimalPenUtility.NeedsToBeManagedByRope(a))
                    {
                        continue;
                    }
                    if ((a.Position - entry.Position).LengthHorizontalSquared
                        > FollowPullRadius * FollowPullRadius)
                    {
                        continue;
                    }
                    if (a.CurJobDef == ABDefOf.AB_UseStairs
                        || !a.CanReach(entry, PathEndMode.Touch, Danger.Deadly))
                    {
                        continue;
                    }
                    Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, entry);
                    // Mirror the master's hop so a two-link elevator car is not ambiguous.
                    job.targetC = dest;
                    a.jobs?.StartJob(job, JobCondition.InterruptForced);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "pet follow through stairs");
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
