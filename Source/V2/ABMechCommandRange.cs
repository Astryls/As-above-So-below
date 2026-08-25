using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// BAND-LOCAL MECHANITOR COMMAND RANGE.
    ///
    /// `Pawn_MechanitorTracker.CanCommandTo` is a raw radius on unremapped map coordinates:
    ///
    ///     return (float)pawn.Position.DistanceToSquared(target.Cell) &lt; 620.01f;  // 24.9 cells
    ///
    /// where `pawn` is the MECHANITOR and `target` is wherever the player just clicked. Our
    /// smallest slot is 128 cells of z per band, so the vertical term alone is over 5x the
    /// whole radius, and squaring makes it 26x the budget. On a banded map that test can
    /// therefore NEVER pass for a target on another band - not "rarely", not "at the far
    /// edge": never, at any level size, from any position.
    ///
    /// ⚠ IT IS THE SLICING RULE'S "MAP-WIDE SCALAR" ROW WEARING A DIFFERENT HAT (§1): a
    /// distance measured straight THROUGH the stack, as though the levels were one field of
    /// open ground. The mod's answer to that shape is always the same - remap the point into
    /// the observer's band and measure there.
    ///
    /// WHAT IT BREAKS WITHOUT THIS PATCH. Every consumer of the check is an ORDER path, so
    /// the damage is "mechs cannot be told to do anything on another floor":
    ///   - FloatMenuOptionProvider_DraftedMove   -> "Cannot go: out of command range"
    ///   - FloatMenuOptionProvider_DraftedAttack -> attack orders refused
    ///   - FloatMenuUtility (melee + ranged)     -> "OutOfCommandRange"
    ///   - MultiPawnGotoController               -> dest set to IntVec3.Invalid, and
    ///     IssueGotoJobs then SKIPS that pawn with no message at all, so a group order
    ///     silently drops every mech in the selection.
    /// With the mechanitor on the surface, the entire basement is out of range for all of
    /// them. Autonomous work is NOT affected - `InMechanitorCommandRange` has exactly six
    /// call sites and none of them are in the work pipeline (verified against the whole
    /// decompiled assembly, 2026-08) - so this patch cannot change what mechs choose to do
    /// on their own, only what the player is allowed to order.
    ///
    /// A LEVEL HOP IS FREE (user's call). The bands are stacked in the fiction, so the cell
    /// directly below your feet is arm's length away, not 128 cells. The projection is a pure
    /// superimposition with no per-hop penalty: a mechanitor commands the same 24.9-cell disc
    /// on every level of the column. The practical read is "standing on the stairs lets you
    /// command the whole shaft", which is what a player expects from a lift.
    ///
    /// ⚠ RE-ENTER THE METHOD, DO NOT RE-IMPLEMENT THE RADIUS. `MechCommandRange` is a
    /// `private const` inside Pawn_MechanitorTracker and therefore INLINED into the body -
    /// a copy of 620.01f here would be a second source of truth that cannot be kept honest
    /// (§30's power-facts lesson, same trap). Calling back into the patched method with a
    /// projected cell inherits vanilla's radius, its InBounds guard, and anything a future
    /// version adds (mech boosters, remotes) for free. The [ThreadStatic] latch is what makes
    /// that safe: without it the postfix re-enters itself forever, and a StackOverflow in
    /// .NET is UNCATCHABLE.
    ///
    /// ⚠ IT ONLY EVER RELAXES. A vanilla `true` returns untouched, so nothing that used to be
    /// commandable stops being commandable.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_MechanitorTracker), nameof(Pawn_MechanitorTracker.CanCommandTo))]
    public static class Patch_MechanitorTracker_ABBandLocalRange
    {
        [ThreadStatic]
        private static bool reentrant;

        /// <summary>Orders permitted that vanilla's flat radius would have refused. A guard
        /// that silently early-returns is indistinguishable from an unimplemented feature
        /// (§14), so this is printed by `AB2: riser report`.</summary>
        public static int rescued;

        private static void Postfix(Pawn_MechanitorTracker __instance, LocalTargetInfo target,
            ref bool __result)
        {
            // Vanilla already said yes (same band, or close enough), or this IS the projected
            // pass running underneath us.
            if (__result || reentrant)
            {
                return;
            }
            try
            {
                Pawn mechanitor = __instance?.Pawn;
                if (mechanitor == null || !mechanitor.Spawned)
                {
                    return;
                }
                Map map = mechanitor.MapHeld;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                IntVec3 cell = target.Cell;
                if (!cell.IsValid || !cell.InBounds(map))
                {
                    return;
                }
                int here = ABBands.BandOf(map, mechanitor.Position);
                int there = ABBands.BandOf(map, cell);
                if (here == there)
                {
                    // Same band: vanilla's refusal was an honest one about real distance.
                    return;
                }

                // Superimpose: same x, same offset within the band, the mechanitor's band.
                // Taken as a difference of band rects rather than `(here - there) * Slot` so
                // it stays correct if the layout ever stops being uniformly slotted.
                int dz = ABBands.RectOfBand(map, here).minZ - ABBands.RectOfBand(map, there).minZ;
                IntVec3 projected = new IntVec3(cell.x, cell.y, cell.z + dz);
                if (!projected.InBounds(map))
                {
                    return;
                }

                reentrant = true;
                try
                {
                    __result = __instance.CanCommandTo(projected);
                }
                finally
                {
                    reentrant = false;
                }
                if (__result)
                {
                    rescued++;
                }
            }
            catch (Exception e)
            {
                // A broken latch would wedge every later call into the vanilla answer, so it
                // is released here as well as in the inner finally.
                reentrant = false;
                Log.ErrorOnce(ABLog.Tag + " band-local command range threw: " + e, 0x2B10C7);
            }
        }
    }
}
