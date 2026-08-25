using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// FORBIDDING ONE END OF A STAIRWELL FORBIDS THEM ALL.
    ///
    /// A link is ONE opening presented as TWO Buildings, one per band, and every other
    /// property of the pair is already kept in step (they spawn together, they collapse
    /// together, the wormhole joins them). Forbidding was the exception: the flag lives on
    /// each end's own CompForbiddable, so marking the top of the stairs left the bottom
    /// wide open. The player sees a red X on one level, walks a colonist up from the other,
    /// and reasonably calls that broken - a chokepoint you can only half close is worse than
    /// no chokepoint at all, because it looks closed.
    ///
    /// ⚠ MIRRORING THE ONE FIELD BUYS BOTH BEHAVIOURS, which is why this is the right place
    /// to fix it rather than in the renderer or the pathfinder. `Forbidden` is read by
    /// `OverlayDrawer` for the X and by `Building_Door.IsForbiddenToPass`, which
    /// `PathUtility` consults when deciding whether a pawn may route through a door. Copy
    /// the flag and the marker and the block move together and cannot drift apart.
    ///
    /// ⚠ AND VANILLA ALREADY DOES THE EXPENSIVE PART. `CompForbiddable`'s setter calls
    /// `Map.reachability.ClearCache()` when the parent is a `Building_Door` - which ours is
    /// - so each mirrored write invalidates pathing on its own. We must not skip the setter
    /// and poke the backing field, or the caches go stale and pawns keep routing through a
    /// stairwell that is now closed.
    ///
    /// ⚠ THE RE-ENTRY LATCH IS NOT OPTIONAL. Setting the counterpart's flag runs this same
    /// postfix for the counterpart, which would set ours back, which would... The elevator
    /// makes it worse: it links every level in a FULL MESH, so a three-car shaft is three
    /// mutually-referencing ends and the bounce is not even a simple two-cycle. The latch is
    /// the whole defence; the value check below is only an optimisation on top of it.
    ///
    /// Patching the property setter rather than listening for a comp signal because
    /// CompForbiddable does not send one - there is no FlickedOn-style broadcast for
    /// forbidding, so the setter is the only moment the change is observable.
    /// </summary>
    [HarmonyPatch(typeof(CompForbiddable), nameof(CompForbiddable.Forbidden), MethodType.Setter)]
    public static class Patch_CompForbiddable_ABStairsMirror
    {
        [ThreadStatic]
        private static bool mirroring;

        private static void Postfix(CompForbiddable __instance, bool value)
        {
            // Cheapest rejection first: this setter fires for every item a pawn forbids or
            // unforbids anywhere in the game.
            if (mirroring || !(__instance?.parent is Building_ABStairs2 stairs))
            {
                return;
            }
            try
            {
                IReadOnlyList<Building_ABStairs2> ends = stairs.Counterparts;
                if (ends == null || ends.Count == 0)
                {
                    return;
                }
                mirroring = true;
                try
                {
                    for (int i = 0; i < ends.Count; i++)
                    {
                        Building_ABStairs2 far = ends[i];
                        if (far == null || !far.Spawned || far == stairs)
                        {
                            continue;
                        }
                        CompForbiddable f = far.GetComp<CompForbiddable>();
                        // The value check keeps us off vanilla's ClearCache when nothing
                        // actually changes - the setter early-outs on an equal value anyway,
                        // but a stairwell pair is touched often enough to be worth not
                        // entering it at all.
                        if (f != null && f.Forbidden != value)
                        {
                            f.Forbidden = value;
                        }
                    }
                }
                finally
                {
                    mirroring = false;
                }
            }
            catch (Exception e)
            {
                mirroring = false;
                Log.WarningOnce(ABLog.Tag + " could not mirror the forbidden state across a "
                    + "stairwell pair. " + e.Message, 0x5741A3);
            }
        }
    }
}
