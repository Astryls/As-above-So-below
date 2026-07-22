using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The slab-edge wall facade (round 17): where a slab cell (rooftop or
    /// built sky floor) sits over a supporting wall with open air directly
    /// south, the wall's own sprite appears as a sliver protruding past the
    /// slab edge - the REAL wall texture in place of the skirt's plain dark
    /// face, so a sky wall on the rim continues into the below wall's top
    /// edge as one continuous tall wall. The skirt still draws beneath at
    /// its lower queue, covering spans where nothing printable stands below
    /// (doorways, plain roof edges).
    ///
    /// GEOMETRY IS CLIPPED TO THE SLIVER AT PRINT TIME (#133 redesign). The
    /// first design printed the whole wall shifted south and relied on the
    /// sky terrain to cover the part overlapping the rim cell - unsound: in
    /// 1.6 the terrain families render in queues ABOVE the things atlas
    /// (the depth buffer, not painter order, keeps floors under walls), so
    /// the facade's BelowQueueCeiling-55 clone drew AFTER sky walls and
    /// painted the ground wall over their faces (user bisection, run #133;
    /// the reveal layer survives only because its queue is hard-clamped
    /// under Cutout). Clipping makes ordering irrelevant: the facade ships
    /// ONLY the geometry south of the slab edge - per qualifying cell the
    /// print-space keep-rect is [x, x+1] x [z - 1, z + shift], and every
    /// emitted vertex is pre-shifted south by the depth offset, so the
    /// final mesh spans exactly the sliver plus the wall's natural drape
    /// fringe and can never overlap a rim cell. The shift is baked into
    /// verts, so the depth slider triggers a reprint (settings hook), not a
    /// draw-time matrix.
    ///
    /// The clip machinery mirrors the verified reveal layer's (kept separate
    /// while round 17 stabilizes; unify afterward): PrintPlane quads only,
    /// corner-matched bilinear uv/color remap (90-degree rotations, atlas
    /// sub-rects, the north-vert altitude bias all preserved - altitude IS
    /// kept here, unlike the reveal, so the wall's own piece layering
    /// survives), shadow-material and malformed submeshes dropped whole.
    /// Kill switch: Rendering.
    /// </summary>
    public class SectionLayer_ABWallFacade : SectionLayer
    {
        public SectionLayer_ABWallFacade(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)ABDefOf.AB_BelowThings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        /// <summary>RETIRED by the height-language rework (2026-07-22): this
        /// layer drew the below wall's face as a sliver SOUTH of the rim line
        /// (on the air side, descending), which reads as a pit wall. The
        /// ascending SectionLayer_ABRimFacade plus the wall-top reveal tell
        /// the height story from the slab's side instead. Class kept for its
        /// verified clip machinery and the round-17 history above.</summary>
        public override bool Visible => false;

        private const float Eps = 0.001f;

        /// <summary>How far south of the wall cell the print may reach in the
        /// kept region: the drape/corner-filler fringe hangs below the cell
        /// and reads as the wall's natural bottom edge over the shifted
        /// ground. Anything past one full cell is not wall content.</summary>
        private const float DrapeAllowance = 1f;

        private readonly List<int> vertsBefore = new List<int>();
        private readonly List<int> trisBefore = new List<int>();

        /// <summary>Print-space keep-rects, packed as (x0, z0, x1, z1).</summary>
        private readonly List<Vector4> clipRects = new List<Vector4>();

        private readonly List<Vector3> qVerts = new List<Vector3>();
        private readonly List<Vector3> qUvs = new List<Vector3>();
        private readonly List<Color32> qCols = new List<Color32>();

        /// <summary>South shift baked into every emitted vertex this rebuild.</summary>
        private float shiftZ;

        public override void Regenerate()
        {
            // Retired (see Visible): never build geometry.
            ClearSubMeshes(MeshParts.All);
        }

        private void Regenerate_Retired()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering) || map.Level() != 1)
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
                TerrainDef cap = ABDefOf.AB_MountainTop;
                FogGrid lowerFog = lower.fogGrid;
                shiftZ = Mathf.Max(
                    // Retired path: the depth-shift setting is gone; the old
                    // default is pinned so this reference code still compiles.
                    0.25f,
                    LevelRenderer.SkirtLedgeWidth);
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(lower) || !IsSouthRimCell(map, skyTerrain, c, air, cap))
                    {
                        continue;
                    }
                    Building ed = lower.edificeGrid[c];
                    if (!ABRimPrint.QualifiesAsSupport(ed)
                        || (!ed.def.seeThroughFog && lowerFog.IsFogged(ed.Position)))
                    {
                        continue;
                    }
                    if (!IsFirstQualifyingCell(map, lower, skyTerrain, ed, c, air, cap))
                    {
                        continue;
                    }
                    GatherSliverRects(map, lower, skyTerrain, ed, air, cap);
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
                        Log.WarningOnce(ABLog.Tag + " Facade print failed for " + ed.LabelCap
                            + ": " + e.Message, ed.thingIDNumber ^ 762195851);
                    }
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "wall facade layer");
            }
        }

        /// <summary>Slab cell (anything but open air and mountain cap - the
        /// skirt's own solidity rule, so facade and skirt always agree on
        /// where the edge is) whose south sky neighbor is open air.</summary>
        private static bool IsSouthRimCell(Map sky, TerrainGrid grid, IntVec3 c,
            TerrainDef air, TerrainDef cap)
        {
            if (!c.InBounds(sky))
            {
                return false;
            }
            TerrainDef t = grid.TerrainAt(c);
            if (t == null || t == air || t == cap)
            {
                return false;
            }
            IntVec3 s = c + IntVec3.South;
            return s.InBounds(sky) && grid.TerrainAt(s) == air;
        }

        private static bool IsFirstQualifyingCell(Map sky, Map lower, TerrainGrid grid,
            Building ed, IntVec3 c, TerrainDef air, TerrainDef cap)
        {
            CellRect rect = ed.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (q.InBounds(lower) && IsSouthRimCell(sky, grid, q, air, cap))
                    {
                        return q.x == c.x && q.z == c.z;
                    }
                }
            }
            return false;
        }

        /// <summary>One keep-rect per qualifying (south-rim) cell the edifice
        /// occupies: [x, x+1] x [z - DrapeAllowance, z + shift] in PRINT
        /// space. After the bake-shift, the kept geometry's top edge lands
        /// exactly on the slab boundary - zero rim-cell overlap.</summary>
        private void GatherSliverRects(Map sky, Map lower, TerrainGrid grid, Building ed,
            TerrainDef air, TerrainDef cap)
        {
            clipRects.Clear();
            CellRect rect = ed.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (q.InBounds(lower) && IsSouthRimCell(sky, grid, q, air, cap))
                    {
                        clipRects.Add(new Vector4(x, z - DrapeAllowance, x + 1f, z + shiftZ));
                    }
                }
            }
        }

        /// <summary>Rewrites everything the print just appended as clipped,
        /// pre-shifted quads. Returns true when any geometry survived.</summary>
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
                // Shadow volumes never belong here (solid black when drawn as
                // plain geometry); PrintPlane structure only for the rest.
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
                for (int r = 0; r < clipRects.Count; r++)
                {
                    Vector4 rect = clipRects[r];
                    if (xMin >= rect.x - Eps && zMin >= rect.y - Eps
                        && xMax <= rect.z + Eps && zMax <= rect.w + Eps)
                    {
                        CopyQuadShifted(sub, b);
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
                int vi = sub.verts.Count;
                AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx0, cz0);
                AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx0, cz1);
                AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx1, cz1);
                AddClippedVert(sub, i00, i01, i11, i10, xMin, xMax, zMin, zMax, cx1, cz0);
                AddQuadTris(sub, vi);
                emitted = true;
            }
            return emitted;
        }

        private void CopyQuadShifted(LayerSubMesh sub, int b)
        {
            int vi = sub.verts.Count;
            for (int k = 0; k < 4; k++)
            {
                Vector3 v = qVerts[b + k];
                sub.verts.Add(new Vector3(v.x, v.y, v.z - shiftZ));
                sub.uvs.Add(qUvs[b + k]);
                sub.colors.Add(qCols[b + k]);
            }
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

        /// <summary>Bilinear sample of the source quad's altitude, uv, and
        /// color at one clipped corner. Altitude IS preserved here (unlike
        /// the reveal): the wall's own piece layering rides on it. The south
        /// shift is baked into z.</summary>
        private void AddClippedVert(LayerSubMesh sub, int i00, int i01, int i11, int i10,
            float xMin, float xMax, float zMin, float zMax, float x, float z)
        {
            float s = (x - xMin) / (xMax - xMin);
            float t = (z - zMin) / (zMax - zMin);
            float y = Mathf.Lerp(
                Mathf.Lerp(qVerts[i00].y, qVerts[i10].y, s),
                Mathf.Lerp(qVerts[i01].y, qVerts[i11].y, s), t);
            sub.verts.Add(new Vector3(x, y, z - shiftZ));
            sub.uvs.Add(Vector3.Lerp(
                Vector3.Lerp(qUvs[i00], qUvs[i10], s),
                Vector3.Lerp(qUvs[i01], qUvs[i11], s), t));
            sub.colors.Add(Color32.Lerp(
                Color32.Lerp(qCols[i00], qCols[i10], s),
                Color32.Lerp(qCols[i01], qCols[i11], s), t));
        }

        /// <summary>Draws through queue clones (BelowQueueCeiling - 55: above
        /// the skirt and the below band). Ordering against sky content no
        /// longer matters - the mesh never overlaps a rim cell.</summary>
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
                    LevelRenderer.DrawWallFacadeSubMesh(sub);
                }
            }
        }
    }
}
