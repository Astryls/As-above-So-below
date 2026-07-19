using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lets idle colonists find work on directly linked levels. When the local
    /// work scan comes up empty, the pawn is virtually placed at a linked
    /// stairwell's exit and the vanilla work scan runs on the other map; if it
    /// finds anything, the pawn takes the stairs and re-scans on arrival. A
    /// per-pawn cooldown prevents oscillating between levels, and the cooldown
    /// is charged even for empty scans so idle pawns do not re-scan other maps
    /// every think cycle.
    /// </summary>
    public static class CrossLevelWork
    {
        private const int MigrationCooldownTicks = 1200;

        /// <summary>True while the virtual scan runs so the postfix that calls us
        /// does not recurse.</summary>
        internal static bool VirtualScanActive;

        private static readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexRef =
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        private static readonly AccessTools.FieldRef<Thing, IntVec3> PositionRef =
            AccessTools.FieldRefAccess<Thing, IntVec3>("positionInt");

        public static ThinkResult? TryMigrateForWork(JobGiver_Work giver, Pawn pawn)
        {
            Map map = pawn.Map;
            LevelComp comp = map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (nextAllowedTick.TryGetValue(pawn.thingIDNumber, out int next) && now < next)
            {
                return null;
            }
            if (nextAllowedTick.Count > 512)
            {
                nextAllowedTick.Clear();
            }
            nextAllowedTick[pawn.thingIDNumber] = now + MigrationCooldownTicks;

            ThinkResult? work = TryTowards(giver, pawn, comp.upperMap) ?? TryTowards(giver, pawn, comp.lowerMap);
            if (work.HasValue)
            {
                return work;
            }
            return TryReturnHome(giver, pawn, comp);
        }

        /// <summary>Truly idle colonists drift back toward the ground level, where
        /// food, beds, and recreation usually live, instead of roaming a work level
        /// forever.</summary>
        private static ThinkResult? TryReturnHome(JobGiver_Work giver, Pawn pawn, LevelComp comp)
        {
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.idleReturnHome || comp.level == 0)
            {
                return null;
            }
            Map home = comp.level > 0 ? comp.lowerMap : comp.upperMap;
            if (home == null || home.Disposed)
            {
                return null;
            }
            Building_ABStairs stairs = NearestUsableStairs(pawn, home, checkReachability: true);
            if (stairs?.Counterpart == null)
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            return new ThinkResult(job, giver, JobTag.Misc);
        }

        private static ThinkResult? TryTowards(JobGiver_Work giver, Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            Building_ABStairs stairs = NearestUsableStairs(pawn, target, checkReachability: true);
            Building_ABStairs exit = stairs?.Counterpart;
            if (exit == null)
            {
                return null;
            }
            if (!WorkExistsAt(giver, pawn, target, exit.Position))
            {
                return null;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            return new ThinkResult(job, giver, JobTag.Misc);
        }

        /// <summary>Nearest stairwell on the pawn's map whose counterpart sits on the
        /// target map. Reachability checks are optional because they are too heavy
        /// for per-frame gizmo building.</summary>
        public static Building_ABStairs NearestUsableStairs(Pawn pawn, Map target, bool checkReachability)
        {
            List<Building_ABStairs> stairs = pawn.Map.Levels()?.Stairs;
            if (stairs == null)
            {
                return null;
            }
            Building_ABStairs best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < stairs.Count; i++)
            {
                Building_ABStairs s = stairs[i];
                if (s == null || !s.Spawned)
                {
                    continue;
                }
                Building_ABStairs cp = s.Counterpart;
                if (cp == null || cp.Map != target)
                {
                    continue;
                }
                float d = (s.Position - pawn.Position).LengthHorizontalSquared;
                if (d >= bestDist)
                {
                    continue;
                }
                if (checkReachability && !pawn.CanReach(s, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                best = s;
                bestDist = d;
            }
            return best;
        }

        /// <summary>Runs the vanilla work scan as if the pawn stood at the stairwell
        /// exit on the target map. Position and map index are swapped through the
        /// private fields (the MultiFloors-proven technique) and restored in a
        /// finally block no matter what the scan does. Any job the scan produces is
        /// discarded; the real job gets picked normally after the transfer.</summary>
        private static bool WorkExistsAt(JobGiver_Work giver, Pawn pawn, Map target, IntVec3 entryCell)
        {
            sbyte targetIndex = (sbyte)Find.Maps.IndexOf(target);
            if (targetIndex < 0)
            {
                return false;
            }
            sbyte oldMapIndex = MapIndexRef(pawn);
            IntVec3 oldPos = PositionRef(pawn);
            bool found = false;
            VirtualScanActive = true;
            try
            {
                MapIndexRef(pawn) = targetIndex;
                PositionRef(pawn) = entryCell;
                ThinkResult result = giver.TryIssueJobPackage(pawn, default(JobIssueParams));
                found = result.Job != null;
            }
            finally
            {
                MapIndexRef(pawn) = oldMapIndex;
                PositionRef(pawn) = oldPos;
                VirtualScanActive = false;
            }
            return found;
        }
    }
}
