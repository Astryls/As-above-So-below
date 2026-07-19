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
        private static Texture2D upIcon;
        private static Texture2D downIcon;

        private static Texture2D UpIcon =>
            upIcon ?? (upIcon = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsUp")?.uiIcon);

        private static Texture2D DownIcon =>
            downIcon ?? (downIcon = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown")?.uiIcon);

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
                if (up != null && !up.Disposed && UpIcon != null
                    && row.ButtonIcon(UpIcon, "AB_ViewAbove".Translate() + "\n" + "AB_LevelWidgetTip".Translate()))
                {
                    LevelCamera.JumpPreservingView(up);
                }
                if (down != null && !down.Disposed && DownIcon != null
                    && row.ButtonIcon(DownIcon, "AB_ViewBelow".Translate() + "\n" + "AB_LevelWidgetTip".Translate()))
                {
                    LevelCamera.JumpPreservingView(down);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level view buttons");
            }
        }
    }
}
