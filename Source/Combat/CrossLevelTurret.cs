using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Cross-level fire for ALL projectile turrets - vanilla and modded, any faction.
    /// Capability-based, no named-mod code:
    ///   - ARC verbs (flyOverhead: mortars, artillery, ICBM-class launchers) lob over
    ///     everything. Sky->surface needs the TARGET column open (shell falls through
    ///     the hole, vanilla roof punch on impact); surface->sky needs the SHOOTER
    ///     column open. Cells or things; vanilla forced-miss scatter.
    ///   - DIRECT verbs (autocannons, uranium slug, mini-turrets, modded laser or
    ///     charge turrets with projectiles) use the same gap line-of-fire rules as
    ///     pawns: column exposure + a clear sight line on the sky plane + range, with
    ///     the mirrored ShotReport accuracy (ShootingAccuracyTurret via
    ///     HitFactorFromShooter's Thing path). Things only.
    ///
    /// Vanilla cannot hold a cross-map forced target (Building_Turret.Tick clears it),
    /// and its burst machinery is same-map, so this system keeps its own store and a
    /// TICK-ACCURATE state machine (warmup -> burst at ticksBetweenBurstShots ->
    /// cooldown, all values from the turret's own def and verb). The per-tick driver
    /// early-outs on a single static count when no cross-level order exists - zero
    /// recurring cost when idle. Acquisition:
    ///   - player-ordered via the targeter (only turrets whose own gizmo allows forced
    ///     targeting ever reach us - vanilla permission model untouched);
    ///   - AUTO on the 250-tick scan: an idle, ready turret of ANY faction acquires the
    ///     nearest enemy pawn on the paired level (enemy siege mortars will bombard sky
    ///     platforms). Local fights always take precedence; auto entries drop when the
    ///     line is lost. Gated by crossLevelAutoEngage.
    /// Orders are NOT saved across save/load (static state cleared on game load).
    /// Everything fails open under ABGuard.Combat.
    /// </summary>
    internal static class CrossLevelTurret
    {
        private enum Phase
        {
            Warmup,
            Burst,
            Cooldown
        }

        private sealed class Entry
        {
            public Building_Turret turret;
            public LocalTargetInfo target;
            public Map targetMap;
            public bool arc;
            public bool auto;
            public Phase phase;
            public int nextEventTick;
            public int burstShotsLeft;
            public int revalidateAt;
        }

        private const int RevalidateInterval = 30;

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

        /// <summary>Per-turret retry cooldown for failed auto-acquisition probes.</summary>
        private static readonly Dictionary<int, int> nextAutoTry = new Dictionary<int, int>();

        private static readonly List<int> tmpDead = new List<int>();

        private static readonly List<Pawn> tmpTargets = new List<Pawn>();

        /// <summary>The turret's projectile-launching verb, or null (beam and other
        /// non-projectile turrets are not simulated across the gap).</summary>
        internal static Verb_LaunchProjectile LauncherVerb(Building_Turret turret)
        {
            return turret?.AttackVerb as Verb_LaunchProjectile;
        }

        /// <summary>Arc classification must NOT depend on the loaded shell: an
        /// unloaded mortar's CompChangeableProjectile reports a null projectile
        /// (run-3 self-test failure - the mortar classified as direct-fire and cell
        /// orders were rejected). A lobbing weapon is identified by its verb props:
        /// no line-of-sight requirement (the mortar-class signature), or a fly-over
        /// projectile either loaded or default.</summary>
        internal static bool IsArc(Verb_LaunchProjectile verb)
        {
            if (verb == null)
            {
                return false;
            }
            if (verb.Projectile?.projectile?.flyOverhead ?? false)
            {
                return true;
            }
            if (verb.verbProps.defaultProjectile?.projectile?.flyOverhead ?? false)
            {
                return true;
            }
            return !verb.verbProps.requireLineOfSight;
        }

        /// <summary>Direct-fire check for a turret; footprint-aware via the shared
        /// CanCrossGapFire (which probes every occupied cell).</summary>
        internal static bool TurretCanFire(Building_Turret turret, Thing target,
            Verb_LaunchProjectile verb, out CrossLevelCombat.GapShot shot)
        {
            return CrossLevelCombat.CanCrossGapFire(turret, target, verb, out shot);
        }

        /// <summary>Player order from the targeter click. Arc verbs accept cells or
        /// things; direct verbs need a thing. Manning is not required to SET the order
        /// (vanilla allows pre-targeting too); firing checks it.</summary>
        internal static bool TryOrder(Building_Turret turret, LocalTargetInfo target, Map targetMap)
        {
            try
            {
                Verb_LaunchProjectile verb = LauncherVerb(turret);
                if (verb == null || !target.IsValid || targetMap == null)
                {
                    return false;
                }
                bool arc = IsArc(verb);
                if (arc)
                {
                    if (!CrossLevelCombat.CanArcFireAt(turret.Map, turret.Position, target.Cell, targetMap, verb, out _))
                    {
                        Messages.Message("AB_NoArcPath".Translate(), turret, MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                }
                else
                {
                    if (!target.HasThing || !TurretCanFire(turret, target.Thing, verb, out _))
                    {
                        Messages.Message("AB_NoGapLine".Translate(), turret, MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                }
                Store(turret, target, targetMap, arc, auto: false);
                SoundDefOf.TurretAcquireTarget?.PlayOneShot(new TargetInfo(turret.Position, turret.Map, false));
                Messages.Message((arc ? "AB_MortarTargetSet" : "AB_TurretTargetSet").Translate(turret.LabelShort),
                    turret, MessageTypeDefOf.SilentInput, historical: false);
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "cross level turret order");
                return false;
            }
        }

        private static void Store(Building_Turret turret, LocalTargetInfo target, Map targetMap, bool arc, bool auto)
        {
            if (entries.Count > 128)
            {
                entries.Clear();
            }
            entries[turret.thingIDNumber] = new Entry
            {
                turret = turret,
                target = target,
                targetMap = targetMap,
                arc = arc,
                auto = auto,
                phase = Phase.Warmup,
                nextEventTick = Find.TickManager.TicksGame + WarmupTicks(turret),
                revalidateAt = Find.TickManager.TicksGame + RevalidateInterval
            };
        }

        internal static bool HasOrder(Building_Turret turret, out LocalTargetInfo target, out Map targetMap)
        {
            target = LocalTargetInfo.Invalid;
            targetMap = null;
            if (turret == null || !entries.TryGetValue(turret.thingIDNumber, out Entry e))
            {
                return false;
            }
            target = e.target;
            targetMap = e.targetMap;
            return true;
        }

        internal static void Cancel(Building_Turret turret)
        {
            if (turret != null)
            {
                entries.Remove(turret.thingIDNumber);
            }
        }

        /// <summary>Drop every stored order and retry gate (game load/start).</summary>
        internal static void ClearAll()
        {
            entries.Clear();
            nextAutoTry.Clear();
        }

        // --- auto-acquisition --------------------------------------------------

        /// <summary>Event-path retry gate: vanilla calls TryFindNewTarget every 15
        /// ticks while a turret is idle and ready; our cross-level probe piggybacks
        /// that call but only pays the target sort this often per turret.</summary>
        private const int EventRetryTicks = 120;

        private const int AutoRetryTicks = 700;

        private const int MaxAutoAcquiresPerScan = 3;

        /// <summary>Vanilla-cadence acquisition: called from the TryFindNewTarget
        /// postfix the moment a ready, idle turret finds NOTHING on its own map -
        /// exactly when vanilla would give up. Reaction time matches vanilla's own
        /// 15-tick hunt instead of the 250-tick scan.</summary>
        internal static void TryAutoAcquire(Building_TurretGun turret)
        {
            try
            {
                if (!CrossLevelAutoEngage.AutoEngageEnabled || turret == null || !turret.Spawned)
                {
                    return;
                }
                if (entries.ContainsKey(turret.thingIDNumber) || turret.ForcedTarget.IsValid)
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (nextAutoTry.TryGetValue(turret.thingIDNumber, out int next) && now < next)
                {
                    return;
                }
                Map other = PairedMapOf(turret.Map);
                if (other == null)
                {
                    Charge(turret, now + AutoRetryTicks * 4);
                    return;
                }
                Verb_LaunchProjectile verb = LauncherVerb(turret);
                if (verb == null || turret.Faction == null)
                {
                    Charge(turret, now + AutoRetryTicks * 4);
                    return;
                }
                if (other.mapPawns.AllPawnsSpawned.Count == 0)
                {
                    Charge(turret, now + EventRetryTicks);
                    return;
                }
                Pawn pick = FindAutoTarget(turret, verb, other);
                if (pick == null)
                {
                    Charge(turret, now + EventRetryTicks);
                    return;
                }
                Store(turret, pick, other, IsArc(verb), auto: true);
                ABLog.Dev(turret.LabelShort + " auto-acquired a cross-level target (event path).");
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "turret event acquisition");
            }
        }

        private static Map PairedMapOf(Map map)
        {
            LevelComp comp = map?.Levels();
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

        /// <summary>Idle, ready turrets of ANY faction acquire the nearest enemy pawn
        /// on the paired level. Scan-cadence backstop for Building_Turret subclasses
        /// that never run the vanilla TryFindNewTarget path.</summary>
        internal static void AcquireAuto(Map sky, Map surface)
        {
            int acquired = 0;
            acquired += AcquireOnMap(sky, surface, MaxAutoAcquiresPerScan);
            AcquireOnMap(surface, sky, MaxAutoAcquiresPerScan - acquired);
        }

        private static int AcquireOnMap(Map shooterMap, Map targetMap, int budget)
        {
            if (budget <= 0)
            {
                return 0;
            }
            int made = 0;
            int now = Find.TickManager.TicksGame;
            made += AcquireFromList(shooterMap.listerBuildings.allBuildingsColonist, shooterMap, targetMap, budget, now);
            if (made < budget)
            {
                made += AcquireFromList(shooterMap.listerBuildings.allBuildingsNonColonist, shooterMap, targetMap, budget - made, now);
            }
            return made;
        }

        private static int AcquireFromList(List<Building> buildings, Map shooterMap, Map targetMap, int budget, int now)
        {
            int made = 0;
            for (int i = 0; i < buildings.Count && made < budget; i++)
            {
                if (!(buildings[i] is Building_Turret turret))
                {
                    continue;
                }
                if (entries.ContainsKey(turret.thingIDNumber))
                {
                    continue;
                }
                if (nextAutoTry.TryGetValue(turret.thingIDNumber, out int next) && now < next)
                {
                    continue;
                }
                Verb_LaunchProjectile verb = LauncherVerb(turret);
                if (verb == null || turret.Faction == null)
                {
                    continue;
                }
                // Local business first: a live local target, a vanilla forced target,
                // or not being ready at all leaves the turret to vanilla.
                if (turret.CurrentTarget.IsValid || turret.ForcedTarget.IsValid || !ReadyToFire(turret, verb))
                {
                    Charge(turret, now + AutoRetryTicks);
                    continue;
                }
                Pawn pick = FindAutoTarget(turret, verb, targetMap);
                if (pick == null)
                {
                    Charge(turret, now + AutoRetryTicks);
                    continue;
                }
                Store(turret, pick, targetMap, IsArc(verb), auto: true);
                made++;
                ABLog.Dev(turret.LabelShort + " auto-acquired a cross-level target.");
            }
            return made;
        }

        private static void Charge(Building_Turret turret, int untilTick)
        {
            if (nextAutoTry.Count > 256)
            {
                nextAutoTry.Clear();
            }
            nextAutoTry[turret.thingIDNumber] = untilTick;
        }

        private const int MaxAutoTargetProbes = 4;

        private static Pawn FindAutoTarget(Building_Turret turret, Verb_LaunchProjectile verb, Map targetMap)
        {
            tmpTargets.Clear();
            IReadOnlyList<Pawn> pawns = targetMap.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || p.Downed || !p.Spawned || !p.HostileTo(turret))
                {
                    continue;
                }
                tmpTargets.Add(p);
            }
            if (tmpTargets.Count == 0)
            {
                return null;
            }
            IntVec3 origin = turret.Position;
            tmpTargets.Sort((a, b) =>
                (a.Position - origin).LengthHorizontalSquared.CompareTo((b.Position - origin).LengthHorizontalSquared));
            int probes = Math.Min(tmpTargets.Count, MaxAutoTargetProbes);
            for (int i = 0; i < probes; i++)
            {
                Pawn p = tmpTargets[i];
                bool ok = IsArc(verb)
                    ? CrossLevelCombat.CanArcFireAt(turret.Map, turret.Position, p.Position, targetMap, verb, out _)
                    : TurretCanFire(turret, p, verb, out _);
                if (ok)
                {
                    return p;
                }
            }
            return null;
        }

        // --- tick-accurate firing driver -------------------------------------

        /// <summary>Per-tick driver for one sky/surface pair. First line is a count
        /// early-out: zero recurring cost while no cross-level turret order exists.</summary>
        internal static void TickPair(Map sky, Map surface, int nowOverride = -1)
        {
            if (entries.Count == 0)
            {
                return;
            }
            int now = nowOverride >= 0 ? nowOverride : Find.TickManager.TicksGame;
            tmpDead.Clear();
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                Building_Turret turret = e.turret;
                if (turret == null || turret.Destroyed || !turret.Spawned)
                {
                    tmpDead.Add(kv.Key);
                    continue;
                }
                if (turret.Map != sky && turret.Map != surface)
                {
                    continue; // another column's pair drives it
                }
                if (now >= e.revalidateAt)
                {
                    e.revalidateAt = now + RevalidateInterval;
                    if (!Revalidate(e, turret))
                    {
                        tmpDead.Add(kv.Key);
                        continue;
                    }
                }
                FaceTarget(turret, e);
                if (now < e.nextEventTick)
                {
                    continue;
                }
                switch (e.phase)
                {
                    case Phase.Warmup:
                        e.phase = Phase.Burst;
                        Verb_LaunchProjectile warmVerb = LauncherVerb(turret);
                        e.burstShotsLeft = Mathf.Max(1, warmVerb?.verbProps.burstShotCount ?? 1);
                        // Aim complete: once-per-burst combat-log entry (turrets get no
                        // Shooting XP - the caster is the building, not a pawn).
                        if (warmVerb != null)
                        {
                            ABShotEffects.OnBurstWarmupComplete(turret, warmVerb, e.target, warmVerb.Projectile);
                        }
                        e.nextEventTick = now;
                        goto case Phase.Burst;
                    case Phase.Burst:
                        FireResult result = TryFireOne(e, turret, now);
                        if (result == FireResult.Dead)
                        {
                            tmpDead.Add(kv.Key);
                            break;
                        }
                        if (result == FireResult.Hold)
                        {
                            // Not ready (unmanned / unpowered / empty): nextEventTick
                            // was pushed; the burst is NOT consumed.
                            break;
                        }
                        e.burstShotsLeft--;
                        if (e.burstShotsLeft > 0)
                        {
                            e.nextEventTick = now + Mathf.Max(1, LauncherVerb(turret)?.verbProps.ticksBetweenBurstShots ?? 10);
                        }
                        else
                        {
                            e.phase = Phase.Cooldown;
                            Verb_LaunchProjectile v = LauncherVerb(turret);
                            Pawn manner = turret.TryGetComp<CompMannable>()?.ManningPawn;
                            e.nextEventTick = now + (v != null ? CooldownTicks(turret, v, manner) : 250);
                        }
                        break;
                    case Phase.Cooldown:
                        e.phase = Phase.Warmup;
                        e.nextEventTick = now + WarmupTicks(turret);
                        break;
                }
            }
            for (int i = 0; i < tmpDead.Count; i++)
            {
                entries.Remove(tmpDead[i]);
            }
        }

        /// <summary>Always-on engagement lines for every turret order touching the
        /// viewed map. Called per frame from LevelComp.MapComponentUpdate; the caller
        /// already early-outs, and this early-outs again on the entry count.</summary>
        internal static void DrawVisuals(Map cur)
        {
            if (entries.Count == 0)
            {
                return;
            }
            Map below = cur.Levels()?.lowerMap;
            foreach (KeyValuePair<int, Entry> kv in entries)
            {
                Entry e = kv.Value;
                Building_Turret turret = e.turret;
                if (turret == null || !turret.Spawned)
                {
                    continue;
                }
                Vector3 end = e.target.HasThing ? e.target.Thing.DrawPos : e.target.Cell.ToVector3Shifted();
                if (turret.Map == cur)
                {
                    if (e.targetMap == below)
                    {
                        end = LevelRenderer.ShiftedBelowDrawPos(end);
                    }
                    CrossLevelCombatUI.DrawLine(turret.DrawPos, end);
                    if (Find.Selector.IsSelected(turret))
                    {
                        CrossLevelCombatUI.DrawTargetMarker(end);
                    }
                }
                else if (below != null && turret.Map == below && e.targetMap == cur)
                {
                    CrossLevelCombatUI.DrawLine(LevelRenderer.ShiftedBelowDrawPos(turret.DrawPos), end);
                    if (Find.Selector.IsSelected(turret))
                    {
                        CrossLevelCombatUI.DrawTargetMarker(end);
                    }
                }
            }
        }

        private static bool Revalidate(Entry e, Building_Turret turret)
        {
            if (e.targetMap == null || e.targetMap.Disposed)
            {
                return false;
            }
            if (e.target.HasThing)
            {
                Thing t = e.target.Thing;
                if (t == null || t.Destroyed || !t.Spawned || t.MapHeld != e.targetMap)
                {
                    return false;
                }
            }
            else if (!e.target.Cell.InBounds(e.targetMap))
            {
                return false;
            }
            // A local fight always outranks the cross-level order; auto entries drop,
            // player entries just hold (vanilla forced targets survive local skirmishes).
            if (turret.CurrentTarget.IsValid)
            {
                return !e.auto;
            }
            Verb_LaunchProjectile verb = LauncherVerb(turret);
            if (verb == null)
            {
                return false;
            }
            bool lineOk = e.arc
                ? CrossLevelCombat.CanArcFireAt(turret.Map, turret.Position, e.target.Cell, e.targetMap, verb, out _)
                : e.target.HasThing && TurretCanFire(turret, e.target.Thing, verb, out _);
            if (!lineOk)
            {
                // Auto entries vanish quietly; a player order with a broken line is
                // dropped too (we cannot shoot through solid structure), message once.
                if (!e.auto)
                {
                    Messages.Message("AB_GapLineLost".Translate(turret.LabelShort), turret,
                        MessageTypeDefOf.NeutralEvent, historical: false);
                }
                return false;
            }
            return true;
        }

        private static void FaceTarget(Building_Turret turret, Entry e)
        {
            if (turret is Building_TurretGun gun && gun.Top != null)
            {
                IntVec3 tc = e.target.Cell;
                Vector3 dir = (tc - turret.Position).ToVector3();
                if (dir.sqrMagnitude > 0.01f)
                {
                    gun.Top.CurRotation = dir.AngleFlat();
                }
            }
        }

        private enum FireResult
        {
            Fired,
            Hold,
            Dead
        }

        private static FireResult TryFireOne(Entry e, Building_Turret turret, int now)
        {
            Verb_LaunchProjectile verb = LauncherVerb(turret);
            if (verb == null)
            {
                return FireResult.Dead;
            }
            if (!ReadyToFire(turret, verb))
            {
                // Not dead - just not ready (unmanned, unpowered, empty). Hold the
                // phase and try again shortly; the burst is not consumed.
                e.nextEventTick = now + 60;
                return FireResult.Hold;
            }
            Pawn manner = turret.TryGetComp<CompMannable>()?.ManningPawn;
            if (e.arc)
            {
                if (!CrossLevelCombat.CanArcFireAt(turret.Map, turret.Position, e.target.Cell,
                        e.targetMap, verb, out CrossLevelCombat.GapShot shot))
                {
                    return FireResult.Dead;
                }
                // The changeable-projectile unload is now handled inside FireArcShot's
                // shared per-shot side effects (ABShotEffects.OnShotFired).
                return CrossLevelCombat.FireArcShot(turret, manner, verb, e.target, e.targetMap, shot.distance)
                    ? FireResult.Fired
                    : FireResult.Dead;
            }
            if (!e.target.HasThing)
            {
                return FireResult.Dead;
            }
            return CrossLevelCombat.Fire(turret, verb, e.target.Thing)
                ? FireResult.Fired
                : FireResult.Dead;
        }

        /// <summary>Vanilla firing preconditions: manned when mannable, powered when
        /// powered, a projectile actually loaded, no live local fight.</summary>
        private static bool ReadyToFire(Building_Turret turret, Verb_LaunchProjectile verb)
        {
            CompMannable mannable = turret.TryGetComp<CompMannable>();
            if (mannable != null && !mannable.MannedNow)
            {
                return false;
            }
            CompPowerTrader power = turret.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return false;
            }
            if (verb.Projectile == null)
            {
                return false;
            }
            if (turret.CurrentTarget.IsValid)
            {
                return false;
            }
            return true;
        }

        private static int WarmupTicks(Building_Turret turret)
        {
            float sec = turret.def.building?.turretBurstWarmupTime.RandomInRange ?? 0f;
            return Mathf.Max(1, Mathf.RoundToInt(sec * 60f));
        }

        private static int CooldownTicks(Building_Turret turret, Verb_LaunchProjectile verb, Pawn manner)
        {
            float sec = turret.def.building?.turretBurstCooldownTime ?? -1f;
            if (sec <= 0f)
            {
                try
                {
                    sec = verb.verbProps.AdjustedCooldown(verb, manner);
                }
                catch
                {
                    sec = verb.verbProps.defaultCooldownTime;
                }
            }
            return Mathf.Max(1, Mathf.RoundToInt(sec * 60f));
        }
    }

    /// <summary>Cancel gizmo on a turret holding a cross-level order.</summary>
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.GetGizmos))]
    internal static class Patch_TurretGun_CrossLevelGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building_TurretGun __instance)
        {
            foreach (Gizmo g in __result)
            {
                yield return g;
            }
            if (!ABGuard.On(ABGuard.Combat))
            {
                yield break;
            }
            if (__instance.Faction == Faction.OfPlayer
                && CrossLevelTurret.HasOrder(__instance, out _, out _))
            {
                yield return new Command_Action
                {
                    defaultLabel = "AB_StopCrossBombard".Translate(),
                    defaultDesc = "AB_StopCrossBombardTip".Translate(),
                    icon = TexCommand.ClearPrioritizedWork,
                    action = delegate { CrossLevelTurret.Cancel(__instance); }
                };
            }
        }
    }

    /// <summary>Vanilla-cadence hook: the moment an idle, ready turret finds nothing
    /// on its own map (every 15 ticks), probe the paired level. This is what makes
    /// turret cross-fire react like vanilla instead of on the slow scan.</summary>
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget))]
    internal static class Patch_TurretGun_TryFindNewTarget
    {
        private static void Postfix(Building_TurretGun __instance, LocalTargetInfo __result)
        {
            if (!__result.IsValid && ABGuard.On(ABGuard.Combat))
            {
                CrossLevelTurret.TryAutoAcquire(__instance);
            }
        }
    }
}
