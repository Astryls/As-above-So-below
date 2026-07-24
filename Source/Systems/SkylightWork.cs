using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Reconciling construction work for skylight zones. One scanner covers
    /// both directions: painted-but-solid cells get glass installed,
    /// glass-but-unpainted cells get restored. Work-only like vanilla roofing:
    /// no materials, no fail chance, normal Construction work speed and XP.
    /// </summary>
    public class WorkGiver_ABSkylights : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override IEnumerable<IntVec3> PotentialWorkCellsGlobal(Pawn pawn)
        {
            SkylightMapComp comp = SkylightSystem.CompFor(pawn.Map);
            if (comp == null)
            {
                yield break;
            }
            foreach (IntVec3 c in comp.WorkCells())
            {
                yield return c;
            }
        }

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            if (!SkylightSystem.FeatureOn || !ABGuard.On(ABGuard.Areas))
            {
                return true;
            }
            SkylightMapComp comp = SkylightSystem.CompFor(pawn.Map);
            return comp == null || !comp.AnyWork;
        }

        public override bool HasJobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            SkylightMapComp comp = SkylightSystem.CompFor(pawn.Map);
            if (comp == null)
            {
                return false;
            }
            bool install = comp.IsPlanned(c) && !comp.IsPane(c);
            bool remove = comp.IsPane(c) && !comp.IsPlanned(c);
            if (!install && !remove)
            {
                return false;
            }
            if (install && !SkylightSystem.CellAllowed(pawn.Map, c).Accepted)
            {
                return false;
            }
            if (!pawn.CanReserve(c, 1, -1, ReservationLayerDefOf.Floor, forced))
            {
                return false;
            }
            if (!pawn.CanReach(c, PathEndMode.Touch, pawn.NormalMaxDanger()))
            {
                return false;
            }
            return true;
        }

        public override Job JobOnCell(Pawn pawn, IntVec3 c, bool forced = false)
        {
            SkylightMapComp comp = SkylightSystem.CompFor(pawn.Map);
            bool install = comp != null && comp.IsPlanned(c) && !comp.IsPane(c);
            return JobMaker.MakeJob(install ? ABDefOf.AB_BuildSkylight : ABDefOf.AB_RemoveSkylight, c);
        }
    }

    public abstract class JobDriver_ABSkylightBase : JobDriver
    {
        private static SoundDef constructSound;

        private static SoundDef ConstructSound =>
            constructSound ?? (constructSound =
                DefDatabase<SoundDef>.GetNamedSilentFail("Interact_ConstructMetal"));

        protected float workLeft = -1f;

        protected IntVec3 Cell => job.targetA.Cell;

        protected abstract float TotalWork { get; }

        protected abstract bool StillValid();

        protected abstract void Complete();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, ReservationLayerDefOf.Floor, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !StillValid());
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.Touch);
            Toil work = ToilMaker.MakeToil("ABSkylightWork");
            work.initAction = delegate
            {
                workLeft = TotalWork;
            };
            work.tickAction = delegate
            {
                float speed = work.actor.GetStatValue(StatDefOf.ConstructionSpeed) * 1.7f;
                workLeft -= speed;
                work.actor.skills?.Learn(SkillDefOf.Construction, 0.085f);
                if (workLeft <= 0f)
                {
                    Complete();
                    ReadyForNextToil();
                }
            };
            work.defaultCompleteMode = ToilCompleteMode.Never;
            work.WithProgressBar(TargetIndex.A, () => 1f - workLeft / TotalWork);
            if (ConstructSound != null)
            {
                work.PlaySustainerOrSound(() => ConstructSound);
            }
            work.handlingFacing = true;
            yield return work;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref workLeft, "workLeft", -1f);
        }
    }

    public class JobDriver_ABBuildSkylight : JobDriver_ABSkylightBase
    {
        protected override float TotalWork => 2200f;

        protected override bool StillValid()
        {
            SkylightMapComp comp = SkylightSystem.CompFor(Map);
            return comp != null && comp.IsPlanned(Cell) && !comp.IsPane(Cell)
                && SkylightSystem.CellAllowed(Map, Cell).Accepted;
        }

        protected override void Complete()
        {
            SkylightSystem.PlaceSkylight(Map, Cell);
        }
    }

    public class JobDriver_ABRemoveSkylight : JobDriver_ABSkylightBase
    {
        protected override float TotalWork => 1000f;

        protected override bool StillValid()
        {
            SkylightMapComp comp = SkylightSystem.CompFor(Map);
            return comp != null && comp.IsPane(Cell) && !comp.IsPlanned(Cell);
        }

        protected override void Complete()
        {
            SkylightSystem.RemoveSkylight(Map, Cell);
        }
    }
}
