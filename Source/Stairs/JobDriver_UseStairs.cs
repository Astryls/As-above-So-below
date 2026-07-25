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
        private const int ClimbTicks = Building_ABStairs.BaseClimbTicks;

        private Building_ABStairs Stairs => job.GetTarget(TargetIndex.A).Thing as Building_ABStairs;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (ABGiddyUpCompat.BlockForMount(pawn))
            {
                return false;
            }
            // Stairs are shared infrastructure; any number of pawns may use them.
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => Stairs == null || !Stairs.HasAnyLink);
            // Door parity: a forbid flip on either end aborts the climb for
            // pawns that care about forbid (raiders ignore it, like doors).
            this.FailOn(() => Stairs != null
                && (Stairs.EndForbiddenFor(pawn)
                    || (job.GetTarget(TargetIndex.C).Thing is Building_ABStairs finalExit
                        && finalExit.EndForbiddenFor(pawn))));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(Stairs?.ClimbTicksFor(pawn) ?? ClimbTicks, TargetIndex.A);
            climb.WithProgressBarToilDelay(TargetIndex.A);
            climb.AddPreInitAction(delegate { ClimbAnimation.StartClimb(pawn, Stairs); });
            climb.AddFinishAction(delegate { ClimbAnimation.Stop(pawn); });
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

    /// <summary>Orders that must replay after a stair transfer: job queues do
    /// not survive the despawn, so cross-level ordered actions (for example a
    /// Reverse Commands order for a pawn on another level) stash their real
    /// action here and the transfer runs it on arrival at the right map.
    /// Entries expire and are overwritten by newer orders; everything fails
    /// open.</summary>
    internal static class ABPendingOrders
    {
        private const int ExpiryTicks = 5000;

        // Self-heal: an arrival continuation runs INSIDE the transfer toil,
        // where job resolution can null (mid-cleanup reservations, fresh-spawn
        // region state - the same class of glitch the refuel chain hit). A
        // single delayed re-run, ONLY when the pawn is still idle, makes every
        // routed order continue seamlessly without ever double-issuing (a
        // successful first run leaves the pawn busy -> the retry skips).
        private const int RetryDelayTicks = 18;

        private struct Entry
        {
            public int tick;
            public Map map;
            public Action action;
        }

        private static readonly Dictionary<int, Entry> pending = new Dictionary<int, Entry>();

        private static readonly List<(Pawn pawn, Action action, int tick)> retries =
            new List<(Pawn pawn, Action action, int tick)>();

        public static void Set(Pawn pawn, Map targetMap, Action action)
        {
            if (pawn == null || targetMap == null || action == null)
            {
                return;
            }
            if (pending.Count > 64)
            {
                pending.Clear();
            }
            pending[pawn.thingIDNumber] = new Entry
            {
                tick = Find.TickManager.TicksGame,
                map = targetMap,
                action = action
            };
        }

        public static void TryRun(Pawn pawn, Map arrivedOn)
        {
            if (pawn == null || !pending.TryGetValue(pawn.thingIDNumber, out Entry entry))
            {
                return;
            }
            if (Find.TickManager.TicksGame - entry.tick > ExpiryTicks)
            {
                pending.Remove(pawn.thingIDNumber);
                return;
            }
            if (entry.map != arrivedOn)
            {
                // Not there yet (multi-hop rides consume it later).
                return;
            }
            pending.Remove(pawn.thingIDNumber);
            try
            {
                entry.action();
                // Arm one idle-gated retry so a transfer-time null still lands.
                if (retries.Count > 32)
                {
                    retries.Clear();
                }
                retries.Add((pawn, entry.action, Find.TickManager.TicksGame + RetryDelayTicks));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "pending order replay");
            }
        }

        /// <summary>Called from ABGameComp; no-op unless a retry is queued.
        /// Re-runs a replayed order once, but only if the pawn is still idle
        /// on the arrival map - so a first run that succeeded is never
        /// clobbered and an order that nulled at transfer time still fires.</summary>
        public static void Tick()
        {
            if (retries.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = retries.Count - 1; i >= 0; i--)
            {
                (Pawn pawn, Action action, int tick) r = retries[i];
                if (now < r.tick)
                {
                    continue;
                }
                retries.RemoveAt(i);
                if (!LooksIdle(r.pawn))
                {
                    continue;
                }
                try
                {
                    r.action();
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.Movement, e, "pending order retry");
                }
            }
        }

        /// <summary>Conservative idle test: no queued jobs and either no
        /// current job or a passive wait/wander. A pawn that took up the
        /// replayed order is busy and reads as not-idle, so the retry skips.</summary>
        private static bool LooksIdle(Pawn p)
        {
            if (p == null || !p.Spawned || p.Dead || p.jobs == null)
            {
                return false;
            }
            if (p.jobs.jobQueue != null && p.jobs.jobQueue.Count > 0)
            {
                return false;
            }
            Job cur = p.jobs.curJob;
            if (cur == null)
            {
                return true;
            }
            JobDef d = cur.def;
            return d == JobDefOf.Wait || d == JobDefOf.Wait_MaintainPosture
                || d == JobDefOf.Wait_Wander || d == JobDefOf.GotoWander
                || (d == JobDefOf.Goto && !cur.playerForced);
        }
    }

    /// <summary>What should happen to carried pawn cargo after a transfer:
    /// continue as a rescue, as a capture, or infer from context.</summary>
    public enum CarriedIntent
    {
        Auto,
        Rescue,
        Capture,
        Imprison
    }

    /// <summary>Shared pawn transfer through a linked stairwell, with carried
    /// things riding along and a guarded recovery respawn on failure. Used by the
    /// use-stairs job and the cross-level hauling, rescue, and capture jobs.</summary>
    internal static class StairTransfer
    {
        /// <summary>The pawn currently mid-transfer (despawn->respawn window).
        /// Lets the Hospitality lord-retention patch distinguish a stair ride
        /// from a genuine map exit. Main thread only, cleared in finally.</summary>
        internal static Pawn Transferring;

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
            // Backstop for jobs started before mounting: never despawn a mounted
            // rider, it would strand the mount with a live linkage.
            if (ABGiddyUpCompat.BlockForMount(p))
            {
                return;
            }
            Map sourceMap = stairs.Map;
            IntVec3 sourcePos = stairs.Position;
            // Ride the camera down/up with a colonist the player is watching: only
            // when this exact pawn is the sole selection and the player is looking
            // at the level being left (never yanks the view for AI haulers, pets,
            // or hostiles crossing on their own).
            bool followCam = false;
            try
            {
                ABSettings s = ABMod.Settings;
                followCam = s != null && s.cameraFollowStairs
                    && p.Faction == Faction.OfPlayer && !p.RaceProps.Animal
                    && Find.Selector.SingleSelectedThing == p
                    && sourceMap == Find.CurrentMap;
            }
            catch
            {
                followCam = false;
            }
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
                // Hostiles must leave their map-scoped lord before despawning.
                HostileDescend.NoteLeaving(p);
                Transferring = p;
                IntVec3 cell;
                try
                {
                    p.DeSpawn();
                    cell = landing.Standable(targetMap) ? landing : CellFinder.StandableCellNear(landing, targetMap, 4f);
                    if (!cell.IsValid)
                    {
                        cell = landing;
                    }
                    GenSpawn.Spawn(p, cell, targetMap);
                }
                finally
                {
                    Transferring = null;
                }
                if (carried != null && !carried.Destroyed)
                {
                    if (p.carryTracker == null || !p.carryTracker.TryStartCarry(carried))
                    {
                        GenPlace.TryPlaceThing(carried, cell, targetMap, ThingPlaceMode.Near);
                    }
                    // Import pin + demand cache freshness (2026-07-24): cargo
                    // that just crossed must not bounce straight back to better
                    // storage on the level it came from, and both levels'
                    // demand pictures changed the moment it moved.
                    CrossLevelDemand.NoteTransferred(carried, sourceMap, targetMap);
                    carried = null;
                }
                if (drafted && p.drafter != null)
                {
                    p.drafter.Drafted = true;
                }
                // Arrived hostiles join (or start) the assault on this map.
                HostileDescend.NoteArrived(p, targetMap);
                // Wild animals start a fresh linger window on the new level.
                CrossLevelAnimals.NoteArrived(p);
                // Arrival flourish: ease out of the stairwell (no-op for elevators).
                ClimbAnimation.StartEmerge(p, dest, Math.Sign(targetMap.Level() - sourceMap.Level()));
                ABApi.NotifyPawnTransferred(p, sourceMap, targetMap);
                if (followCam && p.Spawned && !p.Dead)
                {
                    LevelCamera.FollowPawn(p);
                }
                FinishCarriedDelivery(p, intent, sourceMap);
                PullFollowers(p, stairs, sourceMap, dest);
                ContinueRide(p, dest, rideFinal, targetMap);
                ABPendingOrders.TryRun(p, targetMap);
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
                        || ABGiddyUpCompat.IsCarryingRider(a)
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
        private static void FinishCarriedDelivery(Pawn p, CarriedIntent intent, Map cameFrom)
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
                    else if (intent == CarriedIntent.Imprison)
                    {
                        // Already a prisoner: tuck them into the linked-level cell
                        // with the vanilla warden jobs (escort awake, carry wounded).
                        bed = RestUtility.FindBedFor(victim, p, checkSocialProperness: false,
                            ignoreOtherReservations: false, GuestStatus.Prisoner);
                        continuation = victim.Downed
                            ? JobDefOf.TakeWoundedPrisonerToBed
                            : JobDefOf.EscortPrisonerToBed;
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
                else if (!TryOnwardDemandHop(p, carried, cameFrom))
                {
                    p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out Thing _);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "carried delivery finish");
            }
        }

        /// <summary>Onward relay hop (2026-07-25): cargo that arrived at an
        /// interchange with NO local storage used to be dropped at the stair
        /// mouth for a second pawn to rediscover - the "hauling TO stairs
        /// instead of THROUGH stairs" report. When a FURTHER linked level
        /// still wants the carried def (residual-aware, net of other pawns'
        /// en-route cargo), the same pawn lands the load and immediately
        /// carries it on through the next stairwell. Never routes back toward
        /// the map the cargo just came from, so demand on both ends cannot
        /// ping-pong a stack. Storage still wins when it exists (checked by
        /// the caller first), per the one-big-map doctrine.</summary>
        private static bool TryOnwardDemandHop(Pawn p, Thing carried, Map cameFrom)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Logistics) || carried == null || carried is Pawn)
                {
                    return false;
                }
                LevelComp comp = p.Map.Levels();
                if (comp == null)
                {
                    return false;
                }
                Map onward = null;
                Building_ABStairs entry = null;
                Building_ABStairs exit = null;
                int wanted = 0;
                if (comp.upperMap != null && comp.upperMap != cameFrom && !comp.upperMap.Disposed
                    && CrossLevelDemand.TryRouteDemand(p, comp.upperMap, carried, out entry, out exit, out wanted))
                {
                    onward = comp.upperMap;
                }
                else if (comp.lowerMap != null && comp.lowerMap != cameFrom && !comp.lowerMap.Disposed
                    && CrossLevelDemand.TryRouteDemand(p, comp.lowerMap, carried, out entry, out exit, out wanted))
                {
                    onward = comp.lowerMap;
                }
                if (onward == null || entry == null || exit == null || wanted <= 0)
                {
                    return false;
                }
                // Land the load (the haul driver picks it back up - its toils
                // need a spawned target), then continue the errand at once.
                if (!p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out Thing landed)
                    || landed == null || !landed.Spawned)
                {
                    return false;
                }
                Job on = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, landed, entry);
                on.targetC = exit;
                on.count = Math.Min(wanted, Math.Min(landed.stackCount,
                    p.carryTracker.MaxStackSpaceEver(landed.def)));
                CrossLevelDemand.NoteInFlight(p, onward, landed.def, on.count);
                p.jobs?.jobQueue?.EnqueueFirst(on, JobTag.Misc);
                ABLog.Dev("Onward demand hop: " + p.LabelShort + " continues " + landed.LabelShort
                    + " toward level " + onward.Level() + ".");
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "onward demand hop");
                return false;
            }
        }
    }
}
