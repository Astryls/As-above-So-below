using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §95 TIER A - SKY-SPACE FX MODS, GATED AND REMAPPED TO THE VIEWED LEVEL.
    ///
    /// Two mods draw "the sky" over a map that is secretly seven maps tall:
    ///
    /// 1. SUN &amp; MOON BEAMS (yuexiatingsong.sunandmoonbeams). Effecter god-rays. THREE
    ///    defects on a banded map, each with its own patch:
    ///    (a) The anchor Thing (`SmallPitLight_Sun`) spawns at MAP CENTER with a Standable
    ///        check and 10 retries. The center of a stack is some band's rock (or the
    ///        gutter), so on most banded maps the mod silently dies at boot.
    ///        → PlaceAnchorOnSurface re-homes it to a standable surface-band cell.
    ///    (b) Beams scatter over 1.5x the WHOLE map (their coverageMode 1), i.e. across
    ///        every band; only thick roof is rejected, so thin-roofed underground carves
    ///        get god-rays. → RemapBeam folds every scatter position into the VIEWED
    ///        band's rect (the exact dz idiom the fleck mirror uses) and re-runs the
    ///        thick-roof verdict on the folded cell, because the fold happens AFTER their
    ///        own FindValidSpawnPosition roof test already ran on the raw position.
    ///    (c) Underground there is no sky at all. → GateSubEffectTick skips the whole
    ///        effecter tick when the view level is below the surface - same verdict, same
    ///        shape, as Patch_WeatherManager_ABBandOverlays' clause (a).
    ///
    /// 2. [LBY]云 (Araneid.cloud, CloudSkyOverlay.dll). One draw seam,
    ///    CloudSkyRenderer.DrawNow → one underground gate. (The user's ICW suppresses
    ///    DrawNow itself when active - explicitly OUT OF SCOPE per user, w17 - so this
    ///    entry only matters for LBY running standalone, and coexists with ICW's prefix.)
    ///
    /// House rules honored: foreign-type-free (TypeByName + reflection only), absent mod =
    /// silent skip, present-but-changed mod = WarningOnce and fail open, every prefix
    /// wrapped so a throw returns control to the original (ABGuard discipline).
    /// Sky semantics per user ruling (w17): beams/clouds show on ALL non-underground
    /// views - surface AND sky bands; only level &lt; 0 suppresses.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SkyFXCompat
    {
        private static FieldInfo fSunLightThing;
        private static FieldInfo fInitialized;
        private static FieldInfo fCreationAttempted;
        private static FieldInfo fCreationAttemptCount;

        static SkyFXCompat()
        {
            InstallSunAndMoonBeams();
            InstallLbyClouds();
        }

        // ------------------------------------------------------------------ Sun & Moon

        private static void InstallSunAndMoonBeams()
        {
            Type effecterT = AccessTools.TypeByName("SunAndMoonBeams.SubEffecter_SunLightBeam");
            if (effecterT == null)
            {
                return; // mod not installed - the normal case, no noise
            }
            try
            {
                MethodInfo tick = AccessTools.Method(effecterT, "SubEffectTick");
                MethodInfo beam = AccessTools.Method(effecterT, "CreateSingleBeam");
                if (tick == null || beam == null)
                {
                    Log.WarningOnce(ABLog.Tag + " Sun & Moon Beams is present but its"
                        + " effecter changed shape; sunbeams will ignore levels this"
                        + " session.", 0x2B10D0);
                    return;
                }
                HarmonyBoot.Harmony.Patch(tick, prefix: new HarmonyMethod(
                    typeof(SkyFXCompat), nameof(GateSubEffectTick)));
                HarmonyBoot.Harmony.Patch(beam, prefix: new HarmonyMethod(
                    typeof(SkyFXCompat), nameof(RemapBeam)));

                Type managerT = AccessTools.TypeByName("SunAndMoonBeams.SunLightManager");
                MethodInfo create = managerT == null
                    ? null : AccessTools.Method(managerT, "CreateSunLight");
                fSunLightThing = managerT == null
                    ? null : AccessTools.Field(managerT, "sunLightThing");
                fInitialized = managerT == null
                    ? null : AccessTools.Field(managerT, "initialized");
                fCreationAttempted = managerT == null
                    ? null : AccessTools.Field(managerT, "creationAttempted");
                fCreationAttemptCount = managerT == null
                    ? null : AccessTools.Field(managerT, "creationAttemptCount");
                if (create != null && fSunLightThing != null && fInitialized != null
                    && fCreationAttempted != null && fCreationAttemptCount != null)
                {
                    HarmonyBoot.Harmony.Patch(create, prefix: new HarmonyMethod(
                        typeof(SkyFXCompat), nameof(PlaceAnchorOnSurface)));
                }
                else
                {
                    Log.WarningOnce(ABLog.Tag + " Sun & Moon Beams' manager changed shape;"
                        + " its sun anchor keeps vanilla placement (may fail to spawn on"
                        + " banded maps, leaving no beams).", 0x2B10D1);
                }
                ABLog.Dev("Sun & Moon Beams bridge installed (gate + remap + anchor).");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Sun & Moon Beams bridge failed to install: "
                    + e.Message, 0x2B10D2);
            }
        }

        /// <summary>Clause (c): no sky underground. Runs per effecter tick - the guard
        /// chain is bool + memoized comp + int compare before any work.</summary>
        public static bool GateSubEffectTick()
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return true;
                }
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return true;
                }
                return ABBandView.CurrentLevel(map) >= 0;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " sunbeam gate threw: " + e, 0x2B10D3);
                return true;
            }
        }

        /// <summary>Clause (b): fold the scatter position into the viewed band. Their
        /// looping beams store the position we hand back, so loops inherit the fold; a
        /// view-band switch mid-loop is re-folded on the next loop spawn. When we move a
        /// position we re-run the thick-roof verdict ourselves (their test already ran on
        /// the RAW position and its answer no longer applies).</summary>
        public static bool RemapBeam(ref Vector3 position, Map map)
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
                if (ABBandView.CurrentLevel(map) < 0)
                {
                    return false; // belt + braces under the tick gate
                }
                float fx = Fold(position.x, map.Size.x);
                float fz = Fold(position.z, map.Size.z);
                bool moved = fx != position.x || fz != position.z;
                IntVec3 cell = new Vector3(fx, 0f, fz).ToIntVec3();
                if (!cell.InBounds(map) || bands.InGutter(cell))
                {
                    return false;
                }
                int viewBand = ABBandView.CurrentBand(map);
                int srcBand = bands.BandOf(cell);
                if (srcBand != viewBand)
                {
                    int dz = (viewBand - srcBand) * bands.Slot;
                    fz += dz;
                    cell = new IntVec3(cell.x, 0, cell.z + dz);
                    if (!cell.InBounds(map) || bands.InGutter(cell))
                    {
                        return false;
                    }
                    moved = true;
                }
                if (moved)
                {
                    RoofDef roof = map.roofGrid.RoofAt(cell);
                    if (roof != null && roof.isThickRoof)
                    {
                        return false; // mirror their avoidThickRoof default
                    }
                }
                position = new Vector3(fx, position.y, fz);
                return true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " sunbeam remap threw: " + e, 0x2B10D4);
                return true;
            }
        }

        private static float Fold(float v, float size)
        {
            if (size <= 0f)
            {
                return v;
            }
            float m = v % size;
            return m < 0f ? m + size : m;
        }

        /// <summary>Clause (a): home the anchor on the surface band. Full reimplementation
        /// of a 20-line foreign private method, accepted because the alternative is the mod
        /// silently dying at its 10-retry limit; every failure path returns true so THEIR
        /// code (and their give-up counter) stays the authority when we cannot help.</summary>
        public static bool PlaceAnchorOnSurface(MapComponent __instance)
        {
            try
            {
                Map map = __instance?.map;
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return true;
                }
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("SmallPitLight_Sun");
                if (def == null)
                {
                    return true; // their error message names the missing def
                }
                CellRect r = bands.RectOfBand(bands.surfaceBand);
                IntVec3 cell = r.CenterCell;
                Predicate<IntVec3> ok = c => c.InBounds(map) && c.Standable(map)
                    && !bands.InGutter(c) && bands.BandOf(c) == bands.surfaceBand;
                if (!ok(cell) && !CellFinder.TryFindRandomCellNear(cell, map, 40, ok, out cell))
                {
                    // Let their center-of-map attempt run (it will likely fail too, but
                    // its give-up counter is the designed off switch).
                    return true;
                }
                Thing old = fSunLightThing.GetValue(__instance) as Thing;
                if (old != null && !old.Destroyed)
                {
                    old.Destroy();
                }
                Thing t = ThingMaker.MakeThing(def);
                GenSpawn.Spawn(t, cell, map);
                fSunLightThing.SetValue(__instance, t);
                fInitialized.SetValue(__instance, true);
                fCreationAttempted.SetValue(__instance, true);
                fCreationAttemptCount.SetValue(__instance, 0);
                ABLog.Dev("sun anchor homed at " + cell + " (surface band).");
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " sun anchor placement threw; vanilla placement"
                    + " runs instead. " + e.Message, 0x2B10D5);
                return true;
            }
        }

        // ------------------------------------------------------------------ [LBY]云

        private static void InstallLbyClouds()
        {
            Type rendererT = AccessTools.TypeByName("CloudSkyOverlay.CloudSkyRenderer");
            if (rendererT == null)
            {
                return;
            }
            try
            {
                MethodInfo draw = AccessTools.Method(rendererT, "DrawNow");
                if (draw == null)
                {
                    Log.WarningOnce(ABLog.Tag + " [LBY]云 is present but CloudSkyRenderer"
                        + ".DrawNow was not found; clouds will draw underground.", 0x2B10D6);
                    return;
                }
                HarmonyBoot.Harmony.Patch(draw, prefix: new HarmonyMethod(
                    typeof(SkyFXCompat), nameof(GateCloudDraw)));
                ABLog.Dev("[LBY]云 underground gate installed.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " [LBY]云 gate failed to install: " + e.Message,
                    0x2B10D7);
            }
        }

        /// <summary>Same verdict as the weather-overlay gate: underground = no sky.</summary>
        public static bool GateCloudDraw()
        {
            try
            {
                if (!ABGuard.On(ABGuard.Weather))
                {
                    return true;
                }
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return true;
                }
                return ABBandView.CurrentLevel(map) >= 0;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " cloud gate threw: " + e, 0x2B10D8);
                return true;
            }
        }
    }
}
