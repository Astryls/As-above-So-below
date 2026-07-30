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
    /// real terrain fade mechanic, re-queued over the fill) and the EdgeShadow drop-off
    /// ring where the mass meets open air (the elevation cue).
    /// Kill switch: Rendering; regenerates on terrain and building changes.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABMountainCap : SectionLayer
    {
        private static int lowQueue;

        /// <summary>The mass-unification cover's queue: above the wall sprites (cutout),
        /// vanilla fog and the EdgeShadow family, so covered wall cells and the fogged
        /// interior hole read as the same continuous rock field as the open fill.</summary>
        private static int coverQueue;

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
            int fogQ = 0;
            if (MatBases.FogOfWar != null)
            {
                fogQ = MatBases.FogOfWar.renderQueue;
                if (fogQ <= 0 && MatBases.FogOfWar.shader != null)
                {
                    fogQ = MatBases.FogOfWar.shader.renderQueue;
                }
            }
            coverQueue = Mathf.Max(Mathf.Max(cutout, shadow), Mathf.Max(fogQ, lowQueue + 1)) + 1;
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

        /// <summary>Cover clones: same materials, queued above walls + fog + edge
        /// shadows. Separate pool because one source material serves both queues.</summary>
        private static readonly Dictionary<Material, Material> coverClones = new Dictionary<Material, Material>();

        private static Material CoverClone(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (coverClones.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (coverClones.Count > 512)
            {
                coverClones.Clear();
            }
            clone = new Material(source) { renderQueue = coverQueue };
            coverClones[source] = clone;
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
                IntVec3 groundOffset = new IntVec3(0, 0, -bands.Slot);
                int skyBand = bands.surfaceBand + 1;
                ThingDef fallbackRock = FallbackRock(map);
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor();
                // Cover geometry sits at vanilla's fog altitude: above every wall
                // sprite's depth-written pixels, so the later-queued cover can never
                // lose the depth-test tie against the cutout it conceals.
                float coverAlt = AltitudeLayer.FogOfWar.AltitudeFor();
                bool emitted = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    // Banded: only the sky band caps. Mined-rock leave-terrain also exists
                    // in the BASEMENT band, which must not grow a mountain top.
                    if (banded && ABBands.BandOf(map, c) != skyBand)
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
                    // Natural rock WALLS no longer render natively: they are COVERED
                    // with properly-masked atlas tiles in a queue above the wall
                    // sprites, vanilla fog and the EdgeShadow family, so fill ring,
                    // wall band and the fogged interior hole read as ONE connected
                    // rock field (the "texture outline errors" + "fog center hole"
                    // screenshot). Exception - visible ORE: an explored resource rock
                    // keeps its native sprite (prospecting information), while FOGGED
                    // ore is covered like plain rock, because a fog dot inside the
                    // unified field would itself be an ore tell vanilla never leaks.
                    // Any OTHER edifice (torch, furniture, built walls) keeps the fill
                    // beneath it like furniture on any floor (run-19).
                    Building edifice = c.GetEdifice(map);
                    bool coverCell = false;
                    bool resourceRock = false;
                    if (edifice != null
                        && (edifice.def.mineable
                            || (edifice.def.building != null && edifice.def.building.isNaturalRock)))
                    {
                        resourceRock = edifice.def.building != null && edifice.def.building.isResourceRock;
                        if (resourceRock && !map.fogGrid.IsFogged(c))
                        {
                            continue; // visible ore renders natively
                        }
                        coverCell = true;
                    }
                    // Rock type comes from the GROUND map's rock at this column - the
                    // stone the mass actually stands on (run-20 diagnosis: sky-side
                    // walls/leave-terrains are noise-picked independently, producing
                    // limestone patches over a slate mountain). Ground-sourced typing
                    // also merges large regions into one material = one seamless
                    // submesh. The mined-floor mapping stays for ELIGIBILITY only.
                    // A covered WALL is the one case that types from ITSELF - it IS
                    // the rock - except fogged ore, which deliberately types from the
                    // ground like its neighbours so the concealment is seamless.
                    ThingDef rock = coverCell && !resourceRock
                        ? edifice.def
                        : (GroundRockAt(ground, c + groundOffset) ?? fallbackRock);
                    float cellY = coverCell ? coverAlt : y;
                    // Variant mode (Better Mountains): when the rock's graphic
                    // is not a linked atlas (BM swaps rocks to Graphic_Random,
                    // painterly 2x2 variants, no atlas), the atlas machinery
                    // would sample nonsense sub-rects. Mimic what the walls
                    // themselves now do: one deterministic variant sprite per
                    // mass cell, centered at the graphic's own drawSize so
                    // neighbors overlap into the same composed rockfield BM's
                    // native walls show. No link masks, no corner fillers (no
                    // baked rounding to cover, and BM's look is lip-less);
                    // the meadow fade applies the same as in atlas mode.
                    Graphic liveGraphic = LiveGraphicFor(rock);
                    if (!(liveGraphic is Graphic_Linked))
                    {
                        emitted |= EmitMeadowFade(map, grid, c, cellY, coverCell);
                        Material[] variants = VariantsFor(rock);
                        if (variants != null)
                        {
                            Material vsrc = variants[StableCellIndex(c, variants.Length)];
                            Material vmat = coverCell ? CoverClone(vsrc) : QueueClone(vsrc);
                            if (vmat != null)
                            {
                                Vector2 ds = liveGraphic != null ? liveGraphic.drawSize : Vector2.one;
                                float hw = Mathf.Max(ds.x, 1f) * 0.5f;
                                float hh = Mathf.Max(ds.y, 1f) * 0.5f;
                                LayerSubMesh vsub = GetSubMesh(vmat);
                                AddQuad(vsub, c.x + 0.5f - hw, c.z + 0.5f - hh,
                                    c.x + 0.5f + hw, c.z + 0.5f + hh, cellY);
                                emitted = true;
                            }
                        }
                        else
                        {
                            // Unknown custom graphic class: flat single-material
                            // fill beats sampling a wrong atlas window.
                            Material flatSrc = AtlasBaseFor(rock);
                            Material flat = coverCell ? CoverClone(flatSrc) : QueueClone(flatSrc);
                            if (flat != null)
                            {
                                LayerSubMesh fsub = GetSubMesh(flat);
                                AddQuad(fsub, c.x, c.z, c.x + 1, c.z + 1, cellY);
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
                    Material tileSrc = MaterialAtlasPool.SubMaterialFromAtlas(baseMat, (LinkDirections)mask);
                    Material tile = coverCell ? CoverClone(tileSrc) : QueueClone(tileSrc);
                    if (tile == null)
                    {
                        continue;
                    }
                    // The plateau boundary: the meadow's own vanilla fade fan over this
                    // mass cell (run-44 wanted a soft transition; the flat-tone skirt it
                    // got now yields to the real fade mechanic).
                    emitted |= EmitMeadowFade(map, grid, c, cellY, coverCell);
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
                    AddQuad(sub, c.x, c.z, c.x + 1, c.z + 1, cellY);
                    emitted = true;
                    if (CornerFillersEnabled)
                    {
                        bool nw = Linked(map, grid, cap, c + IntVec3.North + IntVec3.West);
                        bool ne = Linked(map, grid, cap, c + IntVec3.North + IntVec3.East);
                        bool sw = Linked(map, grid, cap, c + IntVec3.South + IntVec3.West);
                        bool se = Linked(map, grid, cap, c + IntVec3.South + IntVec3.East);
                        if (sw && s0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, -1, cellY);
                        }
                        if (nw && n0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, 1, cellY);
                        }
                        if (ne && n0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, 1, cellY);
                        }
                        if (se && s0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, -1, cellY);
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

        /// <summary>Fade clones for COVERED cells: the fan must beat the cover, which
        /// already sits above fog/shadows, so this pool queues from coverQueue up.</summary>
        private static readonly Dictionary<Material, Material> fadeClonesHigh = new Dictionary<Material, Material>();

        private static Material FadeCloneFor(Material source, bool aboveCover)
        {
            if (source == null)
            {
                return null;
            }
            Dictionary<Material, Material> pool = aboveCover ? fadeClonesHigh : fadeClones;
            if (pool.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (pool.Count > 512)
            {
                pool.Clear();
            }
            int spread = Mathf.Clamp((source.renderQueue - 2000) / 25, 0, 40);
            int queue;
            if (aboveCover)
            {
                queue = coverQueue + 1 + spread;
            }
            else
            {
                int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : lowQueue + 449;
                queue = Mathf.Min(lowQueue + 2 + spread, cutout - 1);
            }
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
            pool[source] = clone;
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
        private bool EmitMeadowFade(Map map, TerrainGrid grid, IntVec3 c, float y, bool aboveCover)
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
                Material mat = FadeCloneFor(map.terrainGrid.GetMaterial(d, false, null), aboveCover);
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
            if (ed != null && ed.def.mineable)
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

        private static readonly Color32 ClearWhite = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, 0);

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
