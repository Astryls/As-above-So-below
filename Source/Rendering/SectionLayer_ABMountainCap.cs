using System;
using System.Collections.Generic;
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
        private static Material capMat;
        private static Material capMatOverWalls;

        private static void EnsureMats()
        {
            if (capMat != null)
            {
                return;
            }
            // The cap draws with THE VANILLA FOG MATERIAL, cloned at our queues:
            // hand-picked colors could never exactly match the fog's rendered
            // tone, so the feathered boundary where real fog meets the cap
            // stayed visible (playtest round 9, answer C). Same material means
            // pixel-identical rendering and the seam dissolves; fully-fogged
            // vertex colors (white, alpha 255) match SectionLayer_FogOfWar's
            // covered verts exactly.
            Material solid = MatBases.FogOfWar;
            int terrain = 2000;
            Material soil = TerrainDefOf.Soil?.graphic?.MatSingle;
            if (soil != null)
            {
                terrain = soil.renderQueue > 0 ? soil.renderQueue
                    : (soil.shader != null ? soil.shader.renderQueue : 2000);
            }
            int shadow = MatBases.EdgeShadow != null ? MatBases.EdgeShadow.renderQueue : terrain;
            int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : terrain + 450;
            lowQueue = Mathf.Clamp(Mathf.Max(terrain, shadow) + 1, terrain + 1, cutout - 1);
            highQueue = cutout + 1;
            // Clones: the over-walls variant sits just above the cutout family
            // (walls, their overhang decals) but below items, filth, and pawns
            // (transparent queues).
            capMat = new Material(solid) { renderQueue = lowQueue };
            capMatOverWalls = new Material(solid) { renderQueue = highQueue };
        }

        private static int lowQueue;
        private static int highQueue;

        private static readonly Dictionary<TerrainDef, Material[]> minedMats =
            new Dictionary<TerrainDef, Material[]>();

        /// <summary>Low and high queue materials for a mined floor: a flat fill
        /// in the source rock's own wall-and-edge color.</summary>
        private static Material[] MinedMatsFor(TerrainDef leaveTerrain, Color rockColor)
        {
            if (minedMats.TryGetValue(leaveTerrain, out Material[] pair))
            {
                return pair;
            }
            Material solid = SolidColorMaterials.SimpleSolidColorMaterial(rockColor);
            pair = new[]
            {
                new Material(solid) { renderQueue = lowQueue },
                new Material(solid) { renderQueue = highQueue }
            };
            minedMats[leaveTerrain] = pair;
            return pair;
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
                bool emitted = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    TerrainDef t = grid.TerrainAt(c);
                    Material lowMat;
                    Material highMat;
                    if (t == cap)
                    {
                        // Unmined mountain top: the vanilla fog fill.
                        lowMat = capMat;
                        highMat = capMatOverWalls;
                    }
                    else if (LevelSync.TryGetMinedRockColor(t, out Color rockColor))
                    {
                        // Mined-out floor (vanilla leave-terrain kept as the
                        // marker): flat fill in the local rock's edge color, so
                        // tunnels read against the fog mass (playtest round 10).
                        Material[] pair = MinedMatsFor(t, rockColor);
                        lowMat = pair[0];
                        highMat = pair[1];
                    }
                    else
                    {
                        continue;
                    }
                    emitted = true;
                    LayerSubMesh baseSub = GetSubMesh(lowMat);
                    // Decal-cover pass: wall sprites drape a wavy overhang skirt
                    // into adjacent floor cells; the wall-facing rim draws just
                    // above the cutout queue to cover it. Tiles are emitted
                    // OVERLAP-FREE: stacking two translucent fog quads darkens
                    // the rim by one extra blend and reads as faint 1x1 cell
                    // edges (playtest round 10, north-facing rows). Cells
                    // holding any edifice (torch, furniture, the wall itself)
                    // get a plain base quad so their sprites are never clipped.
                    if (c.GetEdifice(map) != null)
                    {
                        AddQuad(baseSub, c.x, c.z, c.x + 1, c.z + 1, y);
                        continue;
                    }
                    bool n = RockAt(map, c + IntVec3.North);
                    bool s = RockAt(map, c + IntVec3.South);
                    bool e = RockAt(map, c + IntVec3.East);
                    bool w = RockAt(map, c + IntVec3.West);
                    bool ne = RockAt(map, c + IntVec3.North + IntVec3.East);
                    bool nw = RockAt(map, c + IntVec3.North + IntVec3.West);
                    bool se = RockAt(map, c + IntVec3.South + IntVec3.East);
                    bool sw = RockAt(map, c + IntVec3.South + IntVec3.West);
                    if (!n && !s && !e && !w && !ne && !nw && !se && !sw)
                    {
                        AddQuad(baseSub, c.x, c.z, c.x + 1, c.z + 1, y);
                        continue;
                    }
                    // 3x3 tiling: rim tiles go high wherever any touching rock
                    // (cardinal or diagonal) can drape a decal; every pixel of
                    // the cell is covered by exactly one quad.
                    float x0 = c.x;
                    float z0 = c.z;
                    float x1 = c.x + 1f;
                    float z1 = c.z + 1f;
                    float xa = x0 + StripWidth;
                    float xb = x1 - StripWidth;
                    float za = z0 + StripWidth;
                    float zb = z1 - StripWidth;
                    EmitTile(highMat, baseSub, n || w || nw, x0, zb, xa, z1, y);
                    EmitTile(highMat, baseSub, n, xa, zb, xb, z1, y);
                    EmitTile(highMat, baseSub, n || e || ne, xb, zb, x1, z1, y);
                    EmitTile(highMat, baseSub, w, x0, za, xa, zb, y);
                    AddQuad(baseSub, xa, za, xb, zb, y);
                    EmitTile(highMat, baseSub, e, xb, za, x1, zb, y);
                    EmitTile(highMat, baseSub, s || w || sw, x0, z0, xa, za, y);
                    EmitTile(highMat, baseSub, s, xa, z0, xb, za, y);
                    EmitTile(highMat, baseSub, s || e || se, xb, z0, x1, za, y);
                }
                if (emitted)
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

        private void EmitTile(Material highMat, LayerSubMesh baseSub, bool high,
            float x0, float z0, float x1, float z1, float y)
        {
            AddQuad(high ? GetSubMesh(highMat) : baseSub, x0, z0, x1, z1, y);
        }

        private static readonly Color32 FoggedVert = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        private static void AddQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y, z1));
            sub.verts.Add(new Vector3(x1, y, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            sub.colors.Add(FoggedVert);
            sub.colors.Add(FoggedVert);
            sub.colors.Add(FoggedVert);
            sub.colors.Add(FoggedVert);
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
