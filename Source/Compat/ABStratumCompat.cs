using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Stratum (SolarWeb.Stratum) soft compat. Verified from the 1.6 decompile:
    /// Stratum keeps every roof - its own StratumRoofDefs AND the vanilla defs
    /// it makes buildable - inside the vanilla RoofGrid, and every mutation
    /// (build, deconstruct, retractable open/close, HP collapse) funnels
    /// through RoofGrid.SetRoof, which fires map.events.RoofChanged. Our sky
    /// terrain sync therefore tracks Stratum roofs with no extra wiring, and
    /// its buildable roofs (isNatural=false) classify as walkable rooftop
    /// while its smoothed mountain variants (isNatural=true) stay mountain.
    ///
    /// The one seam that needs a bridge: Stratum HP-tracks ALL roofs in a
    /// per-map RoofIntegrityGrid (its Vanilla_Roofs patch adds the extension
    /// to vanilla defs too). Our sky-level bomb punch used to vaporize the
    /// roof below with a flat 40-damage SetRoof(null) - with Stratum active
    /// that must instead feed RoofIntegrityGrid.TakeDamage so its per-roof
    /// threshold/armor/HP decide; at 0 HP its collapse path calls
    /// SetRoof(null) itself and our rooftop cascade runs as normal.
    ///
    /// Known limitation (documented, no code): building Stratum's "thin rock"
    /// or "overhead mountain" roofs writes the vanilla NATURAL defs into the
    /// grid, which our classification reads as mountain - the sky above such
    /// cells stays open air rather than gaining a walkable rooftop.
    /// Deferred: cross-level ingredient demand for Stratum RoofFrames.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABStratumCompat
    {
        private static Type integrityGridType;

        private static MethodInfo takeDamageMethod;

        private static bool active;

        internal static bool Active
        {
            get { return active; }
        }

        static ABStratumCompat()
        {
            try
            {
                if (!ABCompat.Detect("SolarWeb.Stratum", "Stratum"))
                {
                    return;
                }
                integrityGridType = AccessTools.TypeByName("SolarWeb.Stratum.MapComponents.RoofIntegrityGrid");
                takeDamageMethod = integrityGridType != null
                    ? AccessTools.Method(integrityGridType, "TakeDamage",
                        new[] { typeof(IntVec3), typeof(float), typeof(float), typeof(DamageInfo?) })
                    : null;
                active = takeDamageMethod != null;
                if (active)
                {
                    ABLog.Dev("Stratum detected, rooftop blasts route through its roof integrity system.");
                }
                else
                {
                    Log.Warning(ABLog.Tag + " Stratum is active but its roof integrity internals were not found; rooftop blasts fall back to the vanilla punch.");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Stratum compat setup failed: " + e.Message);
            }
        }

        /// <summary>Feeds blast damage into Stratum's per-cell roof HP on the
        /// given map. True when Stratum handled it (whatever the outcome -
        /// held, damaged, or collapsed); false when the bridge is inactive or
        /// failed, in which case the caller applies the vanilla behavior. A
        /// single failure trips the bridge off for the session.</summary>
        internal static bool TryDamageRoof(Map map, IntVec3 c, float amount, DamageInfo? dinfo)
        {
            if (!active || map == null)
            {
                return false;
            }
            try
            {
                object grid = map.GetComponent(integrityGridType);
                if (grid == null)
                {
                    return false;
                }
                takeDamageMethod.Invoke(grid, new object[] { c, amount, 0f, dinfo });
                return true;
            }
            catch (Exception e)
            {
                active = false;
                Log.Warning(ABLog.Tag + " Stratum roof damage bridge failed and is off for this session: " + e.Message);
                return false;
            }
        }
    }
}
