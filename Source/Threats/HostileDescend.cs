using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Hostiles on pocket levels do not idle forever when nothing up (or down)
    /// there is left to fight: a low-frequency scan sends stuck raiders through
    /// the stairs toward the ground level, and the stair transfer moves them
    /// between per-map assault lords (vanilla lords cannot span maps). Insects
    /// are exempt - a diverted infestation is a self-contained basement fight
    /// by design. Kill switch: hostileMove.
    /// </summary>
    internal static class HostileDescend
    {
        /// <summary>Per-scan cap on reachability probes for one pawn: with many
        /// player targets on the map the first reachable one exits early anyway;
        /// the cap only bounds the pathological all-unreachable case.</summary>
        private const int MaxReachChecksPerPawn = 8;

        /// <summary>Scan one pocket level for stuck hostiles. Called from
        /// LevelComp on a slow cadence, only after a cheap any-hostiles gate.</summary>
        public static void ScanPocketMap(LevelComp comp)
        {
            Map map = comp.map;
            if (!GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer))
            {
                return;
            }
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
                if (!IsMovableHostile(p) || !IsIdle(p) || HasReachableTarget(p))
                {
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
                ABLog.Dev("Hostile " + p.LabelShort + " descending from level " + comp.level + " via " + stairs.ThingID + ".");
            }
        }

        private static bool IsMovableHostile(Pawn p)
        {
            if (p == null || p.Dead || p.Downed || !p.Spawned)
            {
                return false;
            }
            if (!p.HostileTo(Faction.OfPlayer))
            {
                return false;
            }
            // Humanlike raiders and mechs cross levels; insects, manhunting
            // animals and entities stay where they spawned.
            if (!p.RaceProps.Humanlike && !p.RaceProps.IsMechanoid)
            {
                return false;
            }
            if (p.InMentalState)
            {
                // Mental-state think trees override forced jobs; the pawn is
                // caught by a later scan once the state ends.
                return false;
            }
            if (p.CurJobDef == ABDefOf.AB_UseStairs)
            {
                return false;
            }
            if (ABGiddyUpCompat.BlockForMount(p))
            {
                return false;
            }
            return true;
        }

        private static bool IsIdle(Pawn p)
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

        private static bool HasReachableTarget(Pawn p)
        {
            HashSet<IAttackTarget> targets = p.Map.attackTargetsCache.TargetsHostileToFaction(p.Faction);
            int checks = 0;
            foreach (IAttackTarget target in targets)
            {
                Thing t = target.Thing;
                if (t == null || t.Destroyed || !t.Spawned || target.ThreatDisabled(p))
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

        /// <summary>Pull a hostile out of its map-scoped lord before the stair
        /// transfer despawns it; a lord holding a pawn on another map corrupts
        /// its toil state. No-op for anyone else.</summary>
        public static void NoteLeaving(Pawn p)
        {
            try
            {
                if (p == null || p.Faction == null || !p.HostileTo(Faction.OfPlayer))
                {
                    return;
                }
                p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap, null);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.HostileMove, e, "hostile lord release");
            }
        }

        /// <summary>Attach an arrived hostile to an assault lord on the new map:
        /// join the faction's existing assault if one is running, otherwise
        /// start one. Keeps diverted raiders coherent as they trickle down the
        /// stairs instead of drifting lordless.</summary>
        public static void NoteArrived(Pawn p, Map map)
        {
            try
            {
                if (p == null || map == null || p.Faction == null || !p.HostileTo(Faction.OfPlayer))
                {
                    return;
                }
                if (!p.RaceProps.Humanlike && !p.RaceProps.IsMechanoid)
                {
                    return;
                }
                if (p.GetLord() != null)
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
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.HostileMove, e, "hostile lord join");
            }
        }
    }
}
