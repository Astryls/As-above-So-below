using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Draws one unbroken flat quad over every mountain-cap cell on the sky
    /// level, in a render queue above terrain, terrain edges, and ambient edge
    /// shadows but below walls, edge strips, filth, and pawns. The cap TERRAIN
    /// alone cannot deliver the "one solid fog-colored layer" look: vanilla
    /// paints edge shadow gradients and Underwall edge transitions over floor
    /// cells, which read as gaps and halos against a flat color (playtest
    /// round 7; Z-Levels beta likewise composited dedicated layers instead of
    /// relying on terrain). Queue bounds are read from live materials at
    /// runtime, never assumed. Quads under unmined rock cost nothing visually
    /// and make mining reveal the cap with no regeneration. Kill switch:
    /// Rendering; regenerates only on terrain changes.
    /// </summary>
    public class SectionLayer_ABMountainCap : SectionLayer
    {
        /// <summary>Single tuning point for the cap tone, matched to vanilla
        /// fog of war during playtest.</summary>
        private static readonly Color CapColor = new Color32(30, 28, 26, byte.MaxValue);

        private static Material capMat;

        private static Material CapMat
        {
            get
            {
                if (capMat == null)
                {
                    Material solid = SolidColorMaterials.SimpleSolidColorMaterial(CapColor);
                    int terrain = 2000;
                    Material soil = TerrainDefOf.Soil?.graphic?.MatSingle;
                    if (soil != null)
                    {
                        terrain = soil.renderQueue > 0 ? soil.renderQueue
                            : (soil.shader != null ? soil.shader.renderQueue : 2000);
                    }
                    int shadow = MatBases.EdgeShadow != null ? MatBases.EdgeShadow.renderQueue : terrain;
                    int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : terrain + 450;
                    int queue = Mathf.Clamp(Mathf.Max(terrain, shadow) + 1, terrain + 1, cutout - 1);
                    // The pooled solid-color material is shared; clone before
                    // touching its queue.
                    capMat = new Material(solid) { renderQueue = queue };
                }
                return capMat;
            }
        }

        public SectionLayer_ABMountainCap(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering);

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering) || map.Level() != 1)
            {
                return;
            }
            try
            {
                TerrainGrid grid = map.terrainGrid;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor();
                LayerSubMesh sub = null;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (grid.TerrainAt(c) != cap)
                    {
                        continue;
                    }
                    if (sub == null)
                    {
                        sub = GetSubMesh(CapMat);
                    }
                    int vi = sub.verts.Count;
                    sub.verts.Add(new Vector3(c.x, y, c.z));
                    sub.verts.Add(new Vector3(c.x, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z));
                    sub.tris.Add(vi);
                    sub.tris.Add(vi + 1);
                    sub.tris.Add(vi + 2);
                    sub.tris.Add(vi);
                    sub.tris.Add(vi + 2);
                    sub.tris.Add(vi + 3);
                }
                if (sub != null)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "mountain cap layer");
            }
        }
    }
}
