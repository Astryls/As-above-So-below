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

        /// <summary>Cells of padding around the view so panning does not force a
        /// rebuild every frame; rebuilds happen when the camera escapes the pad.</summary>
        private const int MaskPadCells = 8;

        /// <summary>Night opacity cap. High enough to sell darkness, low enough that
        /// the ground below stays readable, like Z-Levels beta.</summary>
        private const float MaskMaxDarkness = 0.62f;

        /// <summary>True only while the lower map's dynamic draw runs; DrawPos
        /// postfixes read it. Volatile because pre-draw can use worker threads.</summary>
        public static volatile bool OffsetActive;

        private static readonly Matrix4x4 OffsetMatrix = Matrix4x4.Translate(new Vector3(0f, BelowOffset, 0f));

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> LayersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> SectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        /// <summary>Exact layer types the below-view copies: world CONTENT only.
        /// Everything else (fog, darkness, lighting, plans, the vanilla power grid
        /// overlay, DBH and VEF pipe overlays, any future mod overlay) is excluded
        /// by construction, so overlays only ever render on the level being viewed.
        /// Exact types, not IsAssignableFrom: the power overlay subclasses the
        /// things layer and would slip through an inheritance check.</summary>
        private static readonly HashSet<Type> ContentLayerTypes = BuildContentLayerTypes();

        private static HashSet<Type> BuildContentLayerTypes()
        {
            HashSet<Type> set = new HashSet<Type>
            {
                typeof(SectionLayer_Terrain),
                typeof(SectionLayer_ThingsGeneral),
                typeof(SectionLayer_BuildingsDamage),
                typeof(SectionLayer_Snow),
                typeof(SectionLayer_Gas),
                typeof(SectionLayer_PollutionCloud),
                typeof(SectionLayer_EdgeShadows)
            };
            AddByName(set, "Verse.SectionLayer_SunShadows");
            // Version or DLC dependent layers, added when present.
            AddByName(set, "Verse.SectionLayer_Sand");
            AddByName(set, "RimWorld.SectionLayer_TerrainEdges");
            AddByName(set, "Verse.SectionLayer_TerrainScatter");
            AddByName(set, "RimWorld.SectionLayer_BridgeProps");
            return set;
        }

        private static void AddByName(HashSet<Type> set, string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type != null)
            {
                set.Add(type);
            }
        }

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
                        if (!ContentLayerTypes.Contains(layer.GetType()) || !layer.Visible)
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
            bool viewContained = maskMesh != null
                && maskLastRect.Contains(new IntVec3(view.minX, 0, view.minZ))
                && maskLastRect.Contains(new IntVec3(view.maxX, 0, view.maxZ));
            if (!viewContained || frame - maskLastFrame >= MaskRebuildIntervalFrames
                || lower.uniqueID != maskLastLowerId)
            {
                CellRect buildRect = view.ExpandedBy(MaskPadCells).ClipInsideMap(sky);
                RebuildMask(sky, lower, buildRect);
                maskLastFrame = frame;
                maskLastRect = buildRect;
                maskLastLowerId = lower.uniqueID;
            }
            if (maskMesh != null && maskMesh.vertexCount > 0)
            {
                Graphics.DrawMesh(maskMesh, Matrix4x4.identity, maskMat, 0);
            }
        }

        private static int maskLastStep = 1;

        private static void RebuildMask(Map sky, Map lower, CellRect rect)
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
            // Resolution switches use hysteresis so zooming near the threshold does
            // not pop between block sizes.
            int step = maskLastStep;
            if (step == 1 && rect.Width > 150)
            {
                step = 2;
            }
            else if (step == 2 && rect.Width < 125)
            {
                step = 1;
            }
            maskLastStep = step;
            // Anchor the sampling grid to world coordinates so blocks stay put
            // while the camera pans; a view-anchored grid shifts a cell whenever
            // the view edge parity flips, which reads as jitter.
            int startX = rect.minX - (((rect.minX % step) + step) % step);
            int startZ = rect.minZ - (((rect.minZ % step) + step) % step);
            int sizeX = sky.Size.x;
            int sizeZ = sky.Size.z;
            for (int x = startX; x <= rect.maxX; x += step)
            {
                for (int z = startZ; z <= rect.maxZ; z += step)
                {
                    int cx = Mathf.Clamp(x, 0, sizeX - 1);
                    int cz = Mathf.Clamp(z, 0, sizeZ - 1);
                    IntVec3 c = new IntVec3(cx, 0, cz);
                    if (skyTerrain.TerrainAt(c) != air)
                    {
                        continue;
                    }
                    // Artificial glow from the lower map, sky light from the
                    // current map (identical tile, updated every frame).
                    float artificial = lowerGlow.GroundGlowAt(c, ignoreCavePlants: false, ignoreSky: true);
                    float light = lowerRoofs.Roofed(c) ? artificial : Mathf.Max(skyGlowNow, artificial);
                    byte a = (byte)(255f * Mathf.Clamp01(baseDim + (1f - light) * (MaskMaxDarkness - baseDim)));
                    int vi = maskVerts.Count;
                    float x0 = Mathf.Max(x, 0);
                    float z0 = Mathf.Max(z, 0);
                    float x1 = Mathf.Min(x + step, sizeX);
                    float z1 = Mathf.Min(z + step, sizeZ);
                    if (x1 <= x0 || z1 <= z0)
                    {
                        continue;
                    }
                    maskVerts.Add(new Vector3(x0, MaskAltitude, z0));
                    maskVerts.Add(new Vector3(x0, MaskAltitude, z1));
                    maskVerts.Add(new Vector3(x1, MaskAltitude, z1));
                    maskVerts.Add(new Vector3(x1, MaskAltitude, z0));
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
