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
        private static Material capMatOverWalls;

        private static void EnsureMats()
        {
            if (capMat != null)
            {
                return;
            }
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
            int low = Mathf.Clamp(Mathf.Max(terrain, shadow) + 1, terrain + 1, cutout - 1);
            // The pooled solid-color material is shared; clone before touching
            // queues. The over-walls variant sits just above the cutout family
            // (walls, their overhang decals) but below items, filth, and pawns
            // (transparent queues).
            capMat = new Material(solid) { renderQueue = low };
            capMatOverWalls = new Material(solid) { renderQueue = cutout + 1 };
        }

        public SectionLayer_ABMountainCap(Section section) : base(section)
        {
            // Buildings flag included: mining a wall or placing a torch changes
            // which decal-cover strips this section needs.
            relevantChangeTypes = MapMeshFlagDefOf.Terrain | MapMeshFlagDefOf.Buildings;
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
                EnsureMats();
                TerrainGrid grid = map.terrainGrid;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor();
                LayerSubMesh baseSub = null;
                LayerSubMesh stripSub = null;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (grid.TerrainAt(c) != cap)
                    {
                        continue;
                    }
                    if (baseSub == null)
                    {
                        baseSub = GetSubMesh(capMat);
                    }
                    AddQuad(baseSub, c.x, c.z, c.x + 1, c.z + 1, y);
                    // Decal-cover pass: wall sprites drape a wavy overhang skirt
                    // about a third of a cell into adjacent floor, drawn above
                    // the low cap and lit by nearby glow - it reads as a warm
                    // band breaking the flat sheet (playtest round 8). Cover the
                    // wall-facing rim of open cap cells with strips just above
                    // the cutout queue. Cells holding any edifice (torch,
                    // furniture, the wall itself) are skipped so their sprites
                    // are never clipped.
                    if (c.GetEdifice(map) != null)
                    {
                        continue;
                    }
                    bool n = RockAt(map, c + IntVec3.North);
                    bool s = RockAt(map, c + IntVec3.South);
                    bool e = RockAt(map, c + IntVec3.East);
                    bool w = RockAt(map, c + IntVec3.West);
                    if (n)
                    {
                        AddStrip(ref stripSub, c.x, c.z + 1f - StripWidth, c.x + 1, c.z + 1, y);
                    }
                    if (s)
                    {
                        AddStrip(ref stripSub, c.x, c.z, c.x + 1, c.z + StripWidth, y);
                    }
                    if (e)
                    {
                        AddStrip(ref stripSub, c.x + 1f - StripWidth, c.z, c.x + 1, c.z + 1, y);
                    }
                    if (w)
                    {
                        AddStrip(ref stripSub, c.x, c.z, c.x + StripWidth, c.z + 1, y);
                    }
                    // Corner nubs from diagonal-only rock neighbors.
                    if (!n && !e && RockAt(map, c + IntVec3.North + IntVec3.East))
                    {
                        AddStrip(ref stripSub, c.x + 1f - StripWidth, c.z + 1f - StripWidth, c.x + 1, c.z + 1, y);
                    }
                    if (!n && !w && RockAt(map, c + IntVec3.North + IntVec3.West))
                    {
                        AddStrip(ref stripSub, c.x, c.z + 1f - StripWidth, c.x + StripWidth, c.z + 1, y);
                    }
                    if (!s && !e && RockAt(map, c + IntVec3.South + IntVec3.East))
                    {
                        AddStrip(ref stripSub, c.x + 1f - StripWidth, c.z, c.x + 1, c.z + StripWidth, y);
                    }
                    if (!s && !w && RockAt(map, c + IntVec3.South + IntVec3.West))
                    {
                        AddStrip(ref stripSub, c.x, c.z, c.x + StripWidth, c.z + StripWidth, y);
                    }
                }
                if (baseSub != null || stripSub != null)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "mountain cap layer");
            }
        }

        private const float StripWidth = 0.4f;

        private void AddStrip(ref LayerSubMesh sub, float x0, float z0, float x1, float z1, float y)
        {
            if (sub == null)
            {
                sub = GetSubMesh(capMatOverWalls);
            }
            AddQuad(sub, x0, z0, x1, z1, y);
        }

        private static void AddQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y, z1));
            sub.verts.Add(new Vector3(x1, y, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }

        private static bool RockAt(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return true;
            }
            Building ed = c.GetEdifice(map);
            return ed != null
                && (ed.def.mineable || (ed.def.building != null && ed.def.building.isNaturalRock));
        }
    }
}
