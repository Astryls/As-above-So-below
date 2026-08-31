using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §95 TIER B - SIMPLEFX FAMILY, THE PARTS THE FLECK-MIRROR SEAM CANNOT FIX.
    ///
    /// The seam move in ABFleckMirrorBoot already repairs SimpleFX SMOKE (its CompFlecker
    /// bypassed the manager; the system-level seam catches it - nothing to do here) and
    /// leaves SHINIES alone on purpose (its producer only fires for view-rect cells, so
    /// through-hole glints structurally never spawn; fixing that means transpiling their
    /// loop - accepted degradation, w17 scan). What remains are two producers whose SPAWN
    /// LOGIC asks the wrong question on a banded map:
    ///
    /// 1. SPLASHES (Owlchemist.SimpleFX.Splashes): rain-splash flecks on hard unroofed
    ///    terrain, self-gated to the camera rect - which on a banded map means the VIEWED
    ///    band. Two wrong answers remain: splashes while the player watches an underground
    ///    band (rain does not fall through a hundred metres of stone - the same sentence
    ///    that justified Patch_WeatherManager_ABBandOverlays clause (a)), and splashes on
    ///    a freezing sky band where that same patch is busy drawing the rain AS SNOW.
    ///    → GateSplashes skips ProcessSplashes on exactly those two verdicts, using the
    ///    SAME predicates as the overlay patch (rule 52: ask the question the draw asks).
    ///
    /// 2. VAPOR/FREEZERS (Atlas.SimpleFX.Vapor.Revaporized): cold-air glow flecks from a
    ///    transpiler into DoCellSteadyEffects. Its "consider outdoors" bail reads the
    ///    WORLD-TILE temperature - so a -10°C sky band over a warm tile gets no vapor, and
    ///    a cold tile would put vapor on every band regardless of level. Its room-temp
    ///    argument has the same defect for roofless cells (outdoor room temp = tile temp,
    ///    not band temp). And its view-rect check is expanded by 64 cells, which can cross
    ///    the gutter into the neighbouring band's rows. ColdGlow is a public static with
    ///    a tiny tail, so on banded maps we own it: VaporColdGlow returns false and re-runs
    ///    the tail with band-aware temperature, a same-band check, and their own FleckDefs
    ///    resolved by defName. Non-banded maps fall straight through to their original.
    ///    Install fails open: if their defs or settings field are missing, no patch, their
    ///    (tile-based) behavior stands - degraded, not broken.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SimpleFXCompat
    {
        private static FieldInfo fConsiderOutdoors;
        private static FleckDef coldGlow;
        private static FleckDef veryColdGlow;

        static SimpleFXCompat()
        {
            InstallSplashesGate();
            InstallVaporBandTemp();
        }

        // ------------------------------------------------------------------ Splashes

        private static void InstallSplashesGate()
        {
            Type t = AccessTools.TypeByName("SimpleFxSplashes.SplashesUtility");
            if (t == null)
            {
                return;
            }
            try
            {
                MethodInfo m = AccessTools.Method(t, "ProcessSplashes");
                if (m == null)
                {
                    Log.WarningOnce(ABLog.Tag + " SimpleFX Splashes is present but"
                        + " ProcessSplashes was not found; splashes will ignore levels.",
                        0x2B10E0);
                    return;
                }
                HarmonyBoot.Harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(SimpleFXCompat), nameof(GateSplashes)));
                ABLog.Dev("SimpleFX Splashes level gate installed.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " SimpleFX Splashes gate failed to install: "
                    + e.Message, 0x2B10E1);
            }
        }

        /// <summary>Once per tick for the current map - CompOf is memoized and the band
        /// temp only computes on sky-band views.</summary>
        public static bool GateSplashes(Map map)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return true;
                }
                int level = ABBandView.CurrentLevel(map);
                if (level < 0)
                {
                    return false; // underground: no rain to splash
                }
                if (level > 0 && ABBandWeather.BandOutdoorTemp(map, bands, level) < 0f)
                {
                    return false; // the overlay patch is drawing this rain as snow
                }
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " splash gate threw: " + e, 0x2B10E2);
                return true;
            }
        }

        // ------------------------------------------------------------------ Vapor

        private static void InstallVaporBandTemp()
        {
            Type t = AccessTools.TypeByName("SimpleFxVapor.Patch_DoCellSteadyEffects");
            if (t == null)
            {
                return;
            }
            try
            {
                MethodInfo m = AccessTools.Method(t, "ColdGlow",
                    new[] { typeof(IntVec3), typeof(float), typeof(Map) });
                Type settingsT = AccessTools.TypeByName("SimpleFxVapor.ModSettings_SimpleFxVapor");
                fConsiderOutdoors = settingsT == null
                    ? null : AccessTools.Field(settingsT, "considerOutdoors");
                coldGlow = DefDatabase<FleckDef>.GetNamedSilentFail("Owl_ColdGlow");
                veryColdGlow = DefDatabase<FleckDef>.GetNamedSilentFail("Owl_VeryColdGlow");
                if (m == null || coldGlow == null || veryColdGlow == null)
                {
                    Log.WarningOnce(ABLog.Tag + " SimpleFX Vapor is present but changed"
                        + " shape; cold-air glow keeps tile-based temperature on banded"
                        + " maps.", 0x2B10E3);
                    return;
                }
                HarmonyBoot.Harmony.Patch(m, prefix: new HarmonyMethod(
                    typeof(SimpleFXCompat), nameof(VaporColdGlow)));
                ABLog.Dev("SimpleFX Vapor band-temperature bridge installed.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " SimpleFX Vapor bridge failed to install: "
                    + e.Message, 0x2B10E4);
            }
        }

        /// <summary>Banded maps only: reimplements ColdGlow's tail with the band's outdoor
        /// temperature where the original read the world tile's. Mirrored constants
        /// (thresholds, jitter, y, speed) are THEIR values - if their tuning drifts, this
        /// drifts behind it, which is the accepted cost of owning the tail (the seam offers
        /// no narrower purchase; a prefix cannot rewrite a bail mid-method).
        /// Runs on the steady-effects tick path: ordered so the common case exits on
        /// arg compares and one grid read before touching camera state.</summary>
        public static bool VaporColdGlow(IntVec3 c, float temperature, Map map)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return true; // vanilla maps: their code, untouched
                }
                // From here on we own the verdict (returning false always).
                if (map != Find.CurrentMap || !c.InBounds(map))
                {
                    return false;
                }
                bool roofed = map.roofGrid.Roofed(c);
                if (temperature >= 0f && roofed)
                {
                    return false; // warm indoor cell - the overwhelmingly common sample
                }
                if (bands.InGutter(c) || bands.BandOf(c) != ABBandView.CurrentBand(map))
                {
                    return false; // their +64 rect expansion can cross the gutter
                }
                int level = bands.LevelOf(c);
                float eff = roofed
                    ? temperature
                    : ABBandWeather.BandOutdoorTemp(map, bands, level);
                if (fConsiderOutdoors != null && fConsiderOutdoors.GetValue(null) is bool co
                    && co && ABBandWeather.BandOutdoorTemp(map, bands, level) >= 0f)
                {
                    return false; // their setting, answered with the BAND's outdoors
                }
                if (eff >= 0f)
                {
                    return false;
                }
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(64);
                if (!view.Contains(c))
                {
                    return false;
                }
                FleckDef def = eff < -8f ? veryColdGlow : coldGlow;
                FleckCreationData d = FleckMaker.GetDataStatic(
                    new Vector3(c.x + Rand.Range(0.01f, 0.5f), 10.54054f,
                        c.z + Rand.Range(0.01f, 0.5f)),
                    map, def, Rand.Range(2f, 3f));
                d.rotationRate = Rand.Range(-3f, 3f);
                d.velocityAngle = Rand.Range(0, 360);
                d.velocitySpeed = 0.12f;
                map.flecks.CreateFleck(d);
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " vapor bridge threw: " + e, 0x2B10E5);
                return true;
            }
        }
    }
}
