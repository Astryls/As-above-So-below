using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Non-player pawns on pocket levels never get stranded. Pocket maps have
    /// no usable edges, so vanilla "leave the map" AI walks forever; and lords
    /// are map-scoped, so nothing vanilla ever routes an NPC through stairs.
    /// A low-frequency scan handles both sides:
    ///  - HOSTILES (raiders, mechs, and their war or pack ANIMALS) with no
    ///    reachable target take the stairs toward the ground and join or start
    ///    an assault lord there. Bashing an immortal link building counts as
    ///    idle. Insects are exempt: a diverted infestation is a self-contained
    ///    basement fight by design.
    ///  - FRIENDLY OR NEUTRAL NPCs (allies, visitors, traders and their
    ///    animals) whose duty says they are trying to leave - or who idle
    ///    around lordless - descend the same way and get an exit-map lord on
    ///    arrival so they walk off the surface like anyone else.
    /// StairTransfer releases every non-player pawn from its map-scoped lord
    /// before the despawn. Kill switch: hostileMove.
    /// </summary>
    internal static class HostileDescend
    {
        /// <summary>Per-scan cap on reachability probes for one pawn: with many
        /// player targets on the map the first reachable one exits early anyway;
        /// the cap only bounds the pathological all-unreachable case.</summary>
        private const int MaxReachChecksPerPawn = 8;

        /// <summary>Scan one pocket level for stuck non-player pawns. Called
        /// from LevelComp on a slow cadence; the loop itself is the gate (one
        /// faction compare per colonist on calm maps).</summary>
        public static void ScanPocketMap(LevelComp comp)
        {
            Map map = comp.map;
            Map home = comp.level > 0 ? comp.lowerMap : comp.upperMap;
            if (home == null || home.Disposed)
            {
                return;
            }
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                if (i >= pawns.Count)
                {
                    continue;
                }
                Pawn p = pawns[i];
                if (p == null || p.Faction == Faction.OfPlayer || p.Dead || p.Downed || !p.Spawned)
                {
                    continue;
                }
                bool hostile = p.HostileTo(Faction.OfPlayer);
                if (hostile ? !ShouldDescendHostile(p) : !ShouldDescendFriendly(p))
                {
                    continue;
                }
                // A stuck ranged hostile that can shoot a player pawn through the gap
                // engages from where it is instead of abandoning the level (parity:
                // a raider on the rooftop shoots down; one under the hole shoots up).
                if (hostile && CrossLevelAutoEngage.TryEngageInsteadOfDescend(p))
                {
                    ABLog.Dev("Hostile " + p.LabelShort + " engages across the gap instead of descending.");
                    continue;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(p, home, checkReachability: true);
                Building_ABStairs exit = stairs?.CounterpartTowards(home);
                if (exit == null)
                {
                    continue;
                }
                Job job = CrossLevelWork.MakeStairsJob(stairs, exit);
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                ABLog.Dev((hostile ? "Hostile " : "NPC ") + p.LabelShort + " descending from level "
                    + comp.level + " via " + stairs.ThingID + ".");
            }
        }

        /// <summary>Surface hostiles with no reachable target CHASE the colony across
        /// levels instead of milling around while their lord gives up: ranged ones
        /// shoot up/down through the gap when they can, everyone else takes the
        /// stairs toward a linked level that has player pawns or buildings and joins
        /// the assault there on arrival (StairTransfer + NoteArrived). This is the
        /// ascent mirror of the pocket-level descent scan - the round-2 "raiders run
        /// instead of chasing upstairs" fix.</summary>
        public static void ScanGroundHostiles(LevelComp comp)
        {
            Map map = comp.map;
            // Cheap gates: any hostiles here, and a linked level with player presence.
            if (map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer).Count == 0)
            {
                return;
            }
            Map dest = null;
            if (HasPlayerPresence(comp.upperMap))
            {
                dest = comp.upperMap;
            }
            else if (HasPlayerPresence(comp.lowerMap))
            {
                dest = comp.lowerMap;
            }
            if (dest == null)
            {
                return;
            }
            int moved = 0;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0 && moved < 8; i--)
            {
                if (i >= pawns.Count)
                {
                    continue;
                }
                Pawn p = pawns[i];
                if (p == null || p.Faction == Faction.OfPlayer || p.Dead || p.Downed || !p.Spawned
                    || !p.HostileTo(Faction.OfPlayer))
                {
                    continue;
                }
                if (!ShouldDescendHostile(p))
                {
                    continue;
                }
                // Ranged and a clear shot through the gap: shoot from here instead.
                if (CrossLevelAutoEngage.TryEngageInsteadOfDescend(p))
                {
                    moved++;
                    continue;
                }
                Building_ABStairs stairs = CrossLevelWork.NearestUsableStairs(p, dest, checkReachability: true);
                Building_ABStairs exit = stairs?.CounterpartTowards(dest);
                if (exit == null)
                {
                    continue;
                }
                Job job = CrossLevelWork.MakeStairsJob(stairs, exit);
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                moved++;
                ABLog.Dev("Hostile " + p.LabelShort + " ascending toward the colony via " + stairs.ThingID + ".");
            }
        }

        private static bool HasPlayerPresence(Map m)
        {
            if (m == null || m.Disposed)
            {
                return false;
            }
            return m.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count > 0
                || m.listerBuildings.allBuildingsColonist.Count > 0;
        }

        internal static bool ShouldDescendHostile(Pawn p)
        {
            // Humanlike raiders, mechs, and faction animals (war beasts, pack
            // animals) cross levels; insects, manhunters (no faction) and
            // entities stay where they spawned.
            bool movableKind = p.RaceProps.Humanlike || p.RaceProps.IsMechanoid
                || (p.RaceProps.Animal && p.Faction != null);
            if (!movableKind || p.RaceProps.Insect || p.Faction == Faction.OfInsects)
            {
                return false;
            }
            if (ABVehicleCompat.IsVehicle(p))
            {
                return false;
            }
            if (p.InMentalState || p.CurJobDef == ABDefOf.AB_UseStairs || ABGiddyUpCompat.IsMounted(p))
            {
                return false;
            }
            return IsIdle(p) && !HasReachableTarget(p);
        }

        /// <summary>Duties that mean "this pawn is trying to get off the map".
        /// Matched by def so visitor groups mid-visit (Idle, WanderClose) are
        /// never yanked away.</summary>
        private static bool WantsToLeave(Pawn p)
        {
            Lord lord = p.GetLord();
            if (lord == null)
            {
                // Lordless NPC drifting on a pocket level has nothing to do up
                // there; idle means stuck.
                return IsIdle(p);
            }
            DutyDef duty = p.mindState?.duty?.def;
            if (duty == null)
            {
                return false;
            }
            return duty == DutyDefOf.TravelOrLeave
                || duty == DutyDefOf.TravelOrWait
                || duty == DutyDefOf.Kidnap
                || duty == DutyDefOf.Steal
                || duty == DutyDefOf.TakeWoundedGuest
                || duty == DutyDefOf.PrisonerEscape
                || ABApi.IsRegisteredExitDuty(duty);
        }

        private static bool ShouldDescendFriendly(Pawn p)
        {
            if (p.InMentalState || p.CurJobDef == ABDefOf.AB_UseStairs || ABGiddyUpCompat.IsMounted(p))
            {
                return false;
            }
            if (ABVehicleCompat.IsVehicle(p))
            {
                return false;
            }
            if (!p.RaceProps.Humanlike && !p.RaceProps.Animal)
            {
                return false;
            }
            // Wild animals get the wander policy (linger below, leave when hungry
            // or done) instead of the NPC leave-duty rules. Two tiers: the polite
            // path waits for an idle moment; hard-overdue visitors (a full extra
            // linger past due - typically squatting on basement food stores,
            // eating and sleeping and never idle at scan time) are interrupted
            // regardless, so wildlife can never accumulate below permanently.
            if (p.Faction == null && p.RaceProps.Animal)
            {
                return (IsIdle(p) && CrossLevelAnimals.WildAnimalWantsOff(p))
                    || CrossLevelAnimals.WildAnimalMustOff(p);
            }
            return WantsToLeave(p);
        }

        internal static bool IsIdle(Pawn p)
        {
            JobDef cur = p.CurJobDef;
            if (cur == null
                || cur == JobDefOf.Wait_Wander
                || cur == JobDefOf.GotoWander
                || cur == JobDefOf.Wait)
            {
                return true;
            }
            // Raid trash AI loves to bash the only player buildings on a pocket
            // level - the stairs. They are immortal now, so a hostile swinging
            // at a link building is going nowhere: treat as idle and re-order.
            if ((cur == JobDefOf.AttackMelee || cur == JobDefOf.AttackStatic)
                && p.CurJob != null && p.CurJob.targetA.Thing is Building_ABStairs)
            {
                return true;
            }
            return false;
        }

        internal static bool HasReachableTarget(Pawn p)
        {
            if (p.Faction == null)
            {
                // Factionless hostiles (manhunter packs): the attack-targets
                // cache has no bucket for a null faction (vanilla warns and
                // returns empty). Any live player pawn on this map is prey
                // enough to keep them here instead of migrating.
                List<Pawn> colony = p.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                for (int i = 0; i < colony.Count; i++)
                {
                    Pawn q = colony[i];
                    if (!q.Dead && !q.Downed && p.CanReach(q, PathEndMode.Touch, Danger.Deadly))
                    {
                        return true;
                    }
                }
                return false;
            }
            HashSet<IAttackTarget> targets = p.Map.attackTargetsCache.TargetsHostileToFaction(p.Faction);
            int checks = 0;
            foreach (IAttackTarget target in targets)
            {
                Thing t = target.Thing;
                // The stairs are immortal - a hostile whose ONLY reachable "target" is a
                // stairwell must not be pinned here bashing it; treating them as no target
                // frees it to route across the levels (melee reach, or no line of fire).
                if (t == null || t.Destroyed || !t.Spawned || target.ThreatDisabled(p)
                    || t is Building_ABStairs)
                {
                    continue;
                }
                if (++checks > MaxReachChecksPerPawn)
                {
                    // Assume something is reachable rather than over-probing.
                    return true;
                }
                if (p.CanReach(t, PathEndMode.Touch, Danger.Deadly))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Pull any non-player pawn out of its map-scoped lord before
        /// the stair transfer despawns it; a lord holding a pawn on another map
        /// corrupts its toil state. Colonists (caravan gathering and the like)
        /// are handled by their own systems and never touched here.</summary>
        public static void NoteLeaving(Pawn p)
        {
            try
            {
                if (p == null || p.Faction == null || p.Faction == Faction.OfPlayer)
                {
                    return;
                }
                p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap, null);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.HostileMove, e, "npc lord release");
            }
        }

        /// <summary>Attach an arrived non-player pawn to a sensible lord on the
        /// new map: hostiles join (or start) the faction's assault; friendlies
        /// and neutrals get an exit-map lord on the ground level so they leave
        /// the colony like any departing guest.</summary>
        public static void NoteArrived(Pawn p, Map map)
        {
            try
            {
                if (p == null || map == null || p.Faction == null || p.Faction == Faction.OfPlayer)
                {
                    return;
                }
                if (p.GetLord() != null)
                {
                    return;
                }
                if (p.HostileTo(Faction.OfPlayer))
                {
                    if (!p.RaceProps.Humanlike && !p.RaceProps.IsMechanoid && !p.RaceProps.Animal)
                    {
                        return;
                    }
                    List<Lord> lords = map.lordManager.lords;
                    for (int i = 0; i < lords.Count; i++)
                    {
                        Lord lord = lords[i];
                        if (lord.faction == p.Faction && lord.LordJob is LordJob_AssaultColony)
                        {
                            lord.AddPawn(p);
                            return;
                        }
                    }
                    LordMaker.MakeNewLord(p.Faction, new LordJob_AssaultColony(p.Faction), map, Gen.YieldSingle(p));
                    return;
                }
                // Friendly or neutral: only hand out exit lords on the ground
                // level (a pocket arrival gets re-sent by the next scan).
                if (map.Levels()?.level != 0)
                {
                    return;
                }
                LordMaker.MakeNewLord(p.Faction,
                    new LordJob_ExitMapBest(LocomotionUrgency.Jog, canDig: false, canDefendSelf: true),
                    map, Gen.YieldSingle(p));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.HostileMove, e, "npc lord join");
            }
        }
    }
}
