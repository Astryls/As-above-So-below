using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vanilla-parity side effects for a manually-launched cross-level shot.
    ///
    /// Our cross-gap fire (CrossLevelCombat.Fire / FireArcShot) spawns a real vanilla
    /// Projectile on the TARGET's map and launches it by hand, so flight / armour /
    /// impact / damage / rendering are already 100% vanilla on the receiving map. But
    /// the hand-rolled launch bypasses the normal cast pipeline
    /// (Verb.TryCastNextBurstShot -> Verb_LaunchProjectile.TryCastShot ->
    /// Verb_Shoot.WarmupComplete), so every book-keeping side effect that pipeline
    /// produces is missing. This helper replays exactly those side effects so a
    /// cross-level shot is indistinguishable from a same-map one:
    ///
    ///   PER SHOT (OnShotFired, mirrors TryCastNextBurstShot + TryCastShot tail):
    ///     - soundCast at the shooter + soundCastTail on camera (the missing echo),
    ///     - the ShotFlash muzzle fleck at muzzleFlashScale,
    ///     - CasterPawn combat notifies (used verb / engaged+attacked target / mental
    ///       state / health / weapon), the ShotsFired record,
    ///     - CompChangeableProjectile unload + CompApparelVerbOwner_Charged discharge,
    ///     - consumeFuelPerShot.
    ///   ON THE SPAWNED PROJECTILE (ApplyWeaponTraits, mirrors TryCastShot):
    ///     - CompUniqueWeapon damageDefOverride + extraDamages (persona weapons).
    ///   PER BURST (OnBurstWarmupComplete, mirrors WarmupComplete):
    ///     - the BattleLogEntry_RangedFire combat-log entry,
    ///     - Shooting XP (Verb_Shoot rule: 170x cycle vs hostile, 20x friendly, live
    ///       non-mech pawn targets only).
    ///
    /// All of it is best-effort and swallow-safe: a book-keeping notify must never be
    /// able to abort a shot that already left the barrel.
    /// </summary>
    internal static class ABShotEffects
    {
        // Pawn_MindState.Notify_EngagedTarget() / Notify_AttackedTarget(LocalTargetInfo)
        // are internal to Assembly-CSharp, so bind them once as open-instance delegates.
        // Null (and skipped) if the engine ever renames them - never a hard failure.
        private static readonly Action<Pawn_MindState> notifyEngagedTarget =
            TryBindMindState<Action<Pawn_MindState>>("Notify_EngagedTarget");

        private static readonly Action<Pawn_MindState, LocalTargetInfo> notifyAttackedTarget =
            TryBindMindState<Action<Pawn_MindState, LocalTargetInfo>>("Notify_AttackedTarget");

        private static T TryBindMindState<T>(string method) where T : Delegate
        {
            try
            {
                System.Reflection.MethodInfo mi = AccessTools.Method(typeof(Pawn_MindState), method);
                return mi != null ? AccessTools.MethodDelegate<T>(mi) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Per-shot book-keeping. <paramref name="caster"/> is the shooting
        /// Thing (pawn or turret); the pawn-only block is skipped for turrets exactly
        /// as vanilla skips it when CasterIsPawn is false.</summary>
        internal static void OnShotFired(Thing caster, Verb verb, LocalTargetInfo target)
        {
            if (caster == null || !caster.Spawned || verb == null)
            {
                return;
            }
            VerbProperties vp = verb.verbProps;
            if (vp == null)
            {
                return;
            }

            try
            {
                // Muzzle flash + shot report, both at the shooter's own cell/map (vanilla
                // TryCastNextBurstShot). The tail is the "echo" that was missing before.
                if (vp.muzzleFlashScale > 0.01f)
                {
                    FleckMaker.Static(caster.Position, caster.Map, FleckDefOf.ShotFlash, vp.muzzleFlashScale);
                }
                vp.soundCast?.PlayOneShot(new TargetInfo(caster.Position, caster.MapHeld));
                vp.soundCastTail?.PlayOneShotOnCamera(caster.Map);
            }
            catch
            {
                // Cosmetic only.
            }

            if (caster is Pawn p)
            {
                try
                {
                    p.Notify_UsedVerb(p, verb);
                    if (p.mindState != null)
                    {
                        if (p.thinker != null && target == p.mindState.enemyTarget)
                        {
                            notifyEngagedTarget?.Invoke(p.mindState);
                        }
                        notifyAttackedTarget?.Invoke(p.mindState, target);
                    }
                    p.MentalState?.Notify_AttackedTarget(target);
                    p.health?.Notify_UsedVerb(verb, target);
                    verb.EquipmentSource?.Notify_UsedWeapon(p);
                    p.records?.Increment(RecordDefOf.ShotsFired);
                }
                catch
                {
                    // A pawn notify must never abort the shot.
                }
            }

            try
            {
                ThingWithComps eq = verb.EquipmentSource;
                if (eq != null)
                {
                    eq.GetComp<CompChangeableProjectile>()?.Notify_ProjectileLaunched();
                    eq.GetComp<CompApparelVerbOwner_Charged>()?.UsedOnce();
                }
                if (vp.consumeFuelPerShot > 0f)
                {
                    caster.TryGetComp<CompRefuelable>()?.ConsumeFuel(vp.consumeFuelPerShot);
                }
            }
            catch
            {
                // Consumption is book-keeping; never let it break the launch.
            }
        }

        /// <summary>Stamp CompUniqueWeapon traits onto a freshly-spawned projectile,
        /// before it is launched (vanilla TryCastShot). No-op for ordinary weapons.</summary>
        internal static void ApplyWeaponTraits(Projectile proj, Verb verb)
        {
            if (proj == null || verb == null)
            {
                return;
            }
            try
            {
                ThingWithComps eq = verb.EquipmentSource;
                if (eq == null || !eq.TryGetComp(out CompUniqueWeapon comp))
                {
                    return;
                }
                foreach (WeaponTraitDef trait in comp.TraitsListForReading)
                {
                    if (trait.damageDefOverride != null)
                    {
                        proj.damageDefOverride = trait.damageDefOverride;
                    }
                    if (!trait.extraDamages.NullOrEmpty())
                    {
                        proj.extraDamages ??= new List<ExtraDamage>();
                        proj.extraDamages.AddRange(trait.extraDamages);
                    }
                }
            }
            catch
            {
                // Unique-weapon traits are a bonus; never let them break a shot.
            }
        }

        /// <summary>Once-per-burst book-keeping, at the moment aim completes and the
        /// first shot is committed (vanilla WarmupComplete). Adds the combat-log entry
        /// for every shooter, and Shooting XP for pawn shooters. Matches vanilla in
        /// firing even if the committed shot then whiffs.</summary>
        internal static void OnBurstWarmupComplete(Thing caster, Verb verb, LocalTargetInfo target, ThingDef projectile)
        {
            if (caster == null || verb == null)
            {
                return;
            }
            try
            {
                Find.BattleLog.Add(new BattleLogEntry_RangedFire(
                    caster,
                    target.HasThing ? target.Thing : null,
                    verb.EquipmentSource?.def,
                    projectile,
                    (verb.verbProps?.burstShotCount ?? 1) > 1));
            }
            catch
            {
                // Log entry is cosmetic.
            }

            if (caster is Pawn p && p.skills != null
                && target.Thing is Pawn victim && !victim.Downed && !victim.IsColonyMech)
            {
                try
                {
                    float baseXp = victim.HostileTo(caster) ? 170f : 20f;
                    float cycle = verb.verbProps.AdjustedFullCycleTime(verb, p);
                    p.skills.Learn(SkillDefOf.Shooting, baseXp * cycle);
                }
                catch
                {
                    // Skill gain is a bonus; never let it break the burst.
                }
            }
        }
    }
}
