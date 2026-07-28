using System;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 camera jumping across bands.
    ///
    /// Double-clicking a colonist bar portrait (or any "jump to" link - letters, alerts,
    /// quest targets) calls CameraJumper. On a banded map the target may be in a band the
    /// camera is not currently viewing, and because the camera is CLAMPED to the current
    /// band the jump lands nowhere: it tries to pan to a z the camera is not allowed to
    /// occupy, and simply refuses.
    ///
    /// In V1 this manifested as trying to jump to "the other map" and failing. Here the cell
    /// is on the same map, so the fix is small: switch the view band first, then let vanilla
    /// pan normally.
    /// </summary>
    [HarmonyPatch(typeof(CameraJumper), nameof(CameraJumper.TryJump),
        new Type[] { typeof(GlobalTargetInfo), typeof(CameraJumper.MovementMode) })]
    public static class Patch_CameraJumper_ABBandJump
    {
        private static void Prefix(GlobalTargetInfo target)
        {
            try
            {
                if (!target.IsValid || target.WorldObject != null)
                {
                    return;
                }
                Map map = target.Map;
                if (map == null || map != Find.CurrentMap)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                IntVec3 cell = target.HasThing && target.Thing != null && target.Thing.Spawned
                    ? target.Thing.PositionHeld
                    : target.Cell;
                if (!cell.IsValid || !cell.InBounds(map))
                {
                    return;
                }
                int band = bands.BandOf(cell);
                if (band == ABBandView.CurrentBand(map))
                {
                    return;
                }
                // preserveXZ:false - the camera is about to be panned onto the target
                // anyway, so keeping the old in-band position would just cause a visible
                // double move.
                ABBandView.SetBand(map, band, preserveXZ: false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: band jump threw: " + e, 762195881);
            }
        }
    }
}
