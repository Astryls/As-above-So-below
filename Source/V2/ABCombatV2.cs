using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 cross-level combat.
    ///
    /// This is the ONE system that does not come free from the banded design, and it is
    /// worth being clear about why. Hauling, needs, work, prisoners and trade are GRAPH
    /// problems: the wormhole RegionLink makes connectivity correct and vanilla's
    /// reachability does the rest. Combat is a GEOMETRY problem - GenSight and weapon range
    /// are computed in flat 2D cell space, and the band layout fakes vertical adjacency
    /// using DISTANCE. To vanilla, a pawn one level up is literally one Slot (256 cells)
    /// north, behind an impassable gutter: out of range, no line of sight.
    ///
    /// So the bridge's whole job is to answer two questions in BAND space instead of cell
    /// space: is the target within range, and can it be seen. Everything else - damage,
    /// accuracy, cover, stances, hit resolution - is left to vanilla untouched.
    ///
    /// Compared to V1 this is far smaller. V1's Combat/ is ~3,700 lines, a large share of
    /// which (CrossGapProjectiles especially) exists purely to hand projectiles between two
    /// different Map objects and keep map indices straight. Here both parties are on one
    /// map, so a projectile only needs its ORIGIN remapped across the band offset.
    /// </summary>
    public static class ABCombatV2
    {
        /// <summary>How far the shot may drift horizontally per band crossed. A shaft is not
        /// a window: you shoot at what is more or less under (or over) you, and the further
        /// off-axis the target is, the more the intervening floor blocks it.</summary>
        private const float MaxHorizontalPerBand = 12f;

        public static bool Enabled => ABGuard.On(ABGuard.Combat);

        /// <summary>Translate a cell from its own band into <paramref name="toBand"/>,
        /// preserving the in-band position. Bands are aligned 1:1, so this is what makes
        /// "directly above" meaningful.</summary>
        public static IntVec3 ToBand(ABBandMap bands, IntVec3 c, int toBand)
        {
            return bands.Translate(c, toBand);
        }

        /// <summary>Core rule. True when <paramref name="root"/> may shoot
        /// <paramref name="targetCell"/> across exactly one band boundary.
        ///
        /// The shot must pass through a hole, so the cell in the UPPER band directly above
        /// the lower participant has to be open air. Horizontal line of sight is then
        /// checked WITHIN the upper band, between the shooter and that hole, which is the
        /// closest honest analogue of firing down (or up) a shaft.</summary>
        public static bool TryCrossBandShot(Map map, IntVec3 root, IntVec3 targetCell,
            float range, out float effectiveDistance)
        {
            effectiveDistance = 0f;
            if (map == null || !Enabled)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int bandRoot = bands.BandOf(root);
            int bandTarg = bands.BandOf(targetCell);
            if (bandRoot == bandTarg || Mathf.Abs(bandRoot - bandTarg) != 1)
            {
                return false; // same band is vanilla's job; two bands apart is solid rock
            }
            if (bands.InGutter(root) || bands.InGutter(targetCell))
            {
                return false;
            }

            // Horizontal offset, measured with the target brought into the shooter's band.
            IntVec3 targetHere = bands.Translate(targetCell, bandRoot);
            float horizontal = (targetHere - root).LengthHorizontal;
            if (horizontal > MaxHorizontalPerBand)
            {
                return false;
            }
            // One band of separation costs a little range, so a vertical shot is not free.
            effectiveDistance = horizontal + 1f;
            if (effectiveDistance > range)
            {
                return false;
            }

            // The hole: the upper band's cell over the lower participant must be open air.
            bool rootIsUpper = bandRoot > bandTarg;
            int upperBand = rootIsUpper ? bandRoot : bandTarg;
            IntVec3 lowerCell = rootIsUpper ? targetCell : root;
            IntVec3 hole = bands.Translate(lowerCell, upperBand);
            if (!hole.InBounds(map) || map.terrainGrid.TerrainAt(hole) != ABDefOf.AB_OpenAir)
            {
                return false;
            }

            // Horizontal sight within the upper band, from the upper participant to the hole.
            IntVec3 upperCell = rootIsUpper ? root : bands.Translate(root, upperBand);
            if (upperCell != hole && !GenSight.LineOfSight(upperCell, hole, map, skipFirstCell: true))
            {
                return false;
            }
            return true;
        }

        /// <summary>Convenience wrapper used by the Verb patch.</summary>
        public static bool TryCrossBandShot(Verb verb, IntVec3 root, LocalTargetInfo targ,
            out ShootLine line)
        {
            line = default(ShootLine);
            Thing caster = verb?.caster;
            if (caster == null || !caster.Spawned)
            {
                return false;
            }
            if (verb.verbProps == null || verb.verbProps.IsMeleeAttack)
            {
                return false; // no reaching between levels with a knife
            }
            if (!targ.IsValid)
            {
                return false;
            }
            if (targ.HasThing && targ.Thing.Map != caster.Map)
            {
                return false;
            }
            if (!TryCrossBandShot(caster.Map, root, targ.Cell, verb.EffectiveRange, out float _))
            {
                return false;
            }
            line = new ShootLine(root, targ.Cell);
            return true;
        }
    }

    /// <summary>
    /// Range and line of sight, at the single choke point both flow through.
    /// CanHitTargetFrom delegates to this, so one prefix covers targeting validation, AI
    /// target selection and the actual cast.
    /// </summary>
    [HarmonyPatch(typeof(Verb), nameof(Verb.TryFindShootLineFromTo))]
    public static class Patch_Verb_ABCrossBandShootLine
    {
        private static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref bool __result)
        {
            try
            {
                if (!LevelCensus.AnyLevelColumns && !ABBands.Banded(__instance?.caster?.Map))
                {
                    return true;
                }
                if (ABCombatV2.TryCrossBandShot(__instance, root, targ, out ShootLine line))
                {
                    resultingLine = line;
                    __result = true;
                    return false;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band shoot line");
            }
            return true;
        }
    }

    /// <summary>
    /// Projectile visuals. Without this the bullet is launched from the shooter's real
    /// position and has to physically cross a whole band - 256 cells of gutter and terrain -
    /// which both looks absurd and takes seconds to arrive.
    ///
    /// Because bands are aligned 1:1, translating the ORIGIN into the target's band puts
    /// the muzzle flash at the equivalent spot directly above or below, and the projectile
    /// then travels the short real horizontal distance. V1 needed a whole file
    /// (CrossGapProjectiles) to hand projectiles between two Maps; here it is one vector.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new Type[]
    {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo),
        typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef)
    })]
    public static class Patch_Projectile_ABCrossBandOrigin
    {
        private static void Prefix(Thing launcher, ref Vector3 origin, LocalTargetInfo usedTarget)
        {
            try
            {
                if (launcher == null || !launcher.Spawned || !usedTarget.IsValid)
                {
                    return;
                }
                Map map = launcher.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABCombatV2.Enabled)
                {
                    return;
                }
                IntVec3 originCell = origin.ToIntVec3();
                int bandFrom = bands.BandOf(originCell);
                int bandTo = bands.BandOf(usedTarget.Cell);
                if (bandFrom == bandTo)
                {
                    return;
                }
                float within = origin.z - bandFrom * bands.Slot;
                origin = new Vector3(origin.x, origin.y, bandTo * bands.Slot + within);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band projectile origin");
            }
        }
    }
}
