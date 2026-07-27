using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 lighting for banded maps.
    ///
    /// THE PROBLEM: below content is drawn into the sky band's cells, so vanilla's lighting
    /// overlay shades it with the SKY cell's glow. Through open air you are looking at the
    /// surface, so it should be shaded with the SURFACE cell's glow instead. At night that
    /// is the difference between a sun-lamp farm glowing from above and a black rectangle.
    ///
    /// WHY NOT JUST ADD A SECOND OVERLAY: the lighting overlay is a DARKENING mask - alpha
    /// is darkness. Drawing a below-overlay on top of the sky one multiplies them, so
    /// "night sky" x "bright lamp" still comes out dark. The sky overlay has to be
    /// REPLACED for see-through cells, not supplemented.
    ///
    /// HOW: vanilla exposes SectionLayer_LightingOverlay.Bake(map, rect, mat, filter) -
    /// public, static, and filtered per cell, where a filtered-out cell is written as
    /// Color32(0,0,0,0) (fully transparent = contributes no darkening). So we bake TWICE
    /// with complementary filters and draw both:
    ///
    ///   sky bake   over this section's rect, filter = NOT see-through  -> normal lighting
    ///   below bake over that rect minus one Slot, filter = see-through -> drawn translated
    ///
    /// The filters are mutually exclusive per cell, so every cell receives exactly one
    /// non-transparent contribution and nothing is double-darkened. Vanilla's own overlay
    /// is suppressed on banded maps (see the patch below) since this layer replaces it
    /// wholesale.
    ///
    /// Sections can straddle a band seam (Slot is not a multiple of the 17-cell section
    /// size), which is exactly why this is done per CELL rather than per section.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABBelowLighting : SectionLayer
    {
        private LayerSubMesh skyMesh;

        private LayerSubMesh belowMesh;

        private Vector3 skyOffset;

        private Vector3 belowOffset;

        public SectionLayer_ABBelowLighting(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs
                | (ulong)MapMeshFlagDefOf.GroundGlow
                | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering) && DebugViewSettings.drawLightingOverlay;

        public override void Regenerate()
        {
            Release();
            if (!ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            try
            {
                CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
                rect.ClipInsideMap(map);
                int slot = bands.Slot;

                bool anySeeThrough = false;
                foreach (IntVec3 c in rect)
                {
                    if (SeeThrough(map, bands, c))
                    {
                        anySeeThrough = true;
                        break;
                    }
                }

                // Normal lighting for everything that is NOT a window onto the level below.
                skyMesh = SectionLayer_LightingOverlay.Bake(map, rect, MatBases.LightOverlay,
                    idx => !SeeThroughIndex(map, bands, idx));
                skyOffset = CenterOf(rect, 0);

                if (!anySeeThrough)
                {
                    return;
                }
                CellRect belowRect = rect.MovedBy(new IntVec3(0, 0, -slot));
                belowRect.ClipInsideMap(map);
                if (belowRect.Width <= 0 || belowRect.Height <= 0)
                {
                    return;
                }
                // The surface's own lighting, sampled a band down, shown a band up.
                belowMesh = SectionLayer_LightingOverlay.Bake(map, belowRect, MatBases.LightOverlay,
                    idx => SeeThroughIndex(map, bands, OffsetIndex(map, idx, slot)));
                belowOffset = CenterOf(belowRect, slot);
            }
            catch (Exception e)
            {
                Release();
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below lighting");
            }
        }

        /// <summary>Bake centres its geometry on the origin, so placement is a translation
        /// to the rect's centre - plus the band offset for the below pass.</summary>
        private static Vector3 CenterOf(CellRect rect, int zShift)
        {
            return new Vector3(rect.minX + rect.Width / 2f, 0f, rect.minZ + rect.Height / 2f + zShift);
        }

        private static bool SeeThrough(Map map, ABBandMap bands, IntVec3 c)
        {
            if (!c.InBounds(map) || bands.BandOf(c) <= 0 || bands.InGutter(c))
            {
                return false;
            }
            return map.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir;
        }

        /// <summary>The filter receives raw cell indices and walks one past the rect edge on
        /// the vertex grid, so bounds must be checked here rather than assumed.</summary>
        private static bool SeeThroughIndex(Map map, ABBandMap bands, int index)
        {
            if (index < 0 || index >= map.cellIndices.NumGridCells)
            {
                return false;
            }
            return SeeThrough(map, bands, map.cellIndices.IndexToCell(index));
        }

        private static int OffsetIndex(Map map, int index, int zShift)
        {
            if (index < 0 || index >= map.cellIndices.NumGridCells)
            {
                return -1;
            }
            IntVec3 c = map.cellIndices.IndexToCell(index);
            IntVec3 shifted = new IntVec3(c.x, 0, c.z + zShift);
            if (!shifted.InBounds(map))
            {
                return -1;
            }
            return map.cellIndices.CellToIndex(shifted);
        }

        public override void DrawLayer()
        {
            if (!Visible)
            {
                return;
            }
            DrawBaked(skyMesh, skyOffset);
            DrawBaked(belowMesh, belowOffset);
        }

        private static void DrawBaked(LayerSubMesh sub, Vector3 offset)
        {
            if (sub == null || sub.disabled || sub.mesh == null)
            {
                return;
            }
            Graphics.DrawMesh(sub.mesh, Matrix4x4.Translate(offset), sub.material, sub.renderLayer);
        }

        /// <summary>Baked submeshes are free-standing (not owned by this layer's subMeshes
        /// list), so their Unity meshes must be destroyed by hand or every section
        /// regeneration leaks one.</summary>
        private void Release()
        {
            DestroyMesh(ref skyMesh);
            DestroyMesh(ref belowMesh);
        }

        private static void DestroyMesh(ref LayerSubMesh sub)
        {
            if (sub == null)
            {
                return;
            }
            if (sub.mesh != null)
            {
                UnityEngine.Object.Destroy(sub.mesh);
            }
            sub = null;
        }
    }

    /// <summary>
    /// On a banded map SectionLayer_ABBelowLighting replaces vanilla's overlay entirely, so
    /// vanilla's must go quiet or every cell would be darkened twice.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_LightingOverlay), "Visible", MethodType.Getter)]
    public static class Patch_LightingOverlay_ABSuppressOnBanded
    {
        private static readonly AccessTools.FieldRef<MapDrawLayer, Map> MapRef =
            AccessTools.FieldRefAccess<MapDrawLayer, Map>("map");

        private static void Postfix(SectionLayer_LightingOverlay __instance, ref bool __result)
        {
            if (!__result)
            {
                return;
            }
            try
            {
                if (ABGuard.On(ABGuard.Rendering) && ABBands.Banded(MapRef(__instance)))
                {
                    __result = false;
                }
            }
            catch
            {
                // Leave vanilla lighting on if anything here is unexpected.
            }
        }
    }
}
