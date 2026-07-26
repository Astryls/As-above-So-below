using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Targeted soft compat for Owlchemist's Perspective: Ores (bug report
    /// 2026-07-25). Its post-generation ore-lump flood fill (PerspectiveOresSetup
    /// .ProcessMap -> DetermineLump) recurses into neighbouring cells without a
    /// bounds check, so on our pocket levels - whose solid-rock basements have
    /// mineable ore right up to the map edge - it walks off the edge and throws
    /// IndexOutOfRangeException from EdificeGrid, aborting our level generation.
    ///
    /// This prefix simply skips PerspectiveOres' processing on our sky/basement
    /// levels (they are not normal maps, so its perspective ore rendering there
    /// is meaningless anyway), which prevents the throw at the source and avoids
    /// the per-generation warning the generic FinalizeInit guard would otherwise
    /// log. Ground (level 0) maps run PerspectiveOres exactly as before.
    ///
    /// Patched MANUALLY, only when Perspective: Ores is active (its type does not
    /// exist otherwise). If PerspectiveOres is loaded but its internals moved,
    /// this fails open and the generic FinalizeInit guard still keeps our level
    /// generation from being aborted.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class PerspectiveOresCompat
    {
        static PerspectiveOresCompat()
        {
            if (!ABDetect.Active("Owlchemist.PerspectiveOres"))
            {
                return;
            }
            try
            {
                Type t = AccessTools.TypeByName("PerspectiveOres.PerspectiveOresSetup");
                MethodInfo m = t != null ? AccessTools.Method(t, "ProcessMap") : null;
                if (m == null)
                {
                    Log.Warning(ABLog.Tag + " Perspective: Ores compat: ProcessMap not found; relying on the generic FinalizeInit guard.");
                    return;
                }
                HarmonyBoot.Harmony.Patch(m, prefix: new HarmonyMethod(typeof(PerspectiveOresCompat), nameof(SkipOnLevels)));
                ABLog.Dev("Perspective: Ores compat patched (skips our pocket levels).");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Perspective: Ores compat patch failed (generic guard still covers it): " + e.Message);
            }
        }

        // Return false to SKIP PerspectiveOres on our pocket levels; true runs it.
        private static bool SkipOnLevels(Map map)
        {
            LevelComp comp = map != null ? map.Levels() : null;
            return comp == null || comp.level == 0;
        }
    }
}
