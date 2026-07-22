using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Wild-animal wandering across levels ("animals treat stairs as landscape, not
    /// walls"). Two rules, both cheap and event-paced:
    ///
    ///  - AMBIENT DESCENT (rare flavor): on a slow cadence, at most one eligible wild
    ///    surface animal near a basement-linked stairwell wanders down to shelter.
    ///    Gated hard: basement wildlife cap, comfort-temperature check, no predators
    ///    mid-hunt, no manhunters, long global cooldown between descents - it reads
    ///    as ambient life, never as traffic. The SKY is never an ambient destination
    ///    (confirmed design: nothing "wanders up" on its own).
    ///
    ///  - ESCAPE (the correctness rule): a wild animal on a pocket level leaves for
    ///    the surface when it is hungry or its linger timer runs out - so wildlife
    ///    never starves in the dark and pocket levels never accumulate a zoo. Runs
    ///    inside the existing pocket-level scan (HostileDescend), which already
    ///    routes lost NPC animals; this class only supplies the policy.
    ///
    /// Colony and pen animals are NEVER touched (faction filter); pets already follow
    /// their masters through stairs. Kill switch: HostileMove (the NPC cross-level
    /// movement subsystem), plus the crossLevelAnimalWander setting.
    /// </summary>
    internal static class CrossLevelAnimals
    {
        /// <summary>How long an ambient visitor stays below before the escape rule
        /// sends it home (randomized per animal by id so departures stagger).</summary>
        private const int LingerTicksBase = 5000;

        /// <summary>Global minimum spacing between ambient descents per column.</summary>
        private const int AmbientSpacingTicks = 12000;

        /// <summary>Basement wildlife cap: ambient descent stops adding once this
        /// many wild animals are already down there.</summary>
        private const int BasementWildCap = 4;

        private const float AmbientChancePerScan = 0.15f;

        /// <summary>pawn id -> tick after which the escape rule may move it. Bounded
        /// like every per-pawn store in the mod.</summary>
        private static readonly Dictionary<int, int> leaveAfter = new Dictionary<int, int>();

        /// <summary>column ground map id -> next tick ambient descent may fire.</summary>
        private static readonly Dictionary<int, int> nextAmbient = new Dictionary<int, int>();

        internal static bool Enabled
        {
            get
            {
                ABSettings s = ABMod.Settings;
                return ABGuard.On(ABGuard.HostileMove) && s != null && s.crossLevelAnimalWander;
            }
        }

        internal static void ClearAll()
        {
            leaveAfter.Clear();
            nextAmbient.Clear();
        }

        private static bool IsWildAnimal(Pawn p)
        {
            return p != null && p.Faction == null && p.RaceProps.Animal
                && !p.Dead && !p.Downed && p.Spawned;
        }

        // --- escape policy (called from HostileDescend's pocket scan) ---------

        /// <summary>Should this wild animal leave the pocket level now? Hungry
        /// animals leave immediately; comfortable ones linger a randomized while
        /// (recorded on first sight, so spawned-in animals get a full stay too).</summary>
        internal static bool WildAnimalWantsOff(Pawn p)
        {
            if (!Enabled)
            {
                return false;
            }
            Need_Food food = p.needs?.food;
            if (food != null && (p.health?.hediffSet?.HasHediff(HediffDefOf.Malnutrition) == true
                || food.CurLevelPercentage < 0.25f))
            {
                return true;
            }
            int now = Find.TickManager.TicksGame;
            if (leaveAfter.TryGetValue(p.thingIDNumber, out int at))
            {
                return now >= at;
            }
            if (leaveAfter.Count > 256)
            {
                leaveAfter.Clear();
            }
            // First sight of this visitor: start its stay.
            leaveAfter[p.thingIDNumber] = now + LingerTicksBase / 2
                + (p.thingIDNumber * 977) % LingerTicksBase;
            return false;
        }

        /// <summary>Arrival bookkeeping (called from the stair transfer for wild
        /// animals): a fresh linger window on whichever level it just reached.</summary>
        internal static void NoteArrived(Pawn p)
        {
            if (!IsWildAnimal(p))
            {
                return;
            }
            if (leaveAfter.Count > 256)
            {
                leaveAfter.Clear();
            }
            int now = Find.TickManager.TicksGame;
            leaveAfter[p.thingIDNumber] = now + LingerTicksBase / 2
                + (p.thingIDNumber * 977) % LingerTicksBase;
        }

        // --- ambient descent (ground comp, slow cadence) ----------------------

        private static readonly List<Pawn> tmpEligible = new List<Pawn>();

        /// <summary>One scan for the column: maybe send one wild surface animal down
        /// the stairs into the basement. Called from the ground LevelComp on its slow
        /// cadence; every early-out is one or two field reads.</summary>
        public static void ScanSurfaceAmbient(LevelComp comp)
        {
            if (!Enabled)
            {
                return;
            }
            Map surface = comp.map;
            Map basement = comp.lowerMap;
            if (basement == null || basement.Disposed)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (nextAmbient.TryGetValue(surface.uniqueID, out int next) && now < next)
            {
                return;
            }
            if (!Rand.Chance(AmbientChancePerScan))
            {
                return;
            }
            if (WildAnimalCount(basement) >= BasementWildCap)
            {
                Charge(surface, now + AmbientSpacingTicks);
                return;
            }
            float basementTemp = basement.mapTemperature.OutdoorTemp;
            tmpEligible.Clear();
            IReadOnlyList<Pawn> pawns = surface.mapPawns.AllPawnsSpawned;
            int scanned = 0;
            for (int i = 0; i < pawns.Count && scanned < 80; i++)
            {
                Pawn p = pawns[i];
                scanned++;
                if (!IsWildAnimal(p) || p.InMentalState)
                {
                    continue;
                }
                JobDef cur = p.CurJobDef;
                if (cur == JobDefOf.PredatorHunt || cur == ABDefOf.AB_UseStairs)
                {
                    continue;
                }
                if (!p.ComfortableTemperatureRange().Includes(basementTemp))
                {
                    continue;
                }
                tmpEligible.Add(p);
            }
            if (tmpEligible.Count == 0)
            {
                Charge(surface, now + AmbientSpacingTicks / 2);
                return;
            }
            Pawn pick = tmpEligible[Rand.Range(0, tmpEligible.Count)];
            if (TryDescend(pick, basement))
            {
                Charge(surface, now + AmbientSpacingTicks);
                ABLog.Dev("Wild " + pick.LabelShort + " wandering down into the basement.");
            }
            else
            {
                Charge(surface, now + AmbientSpacingTicks / 4);
            }
        }

        private static void Charge(Map surface, int untilTick)
        {
            if (nextAmbient.Count > 64)
            {
                nextAmbient.Clear();
            }
            nextAmbient[surface.uniqueID] = untilTick;
        }

        private static int WildAnimalCount(Map map)
        {
            int count = 0;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (IsWildAnimal(pawns[i]))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>Send one animal down the stairs (roll-free core, used by the scan
        /// and directly by the self-test).</summary>
        internal static bool TryDescend(Pawn p, Map basement)
        {
            try
            {
                if (p == null || !p.Spawned || basement == null || basement.Disposed)
                {
                    return false;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(p, basement, checkReachability: true);
                Building_ABStairs exit = stairs?.CounterpartTowards(basement);
                if (exit == null)
                {
                    return false;
                }
                Job job = CrossLevelWork.MakeStairsJob(stairs, exit);
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.HostileMove, e, "animal ambient descent");
                return false;
            }
        }
    }
}
