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
    [StaticConstructorOnStartup]
    public static class LevelRenderer
    {
        /// <summary>Altitude shift for below content. Keeps it under the sky map's
        /// terrain (y=0) but above the camera far plane at any zoom.</summary>
        public const float BelowOffset = -2.5f;

        /// <summary>Extra render-queue drop for the two-deep band (basement
        /// seen from the sky through stacked glass panes). Keeps the whole
        /// basement band strictly under every surface-band clone.</summary>
        private const int StackedDrop = 420;

        /// <summary>Submeshes whose bounds sit above this are skipped (fog of war,
        /// lighting, silhouettes, overlays); the mask mesh replaces them. 1.6 real
        /// altitudes: content prints span 0..7 (LowPlant 4.02, Building 5.49, Item
        /// 6.59) and overlays start at 12+ (FogOfWar 12.07, Silhouettes 13.54), so
        /// the boundary sits at 10. The old value 2 assumed the AltInc scale and
        /// silently skipped every wall, plant, and item submesh.</summary>
        private const float MaxSubMeshAltitude = 10f;

        private const float MaskAltitude = -0.10f;

        /// <summary>Skirt quads sit slightly above the mask plane; render
        /// queues do the real ordering, the altitude only avoids z-fighting.</summary>
        private const float SkirtAltitude = -0.05f;

        /// <summary>Width of the thin ledge hairlines on east/west slab edges
        /// and of the bright top-corner lip line on south slab edges.</summary>
        internal const float SkirtLedgeWidth = 0.08f;

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

        // --- Below-view transform (height-language rework, 2026-07-22) ---
        // The lower level draws PLUMB: x/z pass through untouched and only
        // the altitude drops by BelowOffset so the band sorts under the sky
        // map. The old faux-perspective terms (fixed south depth shift +
        // camera-anchored parallax scale) are deleted - displacing the ground
        // put the vertical face on the AIR side of the rim line and read as
        // "floor sunk into a pit". Height is told the vanilla way instead:
        // ascending rim facades on the slab's own edge cells
        // (SectionLayer_ABRimFacade), a narrow contact shadow at their feet
        // (skirt), a bright south-rim lip, and bright plumb ground. Side
        // effect: screen<->below mapping is exact at every zoom.
        private static int transformFrame = -1;
        private static readonly Matrix4x4 belowMatrixConst = Matrix4x4.Translate(new Vector3(0f, BelowOffset, 0f));

        /// <summary>Uniform per-object shrink for below-level content (the
        /// "fake zoom out"): printed things scale at print time in
        /// SectionLayer_ABBelowThings, live pawns via the PawnDrawParms matrix
        /// patch. Each object shrinks about its OWN position, so unlike the
        /// parallax scale nothing slides off its cell. Refreshed with the
        /// transform each frame.</summary>
        internal static float BelowThingScale = 0.85f;

        /// <summary>Per-frame below-band transform; refreshes lazily on first
        /// use each frame (a single int compare afterward).</summary>
        internal static Matrix4x4 BelowMatrix
        {
            get
            {
                RefreshBelowTransform();
                return belowMatrixConst;
            }
        }

        /// <summary>Applies the current frame's below-view transform to one
        /// dynamic draw position (called from the DrawPos postfix). Main
        /// thread only while OffsetActive is true; the fields are written at
        /// frame start before the pass begins.</summary>
        internal static void ApplyDrawShift(ref Vector3 v)
        {
            v.y += BelowOffset;
        }

        /// <summary>Ensures the per-frame below transform is current. Safe to
        /// call from the input/GUI phase (selection hit-testing, bracket draw)
        /// where the render pass may not have run yet this frame.</summary>
        internal static void EnsureBelowTransform()
        {
            RefreshBelowTransform();
        }

        /// <summary>The on-screen center a below-level draw position renders at,
        /// i.e. its DrawPos run through the current see-below transform. Used to
        /// hit-test mouse clicks against the lower map as it appears from above.
        /// The per-object shrink (BelowThingScale) scales about this same center,
        /// so the center is exactly where the sprite sits on screen.</summary>
        internal static Vector3 ShiftedBelowDrawPos(Vector3 drawPos)
        {
            RefreshBelowTransform();
            ApplyDrawShift(ref drawPos);
            return drawPos;
        }

        /// <summary>Inverse of the see-below transform: maps a point in the sky map's
        /// world space (e.g. the cursor) back to the surface world position that renders
        /// there. Identity in x/z since the height-language rework (the below view is
        /// plumb); kept so every consumer funnels through ONE inversion point should a
        /// transform ever return.</summary>
        internal static Vector3 ScreenToBelowPos(Vector3 screenPos)
        {
            return screenPos;
        }

        private static void RefreshBelowTransform()
        {
            int frame = Time.frameCount;
            if (frame == transformFrame)
            {
                return;
            }
            transformFrame = frame;
            ABSettings settings = ABMod.Settings;
            BelowThingScale = Mathf.Clamp(settings?.belowThingScale ?? 0.85f, 0.5f, 1f);
        }

        /// <summary>Below-level lighting toggle (Rendering tab, default on).</summary>
        private static bool BelowLightingOn => ABMod.Settings?.belowLighting ?? true;

        private static readonly AccessTools.FieldRef<Section, List<SectionLayer>> LayersRef =
            AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");

        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> SectionsRef =
            AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");

        /// <summary>True once the map's MapDrawer has built its section grid
        /// (RegenerateEverythingNow ran). WholeMapChanged before that throws.</summary>
        internal static bool DrawerReady(Map m)
        {
            return m?.mapDrawer != null && SectionsRef(m.mapDrawer) != null;
        }

        /// <summary>Diagnostic accessors for the below-view diagnostic dev tool.</summary>
        internal static int DebugQueueCeiling => BelowQueueCeiling;

        internal static Section[,] DebugSections(Map m)
        {
            return m?.mapDrawer != null ? SectionsRef(m.mapDrawer) : null;
        }

        internal static List<SectionLayer> DebugLayers(Section s)
        {
            return LayersRef(s);
        }

        /// <summary>Exact layer types the below-view copies: world CONTENT only.
        /// Everything else (fog, darkness, lighting, plans, the vanilla power grid
        /// overlay, DBH and VEF pipe overlays, any future mod overlay) is excluded
        /// by construction, so overlays only ever render on the level being viewed.
        /// Exact types, not IsAssignableFrom: the power overlay subclasses the
        /// things layer and would slip through an inheritance check.</summary>
        private static readonly HashSet<Type> ContentLayerTypes = BuildContentLayerTypes();

        private static HashSet<Type> BuildContentLayerTypes()
        {
            // SectionLayer_ThingsGeneral and SectionLayer_BuildingsDamage are
            // deliberately ABSENT since the per-cell printed layer
            // (SectionLayer_ABBelowThings) took over things content: baked
            // section meshes cover every cell and cannot skip covered ones, so
            // drawing them under the sky map made rooftop opacity a render
            // -queue contest (and lost it on 1.6's atlas pipeline).
            HashSet<Type> set = new HashSet<Type>
            {
                typeof(SectionLayer_Terrain),
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
                { typeof(SectionLayer_Snow), 90 },
                { typeof(SectionLayer_Gas), 85 },
                { typeof(SectionLayer_PollutionCloud), 80 },
                // Lowest offset = drawn last inside the band: the lower map's
                // glow-and-darkness mesh paints over every content clone, under
                // the mask and every sky-side material.
                { typeof(SectionLayer_LightingOverlay), 75 }
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
        private static int maskLastDeepId = -1;
        private static readonly List<Vector3> maskVerts = new List<Vector3>();
        private static readonly List<int> maskTris = new List<int>();
        private static readonly List<Color32> maskColors = new List<Color32>();

        // Slab-edge skirt (depth-cue removal, 2026-07-22): the painted-depth
        // experiments (descending faces, ascending facades, contact shadows,
        // bright lips) are all retired by user direction. What remains is the
        // minimal edge delineation: one thin dark hairline in the air cell
        // against EVERY adjacent slab edge, so rooftop and floor borders read
        // as clean seams rather than raw texture boundaries. Mountain-cap
        // borders stay excluded (the rock art carries its own edges).
        // Geometry rebuilds inside the existing mask job (same inputs, same
        // cadence) and draws at IDENTITY.
        private static Mesh skirtMesh;
        private static Material skirtMat;
        private static readonly List<Vector3> skirtVerts = new List<Vector3>();
        private static readonly List<int> skirtTris = new List<int>();
        private static readonly List<Color32> skirtColors = new List<Color32>();

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
        private static int maskJobDeepId;
        private static readonly List<Vector3> jobVerts = new List<Vector3>();
        private static readonly List<int> jobTris = new List<int>();
        private static readonly List<Color32> jobColors = new List<Color32>();
        private static readonly List<Vector3> jobSkirtVerts = new List<Vector3>();
        private static readonly List<int> jobSkirtTris = new List<int>();
        private static readonly List<Color32> jobSkirtColors = new List<Color32>();
        private static bool maskJobSkirtOn;

        /// <summary>Forced-queue draw for one printed below-view submesh
        /// (SectionLayer_ABBelowThings): same band as the cloned layers,
        /// stepped by the submesh's real print altitude so plants, walls, and
        /// items keep their painter order inside the view below.</summary>
        internal static void DrawBelowSubMesh(LayerSubMesh sub)
        {
            int baseQueue = Mathf.Max(BelowQueueCeiling - BelowThingsOffset, 1);
            float subY = sub.mesh.bounds.center.y;
            int queue = baseQueue + Mathf.Clamp((int)(subY * 14f), 0, 99);
            Material mat = BelowMaterialFor(sub.material, queue);
            if (mat != null)
            {
                Graphics.DrawMesh(sub.mesh, BelowMatrix, mat, 0);
            }
        }

        /// <summary>Queue offset for the slab-edge wall facade: just above the
        /// skirt's ceiling-60 clone so the real wall texture paints over the
        /// flat dark face, still under every sky-side material.</summary>
        private const int FacadeQueueOffset = 55;

        /// <summary>Identity draw for one wall-facade submesh through its
        /// queue clone. The south shift is BAKED into the verts at print time
        /// (#133 redesign: the mesh is pre-clipped to the sliver and can never
        /// overlap a rim cell, so no draw-time matrix and no ordering fight
        /// with sky content; the depth slider triggers a reprint instead).</summary>
        internal static void DrawWallFacadeSubMesh(LayerSubMesh sub)
        {
            Material mat = BelowMaterialFor(sub.material,
                Mathf.Max(BelowQueueCeiling - FacadeQueueOffset, 1));
            if (mat != null)
            {
                Graphics.DrawMesh(sub.mesh, Matrix4x4.identity, mat, 0);
            }
        }

        private static int wallRevealQueue = -1;

        /// <summary>Queue for the rooftop rim wall-top reveal strips: strictly
        /// above every terrain family we can stand on, HARD-CLAMPED below the
        /// cutout family - the mountain cap's proven window, copied exactly.
        /// Two failed attempts are documented here so nobody retries them:
        /// (#131) sampling MatBases.EdgeShadow put the measured max in the
        /// TRANSPARENT range (edge shadows draw over floors), and the missing
        /// hard clamp then parked the strips above cutout - they painted over
        /// sky walls; (#132) drawing native at the things queue with low
        /// vertex altitude relied on within-queue camera-distance ordering,
        /// which for non-depth-writing map shaders is effective submission
        /// order - this layer draws after the things layer, so the strips
        /// painted over the sky rim wall's face again. Queues are the only
        /// reliable ordering lever (the below band's whole design rests on
        /// that); the clamp makes the worst case "strips hidden under the
        /// steel tile", never "strips over walls".</summary>
        private static int WallRevealQueue
        {
            get
            {
                if (wallRevealQueue < 0)
                {
                    int terrain = 0;
                    terrain = MaxQueue(terrain, TerrainDefOf.Soil?.graphic?.MatSingle);
                    terrain = MaxQueue(terrain, ABDefOf.AB_RoofSurface?.graphic?.MatSingle);
                    terrain = MaxQueue(terrain, ABDefOf.AB_MountainTop?.graphic?.MatSingle);
                    terrain = MaxQueue(terrain, TerrainDefOf.MetalTile?.graphic?.MatSingle);
                    terrain = MaxQueue(terrain, TerrainDefOf.WoodPlankFloor?.graphic?.MatSingle);
                    if (terrain < 500)
                    {
                        terrain = 2000;
                    }
                    int cutout = ShaderDatabase.Cutout != null && ShaderDatabase.Cutout.renderQueue >= 500
                        ? ShaderDatabase.Cutout.renderQueue
                        : terrain + 450;
                    wallRevealQueue = Mathf.Min(terrain + 1, cutout - 1);
                }
                return wallRevealQueue;
            }
        }

        private static int MaxQueue(int current, Material m)
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
            return q >= 500 ? Mathf.Max(current, q) : current;
        }

        /// <summary>Identity draw for one rim-reveal submesh through its clone
        /// in the measured over-terrain window.</summary>
        internal static void DrawWallRevealSubMesh(LayerSubMesh sub)
        {
            Material mat = BelowMaterialFor(sub.material, WallRevealQueue);
            if (mat != null)
            {
                Graphics.DrawMesh(sub.mesh, Matrix4x4.identity, mat, 0);
            }
        }

        public static void DrawBelowStatic(Map map)
        {
            if (!ABGuard.On(ABGuard.Rendering) || map == null || map != Find.CurrentMap)
            {
                return;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level < 0)
            {
                return;
            }
            Map lower = comp.lowerMap;
            if (lower == null || lower.Disposed)
            {
                return;
            }
            // The below band is a SKY-view feature; the ground level draws no
            // band of its own (skylights removed by user directive 2026-07-24).
            if (comp.level == 0)
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
                DrawSections(lower, view, 0, includeBelowThings: false);
                DrawBelowMask(map, lower, null, view);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "see-below rendering");
            }
        }

        /// <summary>extraOffset shifts the whole band deeper (the stacked
        /// basement pass); includeBelowThings additionally draws the lower
        /// map's own SectionLayer_ABBelowThings submeshes - the basement
        /// content it printed at its glass cells - into the deep band.</summary>
        private static void DrawSections(Map lower, CellRect view, int extraOffset, bool includeBelowThings)
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
                        bool belowThingsLayer = layerType == typeof(SectionLayer_ABBelowThings);
                        bool lightingLayer = layerType == typeof(SectionLayer_LightingOverlay);
                        if (belowThingsLayer)
                        {
                            if (!includeBelowThings || !layer.Visible)
                            {
                                continue;
                            }
                        }
                        else if (lightingLayer)
                        {
                            // Below-level lighting (2026-07-24): the lower map's
                            // own glow mesh - lamp pools, skylight shafts, and
                            // the roofed-room darkness encoded in vertex alpha -
                            // drawn into the band through a queue clone. Same
                            // vertex data + same LightOverlay shader as vanilla
                            // same-map rendering, so the composition is exactly
                            // one-big-map (the historical double-darkening came
                            // from the MASK adding its own light-derived dim;
                            // nothing here touches the mask). Updates are free:
                            // GroundGlow/Roofs dirty flags regenerate the mesh
                            // through the MapMeshDrawerUpdate_First pump the
                            // band already runs per frame, and the clone
                            // material is cached like every other layer's.
                            if (!BelowLightingOn || !layer.Visible)
                            {
                                continue;
                            }
                        }
                        else if (!ContentLayerTypes.Contains(layerType) || !layer.Visible)
                        {
                            continue;
                        }
                        int offset;
                        if (belowThingsLayer)
                        {
                            // Basement content printed by the surface's own
                            // below-things layer belongs one full band deeper.
                            offset = StackedDrop + BelowThingsOffset;
                        }
                        else if (!BelowLayerOffsets.TryGetValue(layerType, out offset))
                        {
                            offset = BelowDefaultOffset;
                        }
                        int baseQueue = Mathf.Max(BelowQueueCeiling - offset - extraOffset, 1);
                        bool terrainLayer = layerType == typeof(SectionLayer_Terrain);
                        List<LayerSubMesh> subs = layer.subMeshes;
                        for (int j = 0; j < subs.Count; j++)
                        {
                            LayerSubMesh sub = subs[j];
                            float subY = sub.mesh.bounds.center.y;
                            // The lighting overlay legitimately sits above the
                            // overlay boundary; it is included BY TYPE, not by
                            // altitude, so it is exempt from the cutoff.
                            if (!sub.finalized || sub.disabled
                                || (!lightingLayer && subY > MaxSubMeshAltitude))
                            {
                                continue;
                            }
                            int queue = baseQueue;
                            if (belowThingsLayer)
                            {
                                queue += Mathf.Clamp((int)(subY * 14f), 0, 99);
                            }
                            else if (terrainLayer && sub.material != null)
                            {
                                // Vanilla terrain materials carry 2000 + renderPrecedence
                                // and rely on that queue spread for who paints over whom
                                // at terrain borders (soil's fade quads must draw OVER
                                // water). Flattening every submesh to one clone queue
                                // made the winner submission-order-random per section -
                                // the "no fade at the water edge" hard stairsteps.
                                // Compress the source spread (~0-400) into the ~15-queue
                                // window under the next layer slot: relative order is
                                // what matters, collisions just fall back to the old
                                // behavior for near-equal precedences.
                                queue += Mathf.Clamp((sub.material.renderQueue - 2000) / 25, 0, 14);
                            }
                            Material mat = BelowMaterialFor(sub.material, queue: queue);
                            if (mat != null)
                            {
                                Graphics.DrawMesh(sub.mesh, BelowMatrix, mat, 0);
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
        private static void DrawBelowMask(Map sky, Map lower, Map deep, CellRect view)
        {
            if (maskMat == null)
            {
                maskMat = new Material(MatBases.FogOfWar)
                {
                    mainTexture = BaseContent.WhiteTex,
                    color = Color.white
                };
            }
            if (skirtMat == null)
            {
                // White vertex-color material: one mesh carries the contact
                // shadow, the hairlines, and the bright lip in per-vertex
                // tints. Same forced queue as before - above every below-band
                // queue, under the sky map's own terrain.
                skirtMat = SolidColorMaterials.NewSolidColorMaterial(Color.white, ShaderDatabase.VertexColor);
                skirtMat.renderQueue = Mathf.Max(BelowQueueCeiling - 60, 1);
            }
            TryApplyMaskJob();
            int frame = Time.frameCount;
            bool viewContained = maskMesh != null
                && maskLastRect.Contains(new IntVec3(view.minX, 0, view.minZ))
                && maskLastRect.Contains(new IntVec3(view.maxX, 0, view.maxZ));
            int deepId = deep?.uniqueID ?? -1;
            if (!viewContained || frame - maskLastFrame >= MaskRebuildIntervalFrames
                || lower.uniqueID != maskLastLowerId || deepId != maskLastDeepId)
            {
                CellRect buildRect = view.ExpandedBy(MaskPadCells).ClipInsideMap(sky);
                if (ABGuard.On(ABGuard.Async))
                {
                    StartMaskJob(sky, lower, deep, buildRect);
                    // The stale mesh keeps drawing until the worker delivers;
                    // the pad absorbs the pan in the meantime.
                    maskLastFrame = frame;
                }
                else
                {
                    RebuildMask(sky, lower, deep, buildRect);
                    maskLastFrame = frame;
                    maskLastRect = buildRect;
                    maskLastLowerId = lower.uniqueID;
                    maskLastDeepId = deepId;
                }
            }
            if (maskMesh != null && maskMesh.vertexCount > 0)
            {
                Graphics.DrawMesh(maskMesh, Matrix4x4.identity, maskMat, 0);
            }
            if (skirtMesh != null && skirtMesh.vertexCount > 0)
            {
                Graphics.DrawMesh(skirtMesh, Matrix4x4.identity, skirtMat, 0);
            }
        }

        private static void StartMaskJob(Map sky, Map lower, Map deep, CellRect rect)
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
            maskJobDeepId = deep?.uniqueID ?? -1;
            // Captured on the main thread; the worker touches only these locals
            // and the job buffers.
            TerrainGrid skyTerrain = sky.terrainGrid;
            FogGrid lowerFog = lower.fogGrid;
            TerrainGrid lowerTerrain = deep != null ? lower.terrainGrid : null;
            FogGrid deepFog = deep?.fogGrid;
            int sizeX = sky.Size.x;
            int sizeZ = sky.Size.z;
            float baseDim = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.6f);
            int step = NextMaskStep(rect);
            maskJobSkirtOn = CurrentSkirtOn();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    BuildMaskBuffers(skyTerrain, lowerFog, lowerTerrain, deepFog,
                        sizeX, sizeZ, maskJobRect, step,
                        (byte)(255f * baseDim), jobVerts, jobTris, jobColors);
                    BuildSkirtBuffers(skyTerrain, sizeX, sizeZ, maskJobRect, maskJobSkirtOn,
                        jobSkirtVerts, jobSkirtTris, jobSkirtColors);
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
            UploadSkirt(jobSkirtVerts, jobSkirtTris, jobSkirtColors);
            maskLastRect = maskJobRect;
            maskLastLowerId = maskJobLowerId;
            maskLastDeepId = maskJobDeepId;
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
        private static void RebuildMask(Map sky, Map lower, Map deep, CellRect rect)
        {
            EnsureMaskMesh();
            float baseDim = Mathf.Clamp(ABMod.Settings?.belowDim ?? 0.12f, 0f, 0.6f);
            BuildMaskBuffers(sky.terrainGrid, lower.fogGrid,
                deep != null ? lower.terrainGrid : null, deep?.fogGrid,
                sky.Size.x, sky.Size.z, rect,
                NextMaskStep(rect), (byte)(255f * baseDim), maskVerts, maskTris, maskColors);
            maskMesh.Clear();
            if (maskVerts.Count > 0)
            {
                maskMesh.SetVertices(maskVerts);
                maskMesh.SetColors(maskColors);
                maskMesh.SetTriangles(maskTris, 0);
                maskMesh.RecalculateBounds();
            }
            BuildSkirtBuffers(sky.terrainGrid, sky.Size.x, sky.Size.z, rect,
                CurrentSkirtOn(), skirtVerts, skirtTris, skirtColors);
            UploadSkirt(skirtVerts, skirtTris, skirtColors);
        }

        /// <summary>Pure buffer build shared by the sync path (main thread) and
        /// the async lane (worker). Touches nothing but the passed grids and
        /// output lists; must stay free of Unity API calls.</summary>
        private static void BuildMaskBuffers(TerrainGrid skyTerrain, FogGrid lowerFog,
            TerrainGrid lowerTerrain, FogGrid deepFog,
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
                    TerrainDef top = skyTerrain.TerrainAt(c);
                    if (top != air)
                    {
                        continue;
                    }
                    bool fogged = lowerFog.IsFogged(c);
                    // Unexplored levels stay hidden; explored cells get only
                    // the constant depth dim. Natural day-night shading arrives
                    // for free through the shared shader globals.
                    Color32 col = fogged
                        ? new Color32(0, 0, 0, 255)
                        : new Color32(0, 0, 0, dimAlpha);
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

        /// <summary>Whether the slab-edge skirt (contact shadow + hairlines +
        /// bright south-rim lip) builds this rebuild.</summary>
        private static bool CurrentSkirtOn()
        {
            ABSettings settings = ABMod.Settings;
            return settings == null || settings.drawSlabEdge;
        }

        private static void EnsureSkirtMesh()
        {
            if (skirtMesh == null)
            {
                skirtMesh = new Mesh
                {
                    name = "AB_SlabSkirt",
                    indexFormat = IndexFormat.UInt32
                };
            }
        }

        private static void UploadSkirt(List<Vector3> verts, List<int> tris, List<Color32> colors)
        {
            EnsureSkirtMesh();
            skirtMesh.Clear();
            if (verts.Count > 0)
            {
                skirtMesh.SetVertices(verts);
                skirtMesh.SetColors(colors);
                skirtMesh.SetTriangles(tris, 0);
                skirtMesh.RecalculateBounds();
            }
        }

        private static readonly Color32 SkirtLedge = new Color32(16, 16, 14, 110);

        /// <summary>Pure buffer build for the slab-edge skirt; worker-safe for
        /// the same reason the mask build is (terrain reads are atomic and a
        /// torn read self-corrects next rebuild). Iterates AIR cells and emits
        /// one thin dark hairline against every adjacent slab edge. Slab = any
        /// sky terrain that is neither open air nor mountain cap (rooftop,
        /// built floors, landing platforms), matching the mask's own solidity
        /// rule.</summary>
        private static void BuildSkirtBuffers(TerrainGrid skyTerrain, int sizeX, int sizeZ,
            CellRect rect, bool enabled, List<Vector3> verts, List<int> tris, List<Color32> colors)
        {
            verts.Clear();
            tris.Clear();
            colors.Clear();
            if (!enabled)
            {
                return;
            }
            TerrainDef air = ABDefOf.AB_OpenAir;
            TerrainDef cap = ABDefOf.AB_MountainTop;
            int minX = Mathf.Max(rect.minX, 0);
            int maxX = Mathf.Min(rect.maxX, sizeX - 1);
            int minZ = Mathf.Max(rect.minZ, 0);
            int maxZ = Mathf.Min(rect.maxZ, sizeZ - 1);
            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    TerrainDef here = skyTerrain.TerrainAt(new IntVec3(x, 0, z));
                    if (here != air)
                    {
                        continue;
                    }
                    if (z + 1 < sizeZ && IsSlab(skyTerrain, x, z + 1, air, cap))
                    {
                        AddSkirtQuad(verts, tris, colors, x, x + 1f, z + 1f - SkirtLedgeWidth, z + 1f,
                            SkirtLedge, SkirtLedge, SkirtLedge, SkirtLedge);
                    }
                    if (x + 1 < sizeX && IsSlab(skyTerrain, x + 1, z, air, cap))
                    {
                        AddSkirtQuad(verts, tris, colors, x + 1f - SkirtLedgeWidth, x + 1f, z, z + 1f,
                            SkirtLedge, SkirtLedge, SkirtLedge, SkirtLedge);
                    }
                    if (x - 1 >= 0 && IsSlab(skyTerrain, x - 1, z, air, cap))
                    {
                        AddSkirtQuad(verts, tris, colors, x, x + SkirtLedgeWidth, z, z + 1f,
                            SkirtLedge, SkirtLedge, SkirtLedge, SkirtLedge);
                    }
                    if (z - 1 >= 0 && IsSlab(skyTerrain, x, z - 1, air, cap))
                    {
                        AddSkirtQuad(verts, tris, colors, x, x + 1f, z, z + SkirtLedgeWidth,
                            SkirtLedge, SkirtLedge, SkirtLedge, SkirtLedge);
                    }
                }
            }
        }

        private static bool IsSlab(TerrainGrid grid, int x, int z, TerrainDef air, TerrainDef cap)
        {
            TerrainDef t = grid.TerrainAt(new IntVec3(x, 0, z));
            return t != null && t != air && t != cap;
        }

        /// <summary>Vertex order (x0,z0), (x0,z1), (x1,z1), (x1,z0); colors
        /// follow the same order (c00, c01, c11, c10).</summary>
        private static void AddSkirtQuad(List<Vector3> verts, List<int> tris, List<Color32> colors,
            float x0, float x1, float z0, float z1,
            Color32 c00, Color32 c01, Color32 c11, Color32 c10)
        {
            int vi = verts.Count;
            verts.Add(new Vector3(x0, SkirtAltitude, z0));
            verts.Add(new Vector3(x0, SkirtAltitude, z1));
            verts.Add(new Vector3(x1, SkirtAltitude, z1));
            verts.Add(new Vector3(x1, SkirtAltitude, z0));
            colors.Add(c00);
            colors.Add(c01);
            colors.Add(c11);
            colors.Add(c10);
            tris.Add(vi);
            tris.Add(vi + 1);
            tris.Add(vi + 2);
            tris.Add(vi);
            tris.Add(vi + 2);
            tris.Add(vi + 3);
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
            if (comp == null || comp.level < 0)
            {
                return;
            }
            Map lower = comp.lowerMap;
            if (lower == null || lower.Disposed)
            {
                return;
            }
            if (comp.level == 0)
            {
                return;
            }
            try
            {
                RefreshBelowTransform();
                OffsetActive = true;
                if (!TryDrawFilteredDynamic(map, lower))
                {
                    // Reflection fallback: the unfiltered vanilla pass (pre-spec
                    // behavior) rather than no live view at all.
                    lower.dynamicDrawManager.DrawDynamicThings();
                }
                // Forbid (and other persistent-handle) overlays for the surface,
                // enqueued onto THIS map's overlay drawer so vanilla's own
                // DrawAllOverlays (which runs immediately after DrawDynamicThings)
                // flushes them at the plumb position. Enqueue only - no drawing -
                // so OffsetActive is irrelevant here.
                DrawBelowOverlays(map, lower);
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

        private static readonly AccessTools.FieldRef<OverlayDrawer, Dictionary<Thing, ThingOverlaysHandle>> OverlayHandlesRef =
            AccessTools.FieldRefAccess<OverlayDrawer, Dictionary<Thing, ThingOverlaysHandle>>("overlayHandles");

        /// <summary>Mirrors the surface's persistent world-space overlays (forbid
        /// icons via CompForbiddable's Enable/Disable handle, plus any comp that
        /// registers a handle) into the sky view. Vanilla only flushes the
        /// CURRENT map's overlay drawer, so a forbidden item on the surface never
        /// showed its red X from above. We re-enqueue each surface handle that is
        /// visible from above onto the sky map's overlay drawer; vanilla's own
        /// DrawAllOverlays (Map.MapUpdate, right after DrawDynamicThings) then
        /// renders it from the thing's own position - which is plumb, so the icon
        /// lands exactly over the item as it appears through the open air. GUI
        /// overlays (stack counts) ride a separate pass (BelowThingOverlays);
        /// this covers the world-space overlay family. Gated on the shared
        /// belowItemOverlays toggle.</summary>
        internal static void DrawBelowOverlays(Map sky, Map lower)
        {
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.belowItemOverlays)
            {
                return;
            }
            OverlayDrawer skyDrawer = sky.overlayDrawer;
            if (skyDrawer == null || lower.overlayDrawer == null)
            {
                return;
            }
            Dictionary<Thing, ThingOverlaysHandle> handles = OverlayHandlesRef(lower.overlayDrawer);
            if (handles == null || handles.Count == 0)
            {
                return;
            }
            CellRect view = Find.CameraDriver.CurrentViewRect.ClipInsideMap(lower);
            foreach (KeyValuePair<Thing, ThingOverlaysHandle> kv in handles)
            {
                Thing t = kv.Key;
                if (t == null || !t.Spawned)
                {
                    continue;
                }
                IntVec3 pos = t.Position;
                if (!view.Contains(pos) || !BelowSelection.CellVisibleFromAbove(pos, sky, lower))
                {
                    continue;
                }
                OverlayTypes ot = kv.Value != null ? kv.Value.OverlayTypes : OverlayTypes.None;
                if (ot != OverlayTypes.None)
                {
                    skyDrawer.DrawOverlay(t, ot);
                }
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
                if (!pos.InBounds(lower) || !pos.InBounds(sky))
                {
                    continue;
                }
                TerrainDef top = skyTerrain.TerrainAt(pos);
                if (top != air || roofs.Roofed(pos))
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
