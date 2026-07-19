using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;

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

    /// <summary>Shared pawn transfer through a linked stairwell, with carried
    /// things riding along and a guarded recovery respawn on failure. Used by the
    /// use-stairs job and the cross-level hauling job.</summary>
    internal static class StairTransfer
    {
        public static void Transfer(Pawn p, Building_ABStairs stairs)
        {
            Building_ABStairs dest = stairs?.Counterpart;
            if (p == null || dest == null || !dest.Spawned)
            {
                return;
            }
            Map sourceMap = stairs.Map;
            IntVec3 sourcePos = stairs.Position;
            try
            {
                Map targetMap = dest.Map;
                IntVec3 landing = dest.Position;
                bool drafted = p.Drafted;
                p.DeSpawn();
                IntVec3 cell = landing.Standable(targetMap) ? landing : CellFinder.StandableCellNear(landing, targetMap, 4f);
                if (!cell.IsValid)
                {
                    cell = landing;
                }
                GenSpawn.Spawn(p, cell, targetMap);
                if (drafted && p.drafter != null)
                {
                    p.drafter.Drafted = true;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "stair transfer");
                if (!p.Spawned && !p.Destroyed && !p.Dead)
                {
                    GenSpawn.Spawn(p, sourcePos, sourceMap);
                }
            }
        }
    }
}
