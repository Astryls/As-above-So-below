using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - which band the player is looking at, and keeping the camera inside it.
    ///
    /// Band isolation is mostly FREE: MapDrawer.DrawMapMesh only draws sections that
    /// overlap the camera's ViewRect, so a band the camera cannot see costs nothing to
    /// render. Clamping the camera to the current band's z-range is therefore the whole
    /// of "hide the other levels" - no section-layer surgery, and above all no
    /// DrawPosOffsetPatcher (V1 had to patch hundreds of DrawPos getters on
    /// ParallelPreDraw worker threads purely because the level below was a different Map;
    /// here it is the same map, so the problem does not exist).
    /// </summary>
    public static class ABBandView
    {
        // The band being viewed lives on ABBandMap.viewBand. It used to be a static
        // Dictionary<int,int> keyed by map.uniqueID here, which was never cleared - and
        // uniqueID restarts at 0 for each new game, so every new or loaded colony inherited
        // the previous colony's band. See ABBandMap.viewBand.

        public static int CurrentBand(Map map)
        {
            if (map == null)
            {
                return 0;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return 0;
            }
            if (bands.viewBand >= 0 && bands.BandExists(bands.viewBand))
            {
                return bands.viewBand;
            }
            return bands.surfaceBand;
        }

        public static int CurrentLevel(Map map)
        {
            ABBandMap bands = ABBands.CompOf(map);
            return bands == null || !bands.Banded ? 0 : CurrentBand(map) - bands.surfaceBand;
        }

        /// <summary>Switch bands, preserving the in-band position and the zoom. Because
        /// bands are aligned 1:1 the camera lands on exactly the cell above/below the one
        /// it was looking at, which is what makes the column read as a single place.</summary>
        public static bool SetBand(Map map, int band, bool preserveXZ = true)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded || !bands.BandExists(band))
            {
                return false;
            }
            if (!bands.IsOpen(band))
            {
                Messages.Message("AB2: that level has not been opened yet - build stairs into it first.",
                    MessageTypeDefOf.RejectInput, false);
                return false;
            }
            int old = CurrentBand(map);
            bands.viewBand = band;
            Patch_CameraDriver_ABClipViewToBand.Invalidate();
            if (preserveXZ && Find.CameraDriver != null)
            {
                IntVec3 look = CameraCell(map);
                if (bands.BandOf(look) == old)
                {
                    IntVec3 moved = bands.Translate(look, band);
                    if (moved.InBounds(map))
                    {
                        Find.CameraDriver.SetRootPosAndSize(
                            new Vector3(moved.x + 0.5f, 0f, moved.z + 0.5f),
                            Find.CameraDriver.ZoomRootSize);
                    }
                }
            }
            return true;
        }

        public static void JumpTo(Map map, IntVec3 cell)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                CameraJumper.TryJump(new GlobalTargetInfo(cell, map));
                return;
            }
            bands.viewBand = bands.BandOf(cell);
            Patch_CameraDriver_ABClipViewToBand.Invalidate();
            CameraJumper.TryJump(new GlobalTargetInfo(cell, map));
        }

        private static IntVec3 CameraCell(Map map)
        {
            Vector3 p = Find.CameraDriver.MapPosition.ToVector3();
            IntVec3 c = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.z));
            return c.InBounds(map) ? c : map.Center;
        }

        public static bool TryStep(Map map, int delta)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            return SetBand(map, CurrentBand(map) + delta);
        }

        /// <summary>World-space z bounds of the current band. The camera must keep its
        /// whole VIEW inside these, not just its centre - see the clamp below.</summary>
        public static bool TryBandBounds(Map map, out float minZ, out float maxZ)
        {
            minZ = 0f;
            maxZ = 0f;
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            CellRect r = bands.RectOfBand(CurrentBand(map));
            minZ = r.minZ;
            maxZ = r.maxZ + 1;
            return true;
        }
    }

    /// <summary>
    /// HIDES THE OTHER LEVELS, whatever the camera does.
    ///
    /// Clipping the view rect to the current band is the whole mechanism, and it is one
    /// patch because vanilla funnels both halves of "what is on screen" through this one
    /// property:
    ///   - MapDrawer.ViewRect is `Find.CameraDriver.CurrentViewRect.ExpandedBy(1)...`, and
    ///     DrawMapMesh only calls Section.DrawSection for sections overlapping it, so
    ///     terrain, buildings, plants and items outside the band stop drawing.
    ///   - DynamicDrawManager.ComputeCulledThings builds its cull job from the same
    ///     property, so pawns and other dynamic things outside the band are culled too.
    ///
    /// Patching here rather than at either draw site avoids touching
    /// ThingCullDetails - a PRIVATE nested struct, which a postfix cannot name in its
    /// signature - and avoids a per-thing loop on a per-frame path.
    ///
    /// This is what makes any edge overhang visually harmless: the neighbouring band simply
    /// is not drawn, so the player sees empty space past the edge of the level they are on.
    ///
    /// ALWAYS ACTIVE as of the baked-bounds rework. It used to be gated on the free-pan
    /// setting, on the reasoning that a strictly clamped camera makes the clip a no-op.
    /// That reasoning died with the setting: per-level `panMargin` values mean the view is
    /// *expected* to overhang, so the clip is now the load-bearing guarantee rather than a
    /// redundant one. It is memoised per frame, so being unconditional costs one branch.
    ///
    /// The returned rect is modified, not the cached `lastViewRect` field, so vanilla's
    /// per-frame cache is not corrupted.
    /// </summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
    public static class Patch_CameraDriver_ABClipViewToBand
    {
        // Per-frame memo, mirroring vanilla's own lastViewRectGetFrame caching right next
        // door. CurrentViewRect is read several times a frame by vanilla and three more
        // times by this mod, and resolving the bounds costs TWO ConditionalWeakTable
        // probes (TryBandBounds and CurrentBand each call ABBands.CompOf). Recomputing that
        // per call on a per-frame render path is exactly the kind of cost this mod has
        // measured and removed before.
        private static int cachedFrame = -1;

        private static bool cachedActive;

        private static int cachedLo;

        private static int cachedHi;

        /// <summary>Called when the viewed band changes, so the clip cannot lag a frame
        /// behind a level switch.</summary>
        public static void Invalidate()
        {
            cachedFrame = -1;
        }

        private static void Postfix(ref CellRect __result)
        {
            try
            {
                if (cachedFrame != Time.frameCount)
                {
                    cachedFrame = Time.frameCount;
                    cachedActive = false;
                    Map map = Find.CurrentMap;
                    if (map != null
                        // Gravship rendering encapsulates its own bounds into the view
                        // downstream of this; leave that path alone rather than clipping a
                        // rect it is about to extend for a different purpose.
                        && !WorldComponent_GravshipController.GravshipRenderInProgess
                        && ABBandView.TryBandBounds(map, out float minZ, out float maxZ))
                    {
                        cachedActive = true;
                        cachedLo = Mathf.RoundToInt(minZ);
                        cachedHi = Mathf.RoundToInt(maxZ) - 1;
                    }
                }
                if (!cachedActive)
                {
                    return;
                }
                int lo2 = cachedLo;
                int hi2 = cachedHi;
                if (__result.minZ >= lo2 && __result.maxZ <= hi2)
                {
                    return; // already inside the band - the common case, and free
                }
                // NEVER hand back an empty or inverted rect.
                //
                // The first version collapsed a fully off-band view to `maxZ = minZ - 1`,
                // i.e. Height 0. That rect does not stay contained: MapDrawer feeds it
                // through ExpandedBy(1), and SectionLayer_SunShadows.GetSunShadowsViewRect
                // shifts its edges by the light vector and re-clips - so a degenerate rect
                // propagates into vanilla geometry and the Burst cull job rather than
                // simply drawing nothing. Clamping to a single valid row at the band edge
                // draws just as little and stays a well-formed rect everywhere downstream.
                CellRect r = __result;
                if (r.maxZ < lo2)
                {
                    r.minZ = lo2;
                    r.maxZ = lo2;
                }
                else if (r.minZ > hi2)
                {
                    r.minZ = hi2;
                    r.maxZ = hi2;
                }
                else
                {
                    r.minZ = Mathf.Max(r.minZ, lo2);
                    r.maxZ = Mathf.Min(r.maxZ, hi2);
                }
                __result = r;
            }
            catch
            {
                // Never let a view-rect tweak break rendering; worst case the neighbouring
                // band shows for a frame.
            }
        }
    }

    /// <summary>
    /// Holds the camera to the current band, using the BAKED per-level bounds in
    /// ABCameraBounds rather than a player-facing setting.
    ///
    /// Run #7 caught the naive version: clamping only rootPos (the view CENTRE) still let
    /// the viewport overhang the band edge, so the gutter and the neighbouring level were
    /// visible as a strip along the top/bottom of the screen. The camera is orthographic,
    /// so the visible half-height in world units IS RootSize - the centre must therefore
    /// stay RootSize away from each band edge, and the zoom must not exceed half the band
    /// height or no centre position can satisfy that. That remains the derived default
    /// (`maxZoom &lt;= 0`, `panMargin == 0`); a level may now widen either deliberately,
    /// because the view clip above guarantees the overhang shows empty space and never the
    /// neighbouring level.
    /// </summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    public static class Patch_CameraDriver_ABClampToBand
    {
        private static void Postfix(CameraDriver __instance)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABBandView.TryBandBounds(map, out float minZ, out float maxZ))
                {
                    return;
                }
                if (ABCameraBounds.CalibrationUnlocked)
                {
                    // The calibration window is open: stand down so the camera can be
                    // pushed PAST the baked limits. A tool that could only reach the
                    // limits already in force could not help choose better ones.
                    return;
                }
                float bandHeight = maxZ - minZ;
                ABCameraBounds.Limits lim = ABCameraBounds.For(ABBandView.CurrentLevel(map));

                // Derived default: never zoom out further than the band can fill.
                float maxSize = lim.maxZoom > 0f ? lim.maxZoom : bandHeight * 0.5f;
                if (ABCameraBounds.RootSize(__instance) > maxSize)
                {
                    ABCameraBounds.RootSize(__instance) = maxSize;
                }

                float half = ABCameraBounds.RootSize(__instance);
                Vector3 p = ABCameraBounds.RootPos(__instance);
                float margin = Mathf.Max(0f, lim.panMargin);
                float lo = minZ + half - margin;
                float hi = maxZ - half + margin;
                float clamped = lo > hi ? (minZ + maxZ) * 0.5f : Mathf.Clamp(p.z, lo, hi);
                if (!Mathf.Approximately(clamped, p.z))
                {
                    p.z = clamped;
                    ABCameraBounds.RootPos(__instance) = p;
                }
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2: camera band clamp threw: " + e);
            }
        }
    }
}
