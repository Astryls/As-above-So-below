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
    /// Also from this layer: the meadow-fade fans at the plateau boundary (vanilla's
    /// real terrain fade mechanic, re-queued over the fill), the EdgeShadow drop-off
    /// ring where the mass meets open air, and the SOUTH-FACING CLIFF FACE (option B)
    /// that makes the plateau read as standing one level up.
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
            // Edge tiles sit ONE step above the field underlay, both far below the
            // cutout family: nothing of ours can ever paint over a plant, pawn or
            // wall sprite (the cover regression's root mistake).
            clone = new Material(source) { renderQueue = lowQueue + 1 };
            queueClones[source] = clone;
            return clone;
        }

        /// <summary>Field clones: the rock's ROUGH TERRAIN material (world-position
        /// sampled - genuinely textured at any scale, where the atlas' fully-linked
        /// mask-15 tile is near-flat; the run-25 lesson relearned the hard way via the
        /// cover regression) re-queued to draw over the cap terrain, under everything
        /// else.</summary>
        private static readonly Dictionary<Material, Material> fieldClones = new Dictionary<Material, Material>();

        private static Material FieldClone(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (fieldClones.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (fieldClones.Count > 512)
            {
                fieldClones.Clear();
            }
            clone = new Material(source) { renderQueue = lowQueue };
            fieldClones[source] = clone;
            return clone;
        }

        /// <summary>Terrain-mesh-shaped quad: verts + colors + tris, deliberately NO
        /// uvs - terrain shaders sample world position, and per-quad uvs are the
        /// documented muddy-smear trap. Terrain materials get their own submeshes, so
        /// uv-free and uv-carrying geometry never share one mesh.</summary>
        private static void AddTerrainQuad(LayerSubMesh sub, IntVec3 c, float y,
            Color32 south, Color32 north)
        {
            if (sub == null)
            {
                return;
            }
            int n = sub.verts.Count;
            sub.verts.Add(new Vector3(c.x, y, c.z));
            sub.verts.Add(new Vector3(c.x, y, c.z + 1));
            sub.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
            sub.verts.Add(new Vector3(c.x + 1, y, c.z));
            sub.colors.Add(south);
            sub.colors.Add(north);
            sub.colors.Add(north);
            sub.colors.Add(south);
            sub.tris.Add(n);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 2);
            sub.tris.Add(n);
            sub.tris.Add(n + 2);
            sub.tris.Add(n + 3);
        }

        public SectionLayer_ABMountainCap(Section section) : base(section)
        {
            // Buildings flag: mining a wall changes both fill eligibility and the
            // link masks of its neighbours.
            // FogOfWar: fogged ore becoming visible ore (and vice versa) changes
            // whether this layer decorates the cell.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)MapMeshFlagDefOf.FogOfWar | (ulong)ABDefOf.AB_BelowThings;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering);

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            // V2 banded maps reuse this layer wholesale. The atlas / link-mask / corner
            // filler machinery below is map-agnostic; only two things are coupled to V1's
            // pocket-map model - the "am I the sky level" test and where the GROUND cell
            // lives. On a banded map the ground is the SAME map, one Slot down in z, so it
            // resolves to a constant cell offset instead of a different Map.
            // V1's pocket-map branch is gone: there is no Map.Level()/LowerMap() any more, so
            // the layer is banded-only. On an unbanded map it simply emits nothing.
            bool banded = ABBands.Banded(map);
            if (!ABGuard.On(ABGuard.Rendering) || !banded)
            {
                return;
            }
            try
            {
                EnsureQueue();
                TerrainGrid grid = map.terrainGrid;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                // The ground is the SAME map, one Slot down in z - a constant cell offset
                // rather than a different Map.
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null)
                {
                    return;
                }
                Map ground = map;
                // One slot down is the band this one stands on - the surface for level +1,
                // and the sky band below for level +2 and up, which is exactly right: each
                // sky level's mass is projected from the level beneath it, so mountains
                // taper as they rise.
                IntVec3 groundOffset = new IntVec3(0, 0, -bands.Slot);
                int surfaceBand = bands.surfaceBand;
                ThingDef fallbackRock = FallbackRock(map);
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor();
                bool emitted = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    // Banded: EVERY band above the surface caps, not just the first one -
                    // with a multi-level plan there can be up to three. Mined-rock
                    // leave-terrain also exists in the BASEMENT bands, which must never grow
                    // a mountain top, hence a strict > rather than !=.
                    if (banded && ABBands.BandOf(map, c) <= surfaceBand)
                    {
                        continue;
                    }
                    TerrainDef t = grid.TerrainAt(c);
                    // Open air beside the mass: the drop-off gets its shadow ring and
                    // nothing else - see-through cells never take fill.
                    if (ABBands.ShowsBelow(t))
                    {
                        emitted |= EmitDropShadowFan(map, grid, cap, c);
                        continue;
                    }
                    bool minedFloor = ABMinedRockLookup.TryGetMinedRockDef(t, out ThingDef minedRock);
                    if (t != cap && !minedFloor)
                    {
                        continue;
                    }
                    // STANDING NATURAL ROCK RENDERS NATIVELY, and this layer stays out
                    // of its cells entirely.
                    //
                    // Suppressing those wall sprites (to make the mass one seamless
                    // field) was a mistake with two reported symptoms: minable rock
                    // read as walkable FLOOR - the wall/floor distinction is load
                    // bearing gameplay information, not decoration - and ore veins,
                    // robbed of the rock context their linked atlas assumes, closed
                    // their outlines into blocky glyph shapes ("strange text" on
                    // compacted steel). Vanilla wall sprites ARE how rock reads as
                    // rock; the lip a wall throws onto adjacent floor is vanilla's
                    // language for "standing rock above walkable ground", not an
                    // artifact. Redundant outlines are fixed where they are actually
                    // caused - the generator no longer speckles the mass into dozens
                    // of tiny clusters.
                    //
                    // Nothing is emitted under a wall: the sprite is opaque, so a
                    // field quad there is pure waste. Any OTHER edifice (torch,
                    // furniture, built walls) keeps the field beneath it like
                    // furniture on any floor (run-19).
                    Building edifice = c.GetEdifice(map);
                    if (edifice != null
                        && (edifice.def.mineable
                            || (edifice.def.building != null && edifice.def.building.isNaturalRock)))
                    {
                        continue;
                    }
                    // Fogged cells belong to vanilla's fog layer.
                    if (map.fogGrid.IsFogged(c))
                    {
                        continue;
                    }
                    // Rock type comes from the GROUND map's rock at this column - the
                    // stone the mass actually stands on (run-20 diagnosis: sky-side
                    // walls/leave-terrains are noise-picked independently, producing
                    // limestone patches over a slate mountain). Ground-sourced typing
                    // also merges large regions into one material = one seamless
                    // submesh. The mined-floor mapping stays for ELIGIBILITY only.
                    ThingDef rock = GroundRockAt(ground, c + groundOffset) ?? fallbackRock;
                    // Option B: is this one of the southern rim cells that form the
                    // cliff face? If so every quad in this cell carries the vertical
                    // brightness ramp, so face and lip shade together instead of the
                    // tile repainting the face at full brightness.
                    int faceDepth = CliffFaceDepth > 0
                        ? FaceDepthAt(map, bands, grid, cap, c, CliffFaceDepth)
                        : -1;
                    Color32 shadeS = faceDepth >= 0 ? FaceShade(faceDepth, false) : White;
                    Color32 shadeN = faceDepth >= 0 ? FaceShade(faceDepth, true) : White;

                    // The unified field underlay: the rock's own rough terrain,
                    // world-position sampled, on every open mass cell.
                    TerrainDef rough = rock?.building?.naturalTerrain
                        ?? fallbackRock?.building?.naturalTerrain;
                    if (rough != null)
                    {
                        Material fieldMat = FieldClone(map.terrainGrid.GetMaterial(rough, false, null));
                        if (fieldMat != null)
                        {
                            AddTerrainQuad(GetSubMesh(fieldMat), c, y, shadeS, shadeN);
                            emitted = true;
                        }
                    }
                    // NO atlas fallback here, deliberately. Drawing AtlasBaseFor() as a
                    // flat 0..1-uv quad was the "compacted steel looks like strange
                    // text" bug: that material is the whole LINKED ATLAS SHEET, so a
                    // full-cell quad crams every sub-tile into one cell and the motif
                    // repeats per cell as glyph soup. An atlas base is only ever valid
                    // through MaterialAtlasPool.SubMaterialFromAtlas. If a modded rock
                    // somehow has no rough terrain at all, this cell simply shows the
                    // cap terrain - dull, but never wrong.
                    // The plateau boundary: the meadow's own vanilla fade fan over
                    // this mass cell (run-44 wanted a soft transition; the flat-tone
                    // skirt yielded to the real fade mechanic).
                    emitted |= EmitMeadowFade(map, grid, c, y);
                    // Cardinal links in Graphic_Linked's own order (N=1 E=2 S=4
                    // W=8): a direction links when the mass continues there. Interior
                    // cells (mask 15) are the field alone - the atlas' fully-linked
                    // tile is near-flat and adds nothing but a tone seam.
                    bool n0 = Linked(map, grid, cap, c + IntVec3.North);
                    bool e0 = Linked(map, grid, cap, c + IntVec3.East);
                    bool s0 = Linked(map, grid, cap, c + IntVec3.South);
                    bool w0 = Linked(map, grid, cap, c + IntVec3.West);
                    int mask = (n0 ? 1 : 0) | (e0 ? 2 : 0) | (s0 ? 4 : 0) | (w0 ? 8 : 0);
                    if (mask == 15)
                    {
                        continue;
                    }
                    Graphic liveGraphic = LiveGraphicFor(rock);
                    if (!(liveGraphic is Graphic_Linked))
                    {
                        // Variant mode (Better Mountains): BM's look is lip-less, so
                        // the silhouette gets one deterministic variant sprite over
                        // the (BM-recolored) field; interior is the field alone.
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
                                    c.x + 0.5f + hw, c.z + 0.5f + hh, y, shadeS, shadeN);
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
                    // The silhouette: the atlas EDGE tile (pale lip + dark outline
                    // toward the unlinked side) over the field, plus the vanilla
                    // corner fillers covering the rounding the atlas bakes into tile
                    // corners (Graphic_LinkedCornerFiller's exact rule).
                    Material tile = QueueClone(MaterialAtlasPool.SubMaterialFromAtlas(baseMat, (LinkDirections)mask));
                    if (tile == null)
                    {
                        continue;
                    }
                    LayerSubMesh sub = GetSubMesh(tile);
                    AddQuad(sub, c.x, c.z, c.x + 1, c.z + 1, y, shadeS, shadeN);
                    emitted = true;
                    if (CornerFillersEnabled)
                    {
                        bool nw = Linked(map, grid, cap, c + IntVec3.North + IntVec3.West);
                        bool ne = Linked(map, grid, cap, c + IntVec3.North + IntVec3.East);
                        bool sw = Linked(map, grid, cap, c + IntVec3.South + IntVec3.West);
                        bool se = Linked(map, grid, cap, c + IntVec3.South + IntVec3.East);
                        if (sw && s0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, -1, y, shadeS);
                        }
                        if (nw && n0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, 1, y, shadeN);
                        }
                        if (ne && n0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, 1, y, shadeN);
                        }
                        if (se && s0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, -1, y, shadeS);
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
        /// plateau (run-44 "reads like a wall" feedback); the meadow fade fan
        /// supplies the soft transition instead.</summary>
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

        /// <summary>Plateau floor the mass visually melts into - EVERY natural ground
        /// the sky gen lays on a plateau, not just literal Soil/Gravel: gravel, the
        /// biome's arable terrain (matched by fertility so biome-specific soils
        /// participate), and the rocks' own ROUGH terrain patches (natural=true,
        /// FadeRough). Literal def-matching missed the rough patches, so the fill drew
        /// its wall lip with no fade against them - a hard "wall" seam INSIDE what
        /// should read as one seamless level. Mined-floor leave-terrains (RoughHewn)
        /// are natural=false and stay MASS cells, so the two classifications never
        /// overlap. Built floors are Hard-edged and deliberately excluded (a hard line
        /// against laid flooring is the vanilla look), and water is excluded outright -
        /// water materials must never be cloned into foreign render queues.</summary>
        internal static bool IsMeadowGround(TerrainDef t)
        {
            if (t == null || t.dontRender || t.IsWater)
            {
                return false;
            }
            if (t == TerrainDefOf.Soil || t == TerrainDefOf.Gravel)
            {
                return true;
            }
            // Rooftops abut the mass where a constructed roof below meets the mountain:
            // the roof tile fades into the ledge (Hard-edged, so its fade clone gets a
            // TerrainFadeRough shader swap in FadeCloneFor) instead of ending in a hard
            // line + atlas lip.
            if (t == ABDefOf.AB_RoofSurface)
            {
                return true;
            }
            if (t.edgeType == TerrainDef.TerrainEdgeType.Hard)
            {
                return false;
            }
            if (t.natural)
            {
                return true;
            }
            return t.fertility > 0f;
        }

        /// <summary>Meadow-fade clones: the meadow terrain's OWN material, re-queued just
        /// above the atlas fill so vanilla's fade fan draws over the rock instead of
        /// being buried under it (the fill deliberately sits above every terrain queue).
        /// The source queue spread (renderPrecedence) is compressed and preserved so
        /// soil still beats gravel where both fade over the same cell - flattening the
        /// spread is the documented stairstep-border trap.</summary>
        private static readonly Dictionary<Material, Material> fadeClones = new Dictionary<Material, Material>();

        private static Material FadeClone(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (fadeClones.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (fadeClones.Count > 512)
            {
                fadeClones.Clear();
            }
            int spread = Mathf.Clamp((source.renderQueue - 2000) / 25, 0, 40);
            int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : lowQueue + 449;
            int queue = Mathf.Min(lowQueue + 2 + spread, cutout - 1);
            clone = new Material(source);
            // Hard-edged sources (the rooftop tile) carry a TerrainHard shader that
            // ignores vertex alpha - the fan would render as a full opaque square.
            // Swap to TerrainFadeRough + the rough alpha-add mask, exactly what
            // TerrainDef does when it builds its own FadeRough graphics. Shader
            // assignment resets Unity's renderQueue, so the queue is set AFTER.
            if (clone.shader == ShaderDatabase.TerrainHard && ShaderDatabase.TerrainFadeRough != null)
            {
                clone.shader = ShaderDatabase.TerrainFadeRough;
                clone.SetTexture(ShaderPropertyIDs.AlphaAddTex, TexGame.AlphaAddTex);
            }
            clone.renderQueue = queue;
            fadeClones[source] = clone;
            return clone;
        }

        /// <summary>Fade fans sit this far above the tile quads and corner fillers (0.03):
        /// the atlas family depth-writes, so the fan must win the depth test outright.</summary>
        private const float MeadowFadeAltBias = 0.04f;

        private readonly bool[] fanCovered = new bool[9];

        private readonly TerrainDef[] meadowAdj = new TerrainDef[8];

        /// <summary>
        /// THE vanilla fade mechanic at the plateau boundary: each distinct meadow
        /// terrain among the eight neighbours gets its own 9-vertex edge fan emitted
        /// OVER this mass cell - the exact geometry SectionLayer_Terrain gives soil
        /// fading onto rough stone at a vanilla mountain foot, with the meadow's own
        /// world-position-sampled material so the texture continues seamlessly out of
        /// the meadow cell. Replaces the run-44 flat-tone skirt strips, which read as a
        /// painted gradient rather than grass creeping onto rock.
        /// </summary>
        private bool EmitMeadowFade(Map map, TerrainGrid grid, IntVec3 c, float y)
        {
            TerrainDef[] adj = meadowAdj;
            adj[0] = MeadowAt(map, grid, c + IntVec3.North);
            adj[1] = MeadowAt(map, grid, c + IntVec3.South);
            adj[2] = MeadowAt(map, grid, c + IntVec3.East);
            adj[3] = MeadowAt(map, grid, c + IntVec3.West);
            adj[4] = MeadowAt(map, grid, c + IntVec3.South + IntVec3.West);
            adj[5] = MeadowAt(map, grid, c + IntVec3.North + IntVec3.West);
            adj[6] = MeadowAt(map, grid, c + IntVec3.North + IntVec3.East);
            adj[7] = MeadowAt(map, grid, c + IntVec3.South + IntVec3.East);
            bool emitted = false;
            for (int i = 0; i < 8; i++)
            {
                TerrainDef d = adj[i];
                if (d == null)
                {
                    continue;
                }
                bool seen = false;
                for (int j = 0; j < i; j++)
                {
                    if (adj[j] == d)
                    {
                        seen = true;
                        break;
                    }
                }
                if (seen)
                {
                    continue;
                }
                Material mat = FadeClone(map.terrainGrid.GetMaterial(d, false, null));
                if (mat == null)
                {
                    continue;
                }
                ABNineFan.Cover(fanCovered,
                    adj[0] == d, adj[1] == d, adj[2] == d, adj[3] == d,
                    adj[4] == d, adj[5] == d, adj[6] == d, adj[7] == d);
                ABNineFan.AddFan(GetSubMesh(mat), c.x, c.z, y + MeadowFadeAltBias, fanCovered,
                    White, ClearWhite);
                emitted = true;
            }
            return emitted;
        }

        /// <summary>The neighbour's terrain when it is meadow ground the fade may sample,
        /// else null. Mirrors vanilla's Underwall rule: a coversFloor edifice owns its
        /// cell's look, so nothing fades out of it.</summary>
        private static TerrainDef MeadowAt(Map map, TerrainGrid grid, IntVec3 n)
        {
            if (!n.InBounds(map))
            {
                return null;
            }
            TerrainDef t = grid.TerrainAt(n);
            if (!IsMeadowGround(t))
            {
                return null;
            }
            Building ed = map.edificeGrid[n];
            if (ed != null && ed.def.coversFloor)
            {
                return null;
            }
            return t;
        }

        private static readonly Color32 EdgeShadowed = new Color32(195, 195, 195, byte.MaxValue);

        private static readonly Color32 EdgeLit = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        /// <summary>
        /// The drop-off ring: where the sky band's open air borders the mass, the cliff
        /// lip gets vanilla's EdgeShadow contact line, drawn INTO the open-air cell over
        /// the see-below view - the same visual language a wall casts onto the floor
        /// beside it, which is what makes the plateau read as HIGH ground rather than a
        /// flat texture change. Ledge cells have no edifice, so vanilla's own
        /// EdgeShadows layer emits nothing at the drop; this fan is that missing border.
        /// EdgeShadow's queue (above the cutout family) also puts it over every below
        /// print, and the fan shape self-composes without the double-multiply overlaps
        /// hand-built corner strips would produce.
        /// </summary>
        private bool EmitDropShadowFan(Map map, TerrainGrid grid, TerrainDef cap, IntVec3 c)
        {
            bool n = MassNeighbor(map, grid, cap, c + IntVec3.North);
            bool s = MassNeighbor(map, grid, cap, c + IntVec3.South);
            bool e = MassNeighbor(map, grid, cap, c + IntVec3.East);
            bool w = MassNeighbor(map, grid, cap, c + IntVec3.West);
            bool sw = MassNeighbor(map, grid, cap, c + IntVec3.South + IntVec3.West);
            bool nw = MassNeighbor(map, grid, cap, c + IntVec3.North + IntVec3.West);
            bool ne = MassNeighbor(map, grid, cap, c + IntVec3.North + IntVec3.East);
            bool se = MassNeighbor(map, grid, cap, c + IntVec3.South + IntVec3.East);
            if (!(n | s | e | w | sw | nw | ne | se))
            {
                return false;
            }
            ABNineFan.Cover(fanCovered, n, s, e, w, sw, nw, ne, se);
            ABNineFan.AddFan(GetSubMesh(MatBases.EdgeShadow), c.x, c.z,
                AltitudeLayer.Shadows.AltitudeFor(), fanCovered, EdgeShadowed, EdgeLit);
            return true;
        }

        private static bool MassNeighbor(Map map, TerrainGrid grid, TerrainDef cap, IntVec3 n)
        {
            return n.InBounds(map) && IsMassCell(map, grid, cap, n);
        }

        /// <summary>
        /// The mass SILHOUETTE for one cell, emitted into ANOTHER layer at a z offset.
        ///
        /// Exists because a mountain on a lower level used to read as flat ground from above.
        /// The see-below view can reproduce terrain and things, but the thing that makes a
        /// mass look like a mountain - the atlas edge tile's pale top lip and dark outline,
        /// plus the corner fillers - is emitted by THIS layer into the mass' own band's
        /// section mesh, which is invisible from any other level. Diagnosed from a below-
        /// layer report: the submeshes showed substituted rough stone and no edge material at
        /// all, so nothing was failing to print - the decoration simply lived elsewhere.
        ///
        /// Interior cells (mask 15) return false: their atlas tile is the near-flat one and
        /// the substituted rough terrain already reads better. Cells holding natural rock
        /// return false too - their own wall sprite carries the edge, and the below view
        /// prints that itself.
        /// </summary>
        internal static bool EmitMassSilhouetteAt(MapDrawLayer layer, Map map, IntVec3 source,
            int zOffset, float altitude)
        {
            if (layer == null || map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            TerrainGrid grid = map.terrainGrid;
            TerrainDef cap = ABDefOf.AB_MountainTop;
            if (!IsMassCell(map, grid, cap, source))
            {
                return false;
            }
            Building edifice = map.edificeGrid[source];
            if (edifice != null
                && (edifice.def.mineable
                    || (edifice.def.building != null && edifice.def.building.isNaturalRock)))
            {
                return false; // its own sprite draws the edge
            }
            EnsureQueue();
            ThingDef rock = GroundRockAt(map, source + new IntVec3(0, 0, -bands.Slot))
                ?? FallbackRock(map);
            if (!(LiveGraphicFor(rock) is Graphic_Linked))
            {
                return false; // variant-mode (Better Mountains) rocks have no lip to draw
            }
            Material baseMat = AtlasBaseFor(rock);
            if (baseMat == null)
            {
                return false;
            }
            bool n0 = Linked(map, grid, cap, source + IntVec3.North);
            bool e0 = Linked(map, grid, cap, source + IntVec3.East);
            bool s0 = Linked(map, grid, cap, source + IntVec3.South);
            bool w0 = Linked(map, grid, cap, source + IntVec3.West);
            int mask = (n0 ? 1 : 0) | (e0 ? 2 : 0) | (s0 ? 4 : 0) | (w0 ? 8 : 0);
            if (mask == 15)
            {
                return false; // interior
            }
            Material tile = QueueClone(
                MaterialAtlasPool.SubMaterialFromAtlas(baseMat, (LinkDirections)mask));
            if (tile == null)
            {
                return false;
            }
            LayerSubMesh sub = layer.GetSubMesh(tile);
            if (sub == null)
            {
                return false;
            }
            IntVec3 at = new IntVec3(source.x, source.y, source.z + zOffset);
            AddQuad(sub, at.x, at.z, at.x + 1, at.z + 1, altitude, White, White);
            if (CornerFillersEnabled)
            {
                bool nw = Linked(map, grid, cap, source + IntVec3.North + IntVec3.West);
                bool ne = Linked(map, grid, cap, source + IntVec3.North + IntVec3.East);
                bool sw = Linked(map, grid, cap, source + IntVec3.South + IntVec3.West);
                bool se = Linked(map, grid, cap, source + IntVec3.South + IntVec3.East);
                if (sw && s0 && w0)
                {
                    AddCornerFiller(sub, map, at, -1, -1, altitude, White);
                }
                if (nw && n0 && w0)
                {
                    AddCornerFiller(sub, map, at, -1, 1, altitude, White);
                }
                if (ne && n0 && e0)
                {
                    AddCornerFiller(sub, map, at, 1, 1, altitude, White);
                }
                if (se && s0 && e0)
                {
                    AddCornerFiller(sub, map, at, 1, -1, altitude, White);
                }
            }
            return true;
        }

        /// <summary>
        /// OPTION B - the south-facing cliff face.
        ///
        /// RimWorld's camera looks straight down, so only SOUTH faces are ever visible;
        /// this is the same reason vanilla draws wall south-faces and throws sun-shadow
        /// skirts south. The face is rendered INSIDE the mass' own southern rim cells
        /// (which option A guarantees exist) as a vertical brightness ramp, NOT projected
        /// into the open-air cell below the drop. That containment is deliberate and is
        /// the whole reason this is safe: open-air cells are where the see-below view
        /// prints surface content at the cutout queue, so geometry drawn there would
        /// either be overdrawn by a surface tree or - if we out-queued it - repeat the
        /// sprite-erasing cover regression. Clipping to cells we already own is immune to
        /// both queue and depth contests.
        ///
        /// Set to 0 to disable (dev A/B); 1 gives a thin lip, 2 reads as a real wall.
        /// </summary>
        internal static int CliffFaceDepth = 2;

        /// <summary>Brightness at the very bottom of the face. Vertex colour multiplies
        /// the terrain shader, so this dims the real rock texture rather than painting a
        /// flat tone over it - the rock detail stays visible down the whole face.</summary>
        private const float FaceDarkest = 0.42f;

        /// <summary>Depth 0 is the cell whose south neighbour is the drop. The ramp runs
        /// from FaceDarkest at the band's bottom edge to full brightness at its top, so a
        /// two-cell face is one continuous gradient rather than two flat steps.</summary>
        private static Color32 FaceShade(int depthFromDrop, bool northEdge)
        {
            float span = Mathf.Max(CliffFaceDepth, 1);
            float height = depthFromDrop + (northEdge ? 1f : 0f);
            float b = Mathf.Lerp(FaceDarkest, 1f, Mathf.Clamp01(height / span));
            byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255);
            return new Color32(v, v, v, byte.MaxValue);
        }

        /// <summary>How far this cell sits north of a southward drop, or -1 when it is
        /// not part of a face band. Gutter cells never count as a drop, so no phantom
        /// face appears along the band seam.</summary>
        private static int FaceDepthAt(Map map, ABBandMap bands, TerrainGrid grid,
            TerrainDef cap, IntVec3 c, int maxDepth)
        {
            for (int d = 0; d < maxDepth; d++)
            {
                IntVec3 probe = new IntVec3(c.x, c.y, c.z - 1 - d);
                if (!probe.InBounds(map) || bands.InGutter(probe))
                {
                    return -1;
                }
                if (ABBands.ShowsBelow(grid.TerrainAt(probe)))
                {
                    return d;
                }
                if (!IsMassCell(map, grid, cap, probe))
                {
                    return -1;
                }
            }
            return -1;
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
            return t == cap || ABMinedRockLookup.TryGetMinedRockDef(t, out _);
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
            if (IsHostStone(ed?.def))
            {
                return ed.def;
            }
            if (ABMinedRockLookup.TryGetMinedRockDef(ground.terrainGrid.TerrainAt(c), out ThingDef mined))
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
                if (IsHostStone(nEd?.def))
                {
                    return nEd.def;
                }
            }
            return null;
        }

        /// <summary>
        /// Host STONE only - never an ore, and never a rock without generated rough
        /// terrain.
        ///
        /// Ore is embedded IN stone: the mountain top above a vein is that host stone,
        /// not the vein. More importantly `TerrainDefGenerator_Stone.ImpliedTerrainDefs`
        /// only builds the `_Rough` / `_RoughHewn` terrains for
        /// `isNaturalRock &amp;&amp; !isResourceRock`, so **ore defs have a null
        /// `building.naturalTerrain`**. Typing a cell from an ore therefore left the
        /// field underlay with no terrain material to draw and fell through to an atlas
        /// fallback that rendered the whole sheet per cell - the "compacted steel looks
        /// like text" report. Requiring naturalTerrain here makes that unrepresentable
        /// rather than merely unlikely.
        /// </summary>
        private static bool IsHostStone(ThingDef def)
        {
            return def != null && def.mineable && def.building != null
                && !def.building.isResourceRock && def.building.naturalTerrain != null;
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
        private static void AddCornerFiller(LayerSubMesh sub, Map map, IntVec3 c, int dx, int dz,
            float y, Color32 color)
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
                y + FillerAltBias, color);
        }

        /// <summary>Vanilla CornerFillUVs: all four verts sample the tile's solid
        /// point (0.5, 0.6) - the filler is a flat-toned square.</summary>
        private static readonly Vector2 CornerFillUV = new Vector2(0.5f, 0.6f);

        private static void AddCornerQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1,
            float y, Color32 color)
        {
            int vi = sub.verts.Count;
            sub.verts.Add(new Vector3(x0, y, z0));
            sub.verts.Add(new Vector3(x0, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y + NorthAltBias, z1));
            sub.verts.Add(new Vector3(x1, y, z0));
            for (int i = 0; i < 4; i++)
            {
                sub.uvs.Add(CornerFillUV);
                sub.colors.Add(color);
            }
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }

        private static readonly Color32 White = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        private static readonly Color32 ClearWhite = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, 0);

        /// <summary>Vanilla Printer_Plane tilts every plane: the north (z+) verts
        /// sit +0.01 higher, giving deterministic overlap at row seams. Without it,
        /// horizontal seam dashes appear at cell bottoms (run-19).</summary>
        private const float NorthAltBias = 0.01f;

        private static void AddQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y,
            Color32 south, Color32 north)
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
            sub.colors.Add(south);
            sub.colors.Add(north);
            sub.colors.Add(north);
            sub.colors.Add(south);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 1);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi);
            sub.tris.Add(vi + 2);
            sub.tris.Add(vi + 3);
        }
    }
}
