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

        /// <summary>Retry cadence after an emergency virtual scan that found
        /// nothing actionable (e.g. someone is down but no bed exists anywhere).
        /// First response is instant: the cooldown is only charged when the
        /// plausibility pre-check already sees an emergency.</summary>
        private const int EmergencyMigrationCooldownTicks = 600;

        /// <summary>True while the virtual scan runs so the postfix that calls us
        /// does not recurse.</summary>
        internal static bool VirtualScanActive;

        private static readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

        private static readonly Dictionary<int, int> nextAllowedEmergencyTick = new Dictionary<int, int>();

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

        /// <summary>Migration for the emergency work pass (rescue, tend,
        /// firefight). Vanilla caches emergency work givers into a separate list
        /// that the normal pass never scans, so without this a doctor idling on
        /// another level never sees a downed pawn below or above. A cheap
        /// plausibility pre-check runs BEFORE any cooldown is charged: response
        /// is immediate when someone actually goes down, while calm-day empty
        /// passes cost two short list walks and no cooldown churn. The cooldown
        /// is separate from the normal migration cooldown so a failed emergency
        /// scan can never starve the real work scanner.</summary>
        public static ThinkResult? TryMigrateForEmergencyWork(JobGiver_Work giver, Pawn pawn)
        {
            LevelComp comp = pawn.Map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (nextAllowedEmergencyTick.TryGetValue(pawn.thingIDNumber, out int next) && now < next)
            {
                return null;
            }
            bool up = EmergencyWorkPlausible(pawn, comp.upperMap);
            bool low = EmergencyWorkPlausible(pawn, comp.lowerMap);
            if (!up && !low)
            {
                return null;
            }
            if (nextAllowedEmergencyTick.Count > 512)
            {
                nextAllowedEmergencyTick.Clear();
            }
            nextAllowedEmergencyTick[pawn.thingIDNumber] = now + EmergencyMigrationCooldownTicks;
            ThinkResult? work = up ? TryTowards(giver, pawn, comp.upperMap) : null;
            if (!work.HasValue && low)
            {
                work = TryTowards(giver, pawn, comp.lowerMap);
            }
            return work;
        }

        /// <summary>Cheap plausibility check for emergency work on a linked
        /// level: any fire, or a faction pawn or colony prisoner who is downed
        /// out of bed or has wounds a player doctor should tend. Cheapest checks
        /// first; this runs on every empty emergency scan of every colonist, so
        /// it must stay allocation-free. The virtual scan stays the authority on
        /// whether the work is actually doable.</summary>
        private static bool EmergencyWorkPlausible(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return false;
            }
            if (target.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0)
            {
                return true;
            }
            if (AnyEmergencyPatient(target.mapPawns.SpawnedPawnsInFaction(pawn.Faction)))
            {
                return true;
            }
            return AnyEmergencyPatient(target.mapPawns.PrisonersOfColonySpawned);
        }

        private static bool AnyEmergencyPatient(List<Pawn> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p.Downed && !p.InBed())
                {
                    return true;
                }
                if (p.health != null && p.health.HasHediffsNeedingTendByPlayer())
                {
                    return true;
                }
            }
            return false;
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
            if (!TryStairsJobToward(pawn, home, out Job job))
            {
                return null;
            }
            return new ThinkResult(job, giver, JobTag.Misc);
        }

        private static ThinkResult? TryTowards(JobGiver_Work giver, Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            if (!TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level()
                    + ": no reachable linked stairs on this level.");
                return null;
            }
            if (!WorkExistsAt(giver, pawn, target, exit.Position))
            {
                ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level()
                    + ": no work reachable from the stairwell exit at " + exit.Position
                    + " (is the work area connected to that exit?).");
                return null;
            }
            ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level() + ": work found, taking stairs.");
            return new ThinkResult(MakeStairsJob(stairs, exit), giver, JobTag.Misc);
        }

        /// <summary>Reachable stairs plus their far-side exit toward a target
        /// level. The shared shape behind every "send this pawn through the
        /// stairs" consumer; previously duplicated six times.</summary>
        public static bool TryResolveStairs(Pawn pawn, Map target, out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            stairs = null;
            exit = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            stairs = NearestUsableStairs(pawn, target, checkReachability: true);
            exit = stairs?.CounterpartTowards(target);
            return exit != null;
        }

        public static Job MakeStairsJob(Building_ABStairs stairs, Building_ABStairs exit)
        {
            Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            job.targetC = exit;
            return job;
        }

        public static bool TryStairsJobToward(Pawn pawn, Map target, out Job job)
        {
            job = null;
            if (!TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            job = MakeStairsJob(stairs, exit);
            return true;
        }

        private struct StairsMemoEntry
        {
            public int tick;
            public Building_ABStairs stairs;
        }

        private static readonly Dictionary<long, StairsMemoEntry> stairsMemo = new Dictionary<long, StairsMemoEntry>();

        /// <summary>Per-tick memo over NearestUsableStairs with reachability. The
        /// haul scanner calls the reachability variant once per candidate ITEM,
        /// but the verdict only depends on (pawn, target map): within one tick a
        /// full-map scan pays the region search twice instead of N times.
        /// Negative results are memoized too (the expensive case is usually "no
        /// reachable stairs", recomputed per item). One-tick staleness on
        /// mid-tick construction or destruction is accepted; the returned stairs
        /// are re-validated cheaply on every hit.</summary>
        public static Building_ABStairs NearestUsableStairsCached(Pawn pawn, Map target)
        {
            if (pawn == null || target == null)
            {
                return null;
            }
            long key = ((long)pawn.thingIDNumber << 32) | (uint)target.uniqueID;
            int now = Find.TickManager.TicksGame;
            if (stairsMemo.TryGetValue(key, out StairsMemoEntry entry) && entry.tick == now)
            {
                Building_ABStairs cached = entry.stairs;
                return cached != null && cached.Spawned && cached.CounterpartTowards(target) != null
                    ? cached
                    : null;
            }
            if (stairsMemo.Count > 1024)
            {
                stairsMemo.Clear();
            }
            Building_ABStairs found = NearestUsableStairs(pawn, target, checkReachability: true);
            stairsMemo[key] = new StairsMemoEntry { tick = now, stairs = found };
            return found;
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
                Building_ABStairs cp = s.CounterpartTowards(target);
                if (cp == null)
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
            if (!ABVirtualPosition.TrySwap(pawn, target, entryCell, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool found = false;
            VirtualScanActive = true;
            try
            {
                ThinkResult result = giver.TryIssueJobPackage(pawn, default(JobIssueParams));
                found = result.Job != null;
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
                VirtualScanActive = false;
            }
            return found;
        }
    }

    /// <summary>
    /// Temporarily relocates a pawn (private position and map index fields) so
    /// vanilla map-scoped queries run as if the pawn stood on another level.
    /// Callers must Restore in a finally block. Shared by cross-level work and
    /// hauling; the MultiFloors-proven technique.
    /// </summary>
    internal static class ABVirtualPosition
    {
        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexRef =
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        private static readonly AccessTools.FieldRef<Thing, IntVec3> PositionRef =
            AccessTools.FieldRefAccess<Thing, IntVec3>("positionInt");

        public struct Token
        {
            internal sbyte mapIndex;
            internal IntVec3 pos;
        }

        public static bool TrySwap(Pawn pawn, Map target, IntVec3 cell, out Token token)
        {
            token = default(Token);
            sbyte idx = (sbyte)Find.Maps.IndexOf(target);
            if (idx < 0)
            {
                return false;
            }
            token.mapIndex = MapIndexRef(pawn);
            token.pos = PositionRef(pawn);
            MapIndexRef(pawn) = idx;
            PositionRef(pawn) = cell;
            return true;
        }

        public static void Restore(Pawn pawn, Token token)
        {
            MapIndexRef(pawn) = token.mapIndex;
            PositionRef(pawn) = token.pos;
        }

        /// <summary>Runs a scan with the pawn virtually placed at a cell on the
        /// target map, restoring no matter what the scan does. Cold paths only:
        /// the lambda closure allocates, so the hottest storage scan
        /// (CrossLevelHaul.Check) keeps its hand-written swap.</summary>
        public static bool WithPawnAt(Pawn pawn, Map target, IntVec3 cell, Func<bool> scan)
        {
            if (!TrySwap(pawn, target, cell, out Token token))
            {
                return false;
            }
            try
            {
                return scan();
            }
            finally
            {
                Restore(pawn, token);
            }
        }

        /// <summary>Position-only swap for non-pawn things. Vanilla storage queries
        /// measure reachability and distance from the ITEM's position, which lives
        /// on the source map; re-seating it at the stairwell exit for the duration
        /// of the query makes both meaningful on the destination map.</summary>
        public static IntVec3 SwapPositionOnly(Thing thing, IntVec3 cell)
        {
            IntVec3 old = PositionRef(thing);
            PositionRef(thing) = cell;
            return old;
        }

        public static void RestorePositionOnly(Thing thing, IntVec3 oldPos)
        {
            PositionRef(thing) = oldPos;
        }
    }
}
