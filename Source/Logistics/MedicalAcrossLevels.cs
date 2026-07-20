using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Cross-level medical and prisoner handling (T7 #2/#7). Doctors rescue
    /// downed colony pawns to beds on linked levels when no local bed exists:
    /// the rescuer carries the patient through the stairs, lands them at the
    /// exit, and continues with a queued vanilla Rescue job to the bed found on
    /// arrival. Capture works the same way through a right-click order when the
    /// only prisoner beds are on another level. Patient feeding is handled by
    /// meal demand in CrossLevelDemand plus normal work migration: food flows
    /// to the patient's level and doctors feed locally.
    /// </summary>
    internal static class TakePawnAcrossLevels
    {
        /// <summary>Stairs on the taker's map leading toward a linked level that
        /// has a valid bed for the victim, checked with both pawns virtually at
        /// the stairwell exit so bed validity and reachability evaluate on the
        /// target map.</summary>
        internal static Building_ABStairs FindStairsTowardBed(Pawn taker, Pawn victim, GuestStatus? guest, out Building_ABStairs exit)
        {
            exit = null;
            LevelComp comp = taker.Map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            return Toward(taker, victim, comp.upperMap, guest, ref exit)
                ?? Toward(taker, victim, comp.lowerMap, guest, ref exit);
        }

        private static Building_ABStairs Toward(Pawn taker, Pawn victim, Map target, GuestStatus? guest, ref Building_ABStairs exitOut)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(taker, target, checkReachability: true);
            Building_ABStairs exit = stairs?.CounterpartTowards(target);
            if (exit == null)
            {
                return null;
            }
            if (!ABVirtualPosition.TrySwap(taker, target, exit.Position, out ABVirtualPosition.Token takerToken))
            {
                return null;
            }
            bool found = false;
            try
            {
                if (ABVirtualPosition.TrySwap(victim, target, exit.Position, out ABVirtualPosition.Token victimToken))
                {
                    try
                    {
                        found = RestUtility.FindBedFor(victim, taker, checkSocialProperness: false,
                            ignoreOtherReservations: false, guest) != null;
                    }
                    finally
                    {
                        ABVirtualPosition.Restore(victim, victimToken);
                    }
                }
            }
            finally
            {
                ABVirtualPosition.Restore(taker, takerToken);
            }
            if (!found)
            {
                return null;
            }
            exitOut = exit;
            return stairs;
        }
    }

    /// <summary>Rescue downed colony pawns to beds on linked levels when this
    /// level has none. Runs after all local doctoring (priorityInType).</summary>
    public class WorkGiver_ABRescueAcrossLevels : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.OnCell;

        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Pawn);

        public override Danger MaxPathDanger(Pawn pawn)
        {
            return Danger.Deadly;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            return pawn.Map.mapPawns.SpawnedDownedPawns;
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!ABGuard.On(ABGuard.Logistics)
                || ABMod.Settings == null || !ABMod.Settings.crossLevelNeeds
                || !pawn.Map.ConnectedToOtherLevel())
            {
                return true;
            }
            List<Pawn> list = pawn.Map.mapPawns.SpawnedPawnsInFaction(pawn.Faction);
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Downed && !list[i].InBed())
                {
                    return false;
                }
            }
            return true;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Pawn victim) || victim.InBed()
                || !HealthAIUtility.CanRescueNow(pawn, victim, forced))
            {
                return false;
            }
            // Babies stay with local childcare handling.
            if (ChildcareUtility.CanSuckle(victim, out _))
            {
                return false;
            }
            // A local bed means vanilla rescue handles it.
            if (RestUtility.FindBedFor(victim, pawn, checkSocialProperness: false) != null)
            {
                return false;
            }
            return TakePawnAcrossLevels.FindStairsTowardBed(pawn, victim, null, out Building_ABStairs _) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!(t is Pawn victim))
            {
                return null;
            }
            Building_ABStairs stairs = TakePawnAcrossLevels.FindStairsTowardBed(pawn, victim, null, out Building_ABStairs exit);
            if (stairs == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_RescueAcrossLevels, victim, stairs);
            job.targetC = exit;
            job.count = 1;
            return job;
        }
    }

    /// <summary>Carry a downed pawn through the stairs; the transfer lands the
    /// victim at the exit and queues the vanilla Rescue or Capture continuation
    /// to the bed found on arrival. Shared by the rescue work giver and the
    /// capture float menu order; the job def picks the intent.</summary>
    public class JobDriver_ABTakePawnAcrossLevels : JobDriver
    {
        private const int ClimbTicks = Building_ABStairs.BaseClimbTicks;

        private Pawn Victim => job.GetTarget(TargetIndex.A).Thing as Pawn;

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
            // Prisoner transport carries awake prisoners too (vanilla escort does
            // the same); every other intent requires a downed victim.
            this.FailOn(() => Victim == null || Victim.Dead
                || (job.def == ABDefOf.AB_TakePrisonerAcrossLevels
                    ? !Victim.IsPrisonerOfColony
                    : !Victim.Downed));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.OnCell)
                .FailOnSomeonePhysicallyInteracting(TargetIndex.A);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            Toil climb = Toils_General.Wait(Stairs?.ClimbTicksFor(pawn) ?? ClimbTicks, TargetIndex.B);
            climb.WithProgressBarToilDelay(TargetIndex.B);
            yield return climb;
            Toil transfer = ToilMaker.MakeToil("AB_TakePawnTransfer");
            transfer.initAction = delegate
            {
                Building_ABStairs dest = job.GetTarget(TargetIndex.C).Thing as Building_ABStairs;
                CarriedIntent intent = CarriedIntent.Rescue;
                if (job.def == ABDefOf.AB_CaptureAcrossLevels)
                {
                    intent = CarriedIntent.Capture;
                }
                else if (job.def == ABDefOf.AB_TakePrisonerAcrossLevels)
                {
                    intent = CarriedIntent.Imprison;
                }
                StairTransfer.Transfer(pawn, Stairs, intent, dest);
            };
            transfer.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return transfer;
        }
    }

    /// <summary>Right-click rescue order toward a linked level's bed, offered
    /// only when this level has no usable bed for the victim so it appears
    /// exactly where the vanilla rescue option turns into a dead "no bed"
    /// row. Assignment-free like vanilla's rescue: any capable pawn can be
    /// ordered, no doctoring assignment needed.</summary>
    public class FloatMenuOptionProvider_RescueAcrossLevels : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return null;
            }
            Pawn taker = context.FirstSelectedPawn;
            if (taker == null)
            {
                return null;
            }
            Map map = taker.Map;
            if (map == null || !map.ConnectedToOtherLevel() || clickedPawn.Map != map)
            {
                return null;
            }
            // Mirror vanilla FloatMenuOptionProvider_RescuePawn gating so this
            // option exists precisely where the vanilla one does (its
            // CanRescueNow(forced) covers downed, reserve and reach).
            if (!HealthAIUtility.CanRescueNow(taker, clickedPawn, forced: true))
            {
                return null;
            }
            if (clickedPawn.mindState != null && clickedPawn.mindState.WillJoinColonyIfRescued)
            {
                return null;
            }
            if (clickedPawn.IsPrisonerOfColony || clickedPawn.IsSlaveOfColony || clickedPawn.IsColonyMech)
            {
                return null;
            }
            if (clickedPawn.Faction != null && clickedPawn.Faction.HostileTo(Faction.OfPlayer))
            {
                return null;
            }
            // Babies stay with local childcare handling.
            if (ChildcareUtility.CanSuckle(clickedPawn, out _))
            {
                return null;
            }
            if (!HealthAIUtility.ShouldSeekMedicalRest(clickedPawn)
                && clickedPawn.ageTracker.CurLifeStage.alwaysDowned)
            {
                return null;
            }
            Pawn_PlayerSettings playerSettings = clickedPawn.playerSettings;
            if (playerSettings != null && playerSettings.medCare == MedicalCareCategory.NoCare)
            {
                // Vanilla already shows a disabled "medical care disabled" row.
                return null;
            }
            try
            {
                // A usable local bed means the vanilla rescue option covers it;
                // check both passes vanilla makes before declaring "no bed".
                if (RestUtility.FindBedFor(clickedPawn, taker, checkSocialProperness: false) != null
                    || RestUtility.FindBedFor(clickedPawn, taker, checkSocialProperness: false,
                        ignoreOtherReservations: true) != null)
                {
                    return null;
                }
                Building_ABStairs stairs = TakePawnAcrossLevels.FindStairsTowardBed(taker, clickedPawn, null, out Building_ABStairs exit);
                if (stairs == null || exit == null)
                {
                    return null;
                }
                bool up = exit.Map.Level() > map.Level();
                string label = (up ? "AB_RescueUpTo" : "AB_RescueDownTo").Translate(clickedPawn.LabelShort);
                FloatMenuOption option = new FloatMenuOption(label, delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_RescueAcrossLevels, clickedPawn, stairs);
                    job.targetC = exit;
                    job.count = 1;
                    job.playerForced = true;
                    taker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                    PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.Rescuing, KnowledgeAmount.Total);
                }, MenuOptionPriority.RescueOrCapture, null, clickedPawn);
                return FloatMenuUtility.DecoratePrioritizedTask(option, taker, clickedPawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level rescue option");
                return null;
            }
        }
    }

    /// <summary>Right-click capture order toward a linked level's prisoner bed,
    /// offered only when this level has no free prisoner bed and only for
    /// clearly capturable targets (downed, hostile or factionless).</summary>
    public class FloatMenuOptionProvider_CaptureAcrossLevels : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return null;
            }
            Pawn taker = context.FirstSelectedPawn;
            if (taker == null || !(clickedThing is Pawn victim))
            {
                return null;
            }
            Map map = taker.Map;
            if (map == null || !map.ConnectedToOtherLevel() || victim.Map != map)
            {
                return null;
            }
            if (!victim.Downed || victim.Dead || victim.IsPrisonerOfColony)
            {
                return null;
            }
            if (victim.Faction != null && !victim.Faction.HostileTo(Faction.OfPlayer))
            {
                return null;
            }
            if (!taker.CanReach(victim, PathEndMode.OnCell, Danger.Deadly))
            {
                return null;
            }
            try
            {
                // A free local prisoner bed means the vanilla capture option
                // already covers it.
                if (RestUtility.FindBedFor(victim, taker, checkSocialProperness: false,
                    ignoreOtherReservations: false, GuestStatus.Prisoner) != null)
                {
                    return null;
                }
                Building_ABStairs stairs = TakePawnAcrossLevels.FindStairsTowardBed(taker, victim, GuestStatus.Prisoner, out Building_ABStairs exit);
                if (stairs == null || exit == null)
                {
                    return null;
                }
                bool up = exit.Map.Level() > map.Level();
                string label = (up ? "AB_CaptureUpTo" : "AB_CaptureDownTo").Translate(victim.LabelShort);
                FloatMenuOption option = new FloatMenuOption(label, delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_CaptureAcrossLevels, victim, stairs);
                    job.targetC = exit;
                    job.count = 1;
                    job.playerForced = true;
                    taker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.High);
                return FloatMenuUtility.DecoratePrioritizedTask(option, taker, victim);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level capture option");
                return null;
            }
        }
    }

    /// <summary>Automatic warden handling for prisoners whose cell is on a linked
    /// level (T7 prisoner transport): mirrors vanilla WorkGiver_Warden_TakeToBed
    /// but only fires when there is no usable prison bed on this level, or the
    /// prisoner has been assigned a bed on a linked level. The warden carries the
    /// prisoner through the stairs (awake or downed) and a queued vanilla escort
    /// or wounded-transport job tucks them into the cell found on arrival. Runs
    /// after vanilla take-to-bed (lower priorityInType) so a local cell always
    /// wins first.</summary>
    public class WorkGiver_ABWardenTakeToBedAcrossLevels : WorkGiver_Warden
    {
        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelPrisoners)
            {
                return null;
            }
            Map map = pawn.Map;
            if (map == null || !map.ConnectedToOtherLevel())
            {
                return null;
            }
            if (!(t is Pawn prisoner) || !prisoner.IsPrisonerOfColony
                || !ShouldTakeCareOfPrisoner(pawn, prisoner, forced))
            {
                return null;
            }
            try
            {
                // Downed prisoners follow vanilla's wounded-transport gating.
                if (prisoner.Downed && (!HealthAIUtility.ShouldSeekMedicalRest(prisoner) || prisoner.InBed()))
                {
                    return null;
                }
                // Respect an explicit reassignment: if the prisoner owns a bed on a
                // directly linked level, take them there even when a local cell is
                // free. Otherwise only act when no usable local prison bed exists.
                LevelComp comp = map.Levels();
                Building_Bed owned = prisoner.ownership?.OwnedBed;
                bool ownedOnLinkedLevel = owned != null && owned.Spawned && comp != null
                    && (owned.Map == comp.upperMap || owned.Map == comp.lowerMap);
                if (!ownedOnLinkedLevel)
                {
                    if (RestUtility.FindBedFor(prisoner, pawn, checkSocialProperness: true,
                        ignoreOtherReservations: false, GuestStatus.Prisoner) != null)
                    {
                        return null;
                    }
                    // An awake prisoner already content in a local bed needs no move.
                    if (!prisoner.Downed && RestUtility.FindBedFor(prisoner, prisoner,
                        checkSocialProperness: true, ignoreOtherReservations: false, GuestStatus.Prisoner) != null)
                    {
                        return null;
                    }
                }
                Building_ABStairs stairs = TakePawnAcrossLevels.FindStairsTowardBed(pawn, prisoner,
                    GuestStatus.Prisoner, out Building_ABStairs exit);
                if (stairs == null || exit == null)
                {
                    return null;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_TakePrisonerAcrossLevels, prisoner, stairs);
                job.targetC = exit;
                job.count = 1;
                return job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level warden take to bed");
                return null;
            }
        }
    }

    /// <summary>Right-click order to move an existing prisoner to a cell on a
    /// linked level, offered when this level has no usable prison bed for them
    /// but a linked one does. Gives the player direct control over which cell a
    /// prisoner ends up in even when the autonomous warden would keep them local.</summary>
    public class FloatMenuOptionProvider_TakePrisonerAcrossLevels : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelPrisoners)
            {
                return null;
            }
            Pawn taker = context.FirstSelectedPawn;
            if (taker == null || clickedPawn == null || !clickedPawn.IsPrisonerOfColony)
            {
                return null;
            }
            Map map = taker.Map;
            if (map == null || !map.ConnectedToOtherLevel() || clickedPawn.Map != map)
            {
                return null;
            }
            if (clickedPawn.InAggroMentalState
                || clickedPawn.IsForbidden(taker)
                || !taker.CanReserveAndReach(clickedPawn, PathEndMode.OnCell, Danger.Deadly))
            {
                return null;
            }
            try
            {
                Building_ABStairs stairs = TakePawnAcrossLevels.FindStairsTowardBed(taker, clickedPawn,
                    GuestStatus.Prisoner, out Building_ABStairs exit);
                if (stairs == null || exit == null)
                {
                    return null;
                }
                bool up = exit.Map.Level() > map.Level();
                string label = (up ? "AB_TakePrisonerUpTo" : "AB_TakePrisonerDownTo").Translate(clickedPawn.LabelShort);
                FloatMenuOption option = new FloatMenuOption(label, delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_TakePrisonerAcrossLevels, clickedPawn, stairs);
                    job.targetC = exit;
                    job.count = 1;
                    job.playerForced = true;
                    taker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.High, null, clickedPawn);
                return FloatMenuUtility.DecoratePrioritizedTask(option, taker, clickedPawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level take prisoner option");
                return null;
            }
        }
    }
}
