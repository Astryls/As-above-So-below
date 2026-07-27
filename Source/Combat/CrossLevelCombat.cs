using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Model B: true through-the-gap ranged combat for the sky &lt;-&gt; surface pair.
    ///
    /// RimWorld has no third dimension: a projectile lives on one map's 2D plane and
    /// vanilla explicitly refuses a cross-map cast (Verb_LaunchProjectile.TryCastShot
    /// early-returns when target.Map != caster.Map, and no AI ever targets off-map).
    /// The robust workaround is to spawn the projectile on the TARGET's map so its
    /// flight, interception, impact, armour, damage AND rendering are 100% vanilla
    /// (LevelRenderer.TryDrawFilteredDynamic already redraws the surface map's live
    /// projectiles from the sky view). Only two things are custom here:
    ///   1. target acquisition across the gap (line-of-fire + range), and
    ///   2. the cast itself (accuracy roll mirroring ShotReport, then a manual launch).
    ///
    /// Line-of-fire rule (sky is the reference plane, level 1; open air = the holes):
    ///   - both the shooter's column and the target's column must be EXPOSED to the gap
    ///     (over/under an open-air cell, or orthogonally beside one so a pawn on a
    ///     platform edge can fire over it),
    ///   - a clear sight line on the sky plane between the two columns (sky walls block),
    ///   - within the weapon's range, with a fixed vertical separation folded in.
    /// Enclosed spaces (a roofed surface room, a sealed sky room) are correctly immune:
    /// there is solid structure between the two pawns. Accuracy uses the shooter's real
    /// stats and the weapon's falloff; plunging fire naturally bypasses horizontal cover.
    ///
    /// Everything is gated by ABGuard.Combat and the crossLevelCombat setting, and fails
    /// open (returns false -> the caller routes down the stairs instead, Model A).
    /// </summary>
    internal static class CrossLevelCombat
    {
        /// <summary>Vertical separation between the sky and the surface, in cells,
        /// folded into the shot distance so a shooter directly above a target still
        /// fires at a real (short) range rather than point blank.</summary>
        internal const float GapHeight = 4f;

        /// <summary>Handoff of the cross-map target to a freshly-created
        /// AB_CrossLevelAttack job (JobDriver has no ctor args); the driver reads
        /// and clears its entry on first tick, then scribes its own reference.</summary>
        internal static readonly Dictionary<int, Thing> PendingTargets = new Dictionary<int, Thing>();

        /// <summary>Cleared on new-game/load (refactor R1): pending job-target
        /// handoffs hold Thing references from the previous session.</summary>
        [ABGameReset]
        internal static void ResetForNewGame()
        {
            PendingTargets.Clear();
        }

        /// <summary>Scratch buffer for firing-cell search. Main-thread only (float-menu
        /// build / job start), so a shared list is safe and avoids per-search allocs.</summary>
        private static readonly List<IntVec3> tmpCandidates = new List<IntVec3>();

        /// <summary>Resolve the sky &lt;-&gt; surface pairing for two maps and say which
        /// is which. Only the level-1 / level-0 pair qualifies; the basement never does.</summary>
        internal static bool AreCrossGapPaired(Map a, Map b, out Map skyMap, out Map surfaceMap)
        {
            skyMap = null;
            surfaceMap = null;
            if (a == null || b == null || a == b || a.Disposed || b.Disposed)
            {
                return false;
            }
            LevelComp ca = a.Levels();
            if (ca == null)
            {
                return false;
            }
            if (ca.level == 1 && ca.lowerMap == b)
            {
                skyMap = a;
                surfaceMap = b;
                return true;
            }
            if (ca.level == 0 && ca.upperMap == b)
            {
                skyMap = b;
                surfaceMap = a;
                return true;
            }
            return false;
        }

        /// <summary>A column is exposed to the gap when the sky cell there is open air,
        /// or any orthogonal neighbour is (a pawn leaning over a platform edge).</summary>
        private static bool ExposedToGap(Map skyMap, IntVec3 col)
        {
            if (!col.InBounds(skyMap))
            {
                return false;
            }
            TerrainDef air = ABDefOf.AB_OpenAir;
            if (skyMap.terrainGrid.TerrainAt(col) == air)
            {
                return true;
            }
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = col + GenAdj.CardinalDirections[i];
                if (n.InBounds(skyMap) && skyMap.terrainGrid.TerrainAt(n) == air)
                {
                    return true;
                }
            }
            return false;
        }

        internal struct GapShot
        {
            public Map targetMap;
            public float distance;
        }

        /// <summary>An open-air cell at the shooter's column, a cardinal neighbour,
        /// or within the first half of the sky-plane line toward the target - the
        /// window the descending bullet actually crosses the plane through.</summary>
        private static bool HasApertureTowards(Map skyMap, IntVec3 from, IntVec3 to)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            if (skyMap.terrainGrid.TerrainAt(from) == air)
            {
                return true;
            }
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = from + GenAdj.CardinalDirections[i];
                if (n.InBounds(skyMap) && skyMap.terrainGrid.TerrainAt(n) == air)
                {
                    return true;
                }
            }
            int half = Mathf.Max(1, (int)((to - from).LengthHorizontal * 0.5f));
            int count = 0;
            foreach (IntVec3 c in GenSight.PointsOnLineOfSight(from, to))
            {
                if (++count > half)
                {
                    break;
                }
                if (c.InBounds(skyMap) && skyMap.terrainGrid.TerrainAt(c) == air)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The best ranged projectile verb for a pawn, or null (melee-only,
        /// or a non-projectile ranged verb we do not simulate across the gap).</summary>
        internal static Verb GetRangedVerb(Pawn p)
        {
            // Vanilla Verb_LaunchProjectile OR, when CE is loaded, a CE projectile verb.
            return ABVerb.GetRangedVerb(p);
        }

        /// <summary>Shared classifier for "this targeting source is the caster's own
        /// equipped direct-fire gun" - the B attack. One definition so the click
        /// dispatcher and the hover cursor can never diverge. Ability verbs (including
        /// ability-shoot hybrids, which are not the equipped weapon's verb), melee, and
        /// arcing launchers are excluded.</summary>
        internal static bool IsEquippedGunVerb(ITargetingSource source, Pawn caster, out Verb gunVerb)
        {
            gunVerb = null;
            if (caster == null || !(source is Verb v) || v is Verb_CastAbility
                || !ABVerb.IsProjectileVerb(v))
            {
                return false;
            }
            if (v != caster.equipment?.PrimaryEq?.PrimaryVerb)
            {
                return false;
            }
            if (ABVerb.ProjectileOf(v)?.projectile?.flyOverhead ?? false)
            {
                return false;
            }
            gunVerb = v;
            return true;
        }

        internal static bool Enabled
        {
            get
            {
                ABSettings s = ABMod.Settings;
                return ABGuard.On(ABGuard.Combat) && s != null && s.crossLevelCombat;
            }
        }

        /// <summary>Can the shooter (pawn or turret) fire at the cross-gap target from
        /// its current cell?</summary>
        internal static bool CanCrossGapFire(Thing shooter, Thing target, Verb verb, out GapShot shot)
        {
            shot = default;
            if (shooter == null || !shooter.Spawned)
            {
                return false;
            }
            // Multi-cell shooters (2x2 autocannons and bigger modded turrets) fire from
            // any exposed cell of their footprint; pawns are the 1x1 special case.
            foreach (IntVec3 c in shooter.OccupiedRect())
            {
                if (CanFireFrom(shooter.Map, c, target, verb, out shot))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Core predicate: can a shot originate from cell <paramref name="sCol"/>
        /// on <paramref name="shooterMap"/> and reach <paramref name="target"/> across the
        /// gap? Cheap: a couple of terrain reads, one sight line, one range compare.</summary>
        internal static bool CanFireFrom(Map shooterMap, IntVec3 sCol, Thing target,
            Verb verb, out GapShot shot)
        {
            shot = default;
            if (!Enabled || shooterMap == null || verb == null || target == null)
            {
                return false;
            }
            if (target.Destroyed || !target.Spawned)
            {
                return false;
            }
            Map targetMap = target.MapHeld;
            if (!AreCrossGapPaired(shooterMap, targetMap, out Map skyMap, out _))
            {
                return false;
            }
            if (!sCol.InBounds(shooterMap))
            {
                return false;
            }
            IntVec3 tCol = target.Position;
            if (!tCol.InBounds(skyMap))
            {
                return false;
            }
            if (shooterMap == skyMap)
            {
                // Shooting DOWN. The victim must be exposed to the gap (its column
                // open air, or a cardinal neighbour is) - the SAME leniency the UP
                // path uses for its target, so the two directions are reciprocal:
                // if a surface pawn beside a hole can fire UP at a sky pawn, the sky
                // pawn can fire DOWN at it. (A strict under-the-hole rule made sky
                // hostiles almost never find a down-shot: "lower attacks upper but
                // not vice versa".) The bullet crosses the sky plane early in its
                // flight, so an open-air cell within the first HALF of the line -
                // NOT shooter-adjacency - is the muzzle requirement (a mid-platform
                // sniper/turret still fires as long as the roof edge is hole-ward).
                if (!ExposedToGap(skyMap, tCol))
                {
                    return false;
                }
                if (!HasApertureTowards(skyMap, sCol, tCol))
                {
                    return false;
                }
            }
            else
            {
                // Shooting UP. Muzzle clearance: no surface roof directly overhead
                // (a platform far above does not block - the bullet only reaches
                // the sky plane near the target). The target must stand at an edge
                // (column or cardinal open air) - exactly what is visible from below.
                if (shooterMap.roofGrid.Roofed(sCol))
                {
                    return false;
                }
                if (!ExposedToGap(skyMap, tCol))
                {
                    return false;
                }
            }
            // A clear line over the open gap on the sky plane (sky walls / solid
            // structure between the columns block the plunging / rising shot).
            if (!GenSight.LineOfSight(sCol, tCol, skyMap, skipFirstCell: true))
            {
                return false;
            }
            float horizSq = (tCol - sCol).LengthHorizontalSquared;
            float dist = Mathf.Sqrt(horizSq + GapHeight * GapHeight);
            float range = verb.EffectiveRange;
            if (range <= 1.42f || dist > range)
            {
                return false;
            }
            float minRange = verb.verbProps.minRange;
            if (minRange > 0f && dist < minRange)
            {
                return false;
            }
            shot = new GapShot { targetMap = targetMap, distance = dist };
            return true;
        }

        /// <summary>Arc (flyOverhead) line-of-fire: mortars and artillery lob over
        /// everything, so no sight line is required. Sky shooter -> surface target:
        /// the TARGET's column must be open air (the shell falls through the hole;
        /// a surface-built roof under it gets vanilla punch-through on impact).
        /// Surface shooter -> sky target: the SHOOTER's column must be open air
        /// (the shell lobs up through the hole, then falls onto the open sky plane).</summary>
        internal static bool CanArcFireAt(Map shooterMap, IntVec3 sCol, IntVec3 tCol,
            Map targetMap, Verb verb, out GapShot shot)
        {
            shot = default;
            if (!Enabled || shooterMap == null || verb == null)
            {
                return false;
            }
            if (!AreCrossGapPaired(shooterMap, targetMap, out Map skyMap, out _))
            {
                return false;
            }
            if (!sCol.InBounds(shooterMap) || !tCol.InBounds(targetMap))
            {
                return false;
            }
            TerrainDef air = ABDefOf.AB_OpenAir;
            IntVec3 holeCol = shooterMap == skyMap ? tCol : sCol;
            if (!holeCol.InBounds(skyMap) || skyMap.terrainGrid.TerrainAt(holeCol) != air)
            {
                return false;
            }
            float horizSq = (tCol - sCol).LengthHorizontalSquared;
            float dist = Mathf.Sqrt(horizSq + GapHeight * GapHeight);
            float range = verb.EffectiveRange;
            if (range <= 1.42f || dist > range)
            {
                return false;
            }
            float minRange = verb.verbProps.minRange;
            if (minRange > 0f && dist < minRange)
            {
                return false;
            }
            shot = new GapShot { targetMap = targetMap, distance = dist };
            return true;
        }

        /// <summary>One arcing shot across the gap (mortar-class): vanilla forced-miss
        /// scatter, shell spawned on the target's map at the shooter's column with the
        /// full-distance origin so flight time reads real. flyOverhead projectiles take
        /// no intercepts en route and do their own roof punch on impact.</summary>
        internal static bool FireArcShot(Thing shooter, Pawn manningPawn, Verb verb,
            LocalTargetInfo target, Map targetMap, float distance)
        {
            try
            {
                if (shooter == null || verb == null || targetMap == null || targetMap.Disposed || !target.IsValid)
                {
                    return false;
                }
                // CE arc/mortar shells use CE's own shell system and are not routed
                // across the gap yet; leave them to route/descend normally.
                if (ABCECompat.Active && ABCECompat.IsCEVerb(verb))
                {
                    return false;
                }
                ThingDef projDef = ABVerb.ProjectileOf(verb);
                if (projDef == null)
                {
                    return false;
                }
                IntVec3 targCell = target.Cell;
                IntVec3 spawnCell = shooter.Position;
                spawnCell.x = Mathf.Clamp(spawnCell.x, 0, targetMap.Size.x - 1);
                spawnCell.z = Mathf.Clamp(spawnCell.z, 0, targetMap.Size.z - 1);
                Vector3 origin = spawnCell.ToVector3Shifted();

                Projectile proj = (Projectile)GenSpawn.Spawn(projDef, spawnCell, targetMap);
                ABShotEffects.ApplyWeaponTraits(proj, verb);
                CrossGapProjectiles.Register(proj);
                Thing equip = verb.EquipmentSource;

                // Vanilla forced-miss scatter (Verb_LaunchProjectile.TryCastShot's
                // ForcedMissRadius branch, distance-adjusted).
                float fmr = verb.verbProps.ForcedMissRadius;
                if (manningPawn != null)
                {
                    fmr *= verb.verbProps.GetForceMissFactorFor(equip, manningPawn);
                }
                float adjusted = VerbUtility.CalculateAdjustedForcedMiss(fmr, targCell - spawnCell);
                IntVec3 dest = targCell;
                if (adjusted > 0.5f)
                {
                    int max = GenRadial.NumCellsInRadius(adjusted);
                    dest = targCell + GenRadial.RadialPattern[Rand.Range(0, max)];
                }
                ProjectileHitFlags flags = ProjectileHitFlags.NonTargetWorld;
                if (Rand.Chance(0.5f))
                {
                    flags = ProjectileHitFlags.All;
                }
                if (dest == targCell && target.HasThing)
                {
                    proj.Launch(shooter, origin, target, target, flags, preventFriendlyFire: false, equip);
                }
                else
                {
                    proj.Launch(shooter, origin, dest, target, flags, preventFriendlyFire: false, equip);
                }
                ABShotEffects.OnShotFired(shooter, verb, target);
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "cross level arc shot");
                return false;
            }
        }

        /// <summary>Faithful mirror of ShotReport.AimOnTargetChance_IgnoringPosture:
        /// shooter+distance factor, weapon falloff, weather, Ideology darkness offset
        /// and target size. Cover is intentionally omitted - plunging / rising fire
        /// bypasses horizontal cover, which is physically correct.</summary>
        internal static float ComputeAimChance(Thing shooter, Verb verb, Thing target, float distance)
        {
            float num = 1f;
            if (verb.verbProps.canGoWild)
            {
                num *= ShotReport.HitFactorFromShooter(shooter, distance);
            }
            num *= verb.verbProps.GetHitChanceFactor(verb.EquipmentSource, distance);
            Map wm = target.MapHeld;
            if (wm != null)
            {
                num *= wm.weatherManager.CurWeatherAccuracyMultiplier;
            }
            num += DarknessOffset(shooter, target);
            if (num < 0.0201f)
            {
                num = 0.0201f;
            }
            num *= TargetSizeFactor(target);
            // High-ground: a subtle accuracy bonus for the upper shooter firing down.
            if (IsHighGround(shooter, target))
            {
                num *= HighGroundAccuracyFactor;
            }
            return Mathf.Clamp01(num);
        }

        /// <summary>Elevation constant: the fraction added to a same-map hit chance when
        /// the shooter fires from the upper level (subtle, per the design).</summary>
        private const float HighGroundAccuracyFactor = 1.10f;

        /// <summary>True when the shooter fires from a higher level than the target - the
        /// high ground. Cross-gap combat is sky (level 1) vs surface (level 0), so this is
        /// simply the sky shooter firing down.</summary>
        private static bool IsHighGround(Thing shooter, Thing target)
        {
            int s = shooter?.Map?.Levels()?.level ?? 0;
            int t = target?.MapHeld?.Levels()?.level ?? 0;
            return s > t;
        }

        private static float DarknessOffset(Thing shooter, Thing target)
        {
            if (!ModsConfig.IdeologyActive || shooter == null || target == null)
            {
                return 0f;
            }
            try
            {
                if (DarknessCombatUtility.IsOutdoorsAndLit(target))
                {
                    return shooter.GetStatValue(StatDefOf.ShootingAccuracyOutdoorsLitOffset);
                }
                if (DarknessCombatUtility.IsOutdoorsAndDark(target))
                {
                    return shooter.GetStatValue(StatDefOf.ShootingAccuracyOutdoorsDarkOffset);
                }
                if (DarknessCombatUtility.IsIndoorsAndDark(target))
                {
                    return shooter.GetStatValue(StatDefOf.ShootingAccuracyIndoorsDarkOffset);
                }
                if (DarknessCombatUtility.IsIndoorsAndLit(target))
                {
                    return shooter.GetStatValue(StatDefOf.ShootingAccuracyIndoorsLitOffset);
                }
            }
            catch
            {
                // Darkness is a minor accuracy term; never let it break a shot.
            }
            return 0f;
        }

        private static float TargetSizeFactor(Thing t)
        {
            float f;
            if (t is Pawn p)
            {
                f = p.BodySize;
            }
            else
            {
                f = t.def.fillPercent * t.def.size.x * t.def.size.z * 2.5f;
            }
            return Mathf.Clamp(f, 0.5f, 2f);
        }

        /// <summary>Fire one shot across the gap: roll hit/miss with the vanilla-faithful
        /// aim chance, spawn a real projectile on the target's map a couple of cells from
        /// the target (angled toward the shooter, so the flight is short and mostly
        /// unobstructed - a plunging shot) and launch it. All damage is vanilla from
        /// there. Returns false and fails open on any problem.</summary>
        internal static bool Fire(Thing shooter, Verb verb, Thing target)
        {
            try
            {
                if (shooter == null || verb == null || target == null)
                {
                    return false;
                }
                if (!CanCrossGapFire(shooter, target, verb, out GapShot shot))
                {
                    return false;
                }
                ThingDef projDef = ABVerb.ProjectileOf(verb);
                if (projDef == null)
                {
                    return false;
                }
                Map map = shot.targetMap;

                // Full 1:1 flight: the round spawns at the shooter's OWN column on the
                // target map and flies the entire real distance to the target, so the
                // projectile itself reads the cross-gap shot (the fake tracer is gone).
                // Hit-case flags stay IntendedTarget only, so intervening surface walls
                // and bystanders do NOT intercept a shot the sky-plane line-of-fire
                // model already called clear (Projectile.CanHit gates non-target world
                // and pawns behind flags we deliberately omit); a miss can legitimately
                // clip cover along the way, exactly as a same-map shot would.
                IntVec3 spawnCell = shooter.Position;
                spawnCell.x = Mathf.Clamp(spawnCell.x, 0, map.Size.x - 1);
                spawnCell.z = Mathf.Clamp(spawnCell.z, 0, map.Size.z - 1);
                Vector3 originGround = spawnCell.ToVector3Shifted();

                Thing equip = verb.EquipmentSource;

                // Combat Extended weapons launch a ProjectileCE through CE's own ballistics
                // (option B - real CE accuracy: aim at the target, CE spread + our
                // high-ground bonus decide the hit; CE resolves armour, penetration,
                // suppression and ammo natively on the target map).
                if (ABCECompat.Active && ABCECompat.IsCEVerb(verb))
                {
                    if (!ABCECompat.FireCE(shooter, verb, map, spawnCell, originGround,
                            new LocalTargetInfo(target), IsHighGround(shooter, target)))
                    {
                        return false;
                    }
                    ABShotEffects.OnShotFired(shooter, verb, target);
                    return true;
                }

                // Vanilla: our aim roll decides hit/miss.
                float aim = ComputeAimChance(shooter, verb, target, shot.distance);
                bool hit = Rand.Chance(aim);
                Projectile proj = (Projectile)GenSpawn.Spawn(projDef, spawnCell, map);
                ABShotEffects.ApplyWeaponTraits(proj, verb);
                CrossGapProjectiles.Register(proj);

                if (hit)
                {
                    ProjectileHitFlags flags = ProjectileHitFlags.IntendedTarget;
                    if (!target.def.destroyable || target.def.Fillage == FillCategory.Full)
                    {
                        flags |= ProjectileHitFlags.NonTargetWorld;
                    }
                    proj.Launch(shooter, originGround, target, target, flags, preventFriendlyFire: false, equip);
                }
                else
                {
                    ShootLine line = new ShootLine(spawnCell, target.Position);
                    bool flyOverhead = projDef.projectile != null && projDef.projectile.flyOverhead;
                    line.ChangeDestToMissWild(aim, flyOverhead, map);
                    ProjectileHitFlags flags = ProjectileHitFlags.NonTargetWorld | ProjectileHitFlags.NonTargetPawns;
                    proj.Launch(shooter, originGround, line.Dest, target, flags, preventFriendlyFire: false, equip);
                }

                // The full vanilla per-shot side effects at the shooter (sound + tail,
                // muzzle flash, records, notifies, changeable/charged, fuel).
                ABShotEffects.OnShotFired(shooter, verb, target);
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "cross level fire");
                return false;
            }
        }

        /// <summary>Diagnostic mirror of CanFireFrom: names the FIRST failing stage (or
        /// "OK"). Dev-tools only - keeps the hot path branch-free while letting the
        /// cross-fire probe say exactly why a shot is rejected.</summary>
        internal static string ExplainCanFire(Map shooterMap, IntVec3 sCol, Thing target, Verb verb)
        {
            if (!ABGuard.On(ABGuard.Combat))
            {
                return "combat kill switch OFF (ABGuard)";
            }
            ABSettings s = ABMod.Settings;
            if (s == null || !s.crossLevelCombat)
            {
                return "crossLevelCombat setting OFF";
            }
            if (verb == null)
            {
                return "no ranged projectile verb (GetRangedVerb null)";
            }
            if (target == null || target.Destroyed || !target.Spawned)
            {
                return "target invalid";
            }
            Map targetMap = target.MapHeld;
            if (!AreCrossGapPaired(shooterMap, targetMap, out Map skyMap, out _))
            {
                return "maps are not a sky<->surface pair";
            }
            if (!sCol.InBounds(shooterMap))
            {
                return "shooter cell out of bounds";
            }
            IntVec3 tCol = target.Position;
            if (!tCol.InBounds(skyMap))
            {
                return "target column out of sky bounds";
            }
            if (shooterMap == skyMap)
            {
                if (!ExposedToGap(skyMap, tCol))
                {
                    return "DOWN: target not exposed to a gap (no open air at/beside its column)";
                }
                if (!HasApertureTowards(skyMap, sCol, tCol))
                {
                    return "DOWN: no aperture toward target (shooter too far from any hole)";
                }
            }
            else
            {
                if (shooterMap.roofGrid.Roofed(sCol))
                {
                    return "UP: shooter cell roofed";
                }
                if (!ExposedToGap(skyMap, tCol))
                {
                    return "UP: target not exposed to a gap";
                }
            }
            if (!GenSight.LineOfSight(sCol, tCol, skyMap, skipFirstCell: true))
            {
                return "sky-plane line of sight blocked";
            }
            float dist = Mathf.Sqrt((tCol - sCol).LengthHorizontalSquared + GapHeight * GapHeight);
            float range = verb.EffectiveRange;
            if (range <= 1.42f)
            {
                return "verb range too short (" + range.ToString("0.#") + ")";
            }
            if (dist > range)
            {
                return "out of range (dist " + dist.ToString("0.#") + " > range " + range.ToString("0.#") + ")";
            }
            float minRange = verb.verbProps.minRange;
            if (minRange > 0f && dist < minRange)
            {
                return "inside minimum range";
            }
            return "OK";
        }

        /// <summary>A cell on the shooter's map with a clear cross-gap line of fire to the
        /// target, preferring the pawn's current cell. Searches outward from the target's
        /// column projected onto the shooter's map (the edge of the hole). Bounded.</summary>
        internal static IntVec3 FindFiringCell(Pawn shooter, Thing target, Verb verb)
        {
            if (shooter == null || !shooter.Spawned)
            {
                return IntVec3.Invalid;
            }
            if (CanFireFrom(shooter.Map, shooter.Position, target, verb, out _))
            {
                return shooter.Position;
            }
            Map map = shooter.Map;
            float radius = Mathf.Clamp(verb.EffectiveRange, 4f, 30f);
            // Pass 1 (cheap): collect fireable, standable cells with their distance to
            // the pawn - no pathfinding yet. Pass 2: reachability-check only the nearest
            // handful, so the pathfind cost is hard-bounded even on a big open map.
            tmpCandidates.Clear();
            int scanned = 0;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(target.Position, radius, useCenter: true))
            {
                if (++scanned > 600)
                {
                    break;
                }
                if (!c.InBounds(map) || !c.Standable(map))
                {
                    continue;
                }
                if (!CanFireFrom(map, c, target, verb, out _))
                {
                    continue;
                }
                tmpCandidates.Add(c);
                if (tmpCandidates.Count >= 64)
                {
                    break;
                }
            }
            IntVec3 origin = shooter.Position;
            tmpCandidates.Sort((a, b) =>
                (a - origin).LengthHorizontalSquared.CompareTo((b - origin).LengthHorizontalSquared));
            int reachChecks = 0;
            for (int i = 0; i < tmpCandidates.Count; i++)
            {
                if (++reachChecks > 16)
                {
                    break;
                }
                if (shooter.CanReach(tmpCandidates[i], PathEndMode.OnCell, Danger.Deadly))
                {
                    return tmpCandidates[i];
                }
            }
            return IntVec3.Invalid;
        }

        /// <summary>Player-directed entry: if the drafted pawn can fire across the gap at
        /// the target (now or after a short reposition), start the sustained cross-level
        /// attack job and return true. Otherwise return false so the caller falls back to
        /// Model A routing (walk the stairs and engage same-map).</summary>
        internal static bool TryStartCrossGapAttack(Pawn pawn, Thing target)
        {
            try
            {
                if (!Enabled || pawn == null || !pawn.Spawned || target == null)
                {
                    return false;
                }
                if (!pawn.Drafted || pawn.Downed || pawn.Dead)
                {
                    return false;
                }
                return StartAttackJob(pawn, target, playerForced: true, allowReposition: true);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "start cross level attack");
                return false;
            }
        }

        /// <summary>AI entry (raiders shooting up through the hole, drafted colonists
        /// returning fire on their own): no draft requirement, job not player-forced.
        /// allowReposition lets a raider walk to the hole's edge; drafted colonists
        /// hold position like vanilla and pass false.</summary>
        internal static bool TryStartAutoEngage(Pawn pawn, Thing target, bool allowReposition)
        {
            try
            {
                if (!Enabled || pawn == null || !pawn.Spawned || target == null
                    || pawn.Downed || pawn.Dead || pawn.jobs == null)
                {
                    return false;
                }
                return StartAttackJob(pawn, target, playerForced: false, allowReposition: allowReposition);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "auto cross level engage");
                return false;
            }
        }

        private static bool StartAttackJob(Pawn pawn, Thing target, bool playerForced, bool allowReposition)
        {
            Verb verb = GetRangedVerb(pawn);
            if (verb == null)
            {
                return false;
            }
            IntVec3 cell;
            if (allowReposition)
            {
                cell = FindFiringCell(pawn, target, verb);
            }
            else
            {
                cell = CanFireFrom(pawn.Map, pawn.Position, target, verb, out _)
                    ? pawn.Position
                    : IntVec3.Invalid;
            }
            if (!cell.IsValid)
            {
                return false;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_CrossLevelAttack);
            job.SetTarget(TargetIndex.B, cell);
            job.playerForced = playerForced;
            if (PendingTargets.Count > 64)
            {
                // A failed job start can strand its entry; bound the handoff so it
                // can never grow for a whole session (same pattern as ABPendingOrders).
                PendingTargets.Clear();
            }
            PendingTargets[pawn.thingIDNumber] = target;
            if (playerForced)
            {
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            else
            {
                pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            }
            return true;
        }
    }
}
