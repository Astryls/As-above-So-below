using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Patches each [HarmonyPatch] class independently so a single dead target
    /// (for example after a game update) disables one patch with a warning
    /// instead of killing every patch in the assembly.
    ///
    /// Only types that actually declare [HarmonyPatch] reach the class
    /// processor. Handing it every type was the Rimefeller ghost-warning bug:
    /// the processor walks a candidate's method list, which makes Mono resolve
    /// method signatures, and (before the object-typed signature audit) that
    /// threw "Could not resolve type ... 'Rimefeller.PipelineNet'" for
    /// soft-compat bridge classes whenever the foreign mod was absent -
    /// logged as "Skipped patch class RimefellerBridge" and misread by
    /// players (and log-triage tools) as a broken mod. Bridges are plain
    /// static classes and never carry patch attributes, so the filter keeps
    /// the processor away from them entirely. IsDefined checks the attribute
    /// token without instantiating it, so it cannot resolve foreign types.
    ///
    /// GetTypes is guarded too: a ReflectionTypeLoadException there would
    /// escape the per-type try/catch and silently kill EVERY patch; the
    /// partial type list the exception carries lets the healthy patches load.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HarmonyBoot
    {
        public static readonly Harmony Harmony = new Harmony("astryl.asabovesobelow");

        static HarmonyBoot()
        {
            Type[] types;
            try
            {
                types = typeof(HarmonyBoot).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                string first = e.LoaderExceptions != null && e.LoaderExceptions.Length > 0
                    ? e.LoaderExceptions[0].Message
                    : "unknown";
                Log.Warning(ABLog.Tag + " Some types failed to load; patching the rest. First loader error: " + first);
            }
            foreach (Type type in types)
            {
                if (type == null || !type.IsDefined(typeof(HarmonyPatch), inherit: false))
                {
                    continue;
                }
                try
                {
                    Harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    Log.Warning(ABLog.Tag + " Skipped patch class " + type.Name + ": " + e.Message);
                }
            }
        }
    }
}
