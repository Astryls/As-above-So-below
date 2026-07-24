using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// AI-side cross-level combat (vanilla parity: "as if one big map"):
    ///  - HOSTILES with a ranged weapon and no reachable target on their own level
    ///    fire up/down through open air at player pawns on the paired level, walking
    ///    to the hole's edge if needed. Hostiles who can still reach a same-map
    ///    target keep vanilla behavior untouched.
    ///  - DRAFTED colonists with fire-at-will enabled return fire across the gap on
    ///    their own, from where they stand (vanilla drafted pawns hold position).
    ///
    /// Cost model: one scan per 250 ticks per column pair, driven from the sky
    /// LevelComp; each scan walks the pawn lists once with one-field filters and
    /// hard caps on sight-line probes; a per-pawn cooldown stops re-probing pawns
    /// with no shot. Zero cost when no sky level or no pawns. Kill switch:
    /// ABGuard.Combat, plus the crossLevelCombat + crossLevelAutoEngage settings.
    /// </summary>
    internal static class CrossLevelAutoEngage
    {
        private const int FailCooldownTicks = 700;

        /// <summary>Max hostiles given engage jobs per scan (per direction) and max
        /// candidate targets probed per shooter; bounds worst-case raycasts.</summary>
        private const int MaxEngagesPerScan = 4;

        private const int MaxTargetProbes = 4;

        private static readonly ABPawnCooldown cooldown = new ABPawnCooldown();

        private static readonly List<Pawn> tmpTargets = new List<Pawn>();

        internal static bool AutoEngageEnabled
        {
            get
            {
                ABSettings s = ABMod.Settings;
                return CrossLevelCombat.Enabled && s != null && s.crossLevelAutoEngage;
            }
        }

        /// <summary>Scan one sky/surface pair in both directions. Called from the sky
        /// map's LevelComp on the slow cadence.</summary>
        public static void ScanPair(Map sky, Map surface)
        {
            if (!AutoEngageEnabled || sky == null || surface == null
                || sky.Disposed || surface.Disposed)
            {
                return;
            }
            ScanHostiles(surface, sky);
            ScanHostiles(sky, surface);
            CrossLevelTurret.AcquireAuto(sky, surface);
        }

        /// <summary>Give one stuck hostile a chance to shoot across the gap instead of
        /// descending the stairs. Called from HostileDescend right before it orders the
        /// descent; false = descend as before.</summary>
        public static bool TryEngageInsteadOfDescend(Pawn p)
        {
            if (!AutoEngageEnabled || p?.Map == null)
            {
                return false;
            }
            Map other = PairedMap(p.Map);
            if (other == null)
            {
                return false;
            }
            return TryEngageAcross(p, other, allowReposition: true);
        }

        private static Map PairedMap(Map map)
        {
            LevelComp comp = map.Levels();
            if (comp == null)
            {
                return null;
            }
            if (comp.level == 1)
            {
                return comp.lowerMap;
            }
            return comp.level == 0 ? comp.upperMap : null;
        }

        private static void ScanHostiles(Map shooterMap, Map targetMap)
        {
            // No player pawns on the target level means nothing to shoot at: skip the
            // whole direction before touching any hostile (the common case - nobody
            // lives on the sky level of most columns).
            if (targetMap.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count == 0)
            {
                return;
            }
            IReadOnlyList<Pawn> pawns = shooterMap.mapPawns.AllPawnsSpawned;
            int engaged = 0;
            int now = Find.TickManager.TicksGame;
            for (int i = pawns.Count - 1; i >= 0 && engaged < MaxEngagesPerScan; i--)
            {
                if (i >= pawns.Count)
                {
                    continue;
                }
                Pawn p = pawns[i];
                if (p == null || p.Dead || p.Downed || !p.Spawned
                    || p.Faction == Faction.OfPlayer || !p.HostileTo(Faction.OfPlayer))
                {
                    continue;
                }
                if (p.InMentalState || ABVehicleCompat.IsVehicle(p))
                {
                    continue;
                }
                JobDef cur = p.CurJobDef;
                if (cur == ABDefOf.AB_CrossLevelAttack || cur == ABDefOf.AB_UseStairs)
                {
                    continue;
                }
                if (!cooldown.Ready(p, now))
                {
                    continue;
                }
                if (CrossLevelCombat.GetRangedVerb(p) == null)
                {
                    cooldown.ChargeUntil(p, now + FailCooldownTicks * 2);
                    continue;
                }
                // ONE-MAP acquisition: a hostile fights whoever is nearest across BOTH
                // levels. Engage across the gap only when the paired-level target is
                // closer than the hostile's nearest same-map enemy - so a raider is
                // never yanked off a closer same-map fight, but it WILL shoot up/down
                // the instant the cross-level target is the nearest thing it can hit,
                // even with (farther) same-map targets available.
                float sameMapNearSq = NearestSameMapEnemyDistSq(p);
                if (TryEngageAcross(p, targetMap, allowReposition: true, sameMapNearSq))
                {
                    engaged++;
                    ABLog.Dev("Hostile " + p.LabelShort + " auto-engaging across the gap.");
                }
                else
                {
                    cooldown.ChargeUntil(p, now + FailCooldownTicks);
                }
            }
        }

        /// <summary>Event-path cadence gate for the drafted-overwatch hook: vanilla's
        /// CheckForAutoAttack runs every ~25 ticks per waiting pawn; the cross-level
        /// probe (a pawn-list sort) only pays out this often per pawn.</summary>
        private const int OverwatchRetryTicks = 60;

        /// <summary>Drafted overwatch return fire, called from the CheckForAutoAttack
        /// postfix at vanilla's own cadence - the moment vanilla finds nothing to
        /// shoot on the pawn's map. This is what makes drafted colonists react like
        /// vanilla instead of on the slow scan.</summary>
        internal static void TryOverwatchCrossFire(Pawn p)
        {
            try
            {
                if (!AutoEngageEnabled || p == null || !p.Spawned || p.Dead || p.Downed)
                {
                    return;
                }
                if (!p.Drafted || p.drafter == null || !p.drafter.FireAtWill || p.InMentalState)
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!cooldown.Ready(p, now))
                {
                    return;
                }
                Map other = PairedMap(p.Map);
                if (other == null || other.Disposed)
                {
                    cooldown.ChargeUntil(p, now + FailCooldownTicks * 2);
                    return;
                }
                // Vanilla just ran its own target search and found nothing shootable
                // on this map; a nearby unshootable same-map threat still outranks the
                // gap (do not blind the pawn to closer danger behind cover).
                if (HasNearbySameMapThreat(p))
                {
                    cooldown.ChargeUntil(p, now + OverwatchRetryTicks);
                    return;
                }
                if (CrossLevelCombat.GetRangedVerb(p) == null)
                {
                    cooldown.ChargeUntil(p, now + FailCooldownTicks * 2);
                    return;
                }
                // Hold position like vanilla drafted overwatch: current cell only.
                if (TryEngageAcross(p, other, allowReposition: false))
                {
                    ABLog.Dev("Colonist " + p.LabelShort + " returning fire across the gap.");
                }
                else
                {
                    cooldown.ChargeUntil(p, now + OverwatchRetryTicks);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "overwatch cross fire");
            }
        }

        /// <summary>Squared horizontal distance to the nearest live same-map enemy of
        /// <paramref name="p"/>, or float.MaxValue if it has none. Used as the one-map
        /// tie-breaker: a cross-level target is only taken when it is closer than this.
        /// Both levels share plumb coordinates, so a straight horizontal compare is a
        /// fair "which threat is nearest" across the gap.</summary>
        private static float NearestSameMapEnemyDistSq(Pawn p)
        {
            float best = float.MaxValue;
            IntVec3 pos = p.Position;
            if (p.Faction == null)
            {
                // Factionless (manhunters): no cache bucket; the live player pawns are
                // the threat set that matters to them.
                List<Pawn> colony = p.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                for (int i = 0; i < colony.Count; i++)
                {
                    Pawn q = colony[i];
                    if (q == null || q.Dead || q.Downed)
                    {
                        continue;
                    }
                    float d = (q.Position - pos).LengthHorizontalSquared;
                    if (d < best)
                    {
                        best = d;
                    }
                }
                return best;
            }
            int checks = 0;
            foreach (IAttackTarget tgt in p.Map.attackTargetsCache.TargetsHostileToFaction(p.Faction))
            {
                if (++checks > 128)
                {
                    break;
                }
                Thing thing = tgt.Thing;
                if (thing == null || thing.Destroyed || !thing.Spawned || tgt.ThreatDisabled(p))
                {
                    continue;
                }
                float d = (thing.Position - pos).LengthHorizontalSquared;
                if (d < best)
                {
                    best = d;
                }
            }
            return best;
        }

        /// <summary>Any live hostile on the pawn's own map within overwatch range
        /// (capped walk over the attack-targets cache; a couple of field reads per
        /// entry, no pathfinding).</summary>
        private static bool HasNearbySameMapThreat(Pawn p)
        {
            const float RangeSq = 40f * 40f;
            if (p.Faction == null)
            {
                // Factionless hostiles (manhunters): no cache bucket for a null
                // faction (vanilla warns). Nearby live player pawns are the
                // threat set that matters to them.
                List<Pawn> colony = p.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                for (int i = 0; i < colony.Count; i++)
                {
                    Pawn q = colony[i];
                    if (!q.Dead && !q.Downed
                        && (q.Position - p.Position).LengthHorizontalSquared <= RangeSq)
                    {
                        return true;
                    }
                }
                return false;
            }
            int checks = 0;
            foreach (IAttackTarget t in p.Map.attackTargetsCache.TargetsHostileToFaction(p.Faction))
            {
                if (++checks > 10)
                {
                    // Plenty of hostiles around: definitely vanilla's problem.
                    return true;
                }
                Thing thing = t.Thing;
                if (thing == null || thing.Destroyed || !thing.Spawned || t.ThreatDisabled(p))
                {
                    continue;
                }
                if ((thing.Position - p.Position).LengthHorizontalSquared <= RangeSq)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Nearest-first probe of enemy targets on the other level; hands the
        /// shooter to the shared attack job on the first target with a clear gap line.</summary>
        internal static bool TryEngageAcross(Pawn shooter, Map targetMap, bool allowReposition,
            float maxDistSq = float.MaxValue)
        {
            Verb_LaunchProjectile verb = CrossLevelCombat.GetRangedVerb(shooter);
            if (verb == null || targetMap == null || targetMap.Disposed)
            {
                return false;
            }
            bool shooterHostile = shooter.HostileTo(Faction.OfPlayer);
            tmpTargets.Clear();
            IReadOnlyList<Pawn> candidates = targetMap.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < candidates.Count; i++)
            {
                Pawn t = candidates[i];
                if (t == null || t.Dead || !t.Spawned)
                {
                    continue;
                }
                // Hostiles shoot the player's pawns; colonists shoot hostiles.
                if (shooterHostile)
                {
                    if (t.Faction != Faction.OfPlayer || t.Downed)
                    {
                        continue;
                    }
                }
                else if (!t.HostileTo(Faction.OfPlayer) || t.Downed)
                {
                    continue;
                }
                tmpTargets.Add(t);
            }
            if (tmpTargets.Count == 0)
            {
                return false;
            }
            IntVec3 origin = shooter.Position;
            tmpTargets.Sort((a, b) =>
                (a.Position - origin).LengthHorizontalSquared.CompareTo((b.Position - origin).LengthHorizontalSquared));
            int probes = Math.Min(tmpTargets.Count, MaxTargetProbes);
            for (int i = 0; i < probes; i++)
            {
                Pawn t = tmpTargets[i];
                // One-map distance gate (candidates are sorted nearest-first, so once
                // one is farther than the same-map rival, all the rest are too).
                if ((t.Position - origin).LengthHorizontalSquared >= maxDistSq)
                {
                    break;
                }
                if (!allowReposition
                    && !CrossLevelCombat.CanFireFrom(shooter.Map, shooter.Position, t, verb, out _))
                {
                    continue;
                }
                if (CrossLevelCombat.TryStartAutoEngage(shooter, t, allowReposition))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Vanilla-cadence hook for drafted overwatch: CheckForAutoAttack runs
    /// every ~25 ticks for a waiting pawn and has just failed to find a same-map
    /// shot when our postfix runs (a vanilla engagement puts the pawn in a busy
    /// stance, checked first). Cross-level return fire reacts at vanilla speed.</summary>
    [HarmonyPatch(typeof(JobDriver_Wait), "CheckForAutoAttack")]
    internal static class Patch_JobDriverWait_CrossLevelOverwatch
    {
        private static void Postfix(JobDriver_Wait __instance)
        {
            if (!ABGuard.On(ABGuard.Combat))
            {
                return;
            }
            Pawn pawn = __instance.pawn;
            if (pawn == null || pawn.stances?.curStance is Stance_Busy)
            {
                // Vanilla found something to shoot (or is mid-attack): its fight.
                return;
            }
            CrossLevelAutoEngage.TryOverwatchCrossFire(pawn);
        }
    }
}
