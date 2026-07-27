using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Stranded-neutral safety net (parity pass 2026-07-24). Pocket levels
    /// have rock for map edges, so a friendly NPC (visitor who wandered down,
    /// a guest whose group left without it, a quest pawn oddly placed) can
    /// never leave the map on its own - stuck forever, faction anger over
    /// time. On a slow cadence, any lord-less friendly humanlike on a
    /// non-surface level with no column lord claiming it gets walked to the
    /// surface, where vanilla exit behavior resumes (edge reachable again).
    ///
    /// Hostiles are HostileDescend's business; guests with a live visit lord
    /// on another level are ABHospitalityCompat's. Kill switch: social.
    /// </summary>
    internal static class ABNeutralExit
    {
        private const int ScanIntervalTicks = 600;

        private static int due;

        private static readonly ABPawnCooldown routeCooldown = new ABPawnCooldown();

        /// <summary>The lord (on any level of this column) whose ownedPawns
        /// still contains the pawn, or null. Membership survives our stair
        /// rides via the retention patch, but map-scoped GetLord cannot see
        /// it - this can.</summary>
        internal static Lord LordMembershipInColumn(Pawn p, Map anyColumnMap)
        {
            Map ground = anyColumnMap.GroundMap();
            if (ground == null)
            {
                return null;
            }
            LevelComp groundComp = ground.Levels();
            Map[] column = { ground, groundComp?.upperMap, groundComp?.lowerMap };
            for (int i = 0; i < column.Length; i++)
            {
                Map m = column[i];
                if (m == null || m.Disposed)
                {
                    continue;
                }
                List<Lord> lords = m.lordManager.lords;
                for (int j = 0; j < lords.Count; j++)
                {
                    if (lords[j] != null && lords[j].ownedPawns.Contains(p))
                    {
                        return lords[j];
                    }
                }
            }
            return null;
        }

        [ABGameTick(60)]
        internal static void Tick()
        {
            if (!ABGuard.On(ABGuard.Social))
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now < due)
            {
                return;
            }
            due = now + ScanIntervalTicks;
            try
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    LevelComp comp = map?.Levels();
                    if (comp == null || comp.level == 0 || map.Disposed)
                    {
                        continue;
                    }
                    Map ground = comp.groundMap;
                    if (ground == null || ground.Disposed)
                    {
                        continue;
                    }
                    SweepMap(map, ground, now);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Social, e, "stranded neutral sweep");
            }
        }

        private static void SweepMap(Map map, Map ground, int now)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || !p.Spawned || !p.RaceProps.Humanlike
                    || p.Faction == null || p.Faction == Faction.OfPlayer
                    || p.HostileTo(Faction.OfPlayer) || p.IsPrisoner || p.Downed
                    || p.InMentalState || !p.Awake()
                    || p.CurJobDef == ABDefOf.AB_UseStairs
                    || ABGiddyUpCompat.IsMounted(p))
                {
                    continue;
                }
                if (map.lordManager.LordOf(p) != null)
                {
                    continue; // a lord on THIS level owns its behavior
                }
                if (LordMembershipInColumn(p, map) != null)
                {
                    continue; // roamed guest: the hospitality sweep owns it
                }
                if (!routeCooldown.Ready(p, now))
                {
                    continue;
                }
                Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(p, ground);
                Building_ABStairs exit = entry?.CounterpartTowards(ground);
                if (exit == null)
                {
                    routeCooldown.ChargeUntil(p, now + ScanIntervalTicks * 4);
                    continue;
                }
                Job job = CrossLevelWork.MakeStairsJob(entry, exit);
                if (job != null)
                {
                    routeCooldown.ChargeUntil(p, now + ScanIntervalTicks * 2);
                    p.jobs?.StartJob(job, JobCondition.InterruptForced);
                    ABLog.Dev("Walking stranded neutral " + p.LabelShort + " back to the surface.");
                }
            }
        }
    }
}
