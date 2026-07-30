using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 water. Three separate defects lived here, and they only look like one problem
    /// because they all read as "the water is wrong".
    ///
    /// 1.6 water is a TWO-PASS SCREEN-SPACE effect, which is the key fact:
    ///   - <c>SectionLayer_Watergen</c> renders <c>TerrainDef.waterDepthMaterial</c> into the
    ///     WaterDepth SUBCAMERA's render texture (a separate Unity layer, not the map mesh).
    ///   - The visible <c>Map/TerrainWater</c> material then samples that texture
    ///     (<c>_WaterOutputTex</c>) at its own SCREEN position to get depth and shoreline.
    /// So water is not self-contained geometry: printing the water material somewhere the
    /// depth pass never wrote produces nothing at all. That is why water VANISHED when
    /// looked at from an upper level - <see cref="SectionLayer_ABBelowWatergen"/> is the
    /// missing half.
    ///
    /// Separately, <c>WaterInfo.SetTextures</c> publishes <c>_MapSize</c> as the map's REAL
    /// size, which on a banded map is the whole stack (e.g. 126 x 896 for seven levels).
    /// Anything the water shader UVs by <c>worldPos.xz / _MapSize</c> is therefore stretched
    /// by the band count along z - which is the "water always flows north-south, even in
    /// lakes" report: not a flow-direction bug at all, a 7x vertical texture stretch that is
    /// uniform across every water cell precisely because it does not depend on flow data.
    ///
    /// And rivers were being carved into the wrong LEVEL - see
    /// <see cref="Patch_TileMutatorWorker_River_ABSurfaceBand"/>.
    /// </summary>
    public static class ABWaterBand
    {
        /// <summary>
        /// Move a generation-time water ANCHOR (river centre, lake centre) into the surface
        /// band.
        ///
        /// Both anchors are picked as a fraction of <c>map.Size.z</c>, which on a banded map
        /// is the height of the whole STACK. On a seven-level map that puts the river centre
        /// somewhere in z 268-627 while the surface band is z 384-510, so most rolls carved
        /// the river through a SKY band - and the post-generation carve then erased it.
        /// Reported as "rivers do not generate on tiles that should have a river": the river
        /// generated perfectly, just three levels above the colony.
        ///
        /// Only z is remapped. x is already band-independent, and the band is a square
        /// (126x126 out of 126x896), so a line through a re-anchored centre crosses the band
        /// exactly as it would cross an ordinary map of that size - the bend noise even peaks
        /// at the segment midpoint, which is the anchor, so the meander lands inside the band
        /// rather than out in the carved rows.
        /// </summary>
        public static IntVec3 ToSurfaceBand(Map map, IntVec3 anchor)
        {
            if (map == null)
            {
                return anchor;
            }
            // DURING generation the component is not set up yet (Setup runs in the
            // GenerateMap postfix), so the pending layout is the only source of truth -
            // exactly why ABBandedGeneration.TryPendingSurfaceRect exists.
            CellRect surface;
            if (ABBandedGeneration.TryPendingSurfaceRect(map, out surface, out _))
            {
                // Nothing to do: already inside.
                if (anchor.z >= surface.minZ && anchor.z <= surface.maxZ)
                {
                    return anchor;
                }
                int height = Mathf.Max(1, surface.Height);
                // Preserve the ROLL, not the absolute z: the vanilla pick is a fraction of
                // the map height and that fraction is the interesting part of the choice.
                float frac = Mathf.Clamp01(map.Size.z > 0 ? anchor.z / (float)map.Size.z : 0.5f);
                int z = surface.minZ + Mathf.Clamp(Mathf.RoundToInt(frac * (height - 1)), 0, height - 1);
                return new IntVec3(anchor.x, anchor.y, z);
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return anchor;
            }
            return bands.Translate(anchor, bands.surfaceBand);
        }

        // ---- band-folded river flow texture --------------------------------

        /// <summary>Keyed by the Map OBJECT, never by uniqueID: ids restart at 0 for a new
        /// game and repeat across loads, and a static keyed by one leaks the previous
        /// session's texture into the next colony.</summary>
        private static readonly ConditionalWeakTable<Map, Texture2D> foldedFlow =
            new ConditionalWeakTable<Map, Texture2D>();

        /// <summary>
        /// The river flow field, FOLDED into one band's worth of rows.
        ///
        /// Vanilla's texture is map-sized and addressed as <c>worldPos.xz / _MapSize</c>.
        /// Once <c>_MapSize.y</c> is republished as one Slot (see
        /// <see cref="Patch_WaterInfo_ABBandWaterGlobals"/>) that address becomes
        /// <c>band + local/Slot</c>, i.e. greater than 1 for every band above the bottom.
        /// A texture whose wrapMode is REPEAT resolves that for us at the SAMPLER, with no
        /// cooperation needed from a shader we cannot read: sampling at y = 3.4 returns the
        /// row at 0.4. Folding every band onto the same Slot rows is therefore not a
        /// compromise, it is what makes one texture correct for all of them - and it is
        /// harmless because rivers only ever exist on the surface band.
        /// </summary>
        public static Texture2D FoldedFlowTexture(Map map, ABBandMap bands)
        {
            if (map == null || bands == null || !bands.Banded)
            {
                return null;
            }
            if (foldedFlow.TryGetValue(map, out Texture2D cached) && cached != null)
            {
                return cached;
            }
            List<float> flow = map.waterInfo?.riverFlowMap;
            if (flow == null || flow.Count < map.Size.x * map.Size.z * 2)
            {
                return null; // no river on this tile - vanilla leaves the texture null too
            }
            int w = map.Size.x;
            int slot = bands.Slot;
            Color[] px = new Color[w * slot];
            TerrainGrid grid = map.terrainGrid;
            for (int x = 0; x < w; x++)
            {
                for (int z = 0; z < map.Size.z; z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!grid.BaseTerrainAt(c).IsRiver)
                    {
                        continue;
                    }
                    Vector2 sum = FlowAt(flow, map, x, z);
                    int n = 1;
                    // Vanilla averages with the eight neighbours here but tests the CENTRE
                    // cell's terrain inside the neighbour loop instead of the neighbour's -
                    // so its "average" is really the centre value counted nine times. Tested
                    // properly, which is what smooths the flow across a river's width.
                    for (int i = 0; i < 8; i++)
                    {
                        IntVec3 nb = c + GenAdj.AdjacentCells[i];
                        if (!nb.InBounds(map) || !grid.BaseTerrainAt(nb).IsRiver)
                        {
                            continue;
                        }
                        sum += FlowAt(flow, map, nb.x, nb.z);
                        n++;
                    }
                    px[(z % slot) * w + x] = new Color(sum.x / n, sum.y / n, 0f);
                }
            }
            Texture2D tex = new Texture2D(w, slot, TextureFormat.RGFloat, false);
            tex.SetPixels(px);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.Apply();
            foldedFlow.Remove(map);
            foldedFlow.Add(map, tex);
            return tex;
        }

        private static Vector2 FlowAt(List<float> flow, Map map, int x, int z)
        {
            int i = (x * map.Size.z + z) * 2;
            return new Vector2(flow[i], flow[i + 1]);
        }

        /// <summary>Unity textures are not garbage collected with their owner, so the folded
        /// texture has to be released explicitly when the map goes.</summary>
        public static void Release(Map map)
        {
            if (map == null || !foldedFlow.TryGetValue(map, out Texture2D tex))
            {
                return;
            }
            foldedFlow.Remove(map);
            if (tex != null)
            {
                LongEventHandler.ExecuteWhenFinished(delegate { UnityEngine.Object.Destroy(tex); });
            }
        }

        public static string Report(Map map)
        {
            StringBuilder sb = new StringBuilder();
            if (map == null)
            {
                return "no map";
            }
            ABBandMap bands = ABBands.CompOf(map);
            sb.AppendLine("map size: " + map.Size);
            sb.AppendLine("banded: " + (bands != null && bands.Banded)
                + (bands != null && bands.Banded
                    ? " (bands=" + bands.bandCount + " slot=" + bands.Slot
                        + " surfaceBand=" + bands.surfaceBand + ")"
                    : ""));
            sb.AppendLine("band water globals: " + ABV2Debug.BandWaterGlobals);
            sb.AppendLine("below water depth pass: " + ABV2Debug.DrawBelowWater);
            WaterInfo wi = map.waterInfo;
            if (wi == null)
            {
                sb.AppendLine("waterInfo: NULL");
                return sb.ToString();
            }
            sb.AppendLine("lakeCenter: " + wi.lakeCenter
                + (bands != null && bands.Banded && wi.lakeCenter.IsValid
                    ? " (band " + bands.BandOf(wi.lakeCenter) + ")" : ""));
            sb.AppendLine("riverGraph: " + (wi.riverGraph?.Count ?? 0) + " node(s)");
            if (wi.riverGraph != null)
            {
                foreach (RiverNode n in wi.riverGraph)
                {
                    sb.AppendLine("  start=" + n.start + " end=" + n.end + " width=" + n.width);
                }
            }
            sb.AppendLine("riverFlowMap: " + (wi.riverFlowMap == null
                ? "NULL (no river mutator ran)"
                : wi.riverFlowMap.Count + " floats"));
            sb.AppendLine("vanilla riverFlowTexture: "
                + (wi.riverFlowTexture == null ? "null" : wi.riverFlowTexture.width + "x" + wi.riverFlowTexture.height));
            if (bands != null && bands.Banded)
            {
                Texture2D folded = foldedFlow.TryGetValue(map, out Texture2D t) ? t : null;
                sb.AppendLine("folded flow texture: "
                    + (folded == null ? "not built" : folded.width + "x" + folded.height + " wrap=" + folded.wrapMode));
                sb.AppendLine("published _MapSize: " + Shader.GetGlobalVector(ShaderPropertyIDs.MapSize));
            }

            // Water census per band: this is what tells rivers-in-the-wrong-band apart from
            // rivers-that-never-generated.
            TerrainGrid grid = map.terrainGrid;
            int count = bands != null && bands.Banded ? bands.bandCount : 1;
            int[] water = new int[count];
            int[] river = new int[count];
            int[] depth = new int[count];
            foreach (IntVec3 c in map.AllCells)
            {
                int b = bands != null && bands.Banded ? bands.BandOf(c) : 0;
                TerrainDef t = grid.TerrainAt(c);
                if (t == null)
                {
                    continue;
                }
                if (t.IsWater)
                {
                    water[b]++;
                    if (t.waterDepthMaterial != null)
                    {
                        depth[b]++;
                    }
                }
                if (grid.BaseTerrainAt(c).IsRiver)
                {
                    river[b]++;
                }
            }
            for (int b = 0; b < count; b++)
            {
                sb.AppendLine("  band " + b
                    + (bands != null && bands.Banded ? " (level " + (b - bands.surfaceBand) + ")" : "")
                    + ": water=" + water[b] + " withDepthMat=" + depth[b] + " baseRiver=" + river[b]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Anchor the river inside the surface band. See <see cref="ABWaterBand.ToSurfaceBand"/>
    /// for why the vanilla pick lands in a sky band.
    ///
    /// Patched on <c>GetRiverCenter</c> rather than on <c>Init</c>, because Init consumes
    /// the centre IMMEDIATELY to build the bend noise and the edge nodes - a postfix on Init
    /// would move the field after everything derived from it had already been computed. The
    /// river variants (delta, confluence, headwater, island) all inherit this method, so one
    /// patch covers the family.
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_River), "GetRiverCenter")]
    public static class Patch_TileMutatorWorker_River_ABSurfaceBand
    {
        private static void Postfix(Map map, ref IntVec3 __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                IntVec3 moved = ABWaterBand.ToSurfaceBand(map, __result);
                if (moved != __result)
                {
                    ABLog.Dev("V2: river centre " + __result + " -> " + moved + " (surface band).");
                    __result = moved;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: river anchor patch threw: " + e, 762195881);
            }
        }
    }

    /// <summary>
    /// Same remap for the lake centre, and it matters twice over: the lake noise is built
    /// from it directly, AND <c>TileMutatorWorker_River.Init</c> PREFERS an existing
    /// <c>waterInfo.lakeCenter</c> over its own roll - so a lakeshore-with-river tile would
    /// re-anchor the river back out of the surface band if only the river patch existed.
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_Lake), "GetLakeCenter")]
    public static class Patch_TileMutatorWorker_Lake_ABSurfaceBand
    {
        private static void Postfix(Map map, ref IntVec3 __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                IntVec3 moved = ABWaterBand.ToSurfaceBand(map, __result);
                if (moved != __result)
                {
                    ABLog.Dev("V2: lake centre " + __result + " -> " + moved + " (surface band).");
                    __result = moved;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: lake anchor patch threw: " + e, 762195882);
            }
        }
    }

    /// <summary>
    /// Republish the water shader's map-size global as ONE BAND, not the whole stack.
    ///
    /// <c>_MapSize</c> is a global the water shader divides world position by. On a banded
    /// map the real size is (w, bandCount * Slot), so every UV derived from it is stretched
    /// by the band count along z - a seven-level map smears the water surface 7x
    /// north-south, uniformly, on every water cell including still lakes. That is the whole
    /// "water shaders always go vertical" symptom, and it is a texture-scale artefact, not a
    /// flow-direction one, which is exactly why it did not vary from cell to cell.
    ///
    /// Publishing one Slot restores a near-square scale (128 against a 126-wide band). The
    /// flow lookup follows along via a REPEAT-wrapped folded texture - see
    /// <see cref="ABWaterBand.FoldedFlowTexture"/> - so no shader cooperation is needed.
    ///
    /// Postfix, not prefix: vanilla's own work (the WaterDepth subcamera hookup, the sky
    /// reflection, the camera matrices) must all still happen.
    /// </summary>
    [HarmonyPatch(typeof(WaterInfo), nameof(WaterInfo.SetTextures))]
    public static class Patch_WaterInfo_ABBandWaterGlobals
    {
        private static readonly AccessTools.FieldRef<MapComponent, Map> MapRef =
            AccessTools.FieldRefAccess<MapComponent, Map>("map");

        private static void Postfix(WaterInfo __instance)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Rendering) || !ABV2Debug.BandWaterGlobals)
                {
                    return;
                }
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                Shader.SetGlobalVector(ShaderPropertyIDs.MapSize,
                    new Vector4(map.Size.x, bands.Slot));
                Texture2D folded = ABWaterBand.FoldedFlowTexture(map, bands);
                if (folded != null)
                {
                    Shader.SetGlobalTexture(ShaderPropertyIDs.WaterOffsetTex, folded);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 band water globals");
            }
        }
    }

    /// <summary>Release the folded flow texture with the map.</summary>
    [HarmonyPatch(typeof(WaterInfo), nameof(WaterInfo.MapRemoved))]
    public static class Patch_WaterInfo_ABReleaseFoldedFlow
    {
        private static readonly AccessTools.FieldRef<MapComponent, Map> MapRef =
            AccessTools.FieldRefAccess<MapComponent, Map>("map");

        private static void Postfix(WaterInfo __instance)
        {
            try
            {
                ABWaterBand.Release(MapRef(__instance));
            }
            catch
            {
                // Cleanup only.
            }
        }
    }

    /// <summary>
    /// V2 see-below: the WATER DEPTH pass for the band underneath - the missing half of
    /// water seen from above.
    ///
    /// <c>SectionLayer_ABBelowV2</c> already prints below water's visible material, and that
    /// was never enough, because 1.6 water is screen-space: the visible
    /// <c>Map/TerrainWater</c> shader samples the WaterDepth subcamera's render texture at
    /// its own screen position to decide colour, shoreline and transparency. Vanilla's
    /// <c>SectionLayer_Watergen</c> only ever writes that texture at the water's REAL cells,
    /// which are one or more bands below where the camera is looking - so from an upper level
    /// the shader sampled empty depth and the water rendered as nothing at all. Not a
    /// masking bug, not a translation bug: half of a two-pass effect was missing.
    ///
    /// <c>SectionLayer_Watergen</c> is <c>internal</c> and cannot be subclassed, so its two
    /// distinguishing behaviours are reproduced: the material is
    /// <c>TerrainDef.waterDepthMaterial</c> (null for anything that is not water, which is
    /// what confines this layer to water cells for free), and DrawLayer targets the
    /// WaterDepth subcamera's Unity LAYER instead of the default one.
    /// </summary>
    public class SectionLayer_ABBelowWatergen : SectionLayer
    {
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        private static readonly Color32 Clear = new Color32(255, 255, 255, 0);

        private readonly TerrainDef[] adj = new TerrainDef[8];

        private readonly bool[] reach = new bool[8];

        private readonly HashSet<TerrainDef> edgeSet = new HashSet<TerrainDef>();

        public SectionLayer_ABBelowWatergen(Section section) : base(section)
        {
            // Terrain only, matching vanilla's Watergen: depth geometry is a pure function
            // of which cell holds which water terrain.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible
        {
            get
            {
                if (!ABGuard.On(ABGuard.Rendering) || !ABV2Debug.DrawBelowWater)
                {
                    return false;
                }
                return DebugViewSettings.drawTerrain && DebugViewSettings.drawTerrainWater;
            }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            if (!ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            try
            {
                TerrainGrid grid = map.terrainGrid;
                FogGrid fog = map.fogGrid;
                float y = AltitudeLayer.Terrain.AltitudeFor();
                bool printed = false;

                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(map) || bands.BandOf(c) <= 0 || bands.InGutter(c))
                    {
                        continue;
                    }
                    if (!ABBands.ShowsBelow(grid.TerrainAt(c)))
                    {
                        continue;
                    }
                    if (!ABBands.TryResolveVisibleBelow(map, bands, c, out IntVec3 below, out _))
                    {
                        continue;
                    }
                    // Unexplored ground is veiled by the fog fan in the below layer, so its
                    // water must not contribute depth either or the veil sits on top of a
                    // shoreline nobody has discovered.
                    if (fog.IsFogged(below))
                    {
                        continue;
                    }
                    TerrainDef def = grid.TerrainAt(below);
                    LayerSubMesh sub = def != null ? GetSubMesh(def.waterDepthMaterial) : null;
                    if (sub == null)
                    {
                        continue; // not water: waterDepthMaterial is null
                    }
                    int n = sub.verts.Count;
                    sub.verts.Add(new Vector3(c.x, y, c.z));
                    sub.verts.Add(new Vector3(c.x, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z));
                    for (int i = 0; i < 4; i++)
                    {
                        sub.colors.Add(White);
                    }
                    sub.tris.Add(n);
                    sub.tris.Add(n + 1);
                    sub.tris.Add(n + 2);
                    sub.tris.Add(n);
                    sub.tris.Add(n + 2);
                    sub.tris.Add(n + 3);
                    printed = true;

                    // Vanilla's Watergen inherits SectionLayer_Terrain's edge-fade pass, and
                    // it matters here: those fans are how a deep-water body blends into the
                    // shallow water around it. Non-water neighbours drop out on their own
                    // (null depth material), so this reproduces only the water-to-water
                    // gradient without an extra terrain test.
                    EmitDepthEdges(map, grid, below, c);
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below watergen");
            }
        }

        /// <summary>Vanilla's nine-vertex terrain edge fan, sampled one or more bands DOWN
        /// and emitted at the viewing cell.</summary>
        private void EmitDepthEdges(Map map, TerrainGrid grid, IntVec3 below, IntVec3 above)
        {
            TerrainDef self = grid.TerrainAt(below);
            edgeSet.Clear();
            IntVec3[] around = GenAdj.AdjacentCellsAroundBottom;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 nb = below + around[i];
                TerrainDef t = nb.InBounds(map) ? grid.TerrainAt(nb) : self;
                adj[i] = t;
                if (t != self && t != null && t.waterDepthMaterial != null
                    && t.edgeType != TerrainDef.TerrainEdgeType.Hard
                    && t.renderPrecedence >= self.renderPrecedence)
                {
                    edgeSet.Add(t);
                }
            }
            if (edgeSet.Count == 0)
            {
                return;
            }
            float x = above.x;
            float z = above.z;
            foreach (TerrainDef other in edgeSet)
            {
                LayerSubMesh sub = GetSubMesh(other.waterDepthMaterial);
                if (sub == null)
                {
                    continue;
                }
                int n = sub.verts.Count;
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z));
                sub.verts.Add(new Vector3(x, 0f, z));
                sub.verts.Add(new Vector3(x, 0f, z + 0.5f));
                sub.verts.Add(new Vector3(x, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z + 0.5f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z));
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z + 0.5f));
                for (int i = 0; i < 8; i++)
                {
                    reach[i] = false;
                }
                for (int i = 0; i < 8; i++)
                {
                    if (adj[i] != other)
                    {
                        continue;
                    }
                    if (i % 2 == 0)
                    {
                        reach[(i - 1 + 8) % 8] = true;
                        reach[i] = true;
                        reach[(i + 1) % 8] = true;
                    }
                    else
                    {
                        reach[i] = true;
                    }
                }
                for (int i = 0; i < 8; i++)
                {
                    sub.colors.Add(reach[i] ? White : Clear);
                }
                sub.colors.Add(Clear);
                for (int i = 0; i < 8; i++)
                {
                    sub.tris.Add(n + i);
                    sub.tris.Add(n + (i + 1) % 8);
                    sub.tris.Add(n + 8);
                }
            }
            edgeSet.Clear();
        }

        /// <summary>The other half of what makes vanilla's Watergen special: the mesh goes to
        /// the WaterDepth SUBCAMERA's Unity layer, not the default one, because it is input
        /// to a screen-space pass rather than something the player sees directly.</summary>
        public override void DrawLayer()
        {
            if (!Visible)
            {
                return;
            }
            int layerId = SubcameraDefOf.WaterDepth.LayerId;
            for (int i = 0; i < subMeshes.Count; i++)
            {
                LayerSubMesh sub = subMeshes[i];
                if (sub.finalized && !sub.disabled)
                {
                    Graphics.DrawMesh(sub.mesh, Matrix4x4.identity, sub.material, layerId);
                }
            }
        }
    }
}
