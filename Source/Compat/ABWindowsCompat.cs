using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Open The Windows (JPT.OpenTheWindows) soft compat: event-leak shield.
    ///
    /// Every Building_Window subscribes itself to the STATIC event
    /// MapUpdateWatcher.MapUpdate in its constructor and never unsubscribes -
    /// not in DeSpawn, not in Destroy. In vanilla that leak is mostly harmless
    /// because maps rarely go away. Our mod creates and removes level maps, so
    /// a window object can outlive its map while still being subscribed; its
    /// handler then calls Thing.Map, whose mapIndexOrState indexes past
    /// Find.Maps and throws ArgumentOutOfRangeException. Because the event is
    /// fired from OTW's ThingGrid.RegisterInCell postfix, the throw lands in
    /// the middle of vanilla MAP GENERATION (every rock spawn fires it) and
    /// aborts the genstep - observed live as "Error in GenStep:
    /// ArgumentOutOfRangeException" with region-cascade errors following.
    ///
    /// Shield: a prefix on Building_Window.MapUpdateHandler. Healthy windows
    /// (valid map index, or unspawned where OTW's own null-Map check is safe)
    /// pass through untouched. Destroyed windows and windows with a dangling
    /// map index are unsubscribed from the event on the spot (self-healing the
    /// leak) and their handler is skipped. Everything is resolved by name at
    /// startup - zero typerefs into the OTW assembly - and the whole shield
    /// fails open on any error, restoring stock behavior.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABWindowsCompat
    {
        private static AccessTools.FieldRef<Thing, sbyte> mapIndexRef;
        private static FieldInfo eventField;
        private static bool broken;

        static ABWindowsCompat()
        {
            try
            {
                if (!ABCompat.Detect("JPT.OpenTheWindows", "Open The Windows"))
                {
                    return;
                }
                Type window = AccessTools.TypeByName("OpenTheWindows.Building_Window");
                Type watcher = AccessTools.TypeByName("OpenTheWindows.MapUpdateWatcher");
                MethodInfo handler = window != null ? AccessTools.Method(window, "MapUpdateHandler") : null;
                eventField = watcher != null ? AccessTools.Field(watcher, "MapUpdate") : null;
                if (handler == null || eventField == null || !eventField.IsStatic)
                {
                    Log.Warning(ABLog.Tag + " Open The Windows detected but its update event internals were not found; the window event shield is off.");
                    return;
                }
                mapIndexRef = AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");
                HarmonyBoot.Harmony.Patch(handler,
                    prefix: new HarmonyMethod(typeof(ABWindowsCompat), nameof(MapUpdatePrefix)));
                ABLog.Dev("Open The Windows detected, window event shield active.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Open The Windows compat setup failed: " + e.Message);
            }
        }

        /// <summary>Skips and unsubscribes handlers of dead windows: destroyed,
        /// or spawned on a map that no longer exists (dangling map index). Live
        /// windows fall through to stock behavior. Fail open: on any internal
        /// error the shield disables itself and OTW runs stock.</summary>
        private static bool MapUpdatePrefix(object __instance)
        {
            if (broken)
            {
                return true;
            }
            try
            {
                Thing t = __instance as Thing;
                if (t == null)
                {
                    return true;
                }
                if (t.Destroyed)
                {
                    Unsubscribe(t);
                    return false;
                }
                sbyte idx = mapIndexRef(t);
                if (idx < 0)
                {
                    // Constructed but never spawned (or cleanly despawned):
                    // Thing.Map is null and OTW's own map comparison handles it.
                    return true;
                }
                List<Map> maps = Find.Maps;
                if (maps != null && idx < maps.Count)
                {
                    return true;
                }
                // Dangling index: the window's map was removed. Thing.Map would
                // throw ArgumentOutOfRangeException inside OTW's handler.
                Unsubscribe(t);
                return false;
            }
            catch (Exception e)
            {
                broken = true;
                Log.Warning(ABLog.Tag + " Open The Windows event shield hit an error and turned itself off: " + e.Message);
                return true;
            }
        }

        /// <summary>Removes every invocation-list entry bound to this window
        /// from OTW's static MapUpdate event. Reflection only; delegate
        /// invocation lists are immutable snapshots, so removing during a
        /// dispatch is safe.</summary>
        private static void Unsubscribe(object window)
        {
            Delegate current = (Delegate)eventField.GetValue(null);
            if (current == null)
            {
                return;
            }
            Delegate[] list = current.GetInvocationList();
            Delegate result = current;
            for (int i = 0; i < list.Length; i++)
            {
                if (ReferenceEquals(list[i].Target, window))
                {
                    result = Delegate.Remove(result, list[i]);
                }
            }
            if (!ReferenceEquals(result, current))
            {
                eventField.SetValue(null, result);
            }
        }
    }
}
