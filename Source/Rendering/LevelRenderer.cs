using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
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
    /// surface's fog of war (opaque: unexplored stays hidden) plus a slight
    /// constant depth dim (slider). The sky map's own day-night darkening
    /// already reaches the below meshes through the shared shader globals, so
    /// the mask adds NO light-based shading of its own - stacking the two read
    /// as near-black nights (playtest regression). Because the mask geometry exists
    /// only over air cells it can never cover the sky level's own rock, floors, or
    /// buildings, regardless of shader depth behavior. Vanilla overlay meshes are
    /// deliberately NOT drawn into the below-view for exactly that reason.
    ///
    /// Render ordering: RimWorld's map shaders do not depth-write, so within one
    /// render queue the GPU paints by camera distance and the below copies (same
    /// materials, same queue as the sky map's own layers) used to OVERPAINT the
    /// sky's terrain - rooftop and rock terrain existed in the mesh but were
    /// invisible (Z-Levels beta solved the same problem with dedicated lower
    /// section layers). The below pass therefore draws through cloned materials
    /// forced into explicit low render queues (1000-1500, stepped to preserve
    /// the painter order between layers and by submesh altitude within the
    /// things layer), guaranteeing every sky-map material draws after - and
    /// therefore over - the view below.
    /// Every entry point is kill-switched via ABGuard.Rendering.
    /// </summary>
    public static class LevelRenderer
    {
        /// <summary>Altitude shift for below content. Keeps it under the sky map's
        /// terrain (y=0) but above the camera far plane at any zoom.</summary>
        public const float BelowOffset = -2.5f;

        /// <summary>Submeshes whose bounds sit above this are skipped (fog of war,
        /// lighting, silhouettes, overlays); the mask mesh replaces them. 1.6 real
        /// altitudes: content prints span 0..7 (LowPlant 4.02, Building 5.49, Item
        /// 6.59) and overlays start at 12+ (FogOfWar 12.07, Silhouettes 13.54), so
        /// the boundary sits at 10. The old value 2 assumed the AltInc scale and
        /// silently skipped every wall, plant, and item submesh.</summary>
        private const float MaxSubMeshAltitude = 10f;

        private const float MaskAltitude = -0.10f;

        private const int MaskRebuildIntervalFrames = 15;

        /// <summary>Cells of padding around the view so panning does not force a
        /// rebuild every frame; rebuilds happen when the camera escapes the pad.</summary>
        private const int MaskPadCells = 8;

        // Historical: a light-based night cap (0.62, then 0.45) lived here. It
        // double-darkened against the sky map's own natural night dimming and
        // was removed entirely after playtest feedback; only fog opacity and
        // the constant baseDim slider remain.

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
            // SectionLayer_Watergen is deliberately ABSENT: it is the water
            // depth-shading layer and only renders correctly through vanilla's
            // WaterDepth subcamera. Drawn flat in the below pass it painted an
            // opaque black slab over every river and lake (playtest round 12).
            // The terrain layer's own water submeshes render with the REAL
            // vanilla water shader instead: DrawBelowStatic pushes the lower
            // map's water shader globals (flow texture, map size) every frame,
            // which is safe because the sky map has no water of its own
            // (playtest round 13, the Z-Levels approach).
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

        /// <summary>Queue OFFSET per below layer type, subtracted from the
        /// runtime-derived ceiling. Flat ground layers pack at the bottom; the
        /// things layer gets headroom for per-submesh altitude stepping;
        /// weather-ish overlays draw last.</summary>
        private static readonly Dictionary<Type, int> BelowLayerOffsets = BuildBelowLayerOffsets();

        private const int BelowThingsOffset = 200;

        private const int BelowDefaultOffset = 150;

        private static Dictionary<Type, int> BuildBelowLayerOffsets()
        {
            Dictionary<Type, int> map = new Dictionary<Type, int>
            {
                { typeof(SectionLayer_Terrain), 300 },
                { typeof(SectionLayer_EdgeShadows), 260 },
                { typeof(SectionLayer_ThingsGeneral), BelowThingsOffset },
                { typeof(SectionLayer_BuildingsDamage), 95 },
                { typeof(SectionLayer_Snow), 90 },
                { typeof(SectionLayer_Gas), 85 },
                { typeof(SectionLayer_PollutionCloud), 80 }
            };

            AddOffsetByName(map, "Verse.SectionLayer_Sand", 285);
            AddOffsetByName(map, "RimWorld.SectionLayer_TerrainEdges", 280);
            AddOffsetByName(map, "Verse.SectionLayer_TerrainScatter", 275);
            AddOffsetByName(map, "Verse.SectionLayer_SunShadows", 255);
            AddOffsetByName(map, "RimWorld.SectionLayer_BridgeProps", 250);
            return map;
        }

        private static void AddOffsetByName(Dictionary<Type, int> map, string typeName, int offset)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type != null)
            {
                map[type] = offset;
            }
        }

        private static int belowQueueCeiling = -1;

        /// <summary>The LOWEST render queue any sky-map terrain material uses,
        /// read from real materials once at runtime. Sampling only Soil was the
        /// steel-tile bleed-through bug: vanilla terrain shader FAMILIES (Hard
        /// vs FadeRough vs the floor variants) do not share one queue, and our
        /// rooftop/mountain-top draw with the Hard family. If that family sits
        /// below Soil's, a ceiling measured from Soil alone parks part of the
        /// below band ABOVE the rooftop tiles, and lower-map grass, walls, and
        /// items paint over the roof (playtest 2026-07-20). The band must hang
        /// strictly under EVERY sky terrain material, so take the minimum over
        /// the families we actually stand on.</summary>
        private static int BelowQueueCeiling
        {
            get
            {
                if (belowQueueCeiling < 0)
                {
                    int min = int.MaxValue;
                    min = MinQueue(min, TerrainDefOf.Soil?.graphic?.MatSingle);
                    min = MinQueue(min, ABDefOf.AB_RoofSurface?.graphic?.MatSingle);
                    min = MinQueue(min, ABDefOf.AB_MountainTop?.graphic?.MatSingle);
                    min = MinQueue(min, TerrainDefOf.MetalTile?.graphic?.MatSingle);
                    min = MinQueue(min, TerrainDefOf.WoodPlankFloor?.graphic?.MatSingle);
                    if (ShaderDatabase.TerrainHard != null)
                    {
                        int q = ShaderDatabase.TerrainHard.renderQueue;
                        if (q >= 500)
                        {
                            min = Mathf.Min(min, q);
                        }
                    }
                    // Sanity floor: with anything implausible, fall back to the
                    // Unity geometry default so the band still sits under it.
                    belowQueueCeiling = min >= 500 && min != int.MaxValue ? min : 2000;
                }
                return belowQueueCeiling;
            }
        }

        private static int MinQueue(int current, Material m)
        {
            if (m == null)
            {
                return current;
            }
            int q = m.renderQueue;
            if (q <= 0 && m.shader != null)
            {
                q = m.shader.renderQueue;
            }
            return q >= 500 ? Mathf.Min(current, q) : current;
        }

        private static readonly Dictionary<(Material, int), Material> belowMats =
            new Dictionary<(Material, int), Material>();

        /// <summary>Cached clone of a section material at a forced render queue.
        /// Source materials are pooled and stable, so the cache stays small; the
        /// cap is a defensive bound only.</summary>
        private static Material BelowMaterialFor(Material source, int queue)
        {
            if (source == null)
            {
                return null;
            }
            (Material, int) key = (source, queue);
            if (belowMats.TryGetValue(key, out Material clone))
            {
                return clone;
            }
            if (belowMats.Count > 1024)
            {
                belowMats.Clear();
            }
            // Pure clone at a forced queue - water materials included: their
            // shader reads per-map globals that DrawBelowStatic re-points at
            // the lower map each frame, so vanilla water just works from above.
            clone = new Material(source) { renderQueue = queue };
            belowMats[key] = clone;
            return clone;
        }

        private static Mesh maskMesh;
        private static Material maskMat;
        private static int maskLastFrame = -999;
        private static CellRect maskLastRect;
        private static int maskLastLowerId = -1;
        private static readonly List<Vector3> maskVerts = new List<Vector3>();
        private static readonly List<int> maskTris = new List<int>();
        private static readonly List<Color32> maskColors = new List<Color32>();

        // Async mask lane. The worker reads live terrain and fog grids - object
        // reference and bool reads are atomic, and a torn read only mis-dims a
        // cell until the next rebuild (cosmetic, self-correcting), so no
        // snapshot copy is needed. The worker owns the job buffers from start
        // to done; the main thread uploads them into the mesh afterward. Any
        // worker exception trips ABGuard.Async and the sync path takes over.
        private static volatile bool maskJobRunning;
        private static volatile bool maskJobDone;
        private static Exception maskJobError;
        private static CellRect maskJobRect;
        private static int maskJobLowerId;
        private static readonly List<Vector3> jobVerts = new List<Vector3>();
        private static readonly List<int> jobTris = new List<int>();
        private static readonly List<Color32> jobColors = new List<Color32>();

        // ---- One-run diagnostic (2026-07-20 bleed-through verification) ----
        // Dumps the live queue layout once so the queue-family theory is proven
        // by measured numbers, not inference. Remove after the verification run.
        private static bool diagDumped;
        private static int diagSamples;
        private static readonly System.Text.StringBuilder diagSb = new System.Text.StringBuilder();

        private static void DiagnosticSample(Type layerType, LayerSubMesh sub, int queue)
        {
            if (diagDumped || diagSamples >= 6 || layerType != typeof(SectionLayer_ThingsGeneral))
            {
                return;
            }
            diagSamples++;
            diagSb.Append(" | things sub ").Append(sub.material != null ? sub.material.name : "null")
                .Append(" y=").Append(sub.mesh.bounds.center.y.ToString("F2"))
                .Append(" q=").Append(queue);
        }

        private static void DiagnosticDump(Map lower)
        {
            if (diagDumped)
            {
                return;
            }
            diagDumped = true;
            Material soil = TerrainDefOf.Soil?.graphic?.MatSingle;
            Material roof = ABDefOf.AB_RoofSurface?.graphic?.MatSingle;
            Log.Warning(ABLog.Tag + " [diag] queue layout: soil="
                + (soil != null ? soil.renderQueue : -1)
                + " roofSurface=" + (roof != null ? roof.renderQueue : -1)
                + " terrainHardShader=" + (ShaderDatabase.TerrainHard != null ? ShaderDatabase.TerrainHard.renderQueue : -1)
                + " ceiling=" + BelowQueueCeiling
                + diagSb);
        }

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
                // The water shader samples per-map globals (river flow texture,
                // map size). The current (sky) map set its own empty values
                // during MapUpdate; override with the lower map's so its rivers
                // and lakes render exactly like vanilla. Harmless for the sky
                // map: it has no water shader in view of its own. Globals are
                // read at render time, so the last setter this frame wins.
                lower.waterInfo?.SetTextures();
                // Process the lower map's dirty sections so the view below stays live.
                lower.mapDrawer.MapMeshDrawerUpdate_First();
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(lower);
                DrawSections(lower, view);
                DiagnosticDump(lower);
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
                        Type layerType = layer.GetType();
                        if (!ContentLayerTypes.Contains(layerType) || !layer.Visible)
                        {
                            continue;
                        }
                        if (!BelowLayerOffsets.TryGetValue(layerType, out int offset))
                        {
                            offset = BelowDefaultOffset;
                        }
                        int baseQueue = Mathf.Max(BelowQueueCeiling - offset, 1);
                        bool stepByAltitude = offset == BelowThingsOffset;
                        List<LayerSubMesh> subs = layer.subMeshes;
                        for (int j = 0; j < subs.Count; j++)
                        {
                            LayerSubMesh sub = subs[j];
                            float subY = sub.mesh.bounds.center.y;
                            if (!sub.finalized || sub.disabled || subY > MaxSubMeshAltitude)
                            {
                                continue;
                            }
                            // Things submeshes bake at their AltitudeLayer heights
                            // and vanilla layers them by distance sorting, which the
                            // forced low queues bypass; step the queue by altitude
                            // so plants, items, and buildings keep their painter
                            // order inside the below view. Scale 14 spreads the real
                            // 0..7 print range across the 0..99 headroom (the old 40
                            // saturated everything at 99 and lost the order).
                            int queue = stepByAltitude
                                ? baseQueue + Mathf.Clamp((int)(subY * 14f), 0, 99)
                                : baseQueue;
                            DiagnosticSample(layerType, sub, queue);
                            Material mat = BelowMaterialFor(sub.material, queue);
                            if (mat != null)
                            {
                                Graphics.DrawMesh(sub.mesh, OffsetMatrix, mat, 0);
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
        /// do not update theirs.
        /// Known accepted limitation (T6 #6, decided): the mask material draws on
        /// top of the transparent pass so it can dim below content that renders
        /// AFTER it (the offset dynamic pass). Sky level projectiles crossing open
        /// air occupy those same cells for a few frames and pick up the same dim.
        /// Below and sky content share render queues and overlapping altitudes, so
        /// no draw-order or depth trick separates them; a clean fix needs a
        /// subcamera composite of the entire below view, out of scope for polish.
        /// Cosmetic, most visible at night, accepted and documented.</summary>
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
            TryApplyMaskJob();
            int frame = Time.frameCount;
            bool viewContained = maskMesh != null
                && maskLastRect.Contains(new IntVec3(view.minX, 0, view.minZ))
                && maskLastRect.Contains(new IntVec3(view.maxX, 0, view.maxZ));
            if (!viewContained || frame - maskLastFrame >= MaskRebuildIntervalFrames
                || lower.uniqueID != maskLastLowerId)
            {
                CellRect buildRect = view.ExpandedBy(MaskPadCells).ClipInsideMap(sky);
                if (ABGuard.On(ABGuard.Async))
                {
                    StartMaskJob(sky, lower, buildRect);
                    // The stale mesh keeps drawing until the worker delivers;
                    // the pad absorbs the pan in the meantime.
                    maskLastFrame = frame;
                }
                else
                {
                    RebuildMask(sky, lower, buildRect);
                    maskLastFrame = frame;
                    maskLastRect = buildRect;
                    maskLastLowerId = lower.uniqueID;
                }
            }
            if (maskMesh != null && maskMesh.vertexCount > 0)
            {
                Graphics.DrawMesh(maskMesh, Matrix4x4.identity, maskMat, 0);
            }
        }

        private static void StartMaskJob(Map sky, Map lower, CellRect rect)
        {
            if (maskJobRunning)
            {
                return;
            }
            maskJobRunning = true;
            maskJobDone = false;
            maskJobError = null;
            maskJobRect = rect;
            maskJobLowerId = lower.uniqueID;
            // Captured on the main thread; the worker touches only these locals
            // and the job buffers.
            TerrainGrid skyTerrain = sky.terrainGrid;
            FogGrid lowerFog = lower.fogGrid;
            int sizeX = sky.Size.x;
            int sizeZ = sky.Size.z;
            float baseDim = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.6f);
            int step = NextMaskStep(rect);
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    BuildMaskBuffers(skyTerrain, lowerFog, sizeX, sizeZ, maskJobRect, step,
                        (byte)(255f * baseDim), jobVerts, jobTris, jobColors);
                }
                catch (Exception e)
                {
                    maskJobError = e;
                }
                finally
                {
                    maskJobDone = true;
                }
            });
        }

        /// <summary>Main thread: upload a finished worker result into the mask
        /// mesh, or trip the async guard if the worker faulted.</summary>
        private static void TryApplyMaskJob()
        {
            if (!maskJobRunning || !maskJobDone)
            {
                return;
            }
            maskJobRunning = false;
            if (maskJobError != null)
            {
                ABGuard.Disable(ABGuard.Async, maskJobError, "async mask build");
                return;
            }
            EnsureMaskMesh();
            maskMesh.Clear();
            if (jobVerts.Count > 0)
            {
                maskMesh.SetVertices(jobVerts);
                maskMesh.SetColors(jobColors);
                maskMesh.SetTriangles(jobTris, 0);
                maskMesh.RecalculateBounds();
            }
            maskLastRect = maskJobRect;
            maskLastLowerId = maskJobLowerId;
        }

        private static int maskLastStep = 1;

        /// <summary>Resolution switches use hysteresis so zooming near the
        /// threshold does not pop between block sizes. Main thread only.</summary>
        private static int NextMaskStep(CellRect rect)
        {
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
            return step;
        }

        private static void EnsureMaskMesh()
        {
            if (maskMesh == null)
            {
                maskMesh = new Mesh
                {
                    name = "AB_BelowMask",
                    indexFormat = IndexFormat.UInt32
                };
            }
        }

        /// <summary>Synchronous rebuild: build into the shared buffers and
        /// upload immediately. Fallback path when the async lane is off.</summary>
        private static void RebuildMask(Map sky, Map lower, CellRect rect)
        {
            EnsureMaskMesh();
            float baseDim = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.6f);
            BuildMaskBuffers(sky.terrainGrid, lower.fogGrid, sky.Size.x, sky.Size.z, rect,
                NextMaskStep(rect), (byte)(255f * baseDim), maskVerts, maskTris, maskColors);
            maskMesh.Clear();
            if (maskVerts.Count > 0)
            {
                maskMesh.SetVertices(maskVerts);
                maskMesh.SetColors(maskColors);
                maskMesh.SetTriangles(maskTris, 0);
                maskMesh.RecalculateBounds();
            }
        }

        /// <summary>Pure buffer build shared by the sync path (main thread) and
        /// the async lane (worker). Touches nothing but the passed grids and
        /// output lists; must stay free of Unity API calls.</summary>
        private static void BuildMaskBuffers(TerrainGrid skyTerrain, FogGrid lowerFog,
            int sizeX, int sizeZ, CellRect rect, int step, byte dimAlpha,
            List<Vector3> verts, List<int> tris, List<Color32> colors)
        {
            verts.Clear();
            tris.Clear();
            colors.Clear();
            TerrainDef air = ABDefOf.AB_OpenAir;
            // Anchor the sampling grid to world coordinates so blocks stay put
            // while the camera pans; a view-anchored grid shifts a cell whenever
            // the view edge parity flips, which reads as jitter.
            int startX = rect.minX - (((rect.minX % step) + step) % step);
            int startZ = rect.minZ - (((rect.minZ % step) + step) % step);
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
                    // Unexplored surface stays hidden; explored cells get only
                    // the constant depth dim. Natural day-night shading arrives
                    // for free through the shared shader globals.
                    byte a = lowerFog.IsFogged(c) ? (byte)255 : dimAlpha;
                    int vi = verts.Count;
                    float x0 = Mathf.Max(x, 0);
                    float z0 = Mathf.Max(z, 0);
                    float x1 = Mathf.Min(x + step, sizeX);
                    float z1 = Mathf.Min(z + step, sizeZ);
                    if (x1 <= x0 || z1 <= z0)
                    {
                        continue;
                    }
                    verts.Add(new Vector3(x0, MaskAltitude, z0));
                    verts.Add(new Vector3(x0, MaskAltitude, z1));
                    verts.Add(new Vector3(x1, MaskAltitude, z1));
                    verts.Add(new Vector3(x1, MaskAltitude, z0));
                    Color32 col = new Color32(0, 0, 0, a);
                    colors.Add(col);
                    colors.Add(col);
                    colors.Add(col);
                    colors.Add(col);
                    tris.Add(vi);
                    tris.Add(vi + 1);
                    tris.Add(vi + 2);
                    tris.Add(vi);
                    tris.Add(vi + 2);
                    tris.Add(vi + 3);
                }
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
                if (!TryDrawFilteredDynamic(map, lower))
                {
                    // Reflection fallback: the unfiltered vanilla pass (pre-spec
                    // behavior) rather than no live view at all.
                    lower.dynamicDrawManager.DrawDynamicThings();
                }
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

        private static AccessTools.FieldRef<DynamicDrawManager, List<Thing>> drawThingsRef;
        private static bool drawThingsRefFailed;

        /// <summary>The one-way mirror rule (playtest spec): a below thing is
        /// visible from above ONLY when its cell is unroofed (any roof kind),
        /// unfogged, in view, and under open air on this level. Pawns walking
        /// under roofs, rooftops, landings, or the mountain simply do not draw.
        /// Single-threaded per-thing Draw mirrors vanilla's own
        /// singleThreadedDrawing fallback path, so no parallel pre-draw is
        /// needed.</summary>
        private static bool TryDrawFilteredDynamic(Map sky, Map lower)
        {
            if (drawThingsRefFailed)
            {
                return false;
            }
            if (drawThingsRef == null)
            {
                try
                {
                    drawThingsRef = AccessTools.FieldRefAccess<DynamicDrawManager, List<Thing>>("drawThings");
                }
                catch (Exception)
                {
                    drawThingsRef = null;
                }
                if (drawThingsRef == null)
                {
                    drawThingsRefFailed = true;
                    Log.Warning(ABLog.Tag + " DynamicDrawManager.drawThings not found; the below view falls back to unfiltered dynamic drawing.");
                    return false;
                }
            }
            List<Thing> things = drawThingsRef(lower.dynamicDrawManager);
            if (things == null)
            {
                drawThingsRefFailed = true;
                return false;
            }
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(lower);
            RoofGrid roofs = lower.roofGrid;
            FogGrid fog = lower.fogGrid;
            TerrainGrid skyTerrain = sky.terrainGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || !t.Spawned)
                {
                    continue;
                }
                IntVec3 pos = t.Position;
                if (!pos.InBounds(lower) || roofs.Roofed(pos))
                {
                    continue;
                }
                if (!pos.InBounds(sky) || skyTerrain.TerrainAt(pos) != air)
                {
                    continue;
                }
                if (fog.IsFogged(pos))
                {
                    continue;
                }
                if (!view.Contains(pos) && !t.def.drawOffscreen)
                {
                    continue;
                }
                try
                {
                    t.DynamicDrawPhase(DrawPhase.Draw);
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " Below draw failed for " + t.LabelCap + ": " + e.Message,
                        t.thingIDNumber ^ 762195846);
                }
            }
            return true;
        }
    }
}
