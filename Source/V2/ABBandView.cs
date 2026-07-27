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
        private static readonly Dictionary<int, int> currentBand = new Dictionary<int, int>();

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
            if (currentBand.TryGetValue(map.uniqueID, out int b) && bands.BandExists(b))
            {
                return b;
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
            currentBand[map.uniqueID] = band;
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
            int band = bands.BandOf(cell);
            currentBand[map.uniqueID] = band;
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

        /// <summary>The z-range the camera may occupy for the current band, in world
        /// units, inset so the view never shows the gutter.</summary>
        public static bool TryBandCameraRange(Map map, out float minZ, out float maxZ)
        {
            minZ = 0f;
            maxZ = 0f;
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            CellRect r = bands.RectOfBand(CurrentBand(map));
            minZ = r.minZ + 2f;
            maxZ = r.maxZ - 1f;
            return true;
        }
    }

    /// <summary>
    /// Re-clamps the camera into the current band. CameraDriver.Update clamps rootPos.z
    /// to the whole map every frame, so this postfix runs after it and narrows the clamp.
    /// A postfix (rather than a transpiler on the inline Mathf.Clamp) keeps us clear of
    /// the many mods that touch camera movement.
    /// </summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    public static class Patch_CameraDriver_ABClampToBand
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPosRef =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        private static void Postfix(CameraDriver __instance)
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABBandView.TryBandCameraRange(map, out float minZ, out float maxZ))
                {
                    return;
                }
                Vector3 p = RootPosRef(__instance);
                float clamped = Mathf.Clamp(p.z, minZ, maxZ);
                if (!Mathf.Approximately(clamped, p.z))
                {
                    p.z = clamped;
                    RootPosRef(__instance) = p;
                }
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2: camera band clamp threw: " + e);
            }
        }
    }
}
