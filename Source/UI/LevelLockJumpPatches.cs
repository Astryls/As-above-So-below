using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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

    /// <summary>
    /// THE "right click does not work across levels" ROOT CAUSE (live test
    /// 2026-07-24): the setter fires MapInterface.Notify_SwitchedMap, which
    /// calls selector.ClearSelection() - so "select pawn, switch level view,
    /// right-click the blueprint" sent an EMPTY selection into the right-click
    /// handler, which requires SelectedPawns.Any() and silently did nothing.
    /// Every downstream menu fix was gated behind this wipe.
    ///
    /// The column is one big map: switching levels is scrolling, not
    /// traveling, so a column-internal view switch preserves the selection.
    /// Prefix snapshots selected THINGS when old and new map share a column;
    /// postfix re-adds them in place (quiet: no sound, no designator drop, no
    /// camera yank - vanilla Select would jump the view to the thing's map).
    ///
    /// Vanilla click-to-select flows are untouched BY CONSTRUCTION:
    /// SelectInternal's cross-map dedup empties the selection BEFORE it
    /// switches maps, so our snapshot is already empty there and normal
    /// "click a pawn on another level" replaces the selection exactly as
    /// always. Zones and plans stay map-scoped (not preserved). If the level
    /// lock prefix cancels the switch, nothing was cleared and the restore
    /// no-ops through its Contains guard.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.CurrentMap), MethodType.Setter)]
    internal static class Patch_Game_CurrentMap_PreserveColumnSelection
    {
        private static void Prefix(Map value, out List<Thing> __state)
        {
            __state = null;
            if (!ABGuard.On(ABGuard.Ui) || value == null)
            {
                return;
            }
            Map old = Find.CurrentMap;
            if (old == null || old == value || !old.SameColumn(value))
            {
                return;
            }
            List<object> selected = Find.Selector?.SelectedObjects;
            if (selected == null || selected.Count == 0)
            {
                return;
            }
            List<Thing> keep = null;
            for (int i = 0; i < selected.Count; i++)
            {
                if (selected[i] is Thing t && !t.Destroyed && t.MapHeld != null)
                {
                    (keep ?? (keep = new List<Thing>())).Add(t);
                }
            }
            __state = keep;
        }

        private static void Postfix(List<Thing> __state)
        {
            if (__state == null)
            {
                return;
            }
            Selector selector = Find.Selector;
            if (selector == null)
            {
                return;
            }
            for (int i = 0; i < __state.Count; i++)
            {
                BelowSelection.RestoreSelected(selector, __state[i]);
            }
        }
    }
}
