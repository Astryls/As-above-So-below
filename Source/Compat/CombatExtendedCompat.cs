using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// COMBAT EXTENDED, CROSS-LEVEL: firing through openings under CE's ballistics, plus the
    /// HIGH-GROUND MODEL the user specified - shooters above their target get suppression
    /// resistance and tighter aim; pawns below fire from above get suppressed harder.
    ///
    /// ⚠ WHY THE VANILLA BRIDGE DOES NOTHING UNDER CE. CE replaces the whole layer: verbs
    /// derive from Verb_LaunchProjectileCE with their OWN shot-line method
    /// (TryFindCEShootLineFromTo - virtual, never calls Verb.TryFindShootLineFromTo), their
    /// own accuracy model (ShiftVecReport, not ShotReport), and their own projectile
    /// (ProjectileCE : ThingWithComps - NOT Verse.Projectile - flying a simulated ballistic
    /// arc with real collision checks along its 2D path). Every §41 patch keys on the vanilla
    /// types, so under CE cross-level combat was simply inert: raw distance made every
    /// cross-band pair "out of range" and nobody ever fired.
    ///
    /// ⚠⚠ AND CE'S PROJECTILE MUST NEVER FLY THE RAW PATH. A vanilla projectile is a visual
    /// that impacts at a precomputed destination; a CE round COLLIDES with what it crosses.
    /// Launched raw across bands it would fly a Slot of gutter and foreign terrain,
    /// suppressing and hitting things on levels the shot never touched in fiction. So the
    /// same three-step shape as §41, at CE's seams:
    ///   1. LEGALITY at TryFindCEShootLineFromTo (permit via ABShaft, DENY the rest - the
    ///      §41c rule; CE's own code would otherwise decide on raw range),
    ///   2. AIM SPACE localized (ShiftVecReportFor's distance cell + ShiftTarget's aim
    ///      vector - CE's bearing and ballistic angle both come from these),
    ///   3. ORIGIN remapped into the target's band at ProjectileCE.Launch, so the round
    ///      flies band-locally where the target actually is - collisions, suppression and
    ///      impact all land on the right level. The relay draws it through ceiling holes
    ///      like any other round (it is registered as a Thing; the relay was retyped for
    ///      exactly this).
    ///
    /// ⚠ EVERY PATCH HERE IS REFLECTION-ONLY AND SHAPE-VALIDATED. No CE type appears in any
    /// signature (project law; also what lets this compile and load without CE installed).
    /// Each Prepare() checks the member exists AND its parameter shape matches before
    /// patching; a future CE rename degrades that one seam to vanilla behaviour and the
    /// combat report says which seam is missing, instead of a red error at startup.
    ///
    /// ⚠ THE HIGH-GROUND NUMBERS (user's spec, constants for now):
    ///   * Suppression is ONE SIGNED RULE: amount x1.5 when the shooter is ABOVE the victim,
    ///     x0.5 when BELOW. That yields both halves of the spec at once - upper pawns resist
    ///     fire from below, lower pawns crumble under plunging fire.
    ///   * Accuracy: shooting DOWN tightens sway and spread to 80%. No bonus shooting up.
    /// </summary>
    public static class ABCECompat
    {
        public const float SuppressionFromAboveMult = 1.5f;

        public const float SuppressionFromBelowMult = 0.5f;

        public const float HighGroundSwayMult = 0.8f;

        public const float HighGroundSpreadMult = 0.8f;

        private static bool resolved;

        private static Type tVerbCE;

        private static Type tProjectileCE;

        private static Type tShiftVecReport;

        internal static FieldInfo fCover;

        internal static FieldInfo fSmoke;

        internal static FieldInfo fSway;

        internal static FieldInfo fSpread;

        internal static FieldInfo fLauncher;

        internal static bool suppressionSeamFound;

        // Observe-only counters for `AB2: combat report`.
        public static int ceShotsSolved;

        public static int ceShotsDenied;

        public static int ceOriginsRemapped;

        public static int ceSuppressionScaled;

        public static int ceHighGroundShots;

        public static void ResetCounters()
        {
            ceShotsSolved = 0;
            ceShotsDenied = 0;
            ceOriginsRemapped = 0;
            ceSuppressionScaled = 0;
            ceHighGroundShots = 0;
        }

        public static string CounterReport()
        {
            if (!Active)
            {
                return "CE: not loaded";
            }
            return "CE: solved=" + ceShotsSolved + " denied=" + ceShotsDenied
                + " originsRemapped=" + ceOriginsRemapped
                + " suppressionScaled=" + ceSuppressionScaled
                + " highGroundShots=" + ceHighGroundShots
                + (suppressionSeamFound ? "" : " | ⚠ suppression seam MISSING (CE renamed it)");
        }

        public static bool Active
        {
            get
            {
                Resolve();
                return tVerbCE != null;
            }
        }

        internal static Type VerbCE { get { Resolve(); return tVerbCE; } }

        internal static Type ProjectileCE { get { Resolve(); return tProjectileCE; } }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            tVerbCE = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
            tProjectileCE = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            tShiftVecReport = AccessTools.TypeByName("CombatExtended.ShiftVecReport");
            if (tShiftVecReport != null)
            {
                fCover = AccessTools.Field(tShiftVecReport, "cover");
                fSmoke = AccessTools.Field(tShiftVecReport, "smokeDensity");
                fSway = AccessTools.Field(tShiftVecReport, "swayDegrees");
                fSpread = AccessTools.Field(tShiftVecReport, "spreadDegrees");
            }
            if (tProjectileCE != null)
            {
                fLauncher = AccessTools.Field(tProjectileCE, "launcher");
            }
        }

        /// <summary>Signed suppression scale for a victim at <paramref name="victimBand"/>
        /// taking fire whose true shooter stands at <paramref name="shooterBand"/>.</summary>
        internal static float SuppressionScale(int shooterBand, int victimBand)
        {
            if (shooterBand > victimBand)
            {
                return SuppressionFromAboveMult;
            }
            if (shooterBand < victimBand)
            {
                return SuppressionFromBelowMult;
            }
            return 1f;
        }
    }

    /// <summary>
    /// SEAM 1: shot legality. Same permit/deny logic as the vanilla bridge, at CE's own
    /// chokepoint. CE's CanHitTargetFrom (player targeting + AI) and TryCastShot both call
    /// this, so one prefix gates everything - and the DENY branch matters just as much as
    /// under vanilla, because CE's fallthrough also decides on raw distance (§41c).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CE_ShootLine_ABCrossBand
    {
        private static MethodBase TargetMethod()
        {
            Type t = ABCECompat.VerbCE;
            if (t == null)
            {
                return null;
            }
            // The 4-arg overload (root, targ, out line, out targetPos) is the real body;
            // the 3-arg one forwards to it.
            foreach (MethodInfo m in t.GetMethods(AccessTools.all))
            {
                if (m.Name == "TryFindCEShootLineFromTo" && m.GetParameters().Length == 4)
                {
                    return m;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static bool Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ,
            ref ShootLine resultingLine, ref Vector3 targetPos, ref bool __result)
        {
            try
            {
                if (!ABBands.Banded(__instance?.caster?.Map))
                {
                    return true;
                }
                if (ABCombatV2.TrySolve(__instance, root, targ, out ABShotSolution sol))
                {
                    // targetPos stays the target's REAL centre: TryCastShot feeds it to the
                    // aim pipeline, and the two patches below localize it there. The line's
                    // Source is the spawn cell, exactly like vanilla.
                    targetPos = targ.HasThing
                        ? targ.Thing.TrueCenter()
                        : targ.Cell.ToVector3Shifted();
                    resultingLine = new ShootLine(root, targ.Cell);
                    ABCombatRelay.RecordSolution(__instance.caster, targ.Cell, sol);
                    ABCECompat.ceShotsSolved++;
                    ABV2Debug.Combat("CE shootline OK " + __instance.caster.LabelShortCap
                        + " " + root + " -> " + targ.Cell
                        + (sol.overhead ? " (overhead)" : " via " + sol.opening));
                    __result = true;
                    return false;
                }
                if (ABCombatV2.OwnsPair(__instance, root, targ))
                {
                    resultingLine = new ShootLine(root, targ.Cell);
                    targetPos = targ.Cell.ToVector3Shifted();
                    __result = false;
                    ABCECompat.ceShotsDenied++;
                    return false;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross-band shoot line");
            }
            return true;
        }
    }

    /// <summary>
    /// SEAM 2a: the accuracy report. CE computes shotDist from the raw cell (a Slot across
    /// bands - every accuracy factor then reads a 128-cell shot) and walks cover/smoke along
    /// the raw line through the gutter. Localize the cell going in; going out, strip the
    /// fabricated cover and smoke (§41k's lesson verbatim: the localized walk samples the
    /// SHOOTER's band, and those covers are not decoration - CE aims around them) and apply
    /// the HIGH-GROUND accuracy bonus when the shooter stands above the target.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CE_ShiftVecReport_ABCrossBand
    {
        private static MethodBase TargetMethod()
        {
            Type t = ABCECompat.VerbCE;
            if (t == null)
            {
                return null;
            }
            foreach (MethodInfo m in t.GetMethods(AccessTools.all))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "ShiftVecReportFor" && p.Length == 2
                    && p[0].ParameterType == typeof(LocalTargetInfo)
                    && p[1].ParameterType == typeof(IntVec3))
                {
                    return m;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            return TargetMethod() != null && ABCECompat.fCover != null
                && ABCECompat.fSmoke != null && ABCECompat.fSway != null
                && ABCECompat.fSpread != null;
        }

        private static void Prefix(Verb __instance, ref LocalTargetInfo target,
            ref IntVec3 targetCell, out int __state)
        {
            // __state: 0 = not ours; +1 = shooter above target; -1 = below; 2 = level but
            // cross-band (cannot happen, kept for the compiler's peace of mind).
            __state = 0;
            try
            {
                Thing caster = __instance?.caster;
                if (caster == null || !caster.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(caster.Map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int casterBand = bands.BandOf(caster.Position);
                int targetBand = bands.BandOf(targetCell);
                if (casterBand == targetBand)
                {
                    return;
                }
                __state = casterBand > targetBand ? 1 : -1;
                IntVec3 local = bands.Translate(targetCell, casterBand);
                targetCell = local;
                // The report reads the target's THING for lean offsets; a localized CELL
                // keeps the distance honest and drops the lean nuance - documented trade,
                // same as §41k made for posture before its repair. CE's report is a CLASS,
                // so the postfix repairs directly instead of box/unbox.
                target = new LocalTargetInfo(local);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross-band shift report");
            }
        }

        private static void Postfix(object __result, int __state)
        {
            if (__state == 0 || __result == null)
            {
                return;
            }
            try
            {
                // No fabricated cover or smoke from the shooter's own band: across levels
                // the OPENING is the cover model (§41a's drift cone), and CE would otherwise
                // aim around furniture the shot never crosses.
                ABCECompat.fCover.SetValue(__result, null);
                ABCECompat.fSmoke.SetValue(__result, 0f);
                if (__state > 0)
                {
                    // High ground: braced, aiming down a hole - 20% tighter sway and spread.
                    ABCECompat.fSway.SetValue(__result,
                        (float)ABCECompat.fSway.GetValue(__result) * ABCECompat.HighGroundSwayMult);
                    ABCECompat.fSpread.SetValue(__result,
                        (float)ABCECompat.fSpread.GetValue(__result) * ABCECompat.HighGroundSpreadMult);
                    ABCECompat.ceHighGroundShots++;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross-band report repair");
            }
        }
    }

    /// <summary>
    /// SEAM 2b: the aim vector. ShiftTarget receives the target's REAL centre and derives
    /// bearing, ballistic angle and lead from `v - sourceLoc` - across bands that vector is
    /// dominated by ±Slot, so the pawn would aim due north at a 60-degree lob. Localizing v
    /// into the caster's band makes every derived quantity band-local. Self-contained (no
    /// latch): the band comparison IS the trigger.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CE_ShiftTarget_ABCrossBand
    {
        private static MethodBase TargetMethod()
        {
            Type t = ABCECompat.VerbCE;
            if (t == null)
            {
                return null;
            }
            foreach (MethodInfo m in t.GetMethods(AccessTools.all))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "ShiftTarget" && p.Length == 5
                    && p[1].ParameterType == typeof(Vector3))
                {
                    return m;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Prefix(Verb __instance, ref Vector3 __1)
        {
            try
            {
                Thing caster = __instance?.caster;
                if (caster == null || !caster.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(caster.Map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int casterBand = bands.BandOf(caster.Position);
                int vBand = bands.BandOf(__1.ToIntVec3());
                if (vBand == casterBand)
                {
                    return;
                }
                __1 = new Vector3(__1.x, __1.y,
                    __1.z + (casterBand - vBand) * bands.Slot);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross-band aim vector");
            }
        }
    }

    /// <summary>
    /// SEAM 3: the origin remap. The parked solution from seam 1 carries the band pair;
    /// when this launch belongs to it (same tick, origin on the solved root band), the 2D
    /// origin moves into the TARGET's band and the round flies band-locally where the
    /// target lives - collisions, suppression and impact all on the correct level. Also
    /// registers with the relay, which draws it through ceiling holes like any round.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CE_ProjectileLaunch_ABCrossBand
    {
        private static MethodBase TargetMethod()
        {
            Type t = ABCECompat.ProjectileCE;
            if (t == null)
            {
                return null;
            }
            foreach (MethodInfo m in t.GetMethods(AccessTools.all))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "Launch" && p.Length == 8
                    && p[1].ParameterType == typeof(Vector2))
                {
                    return m;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Prefix(Thing __instance, Thing launcher, ref Vector2 origin)
        {
            try
            {
                if (launcher == null || !launcher.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                Map map = launcher.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                // ⚠ THE VECTOR2'S y IS THE MAP'S z. CE flattens to 2D ground coordinates
                // plus a separate metric height; the band axis lives in .y here.
                IntVec3 originCell = new IntVec3(Mathf.FloorToInt(origin.x), 0,
                    Mathf.FloorToInt(origin.y));
                if (!originCell.InBounds(map))
                {
                    return;
                }
                int bandFrom = bands.BandOf(originCell);
                if (!ABCombatRelay.TryPeekSolutionFor(bandFrom, out ABShotSolution sol))
                {
                    return; // not a cross-band cast this tick: vanilla-CE behaviour
                }
                origin = new Vector2(origin.x,
                    origin.y + (sol.targetBand - sol.rootBand) * bands.Slot);
                ABCECompat.ceOriginsRemapped++;
                ABCombatRelay.Register(__instance, launcher, sol.rootBand, sol.targetBand, sol);
                ABV2Debug.Combat("CE projectile origin remapped band " + sol.rootBand + " -> "
                    + sol.targetBand + " at " + origin);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE cross-band projectile origin");
            }
        }
    }

    /// <summary>
    /// SEAM 4: SUPPRESSION - the half of the high-ground model the user led with.
    ///
    /// CE applies flyover and impact suppression from the projectile, and by the time
    /// CompSuppressable.AddSuppression runs, the origin it receives is OUR REMAPPED cell -
    /// same band as the victim, band delta zero, a naive hook would never fire (our own
    /// remap would have neutralized it). The projectile's `launcher` still stands on the
    /// TRUE band, so the scale reads the launcher.
    ///
    /// Patched at ApplySuppression on the projectile (shape-validated: Pawn first, float
    /// multiplier second) so the multiplier scales BEFORE Suppressability stats - if CE
    /// renames it, Prepare() skips and the combat report flags the missing seam.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_CE_ApplySuppression_ABHighGround
    {
        private static MethodBase target;

        private static MethodBase TargetMethod()
        {
            if (target != null)
            {
                return target;
            }
            Type t = ABCECompat.ProjectileCE;
            if (t == null)
            {
                return null;
            }
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
            {
                ParameterInfo[] p = m.GetParameters();
                if (m.Name == "ApplySuppression" && p.Length >= 1
                    && p[0].ParameterType == typeof(Pawn)
                    && (p.Length == 1 || p[1].ParameterType == typeof(float)))
                {
                    ABCECompat.suppressionSeamFound = p.Length >= 2;
                    target = ABCECompat.suppressionSeamFound ? m : null;
                    return target;
                }
            }
            return null;
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Prefix(Thing __instance, Pawn __0, ref float __1)
        {
            try
            {
                if (__0 == null || !__0.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(__0.Map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                Thing launcher = ABCECompat.fLauncher != null
                    ? ABCECompat.fLauncher.GetValue(__instance) as Thing
                    : null;
                if (launcher == null || !launcher.Spawned || launcher.Map != __0.Map)
                {
                    return;
                }
                int shooterBand = bands.BandOf(launcher.Position);
                int victimBand = bands.BandOf(__0.Position);
                float scale = ABCECompat.SuppressionScale(shooterBand, victimBand);
                if (scale != 1f)
                {
                    __1 *= scale;
                    ABCECompat.ceSuppressionScaled++;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "CE high-ground suppression");
            }
        }
    }
}
