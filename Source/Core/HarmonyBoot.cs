using System;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Patches each [HarmonyPatch] class independently so a single dead target
    /// (for example after a game update) disables one patch with a warning
    /// instead of killing every patch in the assembly.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class HarmonyBoot
    {
        public static readonly Harmony Harmony = new Harmony("astryl.asabovesobelow");

        static HarmonyBoot()
        {
            foreach (Type type in typeof(HarmonyBoot).Assembly.GetTypes())
            {
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
