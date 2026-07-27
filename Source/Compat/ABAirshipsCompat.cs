using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Medieval Trader Airships / Trader ships: ships can land on the sky
    /// level (parity pass 2026-07-24). The host mod picks its landing spot in
    /// IncidentWorkerTraderShip.LandShip(map, ship): painted landing zone
    /// first, then beacons, then a free spot. This prefix redirects the LANDING
    /// map to the column's sky level exactly when the player says so with the
    /// host mod's own tool: a landing zone painted on the ROOFTOP while the
    /// surface zone is empty. No settings, no share rolls - paint the zone
    /// where ships should land, exactly like the host mod's semantics.
    ///
    /// Only the landing map changes; generation, the passing-ship registration,
    /// and departure all keep the host mod's behavior (departure uses the
    /// landed parent's own map). Trading is physical - surface pawns route up
    /// the stairs through the standard cross-level float menu wrap.
    ///
    /// Resolved by name (Joe_Airships new, TraderShips legacy); inert without
    /// the host; fail-open on any throw.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABAirshipsCompat
    {
        private static bool active;

        private static MethodInfo landingZoneGetter;

        private static MethodInfo tryFindShipLandingArea;

        static ABAirshipsCompat()
        {
            try
            {
                if (!ABCompat.Detect("joeownage.automatic.airships", "Airships")
                    && !ABCompat.Detect("joeownage.automatic.traderships", "Trader Ships (joeownage)")
                    && !ABCompat.Detect("automatic.traderships", "Trader Ships"))
                {
                    return;
                }
                Type worker = AccessTools.TypeByName("Joe_Airships.IncidentWorkerTraderShip");
                Type areaExt = AccessTools.TypeByName("Joe_Airships.AreaManagerLandingZone");
                Type zoneType = AccessTools.TypeByName("Joe_Airships.Area_LandingZone");
                if (worker == null)
                {
                    // Legacy namespace (the original Trader ships).
                    worker = AccessTools.TypeByName("TraderShips.IncidentWorkerTraderShip");
                    areaExt = AccessTools.TypeByName("TraderShips.AreaManagerLandingZone");
                    zoneType = AccessTools.TypeByName("TraderShips.Area_LandingZone");
                }
                MethodInfo landShip = worker != null ? AccessTools.Method(worker, "LandShip") : null;
                landingZoneGetter = areaExt != null ? AccessTools.Method(areaExt, "LandingZone") : null;
                tryFindShipLandingArea = zoneType != null
                    ? AccessTools.Method(zoneType, "TryFindShipLandingArea") : null;
                if (landShip == null || landingZoneGetter == null || tryFindShipLandingArea == null)
                {
                    Log.Warning(ABLog.Tag + " Trader airships detected but the landing internals were not found; rooftop landings are off.");
                    return;
                }
                HarmonyBoot.Harmony.Patch(landShip,
                    prefix: new HarmonyMethod(typeof(ABAirshipsCompat), nameof(LandShipPrefix)));
                active = true;
                ABLog.Dev("Trader airships detected; rooftop landing zones active.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Trader airships compat setup failed: " + e.Message);
            }
        }

        private static void LandShipPrefix(ref Map map, Thing ship)
        {
            if (!active || !ABGuard.On(ABGuard.World))
            {
                return;
            }
            try
            {
                if (map == null || map.Disposed || ship?.def == null)
                {
                    return;
                }
                LevelComp comp = map.Levels();
                if (comp == null || comp.level != 0)
                {
                    return;
                }
                Map sky = comp.upperMap;
                if (sky == null || sky.Disposed)
                {
                    return;
                }
                // The host mod's LandingZone() extension auto-creates the area,
                // so "painted" means TrueCount > 0, never a null check.
                Area surfaceZone = landingZoneGetter.Invoke(null, new object[] { map.areaManager }) as Area;
                if (surfaceZone != null && surfaceZone.TrueCount > 0)
                {
                    return; // the player wants ships on the surface
                }
                Area skyZone = landingZoneGetter.Invoke(null, new object[] { sky.areaManager }) as Area;
                if (skyZone == null || skyZone.TrueCount == 0)
                {
                    return;
                }
                object[] args = { ship.def.size, null, null };
                bool ok = (bool)tryFindShipLandingArea.Invoke(skyZone, args);
                if (ok)
                {
                    map = sky;
                    ABLog.Dev("Trader airship redirected to the rooftop landing zone.");
                }
            }
            catch (Exception e)
            {
                active = false;
                Log.Warning(ABLog.Tag + " Rooftop airship landing failed once; feature off for this session: " + e.Message);
            }
        }
    }
}
