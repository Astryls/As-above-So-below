using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Builds the actual cross-level haul job for a seed item whose better
    /// storage lives on a linked level. Two shapes:
    ///
    ///  - BULK (Pick Up And Haul / Hauler's Dream present and the pawn carries
    ///    their inventory-haul comp): scoop the seed plus nearby stacks bound
    ///    for the SAME level into inventory, ride the stairs, and let that mod's
    ///    unloader store them on arrival. One trip moves a whole load.
    ///  - SINGLE (fallback): the vanilla-feel carryTracker haul - one stack
    ///    carried through the stairs, stored by vanilla's carried-thing path.
    ///
    /// Shared by the normal and the Allow-Tool-urgent cross-level haul givers;
    /// the urgent giver passes a filter so an urgent trip carries only urgent
    /// cargo.
    /// </summary>
    public static class CrossLevelHaulJob
    {
        /// <summary>Radius (cells) around the seed within which extra bulk
        /// cargo is gathered - mirrors Pick Up And Haul's own sweep reach.</summary>
        private const float GatherRadius = 12f;

        /// <summary>Hard cap on stacks scooped in one bulk trip; encumbrance
        /// usually stops it sooner.</summary>
        private const int MaxBulkStacks = 16;

        /// <summary>Stop gathering once picking up would push the pawn past this
        /// much of its carry capacity, leaving headroom for the last stack.</summary>
        private const float GatherEncumbranceCap = 0.8f;

        public static Job Build(Pawn pawn, Thing seed, Map target, Building_ABStairs stairs,
            Predicate<Thing> extra = null, bool ignorePins = false)
        {
            if (pawn == null || seed == null || target == null || stairs == null)
            {
                return null;
            }
            Building_ABStairs exit = stairs.CounterpartTowards(target);
            if (exit == null)
            {
                return null;
            }

            if (ABInventoryHaulBridge.AnyActive && ABInventoryHaulBridge.HasComp(pawn))
            {
                return BuildBulk(pawn, seed, target, stairs, exit, extra, ignorePins);
            }
            return BuildSingle(pawn, seed, target, stairs, exit);
        }

        private static Job BuildSingle(Pawn pawn, Thing seed, Map target, Building_ABStairs stairs, Building_ABStairs exit)
        {
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, seed, stairs);
            job.targetC = exit;
            job.count = Mathf.Min(seed.stackCount, pawn.carryTracker.MaxStackSpaceEver(seed.def));
            return job;
        }

        private static Job BuildBulk(Pawn pawn, Thing seed, Map target, Building_ABStairs stairs,
            Building_ABStairs exit, Predicate<Thing> extra, bool ignorePins)
        {
            Job job = JobMaker.MakeJob(ABDefOf.AB_BulkHaulAcrossLevels, stairs);
            job.targetC = exit;
            job.count = 1;
            List<LocalTargetInfo> queue = new List<LocalTargetInfo> { seed };

            ICollection<Thing> haulables = pawn.Map.listerHaulables?.ThingsPotentiallyNeedingHauling();
            if (haulables != null)
            {
                float radiusSq = GatherRadius * GatherRadius;
                foreach (Thing t in haulables)
                {
                    if (queue.Count >= MaxBulkStacks)
                    {
                        break;
                    }
                    if (t == null || t == seed || !t.Spawned)
                    {
                        continue;
                    }
                    if ((t.Position - seed.Position).LengthHorizontalSquared > radiusSq)
                    {
                        continue;
                    }
                    if (extra != null && !extra(t))
                    {
                        continue;
                    }
                    if (t.IsForbidden(pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false))
                    {
                        continue;
                    }
                    // Same-destination only: a bulk trip goes to one level.
                    // Urgent trips carry the caller's pin bypass through to the
                    // gathered extras (they pass the urgent filter too).
                    if (CrossLevelHaul.TargetLevelFor(pawn, t, out Building_ABStairs _, ignorePins) != target)
                    {
                        continue;
                    }
                    queue.Add(t);
                }
            }

            job.targetQueueB = queue;
            return job;
        }
    }
}
