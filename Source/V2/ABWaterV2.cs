using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// ⚠ REJECTED - DO NOT RETRY: republishing <c>_MapSize</c> as one band.
    /// <c>WaterInfo.SetTextures</c> publishes <c>_MapSize</c> as the map's REAL size, which
    /// on a banded map is the whole stack. The theory was that the water shader UVs its
    /// surface by <c>worldPos.xz / _MapSize</c>, so a stacked map smears water
    /// bandCount-times north-south - which fitted "water always goes vertical, even in
    /// lakes" perfectly, including its uniformity. It shipped behind an A/B toggle and was
    /// **DISPROVED IN ONE LAUNCH**: republishing as one Slot is what MAKES the water run
    /// north-south, and vanilla's full-stack value looks correct.
    /// The measurement was airtight because <c>surfaceBand</c> was 0 on the test map, so the
    /// band fold was the IDENTITY and both addressings resolved to the same texture row -
    /// the flow data reaching the shader was bit-identical, leaving <c>_MapSize.y</c> as the
    /// only variable. The band-folded REPEAT-wrapped flow texture went with it: vanilla's
    /// full-height texture is already 1:1 correct for every band, because rivers only ever
    /// exist on the surface band.
    /// The north-south symptom was most likely the GHOST RIVER instead - a river generated
    /// into a sky band and carved away still leaves a flow field behind for surviving water
    /// to sample - which the anchoring fix below removes at the source.
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
            // Read back deliberately. _MapSize was the prime suspect for the north-south
            // symptom and was DISPROVED in play (see the file header); keeping it in the
            // report is what stops it being re-theorised about later.
            sb.AppendLine("published _MapSize: " + Shader.GetGlobalVector(ShaderPropertyIDs.MapSize)
                + "  (vanilla publishes the FULL STACK, and vanilla is correct here)");

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
    /// COASTLINES ON A STACKED MAP.
    ///
    /// Symptom: a coastal colony generated with its ENTIRE surface band underwater - the
    /// start-spot search reported "36100 water" out of exactly 36100 cells. 100%, not
    /// "mostly", and that precision is what identified it: a lake CANNOT do that (its radius
    /// is 0.6x the map WIDTH, so the band's corners always stay dry), only a map-spanning
    /// coastline can.
    ///
    /// THE FALLOFF IS NOT SCALED WRONG, IT IS CENTRED WRONG. In
    /// <c>MapNoiseUtility.FalloffAtAngle</c> the gradient's size comes only from
    /// <c>map.Size.x</c> (<c>DistFromAxis_Directional(x/2)</c>, then a translate by
    /// <c>x*(0.5-offsetPct)</c>). <c>map.Size.z</c> appears in exactly ONE place: the final
    /// <c>Translate(-x/2, 0, -z/2)</c> that parks the field on the map centre. So on a stacked
    /// map the coast gradient is already the right SIZE - one band wide - and merely sits at
    /// the middle of the whole STACK. A surface band hundreds of cells from a gradient only
    /// ~190 wide is fully saturated: all ocean, or all land.
    ///
    /// The correction is a coordinate rewrite, not a rebuild:
    ///     <c>z' = (z % Slot) + (map.Size.z - bandHeight) / 2</c>
    /// The modulo puts EVERY band on the same rows, so the ocean lands in the same place on
    /// every level - which is what a stacked coastal colony wants. The offset re-centres the
    /// band on the field's centre, giving exactly the window an ordinary bandHeight-tall map
    /// would see.
    ///
    /// NO SCALING, deliberately. Stretching band-local z to span the full map height is the
    /// obvious way to "fit" the gradient and it would have skewed every coastline that is not
    /// axis-aligned (the rotation happens INSIDE the module) and made the displacement noise
    /// anisotropic, smearing beach detail. Re-centring keeps the field isotropic and the angle
    /// exact.
    ///
    /// ONE PATCH COVERS THE WHOLE FAMILY: Bay, Cove, Fjord, Peninsula, Archipelago,
    /// CoastalIsland, CoastalAtoll, Iceberg and Lakeshore all derive from
    /// <c>TileMutatorWorker_Coast</c> and NONE of them override <c>GetNoiseValue</c> - it is
    /// the single point where the coast field is read, by both the elevation pass and the
    /// terrain pass.
    ///
    /// The map comes from <c>MapGenerator.mapBeingGenerated</c> because GetNoiseValue is not
    /// handed one; that also makes this work during a MAP PREVIEW, which sets the same field
    /// and whose band layout TryPendingSurfaceRect infers from the size.
    ///
    /// STILL OPEN: Basin, Cavern, Chasm, Cliffs, Hollow and Valley use FalloffAtAngle too and
    /// carry the same mis-centring, but they do not share this sampling hook.
    /// </summary>
    [HarmonyPatch(typeof(TileMutatorWorker_Coast), "GetNoiseValue")]
    public static class Patch_TileMutatorWorker_Coast_ABBandLocal
    {
        private static void Prefix(ref IntVec3 cell)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                Map map = MapGenerator.mapBeingGenerated;
                if (map == null)
                {
                    return;
                }
                // The rewrite itself now lives in ABBandLocal, because three more
                // consumers of it turned up in the VEE audit (§20). Behaviour is
                // unchanged - this was the original and is still the reference case.
                ABBandLocal.TryRemap(map, ref cell);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: coast band-local patch threw: " + e, 762195883);
            }
        }
    }

    /// <summary>
    /// RIVER WIDTH ON A NARROW BAND (§20 row 7).
    ///
    /// §6b remaps the river CENTRE into the surface band and that has always been enough for
    /// vanilla, whose widest river is 30 cells on a map at least 250 wide - about 12%. It is
    /// not enough once a mod widens them: Grand Rivers Reborn raises LargeRiver to 23 and
    /// Epic Rivers adds a 50-cell EpicRiver, and a 50-cell river on a 126-cell band is 40% of
    /// the level, wide enough to reach the permanently-fogged gutter rows and drown most of
    /// the buildable surface.
    ///
    /// The centre fix and this one are the SAME slicing-rule row seen twice: an anchor was
    /// remapped but the extent measured off it was not. Width is not a fraction of anything
    /// in vanilla - it is an absolute cell count authored against a full-size map - so it has
    /// to be re-based onto the band the river actually occupies.
    ///
    /// PATCHED ON THE PRODUCER, NOT THE DEF. Clamping <c>RiverDef.widthOnMap</c> directly
    /// would be one line and is rejected for the same reason §16 rejected swapping
    /// <c>WeatherDef.Worker.overlays</c>: a Def is global and shared across every map in the
    /// save, so an ordinary quest-site map generated later in the same session would inherit
    /// a river narrowed for a banded colony.
    ///
    /// <c>GenerateRiverGraph</c> is the one place a width reaches
    /// <c>map.waterInfo.riverGraph</c>, but it is virtual and the delta / confluence /
    /// headwater / island variants each declare their own override, so TargetMethods sweeps
    /// every loaded declaration rather than trusting the base. That also picks up modded
    /// workers for free. The postfix is idempotent (a clamp), so a subclass that chains to
    /// base and gets patched twice is harmless.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_TileMutatorWorker_ABRiverWidth
    {
        /// <summary>Widest a river may be, as a fraction of band height. Vanilla's own worst
        /// case is LargeRiver at 30 on a 250 map (12%); this leaves real headroom above that
        /// while keeping a 126-band's river under 28 cells and well clear of the gutter.</summary>
        private const float MaxWidthFraction = 0.22f;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type t in typeof(TileMutatorWorker).AllSubclassesNonAbstract())
            {
                MethodInfo m = AccessTools.DeclaredMethod(t, "GenerateRiverGraph");
                if (m != null)
                {
                    yield return m;
                }
            }
            MethodInfo baseMethod = AccessTools.DeclaredMethod(typeof(TileMutatorWorker_River), "GenerateRiverGraph");
            if (baseMethod != null)
            {
                yield return baseMethod;
            }
        }

        private static void Postfix(Map map)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen) || map?.waterInfo?.riverGraph == null)
                {
                    return;
                }
                if (!ABBandLocal.TryBandGeometry(map, out int bandHeight, out _, out _))
                {
                    return;
                }
                float max = bandHeight * MaxWidthFraction;
                List<RiverNode> graph = map.waterInfo.riverGraph;
                for (int i = 0; i < graph.Count; i++)
                {
                    RiverNode node = graph[i];
                    if (node.width <= max)
                    {
                        continue;
                    }
                    ABLog.Dev("V2: river width " + node.width.ToString("0.#") + " -> "
                        + max.ToString("0.#") + " (band height " + bandHeight + ").");
                    node.width = max;
                    graph[i] = node; // RiverNode may be a struct - write back by index
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: river width clamp threw: " + e, 762195884);
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
            // Terrain for OWN-band cells, matching vanilla's Watergen: depth geometry is a
            // pure function of which cell holds which water terrain. AB_BelowThings is the
            // mirrored below-change signal (§36c-B1) - the water this layer draws lives on
            // the band BELOW, so its terrain changes arrive only through the mirror.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)ABDefOf.AB_BelowThings;
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
                float y = AltitudeLayer.Terrain.AltitudeFor();
                bool printed = false;

                foreach (IntVec3 c in section.CellRect)
                {
                    // THE shared see-below gate. requireUnfogged is true because unexplored
                    // ground is veiled by the fog fan in the below layer, so its water must
                    // not contribute depth either - otherwise the veil sits on top of a
                    // shoreline nobody has discovered.
                    if (!ABBands.TryResolveVisibleFrom(map, bands, c, requireUnfogged: true,
                            out IntVec3 below, out _))
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
