using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Applies the DBH air-conditioning cross-level bridge (DBHAirconBridge) as a
    /// postfix on DubsBadHygiene.HygienePipeMapComp.MapComponentTick, which ticks
    /// (and recomputes CoolingCap on) every plumbing net once per game tick. The
    /// postfix runs immediately after, so the pooled CoolingCap it writes is the
    /// value the room units read this tick.
    ///
    /// Patched MANUALLY here (not via a [HarmonyPatch] attribute) so it is only
    /// applied when DBH is actually loaded - the target type does not exist
    /// otherwise, and an attribute class would make HarmonyBoot log a spurious
    /// "skipped patch class" warning. The postfix signature is clean (MapComponent
    /// base type only); the DBH-typed work lives in DBHAirconBridge's method
    /// bodies, JIT'd only because this patch is applied only when DBH is present.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class DBHAirconPatch
    {
        static DBHAirconPatch()
        {
            if (!ABCompat.Detect("Dubwise.DubsBadHygiene", "Dubs Bad Hygiene"))
            {
                return;
            }
            try
            {
                Type t = AccessTools.TypeByName("DubsBadHygiene.HygienePipeMapComp");
                MethodInfo m = t != null ? AccessTools.Method(t, "MapComponentTick") : null;
                if (m == null)
                {
                    Log.Warning(ABLog.Tag + " DBH aircon bridge: HygienePipeMapComp.MapComponentTick not found; cross-level cooling disabled.");
                    return;
                }
                HarmonyBoot.Harmony.Patch(m, postfix: new HarmonyMethod(typeof(DBHAirconPatch), nameof(AfterNetTick)));
                ABLog.Dev("DBH aircon cross-level cooling bridge patched.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " DBH aircon bridge patch failed (cross-level cooling disabled): " + e.Message);
            }
        }

        private static void AfterNetTick(MapComponent __instance)
        {
            DBHAirconBridge.PoolForMap(__instance?.map);
        }
    }
}
