using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// JUMPING BETWEEN LEVELS - jump packs, and every ability that flies a pawn to a cell.
    ///
    /// This rides entirely on ABShaft, and that is the point: a jump through a hole in the
    /// floor is the same geometry question as a shot through it. Is there an opening, is it in
    /// range, is there line of sight to it. So there is no second solver here, only three
    /// small repairs where the jump path measures things for itself instead of asking a verb.
    ///
    /// ⚠ AND THE PLAYER-FACING HALF OF THIS FEATURE IS NOT IN THIS FILE. Being able to point
    /// at an open-air cell and have it mean "the floor I can see through it" is
    /// Patch_GenUI_ABTargetsAtSeeThrough's cell substitution. By the time anything here runs,
    /// the target is already a real standable cell on another band - which is why
    /// JumpUtility.ValidJumpTarget needs no patch at all. It refuses open air (Impassable),
    /// and it is right to: nothing may LAND in the void. The air cell was never the
    /// destination, it was the aperture.
    ///
    /// ⚠ WALKING INTO AIR STILL DOES NOTHING, DELIBERATELY. Open air is Impassable, so vanilla
    /// already refuses to path into it, and there is no fall system to catch a pawn that
    /// stepped off a ledge. A jump is a decision; a fall would be a consequence, and the mod
    /// does not model consequences yet.
    /// </summary>
    public static class ABBandLeap
    {
        public static int crossBandJumps;

        public static void ResetCounters()
        {
            crossBandJumps = 0;
        }

        public static string CounterReport()
        {
            return "leaps: crossBand=" + crossBandJumps;
        }
    }

    /// <summary>
    /// REPAIR 1: the range and sight test.
    ///
    /// <c>Verb_Jump.CanHitTargetFrom</c> does not chain to <c>Verb.CanHitTargetFrom</c> - it
    /// forwards to <c>JumpUtility.CanHitTargetFrom</c>, which is two raw lines:
    /// <c>pawn.Position.DistanceToSquared(cell)</c> against range, then a flat
    /// <c>GenSight.LineOfSight</c>. Neither knows what a band is, so the shoot-line patch that
    /// covers every other ranged verb never sees a jump at all. That is why jump packs stayed
    /// single-level while shooting worked.
    ///
    /// ⚠ THIS IS THE SAME LESSON AS §37's MECH COMMAND RANGE, in a different subsystem: a
    /// helper that reimplements a range check rather than delegating to the verb is invisible
    /// to a patch on the verb. When a feature works for shooting and not for its sibling,
    /// look for a private distance test, not for a missing flag.
    /// </summary>
    [HarmonyPatch(typeof(JumpUtility), nameof(JumpUtility.CanHitTargetFrom))]
    public static class Patch_JumpUtility_ABCrossBandRange
    {
        private static bool Prefix(Pawn pawn, IntVec3 root, LocalTargetInfo targ, float range,
            ref bool __result)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || !ABCombatV2.Enabled || !targ.IsValid)
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(pawn.Map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                if (bands.BandOf(root) == bands.BandOf(targ.Cell))
                {
                    return true; // one level: vanilla's maths is correct and cheaper
                }
                // A leap needs a real hole, so never the overhead rule - a pawn cannot arc
                // over a ceiling the way a shell arcs over a wall. Minimum range is zero for
                // every jump verb in the game, and passing it explicitly keeps that assumption
                // visible rather than inherited.
                __result = ABShaft.TrySolve(pawn.Map, root, targ.Cell, range, 0f,
                    overheadFire: false, out ABShotSolution sol);
                if (__result)
                {
                    ABV2Debug.Combat("jump solution " + pawn.LabelShortCap + " " + root + " -> "
                        + targ.Cell + " via opening " + sol.opening + " dist "
                        + sol.distance.ToString("0.0"));
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 cross-band jump range");
            }
            return true;
        }
    }

    /// <summary>
    /// REPAIR 2: the arc.
    ///
    /// <c>PawnFlyer.MakeFlyer</c> stores <c>startVec = pawn.TrueCenter()</c> and the flyer
    /// lerps from there to the destination, so a cross-band leap animates the pawn sailing a
    /// whole Slot through the gutter and every level between - the same absurdity the
    /// projectile origin patch fixes for bullets, and fixed the same way. Translating the
    /// START into the destination's band makes the pawn arc out of the opening at the matching
    /// spot, over the short real horizontal distance.
    ///
    /// ⚠ THE START IS TRANSLATED, NOT THE DESTINATION. The destination is where the pawn
    /// genuinely ends up and must not be touched; the start is purely animation state
    /// (vanilla itself lets callers override it, which is what <c>overrideStartVec</c> is
    /// for). Moving the real endpoint would be the §33c mistake - putting a cosmetic effect on
    /// the path that decides an outcome.
    /// </summary>
    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
    public static class Patch_PawnFlyer_ABCrossBandArc
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> StartVecRef =
            AccessTools.FieldRefAccess<PawnFlyer, Vector3>("startVec");

        private static bool Prepare()
        {
            return AccessTools.Field(typeof(PawnFlyer), "startVec") != null;
        }

        private static void Postfix(Pawn pawn, IntVec3 destCell, PawnFlyer __result)
        {
            try
            {
                if (__result == null || pawn == null || !pawn.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(pawn.Map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int fromBand = bands.BandOf(pawn.Position);
                int toBand = bands.BandOf(destCell);
                if (fromBand == toBand)
                {
                    return;
                }
                Vector3 start = StartVecRef(__result);
                float within = start.z - fromBand * bands.Slot;
                StartVecRef(__result) = new Vector3(start.x, start.y,
                    toBand * bands.Slot + within);
                ABBandLeap.crossBandJumps++;
                ABV2Debug.Combat("cross-band leap " + pawn.LabelShortCap + " band " + fromBand
                    + " -> " + toBand + ", arc start remapped");
            }
            catch (Exception e)
            {
                // Cosmetic only: a wrong arc is ugly, a thrown MakeFlyer loses the pawn.
                Log.WarningOnce(ABLog.Tag + " V2: leap arc remap threw: " + e.Message, 762195935);
            }
        }
    }
}
