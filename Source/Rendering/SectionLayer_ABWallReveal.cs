using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The "edge aware" rooftop rim (user-directed round 17): rim rooftop
    /// cells - steel tile over a supporting wall, with open air on at least
    /// one cardinal side - reprint the SUPPORTING WALL'S own sprite clipped
    /// to the air-facing strip of the cell, so part of the wall top shows
    /// through the outer tile ring and the slab reads as resting on the wall
    /// line below instead of floating.
    ///
    /// Draw order does the covering, not geometry: the strips draw through
    /// clones forced into the mountain cap's proven queue window - strictly
    /// above every terrain family, HARD-CLAMPED below the cutout family
    /// (LevelRenderer.WallRevealQueue) - so the steel tile underdraws them
    /// and sky walls, doors, and furniture always draw over them. Within-
    /// queue tricks DO NOT work and were tried twice (#131 EdgeShadow
    /// -poisoned measurement without the clamp; #132 native materials +
    /// flattened altitude - map shaders do not depth-write, so same-queue
    /// order is effective submission order and this layer draws after the
    /// things layer). Laying a sky floor replaces the rooftop terrain and
    /// disqualifies the cell outright. The below-things band is untouched:
    /// rooftop opacity-by-construction still holds - this layer only ever
    /// paints ON TOP of the roof.
    ///
    /// Clipping happens at print time on the freshly appended quads
    /// (PrintPlane structure: 4 verts / 4 Vector3 uvs / 4 colors / 6 tris
    /// per quad, verified against 1.6 source; the north verts carry a +0.01
    /// altitude bias which the bilinear remap preserves). Axis-aligned quads
    /// - including 90-degree building rotations, whose UV arrangement the
    /// corner matching resolves - are cut exactly with linearly remapped
    /// UVs, safe inside atlas sub-rects. The rare free-rotated quad is kept
    /// only when it already fits inside a strip, dropped otherwise. Any
    /// unexpected print structure rolls back whole: a missing strip is the
    /// safe failure. Kill switch: Rendering.
    /// </summary>
    public class SectionLayer_ABWallReveal : SectionLayer
    {
        public SectionLayer_ABWallReveal(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)ABDefOf.AB_BelowThings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible =>
            ABGuard.On(ABGuard.Rendering) && (ABMod.Settings?.drawWallReveal ?? true);

        private const float Eps = 0.001f;

        private readonly List<int> vertsBefore = new List<int>();
        private readonly List<int> trisBefore = new List<int>();

        /// <summary>Disjoint keep-rects for the current print, packed as
        /// (x0, z0, x1, z1).</summary>
        private readonly List<Vector4> clipRects = new List<Vector4>();

        private readonly List<Vector3> qVerts = new List<Vector3>();
        private readonly List<Vector3> qUvs = new List<Vector3>();
        private readonly List<Color32> qCols = new List<Color32>();

        /// <summary>Flattened altitude for every strip vertex (the floor
        /// -emplacement plane, same as the mountain cap's quads). Ordering
        /// comes from the forced queue, never from altitude; the flatten just
        /// keeps the strip mesh out of dynamic-content altitude ranges.</summary>
        private float stripAltitude;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering) || map.Level() <= 0)
            {
                return;
            }
            try
            {
                Map lower = map.LowerMap();
                if (lower == null || lower.Disposed)
                {
                    return;
                }
                TerrainGrid skyTerrain = map.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef rooftop = ABDefOf.AB_RoofSurface;
                FogGrid lowerFog = lower.fogGrid;
                float width = Mathf.Clamp(ABMod.Settings?.wallRevealWidth ?? 0.5f, 0.15f, 0.75f);
                stripAltitude = AltitudeLayer.FloorEmplacement.AltitudeFor();
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(lower) || !IsRimCell(map, skyTerrain, c, air, rooftop))
                    {
                        continue;
                    }
                    Building ed = lower.edificeGrid[c];
                    if (!ABRimPrint.QualifiesAsSupport(ed)
                        || (!ed.def.seeThroughFog && lowerFog.IsFogged(ed.Position)))
                    {
                        continue;
                    }
                    // Multi-cell edifices print once, from their first
                    // qualifying cell in scan order - deterministic even when
                    // the occupied rect straddles a section boundary.
                    if (!IsFirstQualifyingCell(map, lower, skyTerrain, ed, c, air, rooftop))
                    {
                        continue;
                    }
                    GatherStrips(map, lower, skyTerrain, ed, air, rooftop, width);
                    if (clipRects.Count == 0)
                    {
                        continue;
                    }
                    ABRimPrint.Snapshot(subMeshes, vertsBefore, trisBefore);
                    try
                    {
                        ed.Print(this);
                        if (ClipNewGeometry())
                        {
                            printed = true;
                        }
                    }
                    catch (Exception e)
                    {
                        ABRimPrint.Rollback(subMeshes, vertsBefore, trisBefore);
                        Log.WarningOnce(ABLog.Tag + " Rim reveal print failed for " + ed.LabelCap
                            + ": " + e.Message, ed.thingIDNumber ^ 762195850);
                    }
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "wall reveal layer");
            }
        }

        private static bool AirAt(Map sky, TerrainGrid grid, IntVec3 c, TerrainDef air)
        {
            return c.InBounds(sky) && grid.TerrainAt(c) == air;
        }

        /// <summary>Rooftop tile with open air on at least one cardinal side.
        /// Exactly AB_RoofSurface: a laid floor replaces the rooftop terrain
        /// and turns the reveal off for that cell by design.</summary>
        private static bool IsRimCell(Map sky, TerrainGrid grid, IntVec3 c,
            TerrainDef air, TerrainDef rooftop)
        {
            if (!c.InBounds(sky) || grid.TerrainAt(c) != rooftop)
            {
                return false;
            }
            return AirAt(sky, grid, c + IntVec3.North, air)
                || AirAt(sky, grid, c + IntVec3.South, air)
                || AirAt(sky, grid, c + IntVec3.East, air)
                || AirAt(sky, grid, c + IntVec3.West, air);
        }

        private static bool IsFirstQualifyingCell(Map sky, Map lower, TerrainGrid grid,
            Building ed, IntVec3 c, TerrainDef air, TerrainDef rooftop)
        {
            CellRect rect = ed.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (q.InBounds(lower) && IsRimCell(sky, grid, q, air, rooftop))
                    {
                        return q.x == c.x && q.z == c.z;
                    }
                }
            }
            return false;
        }

        /// <summary>Disjoint air-facing strips over every qualifying cell the
        /// edifice occupies: full-width bands on north/south air sides, the
        /// left-over middle span on east/west air sides. Strips of different
        /// cells never overlap, so no pixel is emitted twice.</summary>
        private void GatherStrips(Map sky, Map lower, TerrainGrid grid, Building ed,
            TerrainDef air, TerrainDef rooftop, float width)
        {
            clipRects.Clear();
            CellRect rect = ed.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (!q.InBounds(lower) || !IsRimCell(sky, grid, q, air, rooftop))
                    {
                        continue;
                    }
                    bool n = AirAt(sky, grid, q + IntVec3.North, air);
                    bool s = AirAt(sky, grid, q + IntVec3.South, air);
                    bool e = AirAt(sky, grid, q + IntVec3.East, air);
                    bool w = AirAt(sky, grid, q + IntVec3.West, air);
                    if (n)
                    {
                        clipRects.Add(new Vector4(x, z + 1f - width, x + 1f, z + 1f));
                    }
                    if (s)
                    {
                        clipRects.Add(new Vector4(x, z, x + 1f, z + width));
                    }
                    float zLo = z + (s ? width : 0f);
                    float zHi = z + 1f - (n ? width : 0f);
                    if (zHi - zLo > Eps)
                    {
                        if (e)
                        {
                            clipRects.Add(new Vector4(x + 1f - width, zLo, x + 1f, zHi));
                        }
                        if (w)
                        {
                            clipRects.Add(new Vector4(x, zLo, x + width, zHi));
                        }
                    }
                }
            }
        }

        /// <summary>Rewrites everything the print just appended as strip
        /// -clipped quads. Returns true when any geometry survived.</summary>
        private bool ClipNewGeometry()
        {
            bool any = false;
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sub = subs[i];
                int vFrom = i < vertsBefore.Count ? vertsBefore[i] : 0;
                int tFrom = i < trisBefore.Count ? trisBefore[i] : 0;
                int added = sub.verts.Count - vFrom;
                if (added <= 0)
                {
                    continue;
                }
                // Shadow volumes never belong in the strips (drawn as plain
                // geometry they render solid black); PrintPlane structure
                // only for everything else - parallel arrays in quads.
                if (ABRimPrint.IsShadowMaterial(sub.material)
                    || added % 4 != 0 || sub.tris.Count - tFrom != added / 4 * 6
                    || sub.uvs.Count != sub.verts.Count || sub.colors.Count != sub.verts.Count)
                {
                    ABRimPrint.Truncate(sub, vFrom, tFrom);
                    continue;
                }
                qVerts.Clear();
                qUvs.Clear();
                qCols.Clear();
                for (int v = vFrom; v < sub.verts.Count; v++)
                {
                    qVerts.Add(sub.verts[v]);
                    qUvs.Add(sub.uvs[v]);
                    qCols.Add(sub.colors[v]);
                }
                ABRimPrint.Truncate(sub, vFrom, tFrom);
                int quads = qVerts.Count / 4;
                for (int q = 0; q < quads; q++)
                {
                    if (EmitClippedQuad(sub, q * 4))
                    {
                        any = true;
                    }
                }
            }
            return any;
        }

        private bool EmitClippedQuad(LayerSubMesh sub, int b)
        {
            float xMin = float.MaxValue;
            float xMax = float.MinValue;
            float zMin = float.MaxValue;
            float zMax = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                Vector3 v = qVerts[b + k];
                xMin = Mathf.Min(xMin, v.x);
                xMax = Mathf.Max(xMax, v.x);
                zMin = Mathf.Min(zMin, v.z);
                zMax = Mathf.Max(zMax, v.z);
            }
            if (xMax - xMin < Eps || zMax - zMin < Eps)
            {
                return false;
            }
            // Corner matching handles every 90-degree rotation and UV flip: each
            // vert snaps to one bbox corner and brings its own uv/color along.
            bool axis = true;
            int i00 = -1;
            int i01 = -1;
            int i11 = -1;
            int i10 = -1;
            for (int k = 0; k < 4; k++)
            {
                Vector3 v = qVerts[b + k];
                bool loX = v.x - xMin < Eps;
                bool hiX = xMax - v.x < Eps;
                bool loZ = v.z - zMin < Eps;
                bool hiZ = zMax - v.z < Eps;
                if ((!loX && !hiX) || (!loZ && !hiZ))
                {
                    axis = false;
                    break;
                }
                if (loX && loZ)
                {
                    i00 = b + k;
                }
                else if (loX)
                {
                    i01 = b + k;
                }
                else if (hiZ)
                {
                    i11 = b + k;
                }
                else
                {
                    i10 = b + k;
                }
            }
            if (!axis || i00 < 0 || i01 < 0 || i11 < 0 || i10 < 0)
            {
                // Free-rotated or degenerate: keep only when it already sits
                // fully inside one strip, drop otherwise.
                for (int r = 0; r < clipRects.Count; r++)
                {
                    Vector4 rect = clipRects[r];
                    if (xMin >= rect.x - Eps && zMin >= rect.y - Eps
                        && xMax <= rect.z + Eps && zMax <= rect.w + Eps)
                    {
                        CopyQuad(sub, b);
                        return true;
                    }
                }
                return false;
            }
            bool emitted = false;
            for (int r = 0; r < clipRects.Count; r++)
            {
                Vector4 rect = clipRects[r];
                float cx0 = Mathf.Max(xMin, rect.x);
                float cz0 = Mathf.Max(zMin, rect.y);
                float cx1 = Mathf.Min(xMax, rect.z);
                float cz1 = Mathf.Min(zMax, rect.w);
                if (cx1 - cx0 < Eps || cz1 - cz0 < Eps)
                {
                    continue;
                }
                EmitSubQuad(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx0, cz0, cx1, cz1);
                emitted = true;
            }
            return emitted;
        }

        private void CopyQuad(LayerSubMesh sub, int b)
        {
            int vi = sub.verts.Count;
            for (int k = 0; k < 4; k++)
            {
                Vector3 v = qVerts[b + k];
                sub.verts.Add(new Vector3(v.x, stripAltitude, v.z));
                sub.uvs.Add(qUvs[b + k]);
                sub.colors.Add(qCols[b + k]);
            }
            AddQuadTris(sub, vi);
        }

        private void EmitSubQuad(LayerSubMesh sub, int i00, int i01, int i11, int i10,
            float xMin, float xMax, float zMin, float zMax,
            float cx0, float cz0, float cx1, float cz1)
        {
            int vi = sub.verts.Count;
            AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx0, cz0);
            AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx0, cz1);
            AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx1, cz1);
            AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx1, cz0);
            AddQuadTris(sub, vi);
        }

        private static void AddQuadTris(LayerSubMesh sub, int vi)
        {
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }

        /// <summary>Bilinear sample of the source quad's uv and color at one
        /// clipped corner - preserves atlas UV sub-rects and vertex tinting.
        /// Altitude is NOT sampled: every strip vertex flattens to
        /// stripAltitude so the native-queue draw sorts under real prints.</summary>
        private void AddClippedVert(LayerSubMesh sub, int i00, int i01, int i11, int i10,
            float xMin, float xMax, float zMin, float zMax, float x, float z)
        {
            float s = (x - xMin) / (xMax - xMin);
            float t = (z - zMin) / (zMax - zMin);
            sub.verts.Add(new Vector3(x, stripAltitude, z));
            sub.uvs.Add(Vector3.Lerp(
                Vector3.Lerp(qUvs[i00], qUvs[i10], s),
                Vector3.Lerp(qUvs[i01], qUvs[i11], s), t));
            sub.colors.Add(Color32.Lerp(
                Color32.Lerp(qCols[i00], qCols[i10], s),
                Color32.Lerp(qCols[i01], qCols[i11], s), t));
        }

        /// <summary>Draws through clones forced into the measured over
        /// -terrain queue window. Never native materials: within one queue,
        /// non-depth-writing map shaders paint in effective submission order,
        /// and this layer draws after the section's things layer - native
        /// strips overpainted the sky rim wall's face (#132).</summary>
        public override void DrawLayer()
        {
            if (!Visible)
            {
                return;
            }
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sub = subs[i];
                if (sub.finalized && !sub.disabled)
                {
                    LevelRenderer.DrawWallRevealSubMesh(sub);
                }
            }
        }
    }
}
