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
    /// reference photo: an unfogged granite group on the surface - one CONNECTED
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

        /// <summary>
        /// DEV A/B SWITCH (debug action "AB2: toggle mass field fade") for the retracting
        /// field.
        ///
        /// OFF restores the historical behaviour: the rock field is a full-cell opaque quad.
        /// ON erodes it to the same neighbour rule the atlas tile's link mask uses, so the
        /// fill ends underneath the stylised outline instead of squaring it off.
        ///
        /// The reported symptom, from a night shot of a multi-level mountain: "at the
        /// mountain edges we get both the stylized black border of mineable rock AND the
        /// square texture", compounding once per level because every level draws its own
        /// field. The cause is that vanilla rock is ONLY the atlas tile - its transparent
        /// rounded corners are the silhouette - while we draw an opaque quad underneath and
        /// fill those corners back in.
        /// </summary>
        internal static bool MassFieldFadeEnabled = true;

        /// <summary>
        /// DEV A/B SWITCH (debug action "AB2: toggle mass depth cut") for the multi-level
        /// descent.
        ///
        /// OFF resolves one level per column, the shared descent's own answer. ON keeps
        /// descending THROUGH mass and stops at the first solid floor, drawing each level
        /// deepest-first so a mountain reads as one mass continuing down instead of a stack of
        /// tiers each lying on the next.
        ///
        /// ⚠ THIS IS THE ONE TOGGLE WITH A REAL RUNTIME COST. A column over deep mass emits up
        /// to one fill and one outline PER LEVEL rather than one of each, and section
        /// regeneration is this mod's documented hot spot (the unit of waste is a section
        /// regenerate, not a cell). If a mountainous map ever starts stuttering while panning,
        /// turn this off FIRST - it is the cheapest way to confirm or clear it.
        /// </summary>
        /// <summary>⚠ DEFAULTS ON, ACCEPTED ON LOOK ONLY (run #260, `massDepthCut=True` in the
        /// bisect stamp, three stone types, Mountain+Caves). It briefly defaulted OFF while
        /// the hole bug was open; that is no longer the shipped state.
        ///
        /// ⚠ THE FRAME COST WAS NEVER MEASURED. A column over deep mass emits up to one fill
        /// and one outline PER LEVEL, and section regeneration is this mod's documented hot
        /// spot. If a mountainous map stutters while panning, this is the FIRST switch to
        /// throw - and if it turns out to be the cause, flipping this default back is the
        /// whole fix.</summary>
        internal static bool MassDepthCutEnabled = true;

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

        /// <summary>Fade-capable field clone. Vanilla's generated stone terrains are
        /// <c>FadeRough</c> (`TerrainDefGenerator_Stone` sets it on both `_Rough` and
        /// `_RoughHewn`), so their materials already honour vertex alpha and carry the rough
        /// alpha-add mask that makes the retreating edge organic rather than a straight
        /// line. The swap below is for MODDED rock that ships a Hard-edged rough terrain:
        /// a Hard shader ignores vertex alpha outright, so without it the fan would render
        /// as a full opaque square and the fix would silently do nothing on that rock.
        /// Kept separate from <see cref="FieldClone"/> so the cap's opaque in-band quads
        /// cannot change shader as a side effect.</summary>
        private static readonly Dictionary<Material, Material> fieldFadeClones = new Dictionary<Material, Material>();

        private static Material FieldFadeClone(Material source)
        {
            if (source == null)
            {
                return null;
            }
            if (fieldFadeClones.TryGetValue(source, out Material clone))
            {
                return clone;
            }
            if (fieldFadeClones.Count > 512)
            {
                fieldFadeClones.Clear();
            }
            clone = new Material(source);
            if (clone.shader == ShaderDatabase.TerrainHard && ShaderDatabase.TerrainFadeRough != null)
            {
                clone.shader = ShaderDatabase.TerrainFadeRough;
                clone.SetTexture(ShaderPropertyIDs.AlphaAddTex, TexGame.AlphaAddTex);
            }
            // Shader assignment RESETS Unity's renderQueue, so this must come after.
            clone.renderQueue = lowQueue;
            fieldFadeClones[source] = clone;
            return clone;
        }

        [ThreadStatic]
        private static bool[] fieldCovered;

        [ThreadStatic]
        private static Color32[] fieldColors;

        /// <summary>
        /// THE ROCK FIELD FOR ONE MASS CELL - the single implementation, shared by the cap's
        /// own in-band pass and by the cross-level emitter, so the two cannot drift.
        ///
        /// Emitted as a RETRACTING FAN (<see cref="ABNineFan.CoverInterior"/>) rather than a
        /// full-cell quad: the atlas tile drawn on top is transparent outside the rock
        /// outline, and an opaque quad underneath fills those gaps back in, which is what
        /// turned a stylised silhouette into a square. Eroding the fill to the SAME link
        /// rule the tile's mask uses hides it under the art exactly.
        ///
        /// The caller passes its own link rule's answers, because the two callers legitimately
        /// disagree: the cap counts MEADOW ground as linked (so its fill does not retract at a
        /// plateau boundary, where the meadow fade fans own the transition and no lip is
        /// drawn), while the cross-level emitter counts only mass.
        ///
        /// An all-linked cell yields a fully covered fan, which is geometrically the same
        /// square the quad drew - so mass interiors are unchanged.
        /// </summary>
        internal static bool EmitFieldAt(MapDrawLayer layer, Map map, IntVec3 at, float y,
            TerrainDef rough, Color32 shadeS, Color32 shadeN,
            bool n, bool s, bool e, bool w, bool sw, bool nw, bool ne, bool se)
        {
            if (layer == null || map == null || rough == null)
            {
                return false;
            }
            Material source = map.terrainGrid.GetMaterial(rough, false, null);
            Material mat = MassFieldFadeEnabled ? FieldFadeClone(source) : FieldClone(source);
            LayerSubMesh sub = mat != null ? layer.GetSubMesh(mat) : null;
            if (sub == null)
            {
                return false;
            }
            if (!MassFieldFadeEnabled)
            {
                AddTerrainQuad(sub, at, y, shadeS, shadeN);
                return true;
            }
            bool[] covered = fieldCovered ?? (fieldCovered = new bool[9]);
            Color32[] colors = fieldColors ?? (fieldColors = new Color32[9]);
            ABNineFan.CoverInterior(covered, n, s, e, w, sw, nw, ne, se);
            // The cliff-face ramp travels across the fan's THREE vertex rows, not two: the
            // fan has mid-edge and centre vertices at z+0.5 that a quad does not, and giving
            // them the south tone would step the gradient instead of running it.
            Color32 mid = MidShade(shadeS, shadeN);
            colors[0] = WithCoverage(shadeS, covered[0]);
            colors[1] = WithCoverage(mid, covered[1]);
            colors[2] = WithCoverage(shadeN, covered[2]);
            colors[3] = WithCoverage(shadeN, covered[3]);
            colors[4] = WithCoverage(shadeN, covered[4]);
            colors[5] = WithCoverage(mid, covered[5]);
            colors[6] = WithCoverage(shadeS, covered[6]);
            colors[7] = WithCoverage(shadeS, covered[7]);
            colors[8] = WithCoverage(mid, covered[8]);
            ABNineFan.AddFan(sub, at.x, at.z, y, colors);
            return true;
        }

        private static Color32 MidShade(Color32 a, Color32 b)
        {
            return new Color32((byte)((a.r + b.r) / 2), (byte)((a.g + b.g) / 2),
                (byte)((a.b + b.b) / 2), byte.MaxValue);
        }

        private static Color32 WithCoverage(Color32 c, bool covered)
        {
            return new Color32(c.r, c.g, c.b, covered ? byte.MaxValue : (byte)0);
        }

        /// <summary>
        /// COVERAGE FOR A BELOW CELL'S BASE FILL - and WHICH RULE APPLIES DEPENDS ON WHO
        /// DRAWS THE OUTLINE OVER IT. Returns false when nothing will, in which case the
        /// caller must keep its full-cell quad: retracting a fill that nothing covers just
        /// punches a transparent notch.
        ///
        /// ⚠ THE FIRST VERSION OF THIS GATED ON THE DECORATION TEST AND MISSED THE COMMONEST
        /// CASE. The below-terrain mirror asked "is this cell one I emit mass decoration
        /// for?", which carries a `drop > slot` condition that exists to stop the CAP and the
        /// cross-level emitter double-drawing at one level down. That condition belongs to
        /// the DECORATION, not to the FILL. An undug rock outcrop exactly one level below the
        /// viewer therefore kept its hard quad while its own sprite - transparent at the
        /// rounded corners - sat on top, which is the "outcrops still show the floor texture"
        /// report. Two different questions had been collapsed into one gate again.
        ///
        /// A ROCK EDIFICE brings its own outline (the Mineable's sprite, printed by the thing
        /// loop because reaching this code path already proves the cell is unfogged), and
        /// vanilla rock links with rock and the MAP EDGE - never with soil. So it retracts on
        /// the MASS rule. Sky mass and deep mined floors are decorated by our own atlas tile,
        /// which uses the CAP's rule, where meadow counts as linked and no lip is drawn
        /// against grass. Matching each fill to its own outline's rule is the whole point;
        /// using one rule for both would either square off the outcrops or eat the fill at
        /// every plateau/meadow boundary.
        /// </summary>
        internal static bool TryMassFillCoverage(Map map, IntVec3 c, bool decorated, bool[] covered)
        {
            if (map == null || covered == null || !c.InBounds(map))
            {
                return false;
            }
            Building ed = map.edificeGrid[c];
            if (ed != null
                && (ed.def.mineable
                    || (ed.def.building != null && ed.def.building.isNaturalRock)))
            {
                bool n = MassLinked(map, c + IntVec3.North);
                bool s = MassLinked(map, c + IntVec3.South);
                bool e = MassLinked(map, c + IntVec3.East);
                bool w = MassLinked(map, c + IntVec3.West);
                // ⚠ AN ISOLATED ROCK KEEPS ITS FULL QUAD, and this guard is load-bearing.
                //
                // Nothing is drawn BEHIND a retracted fill: in an open-air column vanilla's
                // terrain layer emits nothing (AB_OpenAir is dontRender), so the fill IS the
                // backdrop and eroding it exposes the map background. On a mass edge that is
                // harmless and in fact desirable - the gap falls exactly where the atlas
                // outline lands, which is black anyway. A lone rock with no linked cardinal
                // erodes to the centre vertex alone, which would ring it with a dark halo
                // instead of the clean terrain vanilla shows. It also has nothing to square
                // off AGAINST: its sprite is the isolated tile, outlined on all four sides.
                if (!(n || s || e || w))
                {
                    return false;
                }
                ABNineFan.CoverInterior(covered, n, s, e, w,
                    MassLinked(map, c + IntVec3.South + IntVec3.West),
                    MassLinked(map, c + IntVec3.North + IntVec3.West),
                    MassLinked(map, c + IntVec3.North + IntVec3.East),
                    MassLinked(map, c + IntVec3.South + IntVec3.East));
                return true;
            }
            if (decorated)
            {
                MassFanCoverage(map, c, covered);
                return true;
            }
            return false;
        }

        /// <summary>Retraction coverage for a mass cell under the CAP's link rule, written
        /// into the caller's scratch. Exposed so the below-terrain mirror can erode its own
        /// base quad on exactly the cells this class then decorates, without restating the
        /// rule and letting it drift.</summary>
        internal static void MassFanCoverage(Map map, IntVec3 source, bool[] covered)
        {
            TerrainGrid grid = map.terrainGrid;
            TerrainDef cap = ABDefOf.AB_MountainTop;
            ABNineFan.CoverInterior(covered,
                Linked(map, grid, cap, source + IntVec3.North),
                Linked(map, grid, cap, source + IntVec3.South),
                Linked(map, grid, cap, source + IntVec3.East),
                Linked(map, grid, cap, source + IntVec3.West),
                Linked(map, grid, cap, source + IntVec3.South + IntVec3.West),
                Linked(map, grid, cap, source + IntVec3.North + IntVec3.West),
                Linked(map, grid, cap, source + IntVec3.North + IntVec3.East),
                Linked(map, grid, cap, source + IntVec3.South + IntVec3.East));
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
            //
            // The `banded` local (ABBands.Banded(map)) is gone with it: the bands.Banded test
            // below is the same question asked of the component we need anyway, so the
            // separate call was a second front-cache round trip per section regenerate for a
            // strictly weaker answer (ABBands.Banded also says yes to a dev spike layout,
            // which this layer cannot actually service - see below).
            if (!ABGuard.On(ABGuard.Rendering))
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
                // Require the REAL band component, not ABBands.Banded's answer - that one
                // also returns true for a dev spike layout, and everything below depends on
                // bands.Slot, which is 0 for an unbanded component. A spike map would have
                // produced a zero ground offset and sampled its own cells.
                if (bands == null || !bands.Banded)
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
                    //
                    // Through the RESOLVED component, not the static ABBands.BandOf(map, c).
                    // This is the first test in a 289-cell loop that runs for every section
                    // regenerate, and the static form re-enters the front cache, re-checks
                    // Banded and (before the spikeCount fast-out) probed a second weak table -
                    // all to answer a question the local answers with one divide. Precisely
                    // the redundancy ABBandEnv.BiomeOf's second overload exists to remove.
                    if (bands.BandOf(c) <= surfaceBand)
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

                    // Cardinal AND diagonal links, hoisted above the field: the field is now
                    // eroded to the same rule the atlas mask uses, so both need them and
                    // computing them twice is how the two rules would drift apart.
                    // (Graphic_Linked's own order: N=1 E=2 S=4 W=8.)
                    bool n0 = Linked(map, grid, cap, c + IntVec3.North);
                    bool e0 = Linked(map, grid, cap, c + IntVec3.East);
                    bool s0 = Linked(map, grid, cap, c + IntVec3.South);
                    bool w0 = Linked(map, grid, cap, c + IntVec3.West);
                    bool nw0 = Linked(map, grid, cap, c + IntVec3.North + IntVec3.West);
                    bool ne0 = Linked(map, grid, cap, c + IntVec3.North + IntVec3.East);
                    bool sw0 = Linked(map, grid, cap, c + IntVec3.South + IntVec3.West);
                    bool se0 = Linked(map, grid, cap, c + IntVec3.South + IntVec3.East);

                    // The unified field underlay: the rock's own rough terrain,
                    // world-position sampled, on every open mass cell.
                    TerrainDef rough = rock?.building?.naturalTerrain
                        ?? fallbackRock?.building?.naturalTerrain;
                    emitted |= EmitFieldAt(this, map, c, y, rough, shadeS, shadeN,
                        n0, s0, e0, w0, sw0, nw0, ne0, se0);
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
                    // A direction links when the mass continues there. Interior cells
                    // (mask 15) are the field alone - the atlas' fully-linked tile is
                    // near-flat and adds nothing but a tone seam.
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
                        if (sw0 && s0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, -1, y, shadeS);
                        }
                        if (nw0 && n0 && w0)
                        {
                            AddCornerFiller(sub, map, c, -1, 1, y, shadeN);
                        }
                        if (ne0 && n0 && e0)
                        {
                            AddCornerFiller(sub, map, c, 1, 1, y, shadeN);
                        }
                        if (se0 && s0 && e0)
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
            return EmitMeadowFadeAt(this, map, grid, c, 0, y, fanCovered, meadowAdj);
        }

        /// <summary>Static form, so the see-below view can emit the same fans one or more
        /// bands up. Scratch arrays come from the caller rather than being allocated per
        /// cell - this runs for every mass cell of every section bake.</summary>
        internal static bool EmitMeadowFadeAt(MapDrawLayer layer, Map map, TerrainGrid grid,
            IntVec3 c, int zOffset, float y, bool[] fanCovered, TerrainDef[] adj)
        {
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
                ABNineFan.AddFan(layer.GetSubMesh(mat), c.x, c.z + zOffset,
                    y + MeadowFadeAltBias, fanCovered, White, ClearWhite);
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
            int zOffset, float altitude, bool[] fanScratch, TerrainDef[] adjScratch)
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
            // The meadow fades belong to the mass wherever it is seen from, interior cells
            // included - grass creeping onto rock is not an edge-only effect.
            bool emitted = false;
            if (fanScratch != null && adjScratch != null)
            {
                emitted = EmitMeadowFadeAt(layer, map, grid, source, zOffset, altitude,
                    fanScratch, adjScratch);
            }
            bool n0 = Linked(map, grid, cap, source + IntVec3.North);
            bool e0 = Linked(map, grid, cap, source + IntVec3.East);
            bool s0 = Linked(map, grid, cap, source + IntVec3.South);
            bool w0 = Linked(map, grid, cap, source + IntVec3.West);
            int mask = (n0 ? 1 : 0) | (e0 ? 2 : 0) | (s0 ? 4 : 0) | (w0 ? 8 : 0);
            if (mask == 15)
            {
                return emitted; // interior: no lip, but its fades still count
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
            // Cliff-face shading travels too: the vertical ramp is what tells the player a
            // southern rim is a wall rather than more floor, and it reads exactly the same
            // from two levels up as it does standing on it.
            int faceDepth = CliffFaceDepth > 0
                ? FaceDepthAt(map, bands, grid, cap, source, CliffFaceDepth)
                : -1;
            Color32 shadeS = faceDepth >= 0 ? FaceShade(faceDepth, false) : White;
            Color32 shadeN = faceDepth >= 0 ? FaceShade(faceDepth, true) : White;
            IntVec3 at = new IntVec3(source.x, source.y, source.z + zOffset);
            AddQuad(sub, at.x, at.z, at.x + 1, at.z + 1, altitude, shadeS, shadeN);
            if (CornerFillersEnabled)
            {
                bool nw = Linked(map, grid, cap, source + IntVec3.North + IntVec3.West);
                bool ne = Linked(map, grid, cap, source + IntVec3.North + IntVec3.East);
                bool sw = Linked(map, grid, cap, source + IntVec3.South + IntVec3.West);
                bool se = Linked(map, grid, cap, source + IntVec3.South + IntVec3.East);
                if (sw && s0 && w0)
                {
                    AddCornerFiller(sub, map, at, -1, -1, altitude, shadeS);
                }
                if (nw && n0 && w0)
                {
                    AddCornerFiller(sub, map, at, -1, 1, altitude, shadeN);
                }
                if (ne && n0 && e0)
                {
                    AddCornerFiller(sub, map, at, 1, 1, altitude, shadeN);
                }
                if (se && s0 && e0)
                {
                    AddCornerFiller(sub, map, at, 1, -1, altitude, shadeS);
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
                // §99.A: every rock that can appear on ANY band, not just the tile's list.
                // Once bands carry their own stone, a band rock missing from this set would
                // render the cap with no material at all (rule 34).
                foreach (ThingDef rock in ABBandRocks.AllRocksOnMap(map))
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

        /// <summary>
        /// THE MASS ITSELF, DRAWN FROM ANY DEPTH. Rock at every level.
        ///
        /// <see cref="EmitMassSilhouetteAt"/> adds a LIP to mass the cap is already drawing
        /// in its own band, and it opens with three guards that are right there and fatal
        /// here:
        ///   1. it returns false when the cell has a mineable edifice ("its own sprite draws
        ///      the edge") - but from two levels up that sprite is FOGGED and never printed;
        ///   2. it returns false for non-linked graphics ("variant-mode rocks have no lip") -
        ///      which is every rock once Better Mountains is installed, and is exactly why
        ///      the reported bug was BM-only;
        ///   3. it re-derives its rock from one Slot BELOW the cell handed in - correct when
        ///      the caller passes a sky cell, wrong when it passes an already-descended one.
        ///
        /// Routing the first attempt at this fix through that method meant it could never
        /// fire. This draws the mass rather than its outline, and carries none of those
        /// three assumptions.
        ///
        /// NO FOG TEST, deliberately: the caller has already established the cell is fogged,
        /// and that is precisely when this is needed. An undug mountain should read as rock
        /// from EVERY level, not as rock at +1 (where the cap reaches) and grey fog above it.
        /// </summary>
        internal static bool EmitMassRepresentationAt(MapDrawLayer layer, Map map,
            IntVec3 source, int zOffset, float altitude)
        {
            if (layer == null || map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            ThingDef rock = MassRockAt(map, source);
            if (rock == null)
            {
                return false; // not mass - the fog fan alone is right for open ground
            }
            EnsureQueue();
            IntVec3 at = new IntVec3(source.x, source.y, source.z + zOffset);
            bool emitted = false;

            // Links first, cardinals AND diagonals. The mask is derived from the GROUND
            // map's own mass rather than from the cap's per-band grids, which is what lets
            // this work at an arbitrary depth - and the FIELD is now eroded to the same rule,
            // so both must read one set of answers.
            bool n0 = MassLinked(map, source + IntVec3.North);
            bool e0 = MassLinked(map, source + IntVec3.East);
            bool s0 = MassLinked(map, source + IntVec3.South);
            bool w0 = MassLinked(map, source + IntVec3.West);
            bool nw = MassLinked(map, source + IntVec3.North + IntVec3.West);
            bool ne = MassLinked(map, source + IntVec3.North + IntVec3.East);
            bool sw = MassLinked(map, source + IntVec3.South + IntVec3.West);
            bool se = MassLinked(map, source + IntVec3.South + IntVec3.East);
            int mask = (n0 ? 1 : 0) | (e0 ? 2 : 0) | (s0 ? 4 : 0) | (w0 ? 8 : 0);

            // The field: the rock's own rough terrain, world-position sampled, exactly what
            // the cap lays down in its own band.
            //
            // ⚠ AN ORE DEF HAS NO naturalTerrain (TerrainDefGenerator_Stone only builds the
            // _Rough terrains for isNaturalRock && !isResourceRock), and MassRockAt hands
            // back whatever edifice stands in the cell - ore included. Falling back to the
            // HOST stone beside it keeps every mass cell floored, which the mask-15 skip
            // below depends on: skipping the interior tile is only safe when something else
            // is already covering the cell.
            TerrainDef rough = rock.building?.naturalTerrain
                ?? GroundRockAt(map, source)?.building?.naturalTerrain
                ?? FallbackRock(map)?.building?.naturalTerrain;
            bool fieldDrawn = EmitFieldAt(layer, map, at, altitude, rough, White, White,
                n0, s0, e0, w0, sw, nw, ne, se);
            emitted |= fieldDrawn;

            Graphic live = LiveGraphicFor(rock);
            if (!(live is Graphic_Linked))
            {
                // Variant mode (Better Mountains). Same StableCellIndex the cap uses, so a
                // column does not reshuffle its rocks as the player changes level.
                Material[] variants = VariantsFor(rock);
                if (variants != null && variants.Length > 0)
                {
                    Material vmat = QueueClone(variants[StableCellIndex(source, variants.Length)]);
                    LayerSubMesh vsub = vmat != null ? layer.GetSubMesh(vmat) : null;
                    if (vsub != null)
                    {
                        Vector2 ds = live != null ? live.drawSize : Vector2.one;
                        float hw = Mathf.Max(ds.x, 1f) * 0.5f;
                        float hh = Mathf.Max(ds.y, 1f) * 0.5f;
                        AddQuad(vsub, at.x + 0.5f - hw, at.z + 0.5f - hh,
                            at.x + 0.5f + hw, at.z + 0.5f + hh, altitude + 0.02f, White, White);
                        emitted = true;
                    }
                }
                return emitted;
            }

            // Linked mode (vanilla rock): the atlas tile for this cell's neighbour mask. The
            // mask is derived from the GROUND map's own mass rather than from the cap's
            // per-band grids, which is what lets this work at an arbitrary depth.
            //
            // ⚠ AN ATLAS TILE IS HALF OF VANILLA'S LINKED DRAWER, NOT ALL OF IT. Every tile
            // in the rock atlas has its four corners ROUNDED AWAY; vanilla covers that with
            // Graphic_LinkedCornerFiller's quarter-cell fillers, and the cap's own in-band
            // pass mirrors them. Emitting bare tiles here reproduced the rounding across a
            // whole mass interior: a grid of pale pillows separated by dark diamonds at
            // every four-cell junction, reported as "corners on the inner bound, it looks
            // like holes". BM players never saw it because BM rock returns above, in the
            // variant branch - which is why the regression shipped with the BM support and
            // is the exact inverse of the bug that support fixed.
            Material baseMat = AtlasBaseFor(rock);
            if (baseMat == null)
            {
                return emitted;
            }
            if (mask == 15 && fieldDrawn)
            {
                // Interior, same rule the cap applies in its own band: the fully-linked tile
                // is near-flat, adds nothing but a tone seam, and carries the corner rounding
                // that produced the holes. The rough-stone field alone is the interior.
                return emitted;
            }
            Material tile = QueueClone(
                MaterialAtlasPool.SubMaterialFromAtlas(baseMat, (LinkDirections)mask));
            LayerSubMesh tsub = tile != null ? layer.GetSubMesh(tile) : null;
            if (tsub == null)
            {
                return emitted;
            }
            AddQuad(tsub, at.x, at.z, at.x + 1, at.z + 1, altitude + 0.02f, White, White);
            emitted = true;
            if (CornerFillersEnabled)
            {
                if (sw && s0 && w0)
                {
                    AddCornerFiller(tsub, map, at, -1, -1, altitude + 0.02f, White);
                }
                if (nw && n0 && w0)
                {
                    AddCornerFiller(tsub, map, at, -1, 1, altitude + 0.02f, White);
                }
                if (ne && n0 && e0)
                {
                    AddCornerFiller(tsub, map, at, 1, 1, altitude + 0.02f, White);
                }
                if (se && s0 && e0)
                {
                    AddCornerFiller(tsub, map, at, 1, -1, altitude + 0.02f, White);
                }
            }
            return emitted;
        }

        /// <summary>The rock def of a MASS cell - one whose edifice is mineable or natural
        /// rock. Null for everything else, which is what keeps this off open ground.
        ///
        /// Deliberately NOT <c>ABMinedRockLookup</c>: that maps a mined FLOOR back to the
        /// rock that produced it, and an undug mass cell has no mined floor to map.</summary>
        private static ThingDef MassRockAt(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return null;
            }
            ThingDef d = map.edificeGrid[c]?.def;
            if (d != null && (d.mineable || (d.building != null && d.building.isNaturalRock)))
            {
                return d; // undug ground mountain: the rock wall itself
            }
            // ⚠ SKY-BAND MASS HAS NO EDIFICE AT ALL.
            //
            // A carved sky level's mountain is AB_MountainTop TERRAIN; there is no rock wall
            // to find, because the rock LOOK is supplied entirely by the cap drawing into
            // that band's own mesh. An edifice-only test therefore returns null for exactly
            // the cells that produced "+2 does not show the rock ledge of +1".
            //
            // Type it the way the cap itself does - from the band one Slot below - so the
            // stone matches what the level beneath is made of and a column stays consistent
            // as the player climbs.
            if (map.terrainGrid.TerrainAt(c) == ABDefOf.AB_MountainTop)
            {
                ABBandMap bands = ABBands.CompOf(map);
                if (bands != null && bands.Banded && bands.Slot > 0)
                {
                    return GroundRockAt(map, c + new IntVec3(0, 0, -bands.Slot))
                        ?? FallbackRock(map);
                }
            }
            return null;
        }

        /// <summary>Does the mass CONTINUE into this cell, in the link-mask sense? Off-map
        /// counts as linked - vanilla rock carries the MapEdge link flag and the cap's own
        /// <see cref="Linked"/> says the same - so a mountain running off the map edge does
        /// not draw a phantom lip along it.</summary>
        private static bool MassLinked(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return true;
            }
            return MassRockAt(map, c) != null;
        }

        /// <summary>Cheap "is this cell mass" for neighbour SCANS - the same two
        /// representations <see cref="MassRockAt"/> resolves (a rock edifice, or sky-band
        /// AB_MountainTop terrain) without the band lookup and adjacency walk it does in
        /// order to NAME the stone. Used when hunting for a non-mass neighbour, where the
        /// yes/no is the whole question and the rock def is not wanted.</summary>
        internal static bool CarriesMass(Map map, IntVec3 c)
        {
            if (map == null || !c.InBounds(map))
            {
                return false;
            }
            Building ed = map.edificeGrid[c];
            if (ed != null
                && (ed.def.mineable
                    || (ed.def.building != null && ed.def.building.isNaturalRock)))
            {
                return true;
            }
            return map.terrainGrid.TerrainAt(c) == ABDefOf.AB_MountainTop;
        }

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
