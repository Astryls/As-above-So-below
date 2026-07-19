using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// See-below rendering for the sky level. Draws the lower map's own cached
    /// section meshes (terrain, buildings, items, snow) shifted down in altitude so
    /// they sort under everything on the sky map, re-runs the lower map's dynamic
    /// drawing with a global draw offset for live pawns and projectiles, and covers
    /// ONLY the open-air cells with a custom mask mesh that encodes, per cell, the
    /// surface's fog of war (opaque: unexplored stays hidden) and real per-cell
    /// light (dark at night, lamp glow visible). Because the mask geometry exists
    /// only over air cells it can never cover the sky level's own rock, floors, or
    /// buildings, regardless of shader depth behavior. Vanilla overlay meshes are
    /// deliberately NOT drawn into the below-view for exactly that reason.
    /// Every entry point is kill-switched via ABGuard.Rendering.
    /// </summary>
    public static class LevelRenderer
    {
        /// <summary>Altitude shift for below content. Keeps it under the sky map's
        /// terrain (y=0) but above the camera far plane at any zoom.</summary>
        public const float BelowOffset = -2.5f;

        /// <summary>Submeshes whose bounds sit above this are skipped (fog of war,
        /// lighting, silhouettes, overlays); the mask mesh replaces them.</summary>
        private const float MaxSubMeshAltitude = 2f;

        private const float MaskAltitude = -0.10f;

        private const int MaskRebuildIntervalFrames = 15;

        /// <summary>True only while the lower map's dynamic draw runs; DrawPos
        /// postfixes read it. Volatile because pre-draw can use worker threads.</summary>
        public static volatile bool OffsetActive;

        private static readonly Matrix4x4 OffsetMatrix = Matrix4x4.Translate(new Vector3(0f, BelowOffset, 0f));

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> LayersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> SectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static Mesh maskMesh;
        private static Material maskMat;
        private static int maskLastFrame = -999;
        private static CellRect maskLastRect;
        private static int maskLastLowerId = -1;
        private static readonly List<Vector3> maskVerts = new List<Vector3>();
        private static readonly List<int> maskTris = new List<int>();
        private static readonly List<Color32> maskColors = new List<Color32>();

        public static void DrawBelowStatic(Map map)
        {
            if (!ABGuard.On(ABGuard.Rendering) || map == null || map != Find.CurrentMap)
            {
                return;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level <= 0)
            {
                return;
            }
            Map lower = comp.lowerMap;
            if (lower == null || lower.Disposed)
            {
                return;
            }
            try
            {
                // Vanilla's far clip plane is 65.5 while the camera rises to y=65 at
                // full zoom out, which would clip our below content at y=-2.5. Keep
                // enough depth budget; idempotent in case something resets it.
                Camera cam = Find.Camera;
                if (cam != null && cam.farClipPlane < 70f)
                {
                    cam.farClipPlane = 70f;
                }
                // Process the lower map's dirty sections so the view below stays live.
                lower.mapDrawer.MapMeshDrawerUpdate_First();
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(lower);
                DrawSections(lower, view);
                DrawBelowMask(map, lower, view);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "see-below rendering");
            }
        }

        private static void DrawSections(Map lower, CellRect view)
        {
            Section[,] sections = SectionsRef(lower.mapDrawer);
            int maxSX = sections.GetUpperBound(0);
            int maxSZ = sections.GetUpperBound(1);
            int minX = Mathf.Max(0, view.minX / 17);
            int minZ = Mathf.Max(0, view.minZ / 17);
            int maxX = Mathf.Min(maxSX, view.maxX / 17);
            int maxZ = Mathf.Min(maxSZ, view.maxZ / 17);
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    List<SectionLayer> layers = LayersRef(sections[x, z]);
                    for (int i = 0; i < layers.Count; i++)
                    {
                        SectionLayer layer = layers[i];
                        if (!layer.Visible)
                        {
                            continue;
                        }
                        List<LayerSubMesh> subs = layer.subMeshes;
                        for (int j = 0; j < subs.Count; j++)
                        {
                            LayerSubMesh sub = subs[j];
                            if (sub.finalized && !sub.disabled && sub.mesh.bounds.center.y <= MaxSubMeshAltitude)
                            {
                                Graphics.DrawMesh(sub.mesh, OffsetMatrix, sub.material, 0);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>Air-cells-only mask carrying darkness from the surface's per-cell
        /// light plus the user's base dim. The surface's fog of war is deliberately
        /// NOT inherited: looking down from the sky level reveals sealed areas the
        /// same way Z-Levels beta did; surface pawns' own knowledge is unaffected.
        /// Sky light comes from the CURRENT map's sky manager because inactive maps
        /// do not update theirs.</summary>
        private static void DrawBelowMask(Map sky, Map lower, CellRect view)
        {
            if (maskMat == null)
            {
                maskMat = new Material(MatBases.FogOfWar)
                {
                    mainTexture = BaseContent.WhiteTex,
                    color = Color.white
                };
            }
            int frame = Time.frameCount;
            if (maskMesh == null || frame - maskLastFrame >= MaskRebuildIntervalFrames
                || view != maskLastRect || lower.uniqueID != maskLastLowerId)
            {
                RebuildMask(sky, lower, view);
                maskLastFrame = frame;
                maskLastRect = view;
                maskLastLowerId = lower.uniqueID;
            }
            if (maskMesh != null && maskMesh.vertexCount > 0)
            {
                Graphics.DrawMesh(maskMesh, Matrix4x4.identity, maskMat, 0);
            }
        }

        private static void RebuildMask(Map sky, Map lower, CellRect view)
        {
            if (maskMesh == null)
            {
                maskMesh = new Mesh
                {
                    name = "AB_BelowMask",
                    indexFormat = IndexFormat.UInt32
                };
            }
            maskVerts.Clear();
            maskTris.Clear();
            maskColors.Clear();
            float baseDim = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.6f);
            float skyGlowNow = sky.skyManager.CurSkyGlow;
            TerrainGrid skyTerrain = sky.terrainGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;
            GlowGrid lowerGlow = lower.glowGrid;
            RoofGrid lowerRoofs = lower.roofGrid;
            int step = view.Width > 130 ? 2 : 1;
            for (int x = view.minX; x <= view.maxX; x += step)
            {
                for (int z = view.minZ; z <= view.maxZ; z += step)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!c.InBounds(sky) || skyTerrain.TerrainAt(c) != air)
                    {
                        continue;
                    }
                    // Artificial glow from the lower map, sky light from the
                    // current map (identical tile, updated every frame).
                    float artificial = lowerGlow.GroundGlowAt(c, ignoreCavePlants: false, ignoreSky: true);
                    float light = lowerRoofs.Roofed(c) ? artificial : Mathf.Max(skyGlowNow, artificial);
                    byte a = (byte)(255f * Mathf.Clamp01(baseDim + (1f - light) * (0.82f - baseDim)));
                    int vi = maskVerts.Count;
                    float x1 = Mathf.Min(x + step, view.maxX + 1);
                    float z1 = Mathf.Min(z + step, view.maxZ + 1);
                    maskVerts.Add(new Vector3(x, MaskAltitude, z));
                    maskVerts.Add(new Vector3(x, MaskAltitude, z1));
                    maskVerts.Add(new Vector3(x1, MaskAltitude, z1));
                    maskVerts.Add(new Vector3(x1, MaskAltitude, z));
                    Color32 col = new Color32(0, 0, 0, a);
                    maskColors.Add(col);
                    maskColors.Add(col);
                    maskColors.Add(col);
                    maskColors.Add(col);
                    maskTris.Add(vi);
                    maskTris.Add(vi + 1);
                    maskTris.Add(vi + 2);
                    maskTris.Add(vi);
                    maskTris.Add(vi + 2);
                    maskTris.Add(vi + 3);
                }
            }
            maskMesh.Clear();
            if (maskVerts.Count > 0)
            {
                maskMesh.SetVertices(maskVerts);
                maskMesh.SetColors(maskColors);
                maskMesh.SetTriangles(maskTris, 0);
                maskMesh.RecalculateBounds();
            }
        }

        public static void DrawBelowDynamic(Map map)
        {
            if (!ABGuard.On(ABGuard.Rendering) || map == null || map != Find.CurrentMap)
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.showLiveBelow)
            {
                return;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level <= 0)
            {
                return;
            }
            Map lower = comp.lowerMap;
            if (lower == null || lower.Disposed)
            {
                return;
            }
            try
            {
                OffsetActive = true;
                lower.dynamicDrawManager.DrawDynamicThings();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "see-below dynamic rendering");
            }
            finally
            {
                OffsetActive = false;
            }
        }
    }
}
