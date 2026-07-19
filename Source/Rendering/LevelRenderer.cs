using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// See-below rendering for the sky level. Draws the lower map's own cached
    /// section meshes (terrain, buildings, items, snow) shifted down in altitude
    /// so they sort under everything on the sky map, adds a translucent dim quad,
    /// and re-runs the lower map's dynamic drawing (pawns, projectiles) with a
    /// global draw offset so life below stays live. Open air cells on the sky
    /// map use dontRender terrain, so the world below shows through the holes.
    /// Every entry point is kill-switched via ABGuard.Rendering.
    /// </summary>
    public static class LevelRenderer
    {
        /// <summary>Altitude shift for below content. Keeps it under the sky map's
        /// terrain (y=0) but above the camera far plane at any zoom.</summary>
        public const float BelowOffset = -2.5f;

        /// <summary>Submeshes whose bounds sit above this are skipped (silhouettes,
        /// misc overlays), except lighting and fog which are relocated below.</summary>
        private const float MaxSubMeshAltitude = 2f;

        /// <summary>The lower map's lighting overlay is re-seated just under the sky
        /// map's plane so the view below shows real time-of-day light; its shared
        /// material color is updated per frame by the current map's SkyManager, so
        /// day/night stays in sync for free. Fog of war sits just above it so
        /// unexplored surface stays hidden from above.</summary>
        private const float LightingRelocateY = -0.15f;

        private const float FogRelocateY = -0.10f;

        private const float DimQuadAltitude = -0.05f;

        /// <summary>True only while the lower map's dynamic draw runs; DrawPos
        /// postfixes read it. Volatile because pre-draw can use worker threads.</summary>
        public static volatile bool OffsetActive;

        private static readonly Matrix4x4 OffsetMatrix = Matrix4x4.Translate(new Vector3(0f, BelowOffset, 0f));

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> LayersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> SectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        private static Material dimMat;
        private static float dimMatAlpha = -1f;

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
                SendDiagnosticOnce(map, lower);
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
                DrawDimQuad(view);
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
                        float relocateY = float.NaN;
                        if (layer is SectionLayer_LightingOverlay)
                        {
                            relocateY = LightingRelocateY;
                        }
                        else if (layer is SectionLayer_FogOfWar)
                        {
                            relocateY = FogRelocateY;
                        }
                        List<LayerSubMesh> subs = layer.subMeshes;
                        for (int j = 0; j < subs.Count; j++)
                        {
                            LayerSubMesh sub = subs[j];
                            if (!sub.finalized || sub.disabled)
                            {
                                continue;
                            }
                            if (!float.IsNaN(relocateY))
                            {
                                float y = relocateY - sub.mesh.bounds.center.y;
                                Graphics.DrawMesh(sub.mesh, Matrix4x4.Translate(new Vector3(0f, y, 0f)), sub.material, 0);
                            }
                            else if (sub.mesh.bounds.center.y <= MaxSubMeshAltitude)
                            {
                                Graphics.DrawMesh(sub.mesh, OffsetMatrix, sub.material, 0);
                            }
                        }
                    }
                }
            }
        }

        private static void DrawDimQuad(CellRect view)
        {
            float alpha = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.85f);
            if (alpha < 0.01f)
            {
                return;
            }
            if (dimMat == null || Mathf.Abs(alpha - dimMatAlpha) > 0.001f)
            {
                dimMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0f, 0f, 0f, alpha));
                dimMatAlpha = alpha;
            }
            Vector3 center = view.CenterVector3;
            center.y = DimQuadAltitude;
            Matrix4x4 m = Matrix4x4.TRS(center, Quaternion.identity, new Vector3(view.Width + 2f, 1f, view.Height + 2f));
            Graphics.DrawMesh(MeshPool.plane10, m, dimMat, 0);
        }

        private static bool diagnosticSent;

        /// <summary>Temporary instrumentation: one warning per launch with the sky
        /// map's lighting numbers, delivered through the log so a bad value is
        /// visible without guesswork. Remove after the lighting fix is verified.</summary>
        private static void SendDiagnosticOnce(Map sky, Map lower)
        {
            if (diagnosticSent)
            {
                return;
            }
            diagnosticSent = true;
            try
            {
                Log.Warning(ABLog.Tag + " [diagnostic] sky map " + sky.uniqueID
                    + " skyGlow=" + sky.skyManager.CurSkyGlow.ToString("F2")
                    + " celestial=" + RimWorld.GenCelestial.CurCelestialSunGlow(sky).ToString("F2")
                    + " weather=" + (sky.weatherManager.curWeather != null ? sky.weatherManager.curWeather.defName : "null")
                    + " lowerSkyGlow=" + lower.skyManager.CurSkyGlow.ToString("F2")
                    + " biome=" + (sky.Biome != null ? sky.Biome.defName : "null")
                    + " tileValid=" + sky.Tile.Valid);
            }
            catch (Exception e)
            {
                ABLog.Dev("Diagnostic failed: " + e.Message);
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
