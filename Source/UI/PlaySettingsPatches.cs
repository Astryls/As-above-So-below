using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Level switcher in the real widget area (T10 #1, relocated after playtest:
    /// the free-floating panel collided with the play settings row). Adds view
    /// level above / below buttons to the vanilla global controls WidgetRow next
    /// to the other toggles, shown only when the current column has those
    /// levels. Camera jumps preserve position and zoom. PageUp / PageDown still
    /// work; the row placement means vanilla lays us out and nothing overlaps.
    /// </summary>
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    internal static class Patch_PlaySettings_LevelButtons
    {
        // Tooltips cached: Translate plus concat allocated two strings per
        // button per frame. Stale only across a mid-session language change,
        // which is accepted.
        private static string upTip;
        private static string downTip;

        private static string UpTip =>
            upTip ?? (upTip = "AB_ViewAbove".Translate() + "\n" + "AB_LevelWidgetTip".Translate());

        private static string DownTip =>
            downTip ?? (downTip = "AB_ViewBelow".Translate() + "\n" + "AB_LevelWidgetTip".Translate());

        private static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || row == null || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.showLevelWidget)
            {
                return;
            }
            try
            {
                Map cur = Find.CurrentMap;
                if (cur == null)
                {
                    return;
                }
                Map up = cur.UpperMap();
                Map down = cur.LowerMap();
                if (up == null && down == null)
                {
                    return;
                }
                // Declutter UI suppresses foreign ButtonIcon draws under some of
                // its options; lift the flag only while our two buttons draw.
                bool lifted = ABDeclutterCompat.PushUnsuppressed();
                try
                {
                    if (up != null && !up.Disposed && ABIcons.UpStairs != null
                        && row.ButtonIcon(ABIcons.UpStairs, UpTip))
                    {
                        LevelCamera.JumpPreservingView(up);
                    }
                    if (down != null && !down.Disposed && ABIcons.DownStairs != null
                        && row.ButtonIcon(ABIcons.DownStairs, DownTip))
                    {
                        LevelCamera.JumpPreservingView(down);
                    }
                }
                finally
                {
                    ABDeclutterCompat.Pop(lifted);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level view buttons");
            }
        }
    }
}
