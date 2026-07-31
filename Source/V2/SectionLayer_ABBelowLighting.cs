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
    /// surface, so it must be shaded with the SURFACE cell's glow instead. At night that is
    /// the difference between a lit camp glowing from above and a black rectangle.
    ///
    /// WHY NOT TWO OVERLAYS (the first attempt, which produced VIGNETTING):
    /// vanilla's overlay does not give each cell its own colour - every vertex is the
    /// AVERAGE GLOW OF THE FOUR CELLS TOUCHING IT, and neighbouring cells share vertices.
    /// Baking a "sky" mesh and a "below" mesh with complementary filters therefore cannot
    /// work: each mesh still emits a quad for EVERY cell, so the quad covering a
    /// see-through cell picks up transparent-black corners from its opaque neighbours (and
    /// vice versa). The result is a dark halo around every opening - exactly the vignette.
    ///
    /// THE FIX: ONE overlay, with the glow SOURCE substituted per cell. Vanilla's geometry
    /// is reused via the public Bake(...), then the vertex colours are recomputed with its
    /// own algorithm, except that any cell you can see through resolves its glow, roof and
    /// edifice from the cell one band DOWN. Vertices then average across a coherent set of
    /// values and light falls off between lit and unlit cells exactly as it does anywhere
    /// else on a vanilla map.
    ///
    /// Sections can straddle a band seam (Slot is not a multiple of the 17-cell section
    /// size), which is why the substitution is per CELL rather than per section.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABBelowLighting : SectionLayer
    {
        private LayerSubMesh mesh;

        private Vector3 offset;

        private static readonly Color32 Transparent = new Color32(0, 0, 0, 0);

        public SectionLayer_ABBelowLighting(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs
                | (ulong)MapMeshFlagDefOf.GroundGlow
                | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering)
            && ABV2Debug.DrawBelowLighting
            && DebugViewSettings.drawLightingOverlay;

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
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return;
                }
                // Vanilla builds the geometry; we only take over the colours.
                mesh = SectionLayer_LightingOverlay.Bake(map, rect, MatBases.LightOverlay, null);
                offset = new Vector3(rect.minX + rect.Width / 2f, 0f, rect.minZ + rect.Height / 2f);
                if (mesh?.mesh != null)
                {
                    mesh.mesh.colors32 = BuildColors(map, bands, rect);
                }
            }
            catch (Exception e)
            {
                Release();
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below lighting");
            }
        }

        /// <summary>
        /// Reimplements SectionLayer_LightingOverlay's colour pass with one change: every
        /// cell lookup goes through SourceIndex, so a see-through cell reports the glow,
        /// roof and edifice of the cell one band below it.
        ///
        /// Vertex layout is vanilla's (MakeBaseGeometry): (W+1)x(H+1) corner vertices in
        /// row-major order, followed by WxH centre vertices.
        /// </summary>
        private static Color32[] BuildColors(Map map, ABBandMap bands, CellRect rect)
        {
            int w = rect.Width;
            int h = rect.Height;
            int firstCenterInd = (w + 1) * (h + 1);
            Color32[] colors = new Color32[firstCenterInd + w * h];

            CellIndices indices = map.cellIndices;
            RoofGrid roofs = map.roofGrid;
            GlowGrid glow = map.glowGrid;
            Thing[] edifices = map.edificeGrid.InnerArray;
            int sizeX = map.Size.x;
            int sizeZ = map.Size.z;
            int slot = bands.Slot;

            // --- corner vertices: average the four cells touching each one ---------
            for (int vz = 0; vz <= h; vz++)
            {
                for (int vx = 0; vx <= w; vx++)
                {
                    int worldX = rect.minX + vx;
                    int worldZ = rect.minZ + vz;
                    ColorInt sum = new ColorInt(0, 0, 0, 0);
                    int n = 0;
                    bool roofed = false;

                    for (int corner = 0; corner < 4; corner++)
                    {
                        int cx = worldX - (corner % 2 == 0 ? 1 : 0);
                        int cz = worldZ - (corner < 2 ? 1 : 0);
                        if (cx < 0 || cz < 0 || cx >= sizeX || cz >= sizeZ)
                        {
                            continue;
                        }
                        int idx = SourceIndex(map, bands, indices, indices.CellToIndex(cx, cz), slot, sizeX);
                        if (idx < 0)
                        {
                            continue;
                        }
                        Thing edifice = edifices[idx];
                        RoofDef roof = roofs.RoofAt(idx);
                        if (roof != null && (roof.isThickRoof || edifice == null
                            || !edifice.def.holdsRoof
                            || edifice.def.altitudeLayer == AltitudeLayer.DoorMoveable))
                        {
                            roofed = true;
                        }
                        if (edifice == null || !edifice.def.blockLight)
                        {
                            sum += glow.VisualGlowAt(idx);
                            n++;
                        }
                    }

                    Color32 col = n > 0 ? (sum / n).ProjectToColor32() : Transparent;
                    if (roofed && col.a < 100)
                    {
                        col.a = 100;
                    }
                    colors[vz * (w + 1) + vx] = col;
                }
            }

            // --- centre vertices: average this cell's own four corners --------------
            for (int cz = 0; cz < h; cz++)
            {
                for (int cx = 0; cx < w; cx++)
                {
                    int botLeft = cz * (w + 1) + cx;
                    ColorInt sum = default(ColorInt);
                    sum += colors[botLeft];
                    sum += colors[botLeft + 1];
                    sum += colors[botLeft + w + 1];
                    sum += colors[botLeft + w + 2];
                    Color32 col = new Color32((byte)(sum.r / 4), (byte)(sum.g / 4),
                        (byte)(sum.b / 4), (byte)(sum.a / 4));

                    int worldX = rect.minX + cx;
                    int worldZ = rect.minZ + cz;
                    int idx = SourceIndex(map, bands, indices,
                        indices.CellToIndex(worldX, worldZ), slot, sizeX);
                    if (col.a < 100 && idx >= 0 && roofs.Roofed(idx))
                    {
                        Thing edifice = edifices[idx];
                        if (edifice == null || !edifice.def.holdsRoof)
                        {
                            col.a = 100;
                        }
                    }
                    colors[firstCenterInd + cz * w + cx] = col;
                }
            }
            return colors;
        }

        /// <summary>The whole trick: a cell you can see through reports the cell one band
        /// below it. Index arithmetic rather than IntVec3 round-tripping, since this runs
        /// for every vertex of every section.</summary>
        private static int SourceIndex(Map map, ABBandMap bands, CellIndices indices,
            int idx, int slot, int sizeX)
        {
            if (idx < 0 || idx >= indices.NumGridCells)
            {
                return -1;
            }
            IntVec3 c = indices.IndexToCell(idx);
            if (bands.BandOf(c) <= 0 || bands.InGutter(c))
            {
                return idx;
            }
            if (!ABBands.ShowsBelow(map.terrainGrid.TerrainAt(c)))
            {
                return idx;
            }
            int below = idx - slot * sizeX;
            return below >= 0 ? below : idx;
        }

        public override void DrawLayer()
        {
            if (!Visible || mesh == null || mesh.disabled || mesh.mesh == null)
            {
                return;
            }
            Graphics.DrawMesh(mesh.mesh, Matrix4x4.Translate(offset), mesh.material, mesh.renderLayer);
        }

        /// <summary>Baked submeshes are free-standing (not owned by this layer's subMeshes
        /// list), so their Unity mesh must be destroyed by hand or every section
        /// regeneration leaks one.</summary>
        private void Release()
        {
            if (mesh == null)
            {
                return;
            }
            if (mesh.mesh != null)
            {
                UnityEngine.Object.Destroy(mesh.mesh);
            }
            mesh = null;
        }
    }

    /// <summary>
    /// On a banded map SectionLayer_ABBelowLighting replaces vanilla's overlay entirely, so
    /// vanilla's must go quiet or every cell would be darkened twice.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_LightingOverlay), "Visible", MethodType.Getter)]
    public static class Patch_LightingOverlay_ABSuppressOnBanded
    {
        /// <summary>Resolved from SectionLayer.section, NOT MapDrawLayer.map.
        ///
        /// MapDrawLayer.map is PRIVATE, and the first version of this patch resolved it
        /// inside a try/catch that swallowed the failure and left __result true. Vanilla's
        /// overlay therefore kept drawing while this file's layer added a second one, and
        /// the two darkening masks multiplied. Silent fallback on a render path is very
        /// hard to see; this fails LOUDLY once instead.</summary>
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static bool warned;

        private static void Postfix(SectionLayer_LightingOverlay __instance, ref bool __result)
        {
            if (!__result || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            try
            {
                Map map = SectionRef(__instance)?.map;
                if (map != null && ABBands.Banded(map))
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Error(ABLog.Tag + " V2: could not suppress vanilla lighting overlay on"
                        + " banded maps; below content will be double-darkened. " + e);
                }
            }
        }
    }

    /// <summary>
    /// Stop vanilla's overlay BAKING as well as drawing.
    ///
    /// Suppressing Visible (above) only stops the DRAW. Section.TryUpdate - the path that
    /// regenerates dirty layers for sections in view - does NOT consult Visible; only
    /// RegenerateDirtyLayers/RegenerateAllLayers do. So on a banded map vanilla's overlay
    /// kept building a full lighting mesh that was then never drawn: measured at 3.92 ms per
    /// 2000 frames across 46 regenerations, pure waste.
    ///
    /// Safe because SectionLayer_ABBelowLighting does not call this INSTANCE method - it
    /// calls the static SectionLayer_LightingOverlay.Bake(...) helper, which is untouched.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_LightingOverlay), nameof(SectionLayer_LightingOverlay.Regenerate))]
    public static class Patch_LightingOverlay_ABSkipBakeOnBanded
    {
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static bool Prefix(SectionLayer_LightingOverlay __instance)
        {
            try
            {
                Map map = SectionRef(__instance)?.map;
                if (map != null && ABGuard.On(ABGuard.Rendering) && ABBands.Banded(map))
                {
                    return false; // our layer owns lighting on banded maps
                }
            }
            catch
            {
                // Any doubt -> let vanilla run. A redundant bake costs frames; a missing one
                // would leave the map unlit.
            }
            return true;
        }
    }
}
