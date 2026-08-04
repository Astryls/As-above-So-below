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
        // Observe-only counters for `AB2: combat report`.
        public static int thingsAppended;

        public static int cellsTranslated;

        public static void ResetCounters()
        {
            thingsAppended = 0;
            cellsTranslated = 0;
        }

        public static string CounterReport()
        {
            return "targeting: seeThroughThings=" + thingsAppended
                + " seeThroughCells=" + cellsTranslated;
        }

        /// <summary>
        /// The cell this click actually means, when the cursor is over open air. Delegates to
        /// the shared see-through resolver, so it descends exactly as far as the RENDERER
        /// does - the property that makes "you can target what you can see" true by
        /// construction instead of by two predicates being kept in step by hand.
        /// </summary>
        public static bool TryResolveClicked(Map map, Vector3 clickPos, out IntVec3 below)
        {
            below = IntVec3.Invalid;
            if (map == null || !ABCombatV2.Enabled)
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

    /// <summary>
    /// THE one interception. Everything the player can point at is filtered through this
    /// list; see the banner in ABCombatTargeting for why it is the only place worth patching.
    ///
    /// A POSTFIX that APPENDS, never one that replaces: things on the level you are actually
    /// looking at must keep winning, because <c>Targeter</c> and the float menu both treat
    /// list order as preference. Ordering the below-level things last is what stops a
    /// force-fire click landing on a raider one storey down when there is one right in front
    /// of the cursor.
    /// </summary>
    [HarmonyPatch(typeof(GenUI), nameof(GenUI.ThingsUnderMouse))]
    public static class Patch_GenUI_ABThingsUnderMouseSeeThrough
    {
        private static void Postfix(Vector3 clickPos, TargetingParameters clickParams,
            ITargetingSource source, List<Thing> __result)
        {
            try
            {
                if (__result == null || !ABBelowClickThrough.Enabled)
                {
                    return;
                }
                Map map = Find.CurrentMap;
                if (!ABCombatTargeting.TryResolveClicked(map, clickPos, out IntVec3 below))
                {
                    return;
                }
                List<Thing> things = map.thingGrid.ThingsListAtFast(below);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || __result.Contains(t))
                    {
                        continue;
                    }
                    // clickParams is the caller's own filter - faction relations, pawns only,
                    // buildings only, whatever this particular targeter wants. Honouring it
                    // here is what makes one patch serve psycasts, mortars and jump packs
                    // without knowing anything about them.
                    if (clickParams != null && !clickParams.CanTarget(t, source))
                    {
                        continue;
                    }
                    __result.Add(t);
                    ABCombatTargeting.thingsAppended++;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 see-through target list");
            }
        }
    }

    /// <summary>
    /// The bare-CELL half of the same problem, which the thing list cannot cover.
    ///
    /// <c>GenUI.TargetsAt</c> ends by yielding <c>UI.MouseCell()</c> for any targeter that
    /// accepts locations - mortars, most offensive psycasts, jump packs. Over an open-air cell
    /// that raw cell is a hole in the sky: legal to target and completely pointless, because
    /// the shell or the psycast would resolve in mid-air one level above the thing the player
    /// is looking at.
    ///
    /// ⚠ SO THIS ONE SUBSTITUTES RATHER THAN APPENDS, and that asymmetry with the thing patch
    /// above is deliberate. There is no reading of "I clicked the open air" that means the air
    /// itself. For a thing, both readings exist and the nearer one should win; for a cell, the
    /// see-through reading is the only one.
    /// </summary>
    [HarmonyPatch(typeof(GenUI), nameof(GenUI.TargetsAt))]
    public static class Patch_GenUI_ABTargetsAtSeeThrough
    {
        private static void Postfix(Vector3 clickPos, TargetingParameters clickParams,
            bool thingsOnly, ITargetingSource source,
            ref IEnumerable<LocalTargetInfo> __result)
        {
            if (thingsOnly || __result == null)
            {
                return; // no cell is yielded in that mode, so there is nothing to substitute
            }
            __result = Wrap(__result, clickPos, clickParams, source);
        }

        private static IEnumerable<LocalTargetInfo> Wrap(IEnumerable<LocalTargetInfo> inner,
            Vector3 clickPos, TargetingParameters clickParams, ITargetingSource source)
        {
            Map map = Find.CurrentMap;
            bool resolved = false;
            IntVec3 below = IntVec3.Invalid;
            try
            {
                resolved = ABBelowClickThrough.Enabled
                    && ABCombatTargeting.TryResolveClicked(map, clickPos, out below);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Combat, e, "V2 see-through cell target");
                resolved = false;
            }
            foreach (LocalTargetInfo t in inner)
            {
                if (!resolved || t.HasThing || !t.IsValid)
                {
                    yield return t;
                    continue;
                }
                // ⚠ NO try/catch AROUND A yield - C# forbids it, and this is why the resolve
                // happens above the loop rather than inline where it reads more naturally.
                if (clickParams != null
                    && !clickParams.CanTarget(new TargetInfo(below, map), source))
                {
                    yield return t;
                    continue;
                }
                ABCombatTargeting.cellsTranslated++;
                yield return new LocalTargetInfo(below);
            }
        }
    }

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
