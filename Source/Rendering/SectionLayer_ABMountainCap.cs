using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Rock-top rendering for the sky level's mountain mass (final spec, run-17
    /// reference photo: an unfogged granite group on the surface — one CONNECTED
    /// vanilla texture). The open "floor" cells of the mass render with the rock
    /// type's OWN LINKED ATLAS, exactly the machinery the walls themselves use:
    ///
    ///  - WALL/edifice cells emit nothing: vanilla wall sprites + vanilla fog
    ///    render natively.
    ///  - Every open mass cell (ledge ring, mined tunnels, bare cap terrain) prints
    ///    exactly what a vanilla CornerFiller wall would: the atlas tile for its
    ///    link mask (which neighbours continue the mass - walls or other mass
    ///    cells; map edge counts as linked - in Graphic_Linked's N=1 E=2 S=4 W=8
    ///    order) PLUS the vanilla quarter-cell corner fillers wherever a diagonal
    ///    and both flanking cardinals link, covering the rounding the atlas bakes
    ///    into every tile corner. The edge tiles provide the pale top lip and
    ///    black outline at true air borders; everywhere else tile + fillers
    ///    compose the same seamless field native wall groups show, so the whole
    ///    mass reads as ONE connected vanilla texture per stone type. Native
    ///    walls keep their vanilla edge lips facing the fill: that boundary is
    ///    standing rock above walkable floor. Force-linking it away (run-25
    ///    ShouldLinkWith postfix, reverted) flattened the unfogged wall band
    ///    into the atlas' near-untextured mask-15 tile - "square gray instead
    ///    of rock" - because the interior tile only reads as rock next to its
    ///    edge tiles.
    ///  - Per-cell rock type: mined floors via their leave-terrain -> rock def map;
    ///    bare cells from the nearest standing rock wall; map-rock fallback.
    ///
    /// Historical: fog-material fills, solid color fills, and rim decal tiling all
    /// preceded this and were overruled by playtest reference photos (runs 12-17).
    /// The run-22/23 answer to the baked corner rounding (flat single-texel
    /// interior quads + inset edge base quads) made the fill a flat-vs-textured
    /// patchwork against the native wall field and is replaced by the exact
    /// vanilla filler geometry (run-24).
    /// Kill switch: Rendering; regenerates on terrain and building changes.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABMountainCap : SectionLayer
    {
        private static int lowQueue;

        private static bool queueReady;

        private static void EnsureQueue()
        {
            if (queueReady)
            {
                return;
            }
            // Sample the MAX across every terrain family we stand on (the
            // BelowQueueCeiling / WallRevealQueue lesson: terrain shader
            // families do NOT share one queue, and sampling Soil alone can
            // park the fill under another family's terrain).
            int terrain = 0;
            terrain = MaxQ(terrain, TerrainDefOf.Soil?.graphic?.MatSingle);
            terrain = MaxQ(terrain, ABDefOf.AB_RoofSurface?.graphic?.MatSingle);
            terrain = MaxQ(terrain, ABDefOf.AB_MountainTop?.graphic?.MatSingle);
            terrain = MaxQ(terrain, TerrainDefOf.MetalTile?.graphic?.MatSingle);
            terrain = MaxQ(terrain, TerrainDefOf.WoodPlankFloor?.graphic?.MatSingle);
            if (ShaderDatabase.TerrainHard != null && ShaderDatabase.TerrainHard.renderQueue >= 500)
            {
                terrain = Mathf.Max(terrain, ShaderDatabase.TerrainHard.renderQueue);
            }
            if (terrain < 500)
            {
                terrain = 2000;
            }
            int shadow = MatBases.EdgeShadow != null ? MatBases.EdgeShadow.renderQueue : terrain;
            int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : terrain + 450;
            // Above terrain and ambient edge shadows; below the cutout family so
            // real wall sprites and their overhang decals draw over the fill,
            // exactly like walls over vanilla rough stone.
            lowQueue = Mathf.Clamp(Mathf.Max(terrain, shadow) + 1, Mathf.Min(terrain + 1, cutout - 1), cutout - 1);
            queueReady = true;
        }

        private static int MaxQ(int current, Material m)
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

        /// <summary>Probe support: names the fill branch and material the cap
        /// layer would use at one cell, plus the queue relationship against
        /// the cap terrain underlay - so a "wrong look" report pins the
        /// failing stage (branch choice, variant harvest, tint, or queue
        /// inversion) without guesswork.</summary>
        internal static string DebugCapFillInfo(Map sky, Map ground, IntVec3 c)
        {
            try
            {
                EnsureQueue();
                ThingDef rock = GroundRockAt(ground, c) ?? FallbackRock(sky);
                Graphic g = LiveGraphicFor(rock);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.Append("fill: rock=").Append(rock?.defName ?? "null")
                    .Append(" graphic=").Append(g?.GetType().Name ?? "null")
                    .Append(" drawSize=").Append(g != null ? g.drawSize.ToString() : "-");
                if (g is Graphic_Linked)
                {
                    Material baseMat = AtlasBaseFor(rock);
                    sb.Append(" branch=atlas baseMat=")
                        .Append(baseMat != null ? baseMat.name : "NULL");
                }
                else
                {
                    Material[] variants = VariantsFor(rock);
                    if (variants != null)
                    {
                        Material chosen = variants[StableCellIndex(c, variants.Length)];
                        Material clone = QueueClone(chosen);
                        sb.Append(" branch=variant count=").Append(variants.Length)
                            .Append(" mat=").Append(chosen != null ? chosen.name : "NULL")
                            .Append(" shader=").Append(chosen != null && chosen.shader != null ? chosen.shader.name : "-")
                            .Append(" color=").Append(chosen != null ? chosen.color.ToString() : "-")
                            .Append(" cloneQueue=").Append(clone != null ? clone.renderQueue : -1);
                    }
                    else
                    {
                        Material flat = AtlasBaseFor(rock);
                        sb.Append(" branch=FALLBACK-FLAT mat=")
                            .Append(flat != null ? flat.name : "NULL");
                    }
                }
                Material capMat = ABDefOf.AB_MountainTop?.graphic?.MatSingle;
                sb.Append(" lowQueue=").Append(lowQueue)
                    .Append(" capTerrainQueue=").Append(capMat != null ? capMat.renderQueue : -1)
                    .Append(" guard=").Append(ABGuard.On(ABGuard.Rendering) ? "on" : "OFF");
                return sb.ToString();
            }
            catch (System.Exception e)
            {
                return "fill probe failed: " + e.Message;
            }
        }

        private static readonly AccessTools.FieldRef<Graphic_Linked, Graphic> SubGraphicRef =
            AccessTools.FieldRefAccess<Graphic_Linked, Graphic>("subGraphic");

        private static readonly AccessTools.FieldRef<Graphic_Random, Graphic[]> SubGraphicsRef =
            AccessTools.FieldRefAccess<Graphic_Random, Graphic[]>("subGraphics");

        /// <summary>The def's LIVE graphic: graphicData.Graphic (lazily rebuilt
        /// from the CURRENT graphicData), NOT the def.graphic field. The field
        /// is baked once at PostLoad and never refreshed - Better Mountains
        /// swaps rockDef.graphicData at startup and vanilla keeps working only
        /// because Thing rendering also reads graphicData.Graphic; reading the
        /// stale field made our fill render the vanilla atlas while the walls
        /// around it rendered BM art (run #53 probe: graphic=
        /// Graphic_LinkedCornerFiller on a BM-swapped Granite).</summary>
        private static Graphic LiveGraphicFor(ThingDef rockDef)
        {
            GraphicData gd = rockDef?.graphicData;
            if (gd != null)
            {
                try
                {
                    Graphic g = gd.Graphic;
                    if (g != null && g != BaseContent.BadGraphic)
                    {
                        return g;
                    }
                }
                catch
                {
                    // fall through to the baked field
                }
            }
            return rockDef?.graphic;
        }

        /// <summary>The rock's atlas BASE material (the inner graphic of its linked
        /// wrapper, def-tinted). Cached per def and VALIDATED against the def's
        /// live graphic: Better Mountains replaces rockDef.graphicData wholesale
        /// (startup AND whenever its mod settings change), so a def-keyed cache
        /// alone would keep serving the old look after a swap.</summary>
        private static readonly Dictionary<ThingDef, (Graphic graphic, Material mat)> atlasBase =
            new Dictionary<ThingDef, (Graphic, Material)>();

        private static Material AtlasBaseFor(ThingDef rockDef)
        {
            if (rockDef == null)
            {
                return null;
            }
            Graphic current = LiveGraphicFor(rockDef);
            if (atlasBase.TryGetValue(rockDef, out (Graphic graphic, Material mat) entry)
                && entry.graphic == current)
            {
                return entry.mat;
            }
            Material mat = null;
            try
            {
                if (current is Graphic_Linked linked)
                {
                    Graphic inner = SubGraphicRef(linked);
                    mat = inner?.MatSingle;
                }
                mat = mat ?? current?.MatSingle;
            }
            catch
            {
                mat = current?.MatSingle;
            }
            atlasBase[rockDef] = (current, mat);
            return mat;
        }

        /// <summary>Variant materials for rocks whose graphic is NOT a linked
        /// atlas - Better Mountains swaps rocks to Graphic_Random with painterly
        /// per-cell variants. Same live-graphic validation as the atlas cache.</summary>
        private static readonly Dictionary<ThingDef, (Graphic graphic, Material[] mats)> variantMats =
            new Dictionary<ThingDef, (Graphic, Material[])>();

        private static Material[] VariantsFor(ThingDef rockDef)
        {
            if (rockDef == null)
            {
                return null;
            }
            Graphic current = LiveGraphicFor(rockDef);
            if (variantMats.TryGetValue(rockDef, out (Graphic graphic, Material[] mats) entry)
                && entry.graphic == current)
            {
                return entry.mats;
            }
            Material[] mats = null;
            try
            {
                if (current is Graphic_Random random)
                {
                    Graphic[] subs = SubGraphicsRef(random);
                    if (subs != null && subs.Length > 0)
                    {
                        List<Material> list = new List<Material>(subs.Length);
                        for (int i = 0; i < subs.Length; i++)
                        {
                            Material m = subs[i]?.MatSingle;
                            if (m != null)
                            {
                                list.Add(m);
                            }
                        }
                        if (list.Count > 0)
                        {
                            mats = list.ToArray();
                        }
                    }
                }
            }
            catch
            {
                mats = null;
            }
            variantMats[rockDef] = (current, mats);
            return mats;
        }

        /// <summary>Deterministic variant pick per cell: stable across regens
        /// and section boundaries so panning never reshuffles the rocks.</summary>
        private static int StableCellIndex(IntVec3 c, int count)
        {
            int h = (c.x * 73856093) ^ (c.z * 19349663);
            h &= int.MaxValue;
            return h % count;
        }

        /// <summary>Queue-forced clone per atlas submaterial (16 per rock at most).</summary>
        private static readonly Dictionary<Material, Material> queueClones = new Dictionary<Material, Material>();

        private static Material QueueClone(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (queueClones.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (queueClones.Count > 512)
            {
                queueClones.Clear();
            }
            clone = new Material(source) { renderQueue = lowQueue };
            queueClones[source] = clone;
            return clone;
        }

        public SectionLayer_ABMountainCap(Section section) : base(section)
        {
            // Buildings flag: mining a wall changes both fill eligibility and the
            // link masks of its neighbours.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)ABDefOf.AB_BelowThings;
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
                EnsureQueue();
                TerrainGrid grid = map.terrainGrid;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                Map ground = map.LowerMap();
                ThingDef fallbackRock = FallbackRock(map);
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor();
                bool emitted = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    TerrainDef t = grid.TerrainAt(c);
                    bool minedFloor = LevelSync.TryGetMinedRockDef(t, out ThingDef minedRock);
                    if (t != cap && !minedFloor)
                    {
                        continue;
                    }
                    // Natural rock WALLS render natively - no fill under them. Any
                    // OTHER edifice (torch, furniture, built walls) keeps the fill
                    // beneath it like furniture on any floor: skipping those exposed
                    // the bare cap terrain (run-19 "torch turns the texture into
                    // rock floor" report).
                    Building edifice = c.GetEdifice(map);
                    if (edifice != null
                        && (edifice.def.mineable
                            || (edifice.def.building != null && edifice.def.building.isNaturalRock)))
                    {
                        continue;
                    }
                    // Rock type comes from the GROUND map's rock at this column - the
                    // stone the mass actually stands on (run-20 diagnosis: sky-side
                    // walls/leave-terrains are noise-picked independently, producing
                    // limestone patches over a slate mountain). Ground-sourced typing
                    // also merges large regions into one material = one seamless
                    // submesh. The mined-floor mapping stays for ELIGIBILITY only.
                    ThingDef rock = GroundRockAt(ground, c) ?? fallbackRock;
                    // Variant mode (Better Mountains): when the rock's graphic
                    // is not a linked atlas (BM swaps rocks to Graphic_Random,
                    // painterly 2x2 variants, no atlas), the atlas machinery
                    // would sample nonsense sub-rects. Mimic what the walls
                    // themselves now do: one deterministic variant sprite per
                    // mass cell, centered at the graphic's own drawSize so
                    // neighbors overlap into the same composed rockfield BM's
                    // native walls show. No link masks, no corner fillers (no
                    // baked rounding to cover, and BM's look is lip-less);
                    // meadow fade skirts unchanged.
                    Graphic liveGraphic = LiveGraphicFor(rock);
                    if (!(liveGraphic is Graphic_Linked))
                    {
                        EmitSkirts(map, grid, c, SkirtTone(rock), y);
                        Material[] variants = VariantsFor(rock);
                        if (variants != null)
                        {
                            Material vmat = QueueClone(variants[StableCellIndex(c, variants.Length)]);
                            if (vmat != null)
                            {
                                Vector2 ds = liveGraphic != null ? liveGraphic.drawSize : Vector2.one;
                                float hw = Mathf.Max(ds.x, 1f) * 0.5f;
                                float hh = Mathf.Max(ds.y, 1f) * 0.5f;
                                LayerSubMesh vsub = GetSubMesh(vmat);
                                AddQuad(vsub, c.x + 0.5f - hw, c.z + 0.5f - hh,
                                    c.x + 0.5f + hw, c.z + 0.5f + hh, y);
                                emitted = true;
                            }
                        }
                        else
                        {
                            // Unknown custom graphic class: flat single-material
                            // fill beats sampling a wrong atlas window.
                            Material flat = QueueClone(AtlasBaseFor(rock));
                            if (flat != null)
                            {
                                LayerSubMesh fsub = GetSubMesh(flat);
                                AddQuad(fsub, c.x, c.z, c.x + 1, c.z + 1, y);
                                emitted = true;
                            }
                        }
                        continue;
                    }
                    Material baseMat = AtlasBaseFor(rock);
                    if (baseMat == null)
                    {
                        continue;
                    }
                    // Cardinal links in Graphic_Linked's own order (N=1 E=2 S=4
                    // W=8): a direction links when the mass continues there.
                    bool n0 = Linked(map, grid, cap, c + IntVec3.North);
                    bool e0 = Linked(map, grid, cap, c + IntVec3.East);
                    bool s0 = Linked(map, grid, cap, c + IntVec3.South);
                    bool w0 = Linked(map, grid, cap, c + IntVec3.West);
                    int mask = (n0 ? 1 : 0) | (e0 ? 2 : 0) | (s0 ? 4 : 0) | (w0 ? 8 : 0);
                    Material tile = QueueClone(MaterialAtlasPool.SubMaterialFromAtlas(baseMat, (LinkDirections)mask));
                    if (tile == null)
                    {
                        continue;
                    }
                    // Fade skirt: the open mass melts into adjacent meadow
                    // ground instead of ending in a hard line (run-44 feedback:
                    // the boundary read as "just a wall", not a higher layer).
                    // Flat per-rock tone, vertex-alpha gradient, drawn INSIDE
                    // the meadow cell so the lip keeps its silhouette.
                    EmitSkirts(map, grid, c, SkirtTone(rock), y);
                    // The atlas tile, then the vanilla corner fillers: a quarter
                    // -cell solid quad over every corner whose diagonal AND both
                    // flanking cardinals link (Graphic_LinkedCornerFiller's exact
                    // rule), covering the rounding every atlas tile bakes into
                    // every corner - the mask-15 interior tile included. Tile +
                    // fillers is precisely how native wall groups compose their
                    // seamless field, so junctions are gap-free AND textured;
                    // the flat interior quads and inset base quads this replaces
                    // (runs 22-23) read as flat-vs-textured patchwork (run-24).
                    LayerSubMesh sub = GetSubMesh(tile);
                    AddQuad(sub, c.x, c.z, c.x + 1, c.z + 1, y);
                    emitted = true;
                    if (CornerFillersEnabled)
                    {
                        bool nw = Linked(map, grid, cap, c + IntVec3.North + IntVec3.West);
                        bool ne = Linked(map, grid, cap, c + IntVec3.North + IntVec3.East);
                        bool sw = Linked(map, grid, cap, c + IntVec3.South + IntVec3.West);
                        bool se = Linked(map, grid, cap, c + IntVec3.South + IntVec3.East);
                        if (sw && s0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, -1, y);
                        }
                        if (nw && n0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, 1, y);
                        }
                        if (ne && n0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, 1, y);
                        }
                        if (se && s0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, -1, y);
                        }
                    }
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

        /// <summary>Linked = off-map (vanilla MapEdge link semantics), a mass
        /// cell, or meadow ground the mass fades into - linking the meadow side
        /// stops the atlas from drawing its wall lip + black outline toward the
        /// plateau (run-44 "reads like a wall" feedback); the skirt supplies
        /// the soft transition instead.</summary>
        internal static bool Linked(Map map, TerrainGrid grid, TerrainDef cap, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return true;
            }
            if (IsMassCell(map, grid, cap, c))
            {
                return true;
            }
            return IsMeadowGround(grid.TerrainAt(c));
        }

        /// <summary>Plateau meadow ground the mass visually fades into. Soil and
        /// gravel only exist on the sky level as plateau floor (rough stone
        /// already counts as a mass cell via the mined-floor mapping).</summary>
        internal static bool IsMeadowGround(TerrainDef t)
        {
            return t == TerrainDefOf.Soil || t == TerrainDefOf.Gravel;
        }

        /// <summary>Shared skirt material: white VertexColor, queued just above
        /// the atlas clones so the fade draws over both the tile edge and the
        /// meadow terrain, still under the cutout family (walls, plants).</summary>
        private static Material skirtMatCached;

        private static Material SkirtMat()
        {
            if (skirtMatCached == null)
            {
                skirtMatCached = SolidColorMaterials.NewSolidColorMaterial(Color.white, ShaderDatabase.VertexColor);
                skirtMatCached.renderQueue = lowQueue + 1;
            }
            return skirtMatCached;
        }

        // Run-46 tuning (user): dimmer at the lip, longer dissolve.
        private const float SkirtDepth = 0.8f;

        private const float SkirtAltBias = 0.035f;

        private const byte SkirtNearAlpha = 150;

        /// <summary>Flat tone for the fade: the rock's mined-floor color (the
        /// exact machinery the cap overlay family already uses for flat fills).</summary>
        private static Color SkirtTone(ThingDef rock)
        {
            TerrainDef leave = rock?.building?.leaveTerrain;
            if (leave != null && LevelSync.TryGetMinedRockColor(leave, out Color tone))
            {
                return tone;
            }
            return new Color(0.44f, 0.41f, 0.38f);
        }

        /// <summary>One gradient strip per meadow-adjacent cardinal, spanning the
        /// shared edge and reaching SkirtDepth into the meadow cell, near-solid
        /// at the lip and transparent at the far end. Neighbor cells owned by an
        /// edifice keep their own look.</summary>
        private void EmitSkirts(Map map, TerrainGrid grid, IntVec3 c, Color tone, float y)
        {
            Color32 near = new Color32((byte)(tone.r * 255f), (byte)(tone.g * 255f), (byte)(tone.b * 255f), SkirtNearAlpha);
            Color32 far = new Color32(near.r, near.g, near.b, 0);
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = c + GenAdj.CardinalDirections[i];
                if (!n.InBounds(map) || !IsMeadowGround(grid.TerrainAt(n)) || map.edificeGrid[n] != null)
                {
                    continue;
                }
                LayerSubMesh sub = GetSubMesh(SkirtMat());
                float yq = y + SkirtAltBias;
                int dx = n.x - c.x;
                int dz = n.z - c.z;
                if (dz == 1)
                {
                    // Neighbor to the north: fade upward from its south edge.
                    AddFadeQuad(sub, n.x, n.z, n.x + 1, n.z + SkirtDepth, yq, near, far, far, near);
                }
                else if (dz == -1)
                {
                    // South: fade downward from its north edge.
                    AddFadeQuad(sub, n.x, n.z + 1f - SkirtDepth, n.x + 1, n.z + 1, yq, far, near, near, far);
                }
                else if (dx == 1)
                {
                    // East: fade rightward from its west edge.
                    AddFadeQuad(sub, n.x, n.z, n.x + SkirtDepth, n.z + 1, yq, near, near, far, far);
                }
                else
                {
                    // West: fade leftward from its east edge.
                    AddFadeQuad(sub, n.x + 1f - SkirtDepth, n.z, n.x + 1, n.z + 1, yq, far, far, near, near);
                }
            }
        }

        /// <summary>Quad with per-vertex colors, vertex order matching AddQuad:
        /// (x0,z0), (x0,z1), (x1,z1), (x1,z0). UVs sample the material center.</summary>
        private static void AddFadeQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y,
            Color32 c00, Color32 c01, Color32 c11, Color32 c10)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            for (int i = 0; i < 4; i++)
            {
                sub.uvs.Add(new Vector2(0.5f, 0.5f));
            }
            sub.colors.Add(c00);
            sub.colors.Add(c01);
            sub.colors.Add(c11);
            sub.colors.Add(c10);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }

        /// <summary>A cell continues the mass when it holds natural rock (wall) or is
        /// itself an open mass cell (cap terrain or mined floor).</summary>
        internal static bool IsMassCell(Map map, TerrainGrid grid, TerrainDef cap, IntVec3 c)
        {
            Building ed = map.edificeGrid[c];
            if (ed != null
                && (ed.def.mineable || (ed.def.building != null && ed.def.building.isNaturalRock)))
            {
                return true;
            }
            TerrainDef t = grid.TerrainAt(c);
            return t == cap || LevelSync.TryGetMinedRockDef(t, out _);
        }

        /// <summary>The GROUND map's rock def at (or beside) the column: the standing
        /// mountain rock below, else the nearest one within a cardinal step, else the
        /// leave-terrain of a mined ground cell. Regen-time reads only.</summary>
        private static ThingDef GroundRockAt(Map ground, IntVec3 c)
        {
            if (ground == null || ground.Disposed || !c.InBounds(ground))
            {
                return null;
            }
            Building ed = ground.edificeGrid[c];
            if (ed != null && ed.def.mineable)
            {
                return ed.def;
            }
            if (LevelSync.TryGetMinedRockDef(ground.terrainGrid.TerrainAt(c), out ThingDef mined))
            {
                return mined;
            }
            IntVec3[] adj = GenAdj.AdjacentCells;
            for (int i = 0; i < adj.Length; i++)
            {
                IntVec3 n = c + adj[i];
                if (!n.InBounds(ground))
                {
                    continue;
                }
                Building nEd = ground.edificeGrid[n];
                if (nEd != null && nEd.def.mineable)
                {
                    return nEd.def;
                }
            }
            return null;
        }

        private static ThingDef FallbackRock(Map map)
        {
            try
            {
                foreach (ThingDef rock in Find.World.NaturalRockTypesIn(map.Tile))
                {
                    return rock;
                }
            }
            catch
            {
                // world data unavailable mid-gen
            }
            return ThingDefOf.Granite;
        }

        /// <summary>Dev A/B switch (debug action "AB: toggle cap corner fillers"):
        /// gates the vanilla-mirror filler pass so an artifact can be isolated to
        /// it (off = bare atlas tiles, baked rounding visible at every junction).</summary>
        internal static bool CornerFillersEnabled = true;

        /// <summary>Vanilla Graphic_LinkedCornerFiller geometry: CoverOffsetDist
        /// (DistCenterCorner - CoverSizeCornerCorner / 2) works out to exactly a
        /// quarter cell, so each 0.5 x 0.5 filler is the quarter-cell square at
        /// its corner, nudged 0.09 north like vanilla's ShiftUp vector.</summary>
        private const float FillerCornerOffset = 0.25f;

        private const float FillerNorthShift = 0.09f;

        /// <summary>Fillers sit this far above the tile quads; the atlas family
        /// depth-writes (cutout), so the bias orders them over the tiles by the
        /// same mechanism NorthAltBias orders row seams (run-19).</summary>
        private const float FillerAltBias = 0.03f;

        /// <summary>One vanilla corner filler; dx/dz pick the corner (+-1 each).
        /// Off-map diagonals shift the quad a full cell outward at five times
        /// the size, vanilla's map-edge rule, so no rounding shows against the
        /// off-map surround.</summary>
        private static void AddCornerFiller(LayerSubMesh sub, Map map, IntVec3 c, int dx, int dz, float y)
        {
            float cx = c.x + 0.5f + dx * FillerCornerOffset;
            float cz = c.z + 0.5f + dz * FillerCornerOffset + FillerNorthShift;
            float sx = 0.5f;
            float sz = 0.5f;
            IntVec3 diag = new IntVec3(c.x + dx, 0, c.z + dz);
            if (!diag.InBounds(map))
            {
                if (diag.x < 0)
                {
                    cx -= 1f;
                    sx *= 5f;
                }
                if (diag.z < 0)
                {
                    cz -= 1f;
                    sz *= 5f;
                }
                if (diag.x >= map.Size.x)
                {
                    cx += 1f;
                    sx *= 5f;
                }
                if (diag.z >= map.Size.z)
                {
                    cz += 1f;
                    sz *= 5f;
                }
            }
            AddCornerQuad(sub, cx - sx * 0.5f, cz - sz * 0.5f, cx + sx * 0.5f, cz + sz * 0.5f,
                y + FillerAltBias);
        }

        /// <summary>Vanilla CornerFillUVs: all four verts sample the tile's solid
        /// point (0.5, 0.6) - the filler is a flat-toned square.</summary>
        private static readonly Vector2 CornerFillUV = new Vector2(0.5f, 0.6f);

        private static void AddCornerQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            for (int i = 0; i < 4; i++)
            {
                sub.uvs.Add(CornerFillUV);
                sub.colors.Add(White);
            }
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }

        private static readonly Color32 White = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        /// <summary>Vanilla Printer_Plane tilts every plane: the north (z+) verts
        /// sit +0.01 higher, giving deterministic overlap at row seams. Without it,
        /// horizontal seam dashes appear at cell bottoms (run-19).</summary>
        private const float NorthAltBias = 0.01f;

        private static void AddQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            sub.uvs.Add(new Vector2(0f, 0f));
            sub.uvs.Add(new Vector2(0f, 1f));
            sub.uvs.Add(new Vector2(1f, 1f));
            sub.uvs.Add(new Vector2(1f, 0f));
            sub.colors.Add(White);
            sub.colors.Add(White);
            sub.colors.Add(White);
            sub.colors.Add(White);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }
    }
}
