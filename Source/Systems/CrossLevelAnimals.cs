using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

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

        /// <summary>Randomized per-animal stay length. Unsigned math: for large
        /// thingIDNumbers the multiplication wraps negative, and a signed
        /// remainder then produced a leave tick in the PAST (bug report
        /// 2026-07-24 family: wander timing misbehaving on long saves).</summary>
        private static int LingerWindow(Pawn p)
        {
            return LingerTicksBase / 2
                + (int)((uint)(p.thingIDNumber * 977) % (uint)LingerTicksBase);
        }

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

        [ABGameReset]
        internal static void ClearAll()
        {
            leaveAfter.Clear();
            nextAmbient.Clear();
            // petTrips is SCRIBED state: FinalizeInit runs after load-scribe-in
            // (flag set - keep the restored records) and on new game (no flag -
            // clear any leftovers from a previously loaded session).
            if (!petTripsScribedIn)
            {
                petTrips.Clear();
            }
            petTripsScribedIn = false;
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
            leaveAfter[p.thingIDNumber] = now + LingerWindow(p);
            return false;
        }

        /// <summary>Hard overdue: a wild visitor a FULL extra linger past its
        /// leave tick gets interrupted even mid-meal or mid-sleep. Without this
        /// an animal camped on basement food stores (never hungry, rarely idle
        /// at scan moments) could squat indefinitely - the "animals go down and
        /// never come back" report (2026-07-24). Registers first-sighted
        /// animals (cavern spawns, pre-fix strays) so the clock always runs.</summary>
        internal static bool WildAnimalMustOff(Pawn p)
        {
            if (!Enabled)
            {
                return false;
            }
            int now = Find.TickManager.TicksGame;
            if (leaveAfter.TryGetValue(p.thingIDNumber, out int at))
            {
                return now >= at + LingerTicksBase;
            }
            if (leaveAfter.Count > 256)
            {
                leaveAfter.Clear();
            }
            leaveAfter[p.thingIDNumber] = now + LingerWindow(p);
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
            leaveAfter[p.thingIDNumber] = Find.TickManager.TicksGame + LingerWindow(p);
        }

        // --- pet food-trip returns (2026-07-24) -------------------------------
        //
        // The cross-level food giver may ship a player pet to another level for
        // a meal (EligiblePetForFood, T7 #6). The trip was one-way: nothing in
        // vanilla or the mod ever routed the pet back, so basements accumulated
        // pets one hunger cycle at a time. Every migration now records the home
        // map; once the pet is fed and idle it walks back, hop by hop. Only
        // pets WE moved are returned - a pet the player walked downstairs is
        // never touched. Records expire after a day and die with the pawn's
        // record entry (bounded store).

        private const int PetTripExpireTicks = 60000;

        /// <summary>Fed enough that the return trip will not immediately bounce
        /// on a new hunger migration.</summary>
        private const float PetFedThreshold = 0.35f;

        private struct PetTrip
        {
            public int homeMapId;
            public int expireTick;
        }

        private static readonly Dictionary<int, PetTrip> petTrips = new Dictionary<int, PetTrip>();

        private static bool petTripsScribedIn;

        /// <summary>Pet trips survive save/load (called from ABGameComp
        /// .ExposeData). Without this a save between the meal and the walk
        /// home stranded the pet permanently: after load it eats LOCALLY on
        /// the level it was left on, so no new migration record ever forms
        /// and nothing ever sends it back. Parallel value lists because the
        /// record is a two-field struct.</summary>
        [ABGameExpose]
        internal static void ExposePetTrips()
        {
            List<int> ids = null;
            List<int> homes = null;
            List<int> expiries = null;
            if (Scribe.mode == LoadSaveMode.Saving && petTrips.Count > 0)
            {
                ids = new List<int>();
                homes = new List<int>();
                expiries = new List<int>();
                foreach (KeyValuePair<int, PetTrip> kvp in petTrips)
                {
                    ids.Add(kvp.Key);
                    homes.Add(kvp.Value.homeMapId);
                    expiries.Add(kvp.Value.expireTick);
                }
            }
            Scribe_Collections.Look(ref ids, "abPetTripIds", LookMode.Value);
            Scribe_Collections.Look(ref homes, "abPetTripHomes", LookMode.Value);
            Scribe_Collections.Look(ref expiries, "abPetTripExpiries", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                petTrips.Clear();
                petTripsScribedIn = true;
                if (ids != null && homes != null && expiries != null)
                {
                    int n = Math.Min(ids.Count, Math.Min(homes.Count, expiries.Count));
                    for (int i = 0; i < n; i++)
                    {
                        petTrips[ids[i]] = new PetTrip
                        {
                            homeMapId = homes[i],
                            expireTick = expiries[i]
                        };
                    }
                }
            }
        }

        /// <summary>Zero-cost tick gate: any outstanding pet trips at all.</summary>
        internal static bool AnyPetTrips => petTrips.Count > 0;

        /// <summary>Called by the food giver when it ships a player pet to a
        /// linked level; home is the map the pawn stands on right now.</summary>
        internal static void NotePetFoodTrip(Pawn pawn, Map home)
        {
            if (pawn == null || home == null)
            {
                return;
            }
            if (petTrips.Count > 256)
            {
                petTrips.Clear();
            }
            petTrips[pawn.thingIDNumber] = new PetTrip
            {
                homeMapId = home.uniqueID,
                expireTick = Find.TickManager.TicksGame + PetTripExpireTicks
            };
        }

        /// <summary>One scan over this level's player animals: any pet we
        /// food-shipped here goes home once fed and idle. Runs on every level
        /// (food trips can go up or down); gated on the Logistics kill switch
        /// but NOT the crossLevelNeeds setting - returning misplaced pets is
        /// cleanup and must work even after the player turns the feature off.</summary>
        public static void ScanPetReturns(LevelComp comp)
        {
            if (petTrips.Count == 0 || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            Map map = comp.map;
            int now = Find.TickManager.TicksGame;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                if (i >= pawns.Count)
                {
                    continue;
                }
                Pawn p = pawns[i];
                if (p?.RaceProps == null || !p.RaceProps.Animal || p.Faction != Faction.OfPlayer
                    || p.Dead || p.Downed || !p.Spawned)
                {
                    continue;
                }
                if (!petTrips.TryGetValue(p.thingIDNumber, out PetTrip trip))
                {
                    continue;
                }
                if (now >= trip.expireTick)
                {
                    petTrips.Remove(p.thingIDNumber);
                    continue;
                }
                if (trip.homeMapId == map.uniqueID)
                {
                    // Made it home: trip closed.
                    petTrips.Remove(p.thingIDNumber);
                    continue;
                }
                if (AnimalPenUtility.NeedsToBeManagedByRope(p))
                {
                    // Became a pen animal since the trip: pen logic owns it now.
                    petTrips.Remove(p.thingIDNumber);
                    continue;
                }
                // Only interrupt a fed, idle pet. A hungry one is about to use
                // the food it came for; a sleeping one returns after waking
                // (LayDown is not idle). Lord membership means a caravan or
                // ritual claimed it - leave that alone entirely.
                Need_Food food = p.needs?.food;
                if (food != null && food.CurLevelPercentage < PetFedThreshold)
                {
                    continue;
                }
                if (p.InMentalState || p.CurJobDef == ABDefOf.AB_UseStairs
                    || p.GetLord() != null || !HostileDescend.IsIdle(p))
                {
                    continue;
                }
                Map home = FindColumnMap(map, trip.homeMapId);
                if (home == null)
                {
                    petTrips.Remove(p.thingIDNumber);
                    continue;
                }
                int dir = Math.Sign(home.Level() - map.Level());
                Map next = dir > 0 ? comp.upperMap : dir < 0 ? comp.lowerMap : null;
                if (next == null)
                {
                    continue;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(p, next, checkReachability: true);
                Building_ABStairs exit = stairs?.CounterpartTowards(next);
                if (exit == null)
                {
                    continue;
                }
                Job job = CrossLevelWork.MakeStairsJob(stairs, exit);
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                ABLog.Dev("Pet " + p.LabelShort + " heading home to level " + home.Level()
                    + " after its meal trip.");
            }
        }

        private static Map FindColumnMap(Map from, int uniqueId)
        {
            LevelComp controller = from.Controller();
            if (controller == null)
            {
                return null;
            }
            foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
            {
                Map m = kvp.Value;
                if (m != null && !m.Disposed && m.uniqueID == uniqueId)
                {
                    return m;
                }
            }
            return null;
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
