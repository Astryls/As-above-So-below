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
    /// REPAIR 2: the arc, the flight time, and the pawn's real cell during transit.
    ///
    /// <c>PawnFlyer.MakeFlyer</c> stores <c>startVec = pawn.TrueCenter()</c> and
    /// <c>flightDistance = pawn.Position.DistanceTo(destCell)</c>; the flyer then lerps from
    /// that start to the destination. A cross-band leap is one whole Slot apart in raw
    /// coordinates, so vanilla animates the pawn sailing across the gutter and every level
    /// between - the same absurdity the projectile origin patch fixes for bullets, and fixed
    /// the same way. Translating the START into the destination's band makes the pawn arc out
    /// of the opening at the matching spot, over the short real horizontal distance.
    ///
    /// ⚠ THE START IS TRANSLATED, NOT THE DESTINATION. The destination is where the pawn
    /// genuinely ends up and must not be touched; the start is purely animation state
    /// (vanilla itself lets callers override it, which is what <c>overrideStartVec</c> is
    /// for). Moving the real endpoint would be the §33c mistake - putting a cosmetic effect on
    /// the path that decides an outcome.
    ///
    /// ⚠⚠ BUT IT IS NOT ONLY COSMETIC, WHICH IS WHY THIS BEING BROKEN WAS REPORTED AS A
    /// GAMEPLAY BUG ("they jump across the entirety of the map until they reach the slice the
    /// target was on"). Two vanilla behaviours hang off those two fields:
    ///   * <c>RecomputePosition</c> ends with <c>base.Position = groundPos.ToIntVec3()</c>, so
    ///     the flyer's REAL cell walks the lerp. Unremapped, the pawn is genuinely dragged
    ///     across the map, through the gutter and through every intervening level.
    ///   * <c>SpawnSetup</c> turns <c>flightDistance</c> into <c>ticksFlightTime</c>
    ///     (distance / flightSpeed, floored at flightDurationMin). A Slot-sized distance is a
    ///     Slot-sized flight, so the leap also took tens of seconds instead of a moment.
    /// Both are corrected here, from the same band-local geometry.
    ///
    /// ⚠⚠⚠ AND THE REASON IT NEVER RAN: **MakeFlyer DESPAWNS THE PAWN BEFORE IT RETURNS.**
    /// Its last act before handing back the flyer is
    /// <c>if (pawn.Spawned) pawn.DeSpawn(DestroyMode.WillReplace)</c> followed by
    /// <c>innerContainer.TryAdd(pawn)</c>. The old code was a lone POSTFIX opening with
    /// <c>if (!pawn.Spawned) return;</c> - which is false for every single call - and
    /// <c>pawn.Map</c> is null by then anyway. It shipped from window 8 to window 11 as dead
    /// code, silently, because a postfix that returns early looks exactly like a postfix with
    /// nothing to do.
    ///
    /// ⚠ THE RULE: A FACTORY THAT HANDS BACK A NEW OBJECT HAS USUALLY CONSUMED THE OLD ONE.
    /// Anything a postfix wants to know about the INPUT - is it spawned, what map, what cell,
    /// what band - must be captured in a PREFIX while the input is still alive. `__state` is
    /// the whole mechanism and it costs one struct.
    /// </summary>
    [HarmonyPatch(typeof(PawnFlyer), nameof(PawnFlyer.MakeFlyer))]
    public static class Patch_PawnFlyer_ABCrossBandArc
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> StartVecRef =
            AccessTools.FieldRefAccess<PawnFlyer, Vector3>("startVec");

        private static readonly AccessTools.FieldRef<PawnFlyer, float> FlightDistanceRef =
            AccessTools.FieldRefAccess<PawnFlyer, float>("flightDistance");

        /// <summary>Everything the postfix needs about a pawn that will not exist by then.
        /// Both values are already in the DESTINATION band.</summary>
        public struct Origin
        {
            public bool valid;

            public Vector3 start;

            public float distance;

            public int fromBand;

            public int toBand;

            public string label;
        }

        private static bool Prepare()
        {
            // Two private vanilla fields. If either is renamed the patch removes itself and
            // leaps keep vanilla behaviour - ugly across bands, but never thrown.
            return AccessTools.Field(typeof(PawnFlyer), "startVec") != null
                && AccessTools.Field(typeof(PawnFlyer), "flightDistance") != null;
        }

        private static void Prefix(Pawn pawn, IntVec3 destCell, Vector3? overrideStartVec,
            ref Origin __state)
        {
            __state = default(Origin);
            try
            {
                if (pawn == null || !pawn.Spawned || !ABCombatV2.Enabled)
                {
                    return;
                }
                Map map = pawn.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !destCell.InBounds(map))
                {
                    return;
                }
                int fromBand = bands.BandOf(pawn.Position);
                int toBand = bands.BandOf(destCell);
                if (fromBand == toBand)
                {
                    return; // one level: vanilla's numbers are already right
                }
                // Vanilla's own choice of start, then moved into the destination's band with
                // the in-band offset preserved. Reproducing the `??` here rather than reading
                // the field back means an overridden start (PitBurrow, fleshbeast emergences)
                // is translated too instead of being quietly overwritten.
                Vector3 start = overrideStartVec ?? pawn.TrueCenter();
                float within = start.z - fromBand * bands.Slot;
                __state.start = new Vector3(start.x, start.y, toBand * bands.Slot + within);
                // Band-local flight distance, measured between the two ends we will actually
                // draw. Floored at 1 for the same reason vanilla floors it in SpawnSetup: a
                // zero-length flight divides into a zero-tick flight.
                float dx = __state.start.x - (destCell.x + 0.5f);
                float dz = __state.start.z - (destCell.z + 0.5f);
                __state.distance = Mathf.Max(Mathf.Sqrt(dx * dx + dz * dz), 1f);
                __state.fromBand = fromBand;
                __state.toBand = toBand;
                __state.label = pawn.LabelShortCap;
                __state.valid = true;
            }
            catch (Exception e)
            {
                __state = default(Origin);
                Log.WarningOnce(ABLog.Tag + " V2: leap origin capture threw: " + e.Message,
                    762195934);
            }
        }

        private static void Postfix(PawnFlyer __result, ref Origin __state)
        {
            try
            {
                if (__result == null || !__state.valid)
                {
                    return;
                }
                StartVecRef(__result) = __state.start;
                FlightDistanceRef(__result) = __state.distance;
                ABBandLeap.crossBandJumps++;
                ABV2Debug.Combat("cross-band leap " + __state.label + " band "
                    + __state.fromBand + " -> " + __state.toBand + ", arc start remapped to "
                    + __state.start + " over " + __state.distance.ToString("0.0") + " cells");
            }
            catch (Exception e)
            {
                // A wrong arc is ugly; a throw here would strand a pawn inside a flyer.
                Log.WarningOnce(ABLog.Tag + " V2: leap arc remap threw: " + e.Message, 762195935);
            }
        }
    }

    /// <summary>
    /// REPAIR 3: A PAWN IN FLIGHT IS VISIBLE FROM THE LEVEL ABOVE, like every other thing the
    /// column shows. Reported as "the leaping pawn is not visible across layers like drop pods
    /// and everything else should be", and it was §82.6's parked residue coming due.
    ///
    /// ⚠ THE BELOW-THINGS PASS ALREADY TRIED AND COULD NOT WIN, WHICH IS THE WHOLE LESSON.
    /// ABBelowDynamicDraw hands every below-band thing a LIFTED position via
    /// `t.DynamicDrawPhaseAt(phase, loc)` - and <c>PawnFlyer</c> is the one thing in the game
    /// that ignores the position it is given:
    ///
    ///     public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, ...)
    ///     {
    ///         RecomputePosition();
    ///         FlyingPawn.DynamicDrawPhaseAt(phase, effectivePos);   // &lt;-- not drawLoc
    ///         base.DynamicDrawPhaseAt(phase, drawLoc, flip);
    ///     }
    ///
    /// So the carrier was lifted and the passenger was not, and the pawn drew a whole Slot
    /// below the screen. **Rule 38 from the other side: when a thing insists on drawing itself
    /// at a position of its own, the offset has to be applied to THAT position, not to the one
    /// you pass it.**
    ///
    /// Hence a postfix on the private recompute, moving `effectivePos` (the pawn and its
    /// carried thing) and `groundPos` (the shadow) into the viewed band.
    ///
    /// ⚠⚠ AND IT MUST ONLY FIRE WHEN THE BODY ACTUALLY RECOMPUTED. `RecomputePosition` opens
    /// with `if (positionLastComputedTick != ticksFlying)` and is called from BOTH `DrawPos`
    /// and `DynamicDrawPhaseAt`, several times a frame. A postfix that added the offset every
    /// call would add it to an already-offset field and the flyer would climb off the top of
    /// the map within a second. The prefix records whether the recompute was going to happen;
    /// only then does the postfix touch anything.
    ///
    /// ⚠ `base.Position` IS ASSIGNED INSIDE THE BODY, FROM THE UNLIFTED `groundPos`, BEFORE
    /// THIS RUNS - which is exactly right and must stay that way. The flyer's real cell is
    /// what our own below-pass gate, the fog test and the landing all read; only the DRAW is a
    /// fiction (rule 28: a fiction installed for one subsystem is read as fact by every other).
    ///
    /// ⚠⚠ AND UPWARD LEAPS ARE DRAWN TOO - THE ONE PLACE THIS MOD LOOKS UP.
    /// Everything else here is downward-only: you see your level and whatever the open columns
    /// show beneath it. A flyer heading UP is the exception that earns itself, because §82b
    /// remapped its arc into the DESTINATION band, so from the moment it leaves the ground it
    /// is a band above you and vanishes at takeoff - the player watches a pawn delete itself.
    ///
    /// The gate is the same resolver, run the other way round: ask what the FLYER'S OWN column
    /// shows beneath it, and draw it here only if the answer is a cell on the band you are
    /// looking at. That is exactly the hole it is flying up through, so nothing is invented -
    /// and it degrades honestly: the moment the pawn crosses over solid ground on the level
    /// above, its column stops showing you anything and it leaves the screen, which is what a
    /// ceiling is for.
    /// </summary>
    [HarmonyPatch(typeof(PawnFlyer), "RecomputePosition")]
    public static class Patch_PawnFlyer_ABLiftInTransit
    {
        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> EffectivePosRef =
            AccessTools.FieldRefAccess<PawnFlyer, Vector3>("effectivePos");

        private static readonly AccessTools.FieldRef<PawnFlyer, Vector3> GroundPosRef =
            AccessTools.FieldRefAccess<PawnFlyer, Vector3>("groundPos");

        private static readonly AccessTools.FieldRef<PawnFlyer, int> LastComputedRef =
            AccessTools.FieldRefAccess<PawnFlyer, int>("positionLastComputedTick");

        private static readonly AccessTools.FieldRef<PawnFlyer, int> TicksFlyingRef =
            AccessTools.FieldRefAccess<PawnFlyer, int>("ticksFlying");

        private static bool Prepare()
        {
            return AccessTools.Method(typeof(PawnFlyer), "RecomputePosition") != null
                && AccessTools.Field(typeof(PawnFlyer), "effectivePos") != null
                && AccessTools.Field(typeof(PawnFlyer), "groundPos") != null
                && AccessTools.Field(typeof(PawnFlyer), "positionLastComputedTick") != null
                && AccessTools.Field(typeof(PawnFlyer), "ticksFlying") != null;
        }

        private static void Prefix(PawnFlyer __instance, ref bool __state)
        {
            __state = LastComputedRef(__instance) != TicksFlyingRef(__instance);
        }

        private static void Postfix(PawnFlyer __instance, bool __state)
        {
            try
            {
                if (!__state || !__instance.Spawned || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                Map map = __instance.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                if (!TryTransitOffset(map, bands, ABBandView.CurrentBand(map),
                        __instance.Position, out float dz))
                {
                    return;
                }
                Vector3 offset = new Vector3(0f, 0f, dz);
                EffectivePosRef(__instance) += offset;
                GroundPosRef(__instance) += offset;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: flyer transit lift threw: " + e.Message,
                    762195937);
            }
        }

        /// <summary>
        /// How far to move the DRAW so a flyer on another band appears on the one being
        /// viewed, or false when it should not be drawn at all.
        ///
        /// Both directions go through a see-through test and neither invents visibility:
        /// BELOW uses the shared lift (the column the player is looking down), ABOVE asks the
        /// flyer's own column what it shows and accepts only the view band - which is the same
        /// opening, read from the other end.
        /// </summary>
        private static bool TryTransitOffset(Map map, ABBandMap bands, int viewBand, IntVec3 pos,
            out float dz)
        {
            dz = 0f;
            int band = bands.BandOf(pos);
            if (band == viewBand)
            {
                return false; // vanilla draws it exactly where it is
            }
            if (band < viewBand)
            {
                if (!ABRangeOverlay.TryLiftCell(map, bands, viewBand, pos, out IntVec3 lifted))
                {
                    return false;
                }
                dz = lifted.z - pos.z;
                return true;
            }
            if (!ABBands.TryResolveVisibleFrom(map, bands, pos, requireUnfogged: true,
                    out IntVec3 shown, out int drop))
            {
                return false; // solid ground under it: there is a ceiling in the way
            }
            if (bands.BandOf(shown) != viewBand)
            {
                return false; // the column bottoms out on some other level, not this one
            }
            dz = -drop;
            return true;
        }
    }
}
