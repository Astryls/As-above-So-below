using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Enforces the camera level lock at the true choke point: EVERY view
    /// change to another level flows through the Game.CurrentMap setter -
    /// Selector.SelectInternal switches maps when you select a thing on
    /// another level (colonist-bar single click), CameraJumper does the same
    /// for double-click jumps, alerts, and letters. A prefix that blocks the
    /// setter while the lock is on (and the target is another level of the
    /// same column, and it is not one of our own manual switches) keeps the
    /// view put; the caller's selection still lands, so the pawn is selected
    /// without leaving the level. Our manual switches set LevelCamera.ManualSwitch
    /// (bypassing this), and the cross-level order machinery writes
    /// currentMapIndex directly, so neither is affected. Fails open.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
    internal static class Patch_Game_CurrentMap_LevelLock
    {
        private static bool Prefix(Map value)
        {
            return !LevelCamera.ShouldSuppressJump(value);
        }
    }
}
