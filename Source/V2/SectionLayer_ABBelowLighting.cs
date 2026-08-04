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
            // AB_BelowThings: the mirrored below-change signal (§36c-B1). The glow, roof
            // and edifice this layer samples live on the RESOLVED (below) band; their
            // dirties arrive only through the mirror now.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs
                | (ulong)MapMeshFlagDefOf.GroundGlow
                | (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)ABDefOf.AB_BelowThings;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering)
            && ABV2Debug.DrawBelowLighting
            && DebugViewSettings.drawLightingOverlay;

        public override void Regenerate()
        {
            if (!ABGuard.On(ABGuard.Rendering))
            {
                Release();
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Release();
                return;
            }
            try
            {
                CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
                rect.ClipInsideMap(map);
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    Release();
                    return;
                }
                // ⚠ BAKE THE GEOMETRY ONCE. This method used to open with Release() and
                // re-Bake unconditionally, which allocated a fresh Unity Mesh and Destroy()d
                // the previous one on EVERY regenerate - while vanilla's own overlay, and
                // every other layer in this mod, reuses its submesh forever.
                //
                // It was the most expensive line in the see-below stack and the cost was
                // invisible in an fps average, because it is paid in BURSTS: the dirty mirror
                // next door invalidates a section stack per band, so one mined rock could
                // allocate and destroy one Mesh per band per section touched. Native mesh
                // allocation is not cheap and Destroy is deferred to end of frame, so the
                // churn lands as a hitch during exactly the activity that triggers it.
                //
                // Safe to reuse because the geometry is a pure function of `rect`, and rect
                // derives from section.botLeft, which never changes for a given layer. Unity's
                // fake-null makes the check also catch a mesh destroyed underneath us.
                if (mesh == null || mesh.mesh == null)
                {
                    Release();
                    mesh = SectionLayer_LightingOverlay.Bake(map, rect, MatBases.LightOverlay, null);
                    offset = new Vector3(rect.minX + rect.Width / 2f, 0f, rect.minZ + rect.Height / 2f);
                }
                // Only the COLOURS are per-regenerate - which is the whole reason vanilla
                // splits Bake from the colour pass, and the reason this split was already
                // sitting here unused.
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
                        int idx = SourceIndex(map, bands, indices, indices.CellToIndex(cx, cz));
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
                        indices.CellToIndex(worldX, worldZ));
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

        /// <summary>
        /// The whole trick: a cell you can see through reports the cell the column ACTUALLY
        /// SHOWS.
        ///
        /// ⚠ THIS WAS THE ONE-DESCENT BUG, FOR THE EIGHTH TIME. It used to end with a single
        /// <c>idx - slot * sizeX</c> step - one band down, unconditionally. That is correct
        /// from level +1, where the band below is the opaque surface, and wrong from every
        /// level above it: from +2 or +3 the cell one band down is usually open air too, so
        /// the overlay shaded the below view with the glow, roof and edifice of an EMPTY AIR
        /// CELL while SectionLayer_ABBelowV2 was drawing the ground two or three levels
        /// further down. Unroofed air reads as full daylight, so the symptom is a deep view
        /// that stays bright at night and ignores every lamp actually lighting it - which
        /// looks like a lighting bug, not like a missing descent.
        ///
        /// It survived the standing audit because that audit greps for `- Slot`, and this was
        /// written in index space. See ABBands.TryResolveVisibleFrom.
        ///
        /// Deliberately requireUnfogged: FALSE. The overlay must shade whatever the terrain
        /// layer drew, and that layer draws fogged ground too (behind its own fog fan);
        /// vanilla shades fogged cells as well, so demanding legibility here would put a
        /// bright square under every fog skirt.
        /// </summary>
        private static int SourceIndex(Map map, ABBandMap bands, CellIndices indices, int idx)
        {
            if (idx < 0 || idx >= indices.NumGridCells)
            {
                return -1;
            }
            IntVec3 c = indices.IndexToCell(idx);
            if (!ABBands.TryResolveVisibleFrom(map, bands, c, requireUnfogged: false,
                    out IntVec3 below, out _))
            {
                return idx; // opaque, off-band or in a seam: this cell shades itself
            }
            return indices.CellToIndex(below);
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

    // ⚠⚠ DO NOT RE-ADD THE "SKIP VANILLA'S BAKE TOO" PREFIX. It existed here until
    // 2026-08, prefixing SectionLayer_LightingOverlay.Regenerate to return false whenever
    // Banded(map) && ABGuard.On(Rendering). It was DELETED, and the reasoning is worth the
    // twenty lines because the optimisation looks free and is not.
    //
    // What it bought: 3.92 ms per 2000 frames across 46 regenerations, i.e. ~0.002 ms per
    // frame. Vanilla's overlay is suppressed from DRAWING by the Visible patch above, and
    // Section.TryUpdate does not consult Visible, so it kept baking a mesh nobody drew.
    //
    // What it cost, all three found by a player reading the patch:
    //
    // 1. IT LEFT THE LAYER CLEAN AND EMPTY. Section.TryUpdate sets Dirty = false AFTER
    //    calling Regenerate - it does not care that a prefix skipped the body. So vanilla's
    //    layer sat marked-clean holding zero verts, with sectRect and firstCenterInd never
    //    initialised.
    //
    // 2. ⚠ THE SKIP CONDITION WAS A MUTABLE GLOBAL, AND FLIPPING IT DIRTIED NOTHING.
    //    ABGuard.Rendering is not config: ~10 rendering call sites trip it via
    //    ABGuard.Disable when they throw, the settings panel re-arms it, and Reset() clears
    //    it on load. The instant it flipped, BOTH patches stopped applying together -
    //    vanilla's overlay became Visible again holding the empty mesh from (1), and since
    //    its relevantChangeTypes are only Roofs | GroundGlow, a settled colony can go a very
    //    long time before a section re-dirties. Symptom: guard trips, and the map then draws
    //    with NO lighting overlay at all until the player happens to build something.
    //
    // 3. IT HAD A SECOND CONSUMER WE NEVER COUNTED. LudeonTK's EditWindow_DebugInspector
    //    calls SectionLayer_LightingOverlay.GlowReportAt, which reads the baked mesh's
    //    colors32 DIRECTLY and never consults Visible. Against an unbaked mesh that is an
    //    IndexOutOfRange per frame with the glow report open.
    //
    // Plus an empty `catch { }` that swallowed every exception silently - in the same file
    // whose Visible patch above carries a doc comment about why that is wrong. Guards are
    // per-consumer; fixing one does not fix its neighbour.
    //
    // THE RULE: suppressing a vanilla layer's DRAW is safe because Visible is re-read every
    // frame. Suppressing its BAKE is not, because the mesh persists and the dirty system is
    // not listening to our global. If a future window really needs those 0.002 ms, the price
    // is a shared ownership predicate plus a WholeMapChanged(Roofs | GroundGlow) fired
    // (deferred out of the draw call) on every ABGuard.Rendering transition, AND a guard on
    // GlowReportAt. That is ~40 lines of hazard for a rounding error. Leave it deleted.
}
