using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Declutter UI (sk.dmbb) soft compat. When its "searchable play settings
    /// menu" or "reveal on hover" options are enabled, its WidgetRow.ButtonIcon
    /// patch suppresses every widget drawn by third-party postfixes on
    /// DoPlaySettingsGlobalControls (MapControlsTableContext.SuppressExternal),
    /// which would silently hide our level view buttons. Lift the suppression
    /// flag only for the duration of our two draws and restore it immediately;
    /// vanilla toggles and other mods are untouched. Reflection only, resolved
    /// once, inert when the mod is absent or its internals moved.
    /// </summary>
    internal static class ABDeclutterCompat
    {
        private static bool resolved;
        private static FieldInfo suppressField;

        private static void Resolve()
        {
            resolved = true;
            try
            {
                if (!ABCompat.Detect("sk.dmbb", "Declutter"))
                {
                    return;
                }
                Type ctx = AccessTools.TypeByName("MapControlsTableContext");
                FieldInfo f = ctx != null ? AccessTools.Field(ctx, "SuppressExternal") : null;
                if (f != null && f.IsStatic && f.FieldType == typeof(bool))
                {
                    suppressField = f;
                    ABLog.Dev("Declutter UI detected, level buttons will bypass its widget suppression.");
                }
                else
                {
                    ABLog.Dev("Declutter UI present but MapControlsTableContext.SuppressExternal was not found; its play settings options may hide the level buttons.");
                }
            }
            catch (Exception e)
            {
                ABLog.Dev("Declutter UI compat resolve failed: " + e.Message);
            }
        }

        /// <summary>Clears the suppression flag when it is set. Returns true when
        /// it was lifted and must be restored via Pop.</summary>
        public static bool PushUnsuppressed()
        {
            if (!resolved)
            {
                Resolve();
            }
            if (suppressField == null)
            {
                return false;
            }
            try
            {
                if ((bool)suppressField.GetValue(null))
                {
                    suppressField.SetValue(null, false);
                    return true;
                }
            }
            catch (Exception)
            {
                suppressField = null;
            }
            return false;
        }

        public static void Pop(bool pushed)
        {
            if (!pushed || suppressField == null)
            {
                return;
            }
            try
            {
                suppressField.SetValue(null, true);
            }
            catch (Exception)
            {
                suppressField = null;
            }
        }
    }
}
