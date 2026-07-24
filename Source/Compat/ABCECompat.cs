using System;
using System.Reflection;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Combat Extended cross-level fire support.
    ///
    /// CE replaces the vanilla shot pipeline wholesale: its verbs are
    /// CombatExtended.Verb_LaunchProjectileCE (a direct Verb subclass, NOT
    /// Verb_LaunchProjectile) and its projectiles are CombatExtended.ProjectileCE
    /// (a ThingWithComps, NOT Verse.Projectile). So the vanilla cross-gap launch
    /// (which casts to Verse.Projectile) can never fire a CE weapon - GetRangedVerb
    /// wouldn't even see it. This bridges that: the geometry / line-of-fire model is
    /// verb-agnostic and reused as-is, and only the LAUNCH branches here.
    ///
    /// Design (locked with the user): SIMPLER-CUT accuracy - our own aim roll decides
    /// hit/miss, then CE's own ballistics, armour, penetration and suppression resolve
    /// natively on the receiving map (the projectile is spawned there, exactly like the
    /// vanilla path). Ammo is PARITY - the magazine is decremented per shot via
    /// CompAmmoUser, just like a same-map CE shot.
    ///
    /// Discipline: this is the ONLY file that names CE types, and they appear ONLY in
    /// method BODIES (locals) - never in a field, base type, or method signature - so
    /// the assembly's type layout never forces CE to resolve when CE is absent (the
    /// dev-palette / Prepatcher GetTypes() trap). Every entry point is gated on Active
    /// and fails open.
    /// </summary>
    internal static class ABCECompat
    {
        /// <summary>High-ground shot group tightening (CE spread multiplier) when the
        /// shooter fires from the upper level - subtle, per the design.</summary>
        internal const float HighGroundSpreadFactor = 0.88f;

        /// <summary>Suppression a pawn on the upper level receives, scaled down - it is
        /// harder to pin down someone holding the high ground.</summary>
        internal const float HighGroundSuppressFactor = 0.75f;

        private static bool? active;

        /// <summary>CE loaded? Cached. Uses the postfix-insensitive lookup so a local
        /// copy of CE (with a _steam-suffixed id) still counts.</summary>
        internal static bool Active
        {
            get
            {
                if (!active.HasValue)
                {
                    active = ModLister.GetActiveModWithIdentifier("CETeam.CombatExtended", true) != null;
                }
                return active.Value;
            }
        }

        /// <summary>Is this a CE projectile-launching verb? Only ever called behind an
        /// Active check, so its CE-typed body is never JIT'd when CE is absent.</summary>
        internal static bool IsCEVerb(Verb v)
        {
            return v is Verb_LaunchProjectileCE;
        }

        /// <summary>A CE verb that lobs (mortar-class) - not supported across the gap
        /// yet, so callers keep it out of the direct-fire path.</summary>
        internal static bool IsArcCE(Verb v)
        {
            ThingDef proj = ProjectileOf(v);
            return proj?.projectile != null && proj.projectile.flyOverhead;
        }

        /// <summary>The projectile ThingDef a CE verb would fire right now (the loaded
        /// ammo's projectile for a gun, else the default). Null on anything odd.</summary>
        internal static ThingDef ProjectileOf(Verb v)
        {
            try
            {
                return (v as Verb_LaunchProjectileCE)?.Projectile;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Fire one CE shot across the gap (option B - real CE accuracy). Spawns
        /// a ProjectileCE on the target map and launches it with CE's own trajectory
        /// solution AIMED AT the target, scattered by the weapon's ShotSpread plus the
        /// shooter's sway - CE's ballistics then resolve the hit natively (no separate
        /// hit/miss roll). <paramref name="highGround"/> (shooter on the upper level)
        /// tightens the group subtly. Magazine ammo is decremented (parity). Fails open.</summary>
        internal static bool FireCE(Thing shooter, Verb v, Map targetMap, IntVec3 spawnCell,
            Vector3 originGround, LocalTargetInfo target, bool highGround)
        {
            try
            {
                if (!(v is Verb_LaunchProjectileCE ceVerb) || targetMap == null || targetMap.Disposed)
                {
                    return false;
                }

                // Ammo parity: honour the magazine exactly like a same-map CE shot.
                CompAmmoUser compAmmo = ceVerb.CompAmmo;
                if (compAmmo != null)
                {
                    if (!compAmmo.CanBeFiredNow)
                    {
                        compAmmo.TryStartReload();
                        return false;
                    }
                    if (!compAmmo.TryPrepareShot())
                    {
                        return false;
                    }
                }

                ThingDef projDef = ceVerb.Projectile;
                if (!(projDef?.projectile is ProjectilePropertiesCE propsCE))
                {
                    return false;
                }

                var proj = (ProjectileCE)GenSpawn.Spawn(projDef, spawnCell, targetMap);

                float shotHeight = 1f;
                float shotSpeed = projDef.projectile.speed;
                Vector3 source3D = new Vector3(originGround.x, shotHeight, originGround.z);
                Vector3 aimGround = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
                Vector3 target3D = new Vector3(aimGround.x, shotHeight, aimGround.z);

                BaseTrajectoryWorker tw = propsCE.TrajectoryWorker;
                float shotRotation = tw.ShotRotation(propsCE, source3D, target3D);
                float shotAngle = tw.ShotAngle(propsCE, source3D, target3D, shotSpeed);
                float distance = new Vector2(target3D.x - source3D.x, target3D.z - source3D.z).magnitude;

                // Real CE accuracy: aim dead-on and scatter by the weapon's ShotSpread
                // plus the shooter's sway (skill/stance). CE ballistics decide the hit.
                float spread = 0f;
                Thing eq = ceVerb.EquipmentSource;
                if (eq != null)
                {
                    float mult = propsCE.spreadMult > 0f ? propsCE.spreadMult : 1f;
                    spread = eq.GetStatValue(CE_StatDefOf.ShotSpread) * mult;
                }
                spread += ceVerb.SwayAmplitude;
                if (highGround)
                {
                    spread *= HighGroundSpreadFactor;
                }
                if (spread > 0.0001f)
                {
                    shotRotation += Rand.Range(-spread, spread);
                    shotAngle += Rand.Range(-spread, spread) * Mathf.Deg2Rad;
                }

                proj.canTargetSelf = false;
                proj.minCollisionDistance = 0f;
                proj.intendedTarget = target;
                proj.Launch(shooter, new Vector2(source3D.x, source3D.z), shotAngle, shotRotation,
                    shotHeight, shotSpeed, eq, distance);

                if (compAmmo != null)
                {
                    int consumed = (compAmmo.Props.ammoSet?.ammoConsumedPerShot ?? 1)
                        * Math.Max(1, ceVerb.VerbPropsCE.ammoConsumedPerShotCount);
                    compAmmo.Notify_ShotFired(consumed <= 0 ? 1 : consumed);
                    compAmmo.Notify_PostShotFired();
                }
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross level fire");
                return false;
            }
        }
    }

    /// <summary>
    /// High-ground CE suppression resistance: a pawn on the upper level takes less
    /// suppression (harder to pin from below). Registered as a manual Harmony prefix on
    /// CombatExtended.CompSuppressable.AddSuppression, resolved by NAME (TypeByName) so
    /// nothing here compile-references a CE type - the class is fully absent-safe. The
    /// prefix signature is vanilla (ThingComp + ref float), and its body touches no CE
    /// type, so a scan of this assembly with CE absent never resolves anything CE.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABCESuppressionPatch
    {
        static ABCESuppressionPatch()
        {
            try
            {
                if (!ABCECompat.Active)
                {
                    return;
                }
                Type comp = AccessTools.TypeByName("CombatExtended.CompSuppressable");
                MethodInfo m = comp != null ? AccessTools.Method(comp, "AddSuppression") : null;
                if (m != null)
                {
                    HarmonyBoot.Harmony.Patch(m,
                        prefix: new HarmonyMethod(typeof(ABCESuppressionPatch), nameof(AddSuppressionPrefix)));
                    ABLog.Dev("CE high-ground suppression resistance patch applied.");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " could not patch CE suppression: " + e.Message);
            }
        }

        private static void AddSuppressionPrefix(ThingComp __instance, ref float amount)
        {
            try
            {
                if (amount <= 0f || !ABGuard.On(ABGuard.Combat))
                {
                    return;
                }
                Pawn p = __instance?.parent as Pawn;
                if (p != null && ABSkyDropCells.IsSkyLevel(p.Map))
                {
                    amount *= ABCECompat.HighGroundSuppressFactor;
                }
            }
            catch
            {
                // Suppression is a bonus; never let it break CE's own tick.
            }
        }
    }
}
