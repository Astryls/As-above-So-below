using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 cross-level TARGETING - what the PLAYER can point at.
    ///
    /// ABShaft says whether a shot is possible and ABCombatAcquisition makes the AI notice.
    /// Neither helps the player, who has to be able to select the target in the first place,
    /// and there the mod had a hole with a specific shape:
    ///
    /// ⚠ THE ONLY DOWNWARD ORDER PATH WAS GATED ON WALKING. ABBelowClickThrough translates a
    /// see-through click onto the level below, but only after AnyCanReach agrees - which is
    /// exactly right for a MOVE order and exactly wrong for an ATTACK. Shooting through a
    /// hole in the floor does not require a staircase, so with no stairs built you could see
    /// a raider through the gap, watch your turret engage it, and be unable to order a single
    /// colonist to fire at it. Reachability is a question about legs; combat is a question
    /// about geometry, and one must not gate the other.
    ///
    /// ⚠⚠ THE FIX IS ONE PATCH, NOT A NEW ORDER SYSTEM, AND FINDING THAT WAS THE WHOLE JOB.
    /// Every player-side target resolution in the game - the right-click float menu, the
    /// force-fire targeter, psycast targeting, mortar force-targets, jump packs - bottoms out
    /// in GenUI.ThingsUnderMouse. FloatMenuContext builds ClickedThings from it;
    /// GenUI.TargetsAt (which every ITargetingSource uses) opens by calling it. So appending
    /// what the column SHOWS to that one list makes every one of those paths see through the
    /// floor, and each keeps its own validation:
    ///   * the float menu's attack provider still asks verb.CanHitTarget, which is ABShaft,
    ///   * the targeter still asks ITargetingSource.ValidateTarget, which is ABShaft,
    ///   * TargetingParameters still filter by faction, so force-firing on a NEUTRAL or an
    ///     ALLY works cross-level for exactly the same reason it works on one level - the
    ///     relation test never looks at position at all.
    ///
    /// That last point is why "friendlies, hostiles, allies, neutrals" needs no code of its
    /// own. Vanilla's right-click only offers an attack on things hostile to the player (or
    /// non-humanlike); everything else is force-fire through the targeter. Both now resolve
    /// below-level targets, so both cover every relation.
    /// </summary>
    public static class ABCombatTargeting
    {
        public static void ResetCounters()
        {
            ABMouseDescend.descends = 0;
        }

        /// <summary>Reports the GLOBAL descend now, since that is what does this job.
        /// The old seeThroughThings/seeThroughCells pair counted the two deleted patches
        /// and would have read a permanent zero.</summary>
        public static string CounterReport()
        {
            return ABMouseDescend.CounterReport();
        }

        /// <summary>
        /// The cell this click actually means, when the cursor is over open air. Delegates to
        /// the shared see-through resolver, so it descends exactly as far as the RENDERER
        /// does - the property that makes "you can target what you can see" true by
        /// construction instead of by two predicates being kept in step by hand.
        /// </summary>
        /// <remarks>
        /// ⚠ GATED ON THE **RENDERING** GUARD, NOT THE COMBAT ONE, AND THE DIFFERENCE IS
        /// DELIBERATE (window 12). "Which cell is the player pointing at" is a question about
        /// what is DRAWN, and it is the same question the renderer, the right-click
        /// translation and select-through all answer - all three sit behind ABGuard.Rendering.
        /// Sitting behind ABGuard.Combat instead meant a fault anywhere in the shot solver
        /// silently took away the player's ability to POINT at the level below, which is a
        /// much larger blast radius than the fault deserved. Whether the resulting target can
        /// actually be hit is still ABShaft's call, still behind the combat guard.
        /// </remarks>
        public static bool TryResolveClicked(Map map, Vector3 clickPos, out IntVec3 below)
        {
            below = IntVec3.Invalid;
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (!cell.InBounds(map))
            {
                return false;
            }
            if (!ABBands.TryResolveVisibleFrom(map, bands, cell, requireUnfogged: true,
                    out IntVec3 seen, out int _))
            {
                return false;
            }
            below = seen;
            return true;
        }
    }

    // ⚠⚠ TOMBSTONE - `Patch_GenUI_ABThingsUnderMouseSeeThrough` (window 9) AND
    // `Patch_GenUI_ABTargetsAtSeeThrough` (window 9) LIVED HERE AND WERE DELETED IN
    // WINDOW 13, SUPERSEDED BY `ABMouseDescend`.
    //
    // They were the opt-in half of the see-through model: one appended the things the column
    // shows to GenUI.ThingsUnderMouse, the other substituted the cell the column shows into
    // GenUI.TargetsAt. Both worked (field-verified §82, run #375) and both are now redundant,
    // because the clickPos they were handed - and the UI.MouseCell() that TargetsAt yields -
    // have ALREADY descended at the source.
    //
    // ⚠ DO NOT REINSTATE EITHER. Leaving them in alongside the global descend is the §82a
    // defect a third time: TryResolveClicked asks "is this cell open air?" about a cell that
    // has already been resolved to solid floor, answers no, and the patch goes silently inert
    // - which is harmless but reads, to the next person debugging, like the see-through rule
    // is firing when it is not. The append patch would additionally be capable of adding the
    // SAME thing twice if the resolve ever became non-idempotent.
    //
    // What survives here is the part that was never about pointing: TryResolveClicked (kept
    // as the shared, documented resolver) and the turret forced-target range fix below, which
    // is band arithmetic, not a cursor question.

    /// <summary>
    /// TURRET FORCE-TARGETS, and the last raw distance test in the combat path.
    ///
    /// <c>Building_Turret.OrderAttack</c> validates the player's forced target with two
    /// literal subtractions - <c>(targ.Cell - Position).LengthHorizontal</c> against minimum
    /// and maximum range - before anything the verb owns is consulted. That is the ninth shape
    /// of §1's slicing rule (a raw distance test on anisotropic coordinates), and on a banded
    /// map it is wrong in BOTH directions at once:
    ///
    ///   * TOO PERMISSIVE: a mortar target five cells away horizontally but one level down
    ///     measures as a whole Slot, sails past the 29-cell minimum, and is accepted. The
    ///     shell then never fires, because the verb correctly refuses a target inside minimum
    ///     range - so the turret sits with a forced target and does nothing, which reads as a
    ///     broken mortar rather than a rejected order.
    ///   * TOO RESTRICTIVE: two bands apart on the 254-cell layout is 512 cells, past a
    ///     mortar's 500-cell range, so a target the map-coordinate rule permits is refused
    ///     with "beyond maximum range".
    ///
    /// The prefix therefore re-runs vanilla's two checks in BAND-LOCAL terms for cross-band
    /// targets, keeping vanilla's own messages, and then hands off to the rest of vanilla's
    /// body by reimplementing only the four lines that follow. Same-band orders never enter
    /// this code.
    /// </summary>
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.OrderAttack))]
    public static class Patch_BuildingTurretGun_ABBandLocalForcedTarget
    {
        private static readonly AccessTools.FieldRef<Building_Turret, LocalTargetInfo>
            ForcedTargetRef =
                AccessTools.FieldRefAccess<Building_Turret, LocalTargetInfo>("forcedTarget");

        private static readonly AccessTools.FieldRef<Building_TurretGun, int>
            BurstCooldownRef =
                AccessTools.FieldRefAccess<Building_TurretGun, int>("burstCooldownTicksLeft");

        private static readonly AccessTools.FieldRef<Building_TurretGun, bool> HoldFireRef =
            AccessTools.FieldRefAccess<Building_TurretGun, bool>("holdFire");

        private static bool Prepare()
        {
            // Every member above is private or protected vanilla state. If any one of them is
            // renamed the patch removes itself and turrets keep vanilla behaviour, rather
            // than throwing on the first forced target of the game.
            return AccessTools.Field(typeof(Building_Turret), "forcedTarget") != null
                && AccessTools.Field(typeof(Building_TurretGun), "burstCooldownTicksLeft") != null
                && AccessTools.Field(typeof(Building_TurretGun), "holdFire") != null;
        }

        private static bool Prefix(Building_TurretGun __instance, LocalTargetInfo targ)
        {
            try
            {
                if (!targ.IsValid || !__instance.Spawned)
                {
                    return true;
                }
                Verb verb = __instance.AttackVerb;
                if (verb == null || !ABCombatV2.OwnsPair(verb, __instance.Position, targ))
                {
                    return true; // same band, or not ours: vanilla is correct as written
                }
                if (!ABCombatV2.TrySolve(verb, __instance.Position, targ,
                        out ABShotSolution sol))
                {
                    // Which of the two limits failed, so the message is the true one. The
                    // solver has already refused, so this only has to attribute the refusal.
                    ABBandMap bands = ABBands.CompOf(__instance.Map);
                    float horizontal = (bands.Translate(targ.Cell,
                        bands.BandOf(__instance.Position)) - __instance.Position)
                        .LengthHorizontal;
                    bool tooClose = horizontal
                        < verb.verbProps.EffectiveMinRange(targ, __instance);
                    Messages.Message(
                        (tooClose ? "MessageTargetBelowMinimumRange" : "MessageTargetBeyondMaximumRange")
                            .Translate(),
                        __instance, MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }

                // Vanilla's remaining body, verbatim in behaviour. Reimplemented rather than
                // postfixed because its two range guards are the thing being replaced and
                // they sit AHEAD of the part worth keeping.
                if (ForcedTargetRef(__instance) != targ)
                {
                    ForcedTargetRef(__instance) = targ;
                    if (BurstCooldownRef(__instance) <= 0)
                    {
                        __instance.TryStartShootSomething(canBeginBurstImmediately: false);
                    }
                }
                if (HoldFireRef(__instance))
                {
                    Messages.Message(
                        "MessageTurretWontFireBecauseHoldFire".Translate(__instance.def.label),
                        __instance, MessageTypeDefOf.RejectInput, historical: false);
                }
                ABV2Debug.Combat("turret " + __instance.LabelShortCap + " forced target "
                    + targ.Cell + " accepted, dist " + sol.distance.ToString("0.0")
                    + (sol.overhead ? " (overhead)" : " via opening " + sol.opening));
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 turret cross-band forced target");
            }
            return true;
        }
    }
}
