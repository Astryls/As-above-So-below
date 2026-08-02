using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 see-below: through every open-air cell of a band, show the band underneath.
    ///
    /// V1 needed `SectionLayer_ABBelowThings` PLUS `DrawPosOffsetPatcher` - hundreds of
    /// DrawPos getters patched on ParallelPreDraw worker threads - purely because the level
    /// below lived on a different Map and every draw position had to be lied about. None of
    /// that exists here: the level below is the same Map, one Slot down, so below content
    /// is printed normally and the vertices it emitted are TRANSLATED afterwards. Nothing
    /// is lied to; triangles are moved after they are generated.
    ///
    /// Composition, in draw order per open-air cell:
    ///   1. air mask      - opaque backdrop, SolidColorBehind (never leave a cell empty)
    ///   2. fog of war    - vanilla's own material, when the ground below is unexplored
    ///   3. below terrain - a faithful port of vanilla's terrain print, edge fades included
    ///   4. below things  - printed, then translated and dimmed
    ///
    /// Masking is by construction: only cells whose OWN terrain is AB_OpenAir print
    /// anything, so rooftops and mountain caps are opaque and can never lose a
    /// render-queue contest against below content (V1's hardest-won rendering lesson).
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABBelowV2 : SectionLayer
    {
        /// <summary>Below content draws at FULL brightness and FULL size - no artificial
        /// dim, no "fake zoom out" shrink.
        ///
        /// V1 tinted and shrank below content as a depth cue, because its below view had no
        /// real lighting of its own and needed some way to read as "further away". V2 has
        /// SectionLayer_ABBelowLighting, which shades below content with the SURFACE's own
        /// glow - so an artificial dim on top is exactly the double-darkening that made the
        /// sky view murky, and it fights the ONE BIG MAP premise: looking down a hole should
        /// show the level below as it actually is.
        ///
        /// Depth now reads from what is genuinely there - the air mask and fog around the
        /// opening, and the opaque rooftops and mountain caps framing it.</summary>
        private static readonly Color32 BelowTint = new Color32(255, 255, 255, 255);

        /// <summary>Transparent counterpart of BelowTint. Terrain edge fades encode their
        /// coverage in vertex ALPHA, so RGB stays full and only alpha goes to zero.</summary>
        private static readonly Color32 BelowTintClear = new Color32(255, 255, 255, 0);

        private static readonly Color32 OpaqueWhite = new Color32(255, 255, 255, 255);

        /// <summary>The opaque air mask: what an open-air cell shows when there is nothing
        /// legible beneath it.
        ///
        /// SolidColorBehind, not SimpleSolidColorMaterial: the plain solid-colour material
        /// sits in a LATE render queue and painted straight over below terrain that had
        /// already been emitted in the geometry queue, leaving a black field with only
        /// plants and buildings floating on it. Draw order inside a SectionLayer is decided
        /// by material render QUEUE, not by the altitude handed to the verts.</summary>
        private static readonly Material AirMaskMat =
            SolidColorMaterials.NewSolidColorMaterial(new Color(0.05f, 0.05f, 0.06f, 1f),
                ShaderDatabase.SolidColorBehind);

        private readonly List<int> vertCountsBefore = new List<int>();

        /// <summary>Things already printed during THIS Regenerate pass. Per-instance and
        /// per-pass, so it never leaks across sections or across maps. See the call site
        /// for why a multi-cell thing can otherwise elect more than one anchor.</summary>
        private readonly HashSet<Thing> printedThisPass = new HashSet<Thing>();

        private readonly CellTerrain[] adjTerrain = new CellTerrain[8];

        private readonly bool[] edgeReach = new bool[8];

        private readonly HashSet<CellTerrain> edgeSet = new HashSet<CellTerrain>();

        public SectionLayer_ABBelowV2(Section section) : base(section)
        {
            // AB_BelowThings is the mirrored "something on a band below changed" signal
            // (§36c-B1); the vanilla flags cover this section's OWN cells. Both are needed:
            // the mirror no longer forwards vanilla flags upward.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)MapMeshFlagDefOf.FogOfWar
                | (ulong)ABDefOf.AB_BelowThings;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering);

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
                int slot = bands.Slot;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainGrid terrainGrid = map.terrainGrid;
                FogGrid fog = map.fogGrid;
                ThingGrid thingGrid = map.thingGrid;
                float maskAlt = AltitudeLayer.Terrain.AltitudeFor();
                float terrainAlt = AltitudeLayer.TerrainScatter.AltitudeFor();
                // Vanilla's own fog altitude: above every below print, so the fog skirt
                // can never lose a depth-test tie against a depth-writing cutout wall
                // sprite printed underneath it. Ordering vs terrain is queue-decided.
                float fogAlt = AltitudeLayer.FogOfWar.AltitudeFor();
                bool printed = false;
                printedThisPass.Clear();

                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    // Only bands that HAVE something beneath them, and never the gutter.
                    if (bands.BandOf(c) <= 0 || bands.InGutter(c))
                    {
                        continue;
                    }
                    if (!ABBands.ShowsBelow(terrainGrid.TerrainAt(c)))
                    {
                        continue; // opaque by construction
                    }

                    // Descend to whatever is actually visible - see
                    // ABBands.TryResolveVisibleBelow for why this is not a single step, and
                    // why every downward-looking system shares that one definition.
                    bool inBounds = ABBands.TryResolveVisibleBelow(map, bands, c,
                        out IntVec3 below, out int drop);
                    bool foggedBelow = inBounds && fog.IsFogged(below);

                    if (!inBounds || foggedBelow)
                    {
                        // Nothing legible beneath. An open-air cell must NEVER be left with
                        // zero geometry: AB_OpenAir is dontRender, so vanilla's terrain
                        // layer emits only a ShadowMask, and with nothing on top the cell
                        // renders as shader garbage.
                        //
                        // For UNEXPLORED ground, vanilla's own fog-of-war material goes over
                        // the backdrop, so a mountain the colony has not dug into reads as
                        // solid fog from above exactly as it does from the surface. The fan
                        // (not a hard quad) matters for the fully-fogged cell too: vanilla's
                        // border softness lives in the EXPLORED neighbours' partial fans,
                        // and this cell must use the identical vertex layout so the two
                        // meshes seam invisibly.
                        if (ABV2Debug.DrawBelowAirMask)
                        {
                            AddQuad(GetSubMesh(AirMaskMat), c, maskAlt, OpaqueWhite);
                            if (foggedBelow)
                            {
                                EmitBelowFogFan(map, bands, fog, below, c, fogAlt);
                                // ROCK AT EVERY LEVEL.
                                //
                                // An undug mountain is FOGGED, so its own sprite is never
                                // printed by the thing loop. At +1 that does not show,
                                // because SectionLayer_ABMountainCap draws the mass into its
                                // own band from exactly one Slot down, fog or no fog. From
                                // +2 and up the cap is deriving from the sky band beneath it
                                // instead, nothing else draws mass, and all that survives is
                                // this fog fan - the grey strip in the report.
                                //
                                // `drop > slot` keeps this strictly to the levels the cap
                                // cannot reach, so the +1 view stays byte-identical.
                                // EmitMassRepresentationAt returns false for anything that is
                                // not mass, leaving open ground as plain fog fan.
                                if (slot > 0 && drop > slot)
                                {
                                    SectionLayer_ABMountainCap.EmitMassRepresentationAt(
                                        this, map, below, drop, terrainAlt);
                                }
                            }
                            printed = true;
                        }
                        continue;
                    }

                    if (ABV2Debug.DrawBelowTerrain)
                    {
                        // THE DEPTH STACK. A mass cell is opaque to the shared descent rule,
                        // so a column used to resolve exactly ONE level and stop - which is
                        // why a mountain read as each tier lying on top of the next rather
                        // than one mass continuing down. Here the descent keeps going THROUGH
                        // mass and stops at the first solid floor, and the levels are drawn
                        // DEEPEST FIRST so each one shows through the eroded margins of the
                        // one above it.
                        int levels = BuildDepthStack(map, bands, terrainGrid, fog, below);
                        bool drew = false;
                        bool opaqueBelow = false;
                        for (int d = levels - 1; d >= 0; d--)
                        {
                            // ⚠ EROSION IS PERMITTED ONLY ONCE SOMETHING OPAQUE HAS ACTUALLY
                            // BEEN LAID UNDER US - MEASURED, NOT ASSUMED.
                            //
                            // The first version said "the bottom entry never erodes, therefore
                            // the bottom is opaque", which is an assumption about the bottom
                            // rather than a fact about the mesh. PrintBelowTerrain returns
                            // early and draws NOTHING when the cell's terrain is dontRender or
                            // its material is missing, and then every level above it eroded
                            // onto a hole - the staircase of missing floor along a mass edge.
                            // Carrying the answer up the stack makes it impossible to erode
                            // onto nothing regardless of which entries decline.
                            drew |= PrintBelowTerrain(map, terrainGrid, depthCell[d], c,
                                terrainAlt, slot, d, levels, opaqueBelow, out bool laidOpaque);
                            opaqueBelow |= laidOpaque;
                        }
                        if (!drew && ABV2Debug.DrawBelowAirMask)
                        {
                            // Below terrain is itself dontRender: still needs a backdrop.
                            AddQuad(GetSubMesh(AirMaskMat), c, maskAlt, OpaqueWhite);
                        }
                    }
                    printed = true;

                    // Explored ground bordering unexplored: vanilla's fog boundary reaches
                    // INTO the first explored cell with a corner-smoothed skirt (the
                    // "vanilla style border" around every unmined rock mass). The old
                    // hard per-cell quads stopped dead at the fogged cell's edge, which
                    // is exactly the "black square textures around minable rock" report.
                    if (ABV2Debug.DrawBelowAirMask)
                    {
                        EmitBelowFogFan(map, bands, fog, below, c, fogAlt);
                    }

                    if (!ABV2Debug.DrawBelowThings)
                    {
                        continue;
                    }
                    List<Thing> things = thingGrid.ThingsListAtFast(below);
                    for (int i = 0; i < things.Count; i++)
                    {
                        // NB: everything below is translated by `drop`, the accumulated
                        // descent, not by one slot.
                        Thing t = things[i];
                        DrawerType drawer = t.def.drawerType;
                        if (drawer != DrawerType.MapMeshOnly && drawer != DrawerType.MapMeshAndRealTime)
                        {
                            continue; // realtime things are not part of the map mesh
                        }
                        if (!t.def.seeThroughFog && fog.IsFogged(t.Position))
                        {
                            continue;
                        }
                        if (!IsPrintAnchor(t, below, map, bands, terrainGrid, air, drop))
                        {
                            continue;
                        }
                        // ONE PRINT PER THING PER PASS, unconditionally.
                        //
                        // IsPrintAnchor picks the first cell of a multi-cell thing whose
                        // viewing cell can see it, which is single-valued only when every
                        // cell of that thing resolves to the SAME drop. It does not: `drop`
                        // comes from TryResolveVisibleBelow PER VIEWING CELL (§5), so two
                        // columns over one 2x2 object can descend different distances, each
                        // then computing a different "first visible cell" and each electing
                        // itself the anchor.
                        //
                        // Reported against Medieval Overhaul's golem formations
                        // (DankPyon_GolemRock_*, size 2x2, Graphic_Random, drawSize 4x4):
                        // "drawn four times overlapping" - four cells, four anchors, four
                        // quads at the same TrueCenter, so the alpha stacks instead of the
                        // sprite shifting. Any 2x2+ thing is exposed, not just MO's.
                        //
                        // Deduping by THING rather than by fixing the anchor rule is
                        // deliberate: the anchor rule has a real job (a thing whose origin
                        // cell is hidden but whose far corner is visible must still draw),
                        // and it is the drop-dependence, not the rule, that is unsound.
                        // A set membership test is O(1), correct under every drop layout,
                        // and cannot regress the visible-corner case.
                        if (!printedThisPass.Add(t))
                        {
                            continue;
                        }
                        try
                        {
                            // The depth cue is applied HERE, at print time, because it is a
                            // per-object transform: each thing shrinks about its own centre,
                            // so unlike a whole-layer scale nothing slides off the cell it
                            // stands on. Baked, therefore free per frame - at the price of a
                            // rebake when the setting changes (ABMod.WriteSettings).
                            float shrink = ABDepthView.CanShrink(t)
                                ? ABDepthView.ScaleForLevels(slot > 0 ? drop / slot : 1)
                                : 1f;
                            SnapshotVertCounts();
                            t.Print(this);
                            FinishNewVerts(drop, t.TrueCenter(), shrink);
                        }
                        catch (Exception e)
                        {
                            Log.WarningOnce(ABLog.Tag + " V2 below print failed for " + t.LabelCap
                                + ": " + e.Message, t.thingIDNumber ^ 762195870);
                        }
                    }
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below layer");
            }
        }

        private readonly bool[] fogCovered = new bool[9];

        /// <summary>Scratch for the cap layer's shared fan emitters, owned here so nothing
        /// allocates per cell during a bake.</summary>
        private readonly bool[] fanCovered = new bool[9];

        private readonly TerrainDef[] meadowAdj = new TerrainDef[8];

        /// <summary>A below cell counts as fogged-for-view when it is genuinely fogged OR
        /// not legible at all (off-map, gutter): the veil then runs seamlessly to the
        /// band edge instead of leaving a lit seam against the air mask.</summary>
        private static bool FoggedForView(Map map, ABBandMap bands, FogGrid fog, IntVec3 b)
        {
            return !b.InBounds(map) || bands.InGutter(b) || fog.IsFogged(b);
        }

        /// <summary>
        /// Vanilla SectionLayer_FogOfWar's per-cell corner smoothing, sampled one band
        /// DOWN and emitted one band UP. A fogged cell covers all nine points; an
        /// explored cell is covered only where fogged neighbours reach it (cardinal =
        /// that edge's three points, diagonal = the corner point). Returns true when
        /// anything was emitted.
        /// </summary>
        private bool EmitBelowFogFan(Map map, ABBandMap bands, FogGrid fog, IntVec3 below,
            IntVec3 above, float y)
        {
            bool[] covered = fogCovered;
            if (FoggedForView(map, bands, fog, below))
            {
                ABNineFan.CoverAll(covered);
            }
            else
            {
                ABNineFan.Cover(covered,
                    FoggedForView(map, bands, fog, below + IntVec3.North),
                    FoggedForView(map, bands, fog, below + IntVec3.South),
                    FoggedForView(map, bands, fog, below + IntVec3.East),
                    FoggedForView(map, bands, fog, below + IntVec3.West),
                    FoggedForView(map, bands, fog, below + IntVec3.South + IntVec3.West),
                    FoggedForView(map, bands, fog, below + IntVec3.North + IntVec3.West),
                    FoggedForView(map, bands, fog, below + IntVec3.North + IntVec3.East),
                    FoggedForView(map, bands, fog, below + IntVec3.South + IntVec3.East));
                if (!ABNineFan.Any(covered))
                {
                    return false;
                }
            }
            ABNineFan.AddFan(GetSubMesh(MatBases.FogOfWar), above.x, above.z, y, covered,
                OpaqueWhite, BelowTintClear);
            return true;
        }

        /// <summary>One cell-sized quad in the vanilla terrain-mesh shape (verts + colors +
        /// tris, deliberately no uvs).</summary>
        private void AddQuad(LayerSubMesh sub, IntVec3 c, float y, Color32 color)
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
            sub.colors.Add(color);
            sub.colors.Add(color);
            sub.colors.Add(color);
            sub.colors.Add(color);
            sub.tris.Add(n);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 2);
            sub.tris.Add(n);
            sub.tris.Add(n + 2);
            sub.tris.Add(n + 3);
        }

        /// <summary>
        /// A FAITHFUL PORT of vanilla SectionLayer_Terrain.Regenerate's per-cell work,
        /// sampling one band DOWN and emitting one band UP.
        ///
        /// This replaces a hand-rolled single quad. That quad drew the right texture but
        /// none of what vanilla layers on top of it - terrain EDGE BLENDING above all, plus
        /// snow and sand coverage, the pollution variant, and the Underwall substitution
        /// under wall-covered floors - so the below view read as hard tiled squares rather
        /// than terrain.
        ///
        /// Two details that must not drift from vanilla:
        ///  - NO uvs. RimWorld terrain shaders sample from WORLD POSITION; writing 0..1 uvs
        ///    per quad (as Printer_Plane does) produces a muddy smear.
        ///  - The fade verts sit at y = 0 while the base quad sits at the terrain altitude.
        ///    That reads as a bug but is vanilla's own arrangement: ordering between base
        ///    and fades is settled by material render QUEUE (renderPrecedence), not by
        ///    altitude.
        /// </summary>
        private bool PrintBelowTerrain(Map map, TerrainGrid terrainGrid, IntVec3 below,
            IntVec3 above, float altitude, int slot, int depth, int levels, bool mayErode,
            out bool laidOpaque)
        {
            laidOpaque = false;
            TerrainDef def = terrainGrid.TerrainAt(below);
            if (def == null || def.dontRender)
            {
                return false;
            }
            CellTerrain self = CellTerrainAt(map, terrainGrid, below);
            // A mountain mass on a level below reads as FLAT FLOOR from up here, because
            // AB_MountainTop is a deliberately plain dark terrain and the rock texture that
            // normally covers it comes from SectionLayer_ABMountainCap's rough-stone field -
            // which lives in that band's OWN section mesh and so is invisible from another
            // level. Substituting the host rock's rough terrain gives the mass its stone
            // back when seen from above.
            //
            // PARTIAL by construction: the cap layer's silhouette lips, corner fillers and
            // cliff-face shading are still not reproduced across bands, so a mountain viewed
            // from two levels up reads as rock but without its rim detail. Fixing that
            // properly means parameterising the cap layer's per-cell emitters by a z offset
            // and calling them from here.
            if (def == ABDefOf.AB_MountainTop)
            {
                TerrainDef host = HostRockTerrain(map, below, slot);
                if (host != null)
                {
                    self.def = host;
                }
            }
            Material baseMat = DepthQueue(MaterialFor(map, self), depth, levels);
            LayerSubMesh sub = baseMat != null ? GetSubMesh(baseMat) : null;
            if (sub == null)
            {
                return false;
            }
            // ⚠ THE BASE QUAD IS THE THIRD FULL-CELL FILL UNDER A STYLISED OUTLINE.
            //
            // Two others were found first (the cap's own in-band field and the cross-level
            // emitter's), but a sky-band mass viewed from above gets its rock from HERE - the
            // AB_MountainTop -> host rough stone substitution a few lines up - and this quad
            // is opaque corner to corner. The atlas tile emitted below it is transparent
            // outside the rock outline, so the quad fills those gaps back in and the cell
            // squares off past the black border, once per level down the mountain.
            //
            // Eroded to the CAP's link rule, the same one EmitMassSilhouetteAt uses for the
            // tile that lands on top, so fill and outline agree by construction. Non-mass
            // terrain keeps the plain quad: it has no outline to hide under, and vanilla's
            // own edge fades (below) are what soften it.
            int drop = above.z - below.z;
            bool skyMass = def == ABDefOf.AB_MountainTop;
            bool beyondCapReach = slot > 0
                && drop > slot
                && ABMinedRockLookup.TryGetMinedRockDef(def, out _);
            bool massCell = skyMass || beyondCapReach;
            // ⚠ `massCell` alone is the DECORATION gate and is NOT the right question here.
            // It carries `drop > slot`, which exists so the cap and the cross-level emitter
            // do not both decorate at one level down - irrelevant to whether this FILL should
            // retract. TryMassFillCoverage asks the fill's own question (does anything draw an
            // outline over this cell, and under whose link rule) and answers false when
            // nothing does, which is what keeps ordinary floors on their full quad.
            //
            // ⚠ AND AN ERODED FILL MUST HAVE SOMETHING BEHIND IT, OR IT IS A HOLE.
            //
            // This layer's fill IS the backdrop: the viewing cell is see-through terrain,
            // which is `dontRender`, so vanilla's terrain layer emits only a ShadowMask there
            // and nothing else paints the cell. Eroding the fill exposed that, and an
            // uncovered ShadowMask renders as shader garbage - a red rim tracing the mass
            // outline. The same failure `AirMaskMat` exists to prevent, reintroduced from a
            // new direction.
            //
            // The fix is the one the report proposed: put the ADJACENT GROUND under the rock
            // instead of leaving a gap. That is also what vanilla shows - a rock's
            // transparent corners reveal the ground it stands in, never the void - so the
            // backdrop is not a patch over a hole, it is the correct picture.
            //
            // No non-mass neighbour means an INTERIOR cell, where CoverInterior returns full
            // coverage and the fan is geometrically the quad anyway - so declining to erode
            // there costs exactly nothing and guarantees a hole can never be emitted.
            //
            // ⚠ THE BOTTOM OF THE STACK NEVER ERODES (`mayErode` is false for it). It is the
            // solid floor everything above is seen against, so it must stay opaque - that is
            // what makes a hole unrepresentable rather than merely unlikely, and it is why the
            // lateral backdrop below is now only a FALLBACK for a single-level column.
            //
            // With a stack, the correct thing under an eroded mass cell is the level
            // BENEATH it, not a neighbour beside it. Borrowing sideways is what made every
            // tier read as a separate slab laid on the next: the rock was being drawn against
            // its own level's ground instead of against the mountain continuing downward.
            bool eroded = false;
            if (mayErode
                && SectionLayer_ABMountainCap.MassFieldFadeEnabled
                && SectionLayer_ABMountainCap.TryMassFillCoverage(map, below, massCell, fanCovered))
            {
                if (levels > 1)
                {
                    // The next level down is already drawn underneath: erode straight onto it.
                    eroded = true;
                }
                else if (TryBackdropTerrain(map, terrainGrid, below, out CellTerrain backdrop))
                {
                    Material backMat = MaterialFor(map, backdrop);
                    LayerSubMesh backSub = backMat != null ? GetSubMesh(backMat) : null;
                    if (backSub != null)
                    {
                        AddQuad(backSub, above, altitude, BelowTint);
                        eroded = true;
                        laidOpaque = true; // the borrowed ground covers the cell
                    }
                }
            }
            if (eroded)
            {
                ABNineFan.AddFan(sub, above.x, above.z, altitude, fanCovered,
                    BelowTint, BelowTintClear);
            }
            else
            {
                int count = sub.verts.Count;
                sub.verts.Add(new Vector3(above.x, altitude, above.z));
                sub.verts.Add(new Vector3(above.x, altitude, above.z + 1));
                sub.verts.Add(new Vector3(above.x + 1, altitude, above.z + 1));
                sub.verts.Add(new Vector3(above.x + 1, altitude, above.z));
                for (int i = 0; i < 4; i++)
                {
                    sub.colors.Add(BelowTint);
                }
                sub.tris.Add(count);
                sub.tris.Add(count + 1);
                sub.tris.Add(count + 2);
                sub.tris.Add(count);
                sub.tris.Add(count + 2);
                sub.tris.Add(count + 3);
                laidOpaque = true; // a full-cell quad: everything above may erode onto it
            }

            PrintBelowTerrainEdges(map, terrainGrid, below, above, self, depth, levels);
            // Give a mountain mass its EDGE back. The substituted rough terrain above only
            // restores its stone; the lip, outline and corner fillers that make it read as a
            // mountain are emitted by the cap layer into its own band's mesh, so they have to
            // be re-emitted here at our offset. Slightly above the terrain quad so the tile
            // sits over it rather than fighting for the same depth.
            //
            // ⚠ AND THE GATE HAS TO ALLOW FOR THE CAP'S ONE-SLOT REACH.
            //
            // SectionLayer_ABMountainCap derives its whole cap from `-bands.Slot`, exactly
            // ONE band down (its `groundOffset`). That is right for a sky band's own mass -
            // each level's mass is projected from the level beneath it, which is what makes
            // mountains taper as they rise - but it means the cap can only ever represent
            // rock ONE level below the viewer.
            //
            // Testing `def == AB_MountainTop` alone inherited that limit, because
            // AB_MountainTop is the terrain WE give sky-band mass; ordinary ground rock
            // carries its own rough-stone terrain and never matched. So nothing at all drew
            // ground rock from two levels up:
            //
            //   +1  cap reaches the ground        -> rock drawn (as cap field + silhouette,
            //                                        which is why it reads as a COLOUR SHIFT
            //                                        rather than the real sprite)
            //   +2  cap now derives from +1       -> ground rock simply absent
            //   +3  same shift one level up       -> +1's rock absent
            //
            // Reported exactly that way, and it is the same family as the `- Slot` bug in
            // §5: a single-step assumption standing in for the descent rule. It survived the
            // standing `grep '- Slot'` audit because it is written as a TERRAIN-DEF GATE
            // rather than as arithmetic - the ninth instance, and the first with no `- Slot`
            // in it to find.
            //
            // `drop > slot` is load-bearing: at exactly one slot the cap already draws this
            // mass, and firing here as well would double the lip and corner fillers on every
            // mountain edge at +1. The lookup keeps it to rock-derived terrain, and
            // EmitMassSilhouetteAt self-guards anyway (it returns false when the cell has a
            // mineable edifice whose own sprite draws the edge).
            // ⚠ AND THE SILHOUETTE ALONE IS NOT ENOUGH - USE ITS RETURN VALUE.
            //
            // EmitMassSilhouetteAt covers the vanilla LINKED case (lip plus corner fillers)
            // and RETURNS FALSE when it declines: a mineable edifice, or a variant-mode
            // graphic - which is every rock once Better Mountains is installed. Nothing
            // then drew the mass at all, so a sky ledge one level down rendered as bare
            // substituted stone with no rock on it. Reported as "+2 does not show the rock
            // ledge of +1", and BM-only for exactly that reason.
            //
            // Handing off on its own return value is what keeps this from double-drawing:
            // the representation runs ONLY where the silhouette refused, so vanilla linked
            // rock keeps its lip and corner fillers untouched and BM rock gets its sprite.
            if (massCell)
            {
                if (!SectionLayer_ABMountainCap.EmitMassSilhouetteAt(this, map, below,
                        drop, altitude + 0.02f, fanCovered, meadowAdj))
                {
                    SectionLayer_ABMountainCap.EmitMassRepresentationAt(this, map, below,
                        drop, altitude);
                }
            }
            return true;
        }

        /// <summary>Levels resolved for one column, nearest first. Sized to the hard level
        /// cap (7) plus the terminating floor.</summary>
        private readonly IntVec3[] depthCell = new IntVec3[8];

        /// <summary>
        /// DESCEND THROUGH MASS, STOP AT THE FIRST SOLID FLOOR. Returns how many levels this
        /// column shows, nearest at index 0.
        ///
        /// ⚠ DELIBERATELY NOT AN EXTENSION OF <c>ABBands.TryResolveVisibleBelow</c>. That is
        /// THE shared descent rule, and ten systems - reachability, click-through, combat,
        /// selection, snow - depend on it answering ONE question: what does this column
        /// genuinely show. This is a RENDERING-ONLY descent that treats mass as translucent,
        /// which is true of how it is drawn and false of everything else about it. Widening
        /// the shared rule to match would make rock reachable and clickable through three
        /// levels. The single-step bug came back nine times by way of copies of that rule;
        /// this is not a copy of it, it is a different question asked beside it, and it must
        /// stay that way.
        ///
        /// The walk steps one Slot at a time, re-entering the shared descent whenever it
        /// lands on see-through terrain, so an air gap between two masses is crossed exactly
        /// as the normal view crosses it. It stops on: a non-mass cell (the solid floor, which
        /// is INCLUDED as the opaque bottom of the stack), fog (illegible - the level above
        /// stays opaque instead), the gutter, or the map floor.
        /// </summary>
        private int BuildDepthStack(Map map, ABBandMap bands, TerrainGrid terrainGrid,
            FogGrid fog, IntVec3 first)
        {
            depthCell[0] = first;
            int count = 1;
            if (!SectionLayer_ABMountainCap.MassDepthCutEnabled || bands.Slot <= 0)
            {
                return count;
            }
            IntVec3 cur = first;
            while (count < depthCell.Length
                && SectionLayer_ABMountainCap.CarriesMass(map, cur))
            {
                IntVec3 probe = new IntVec3(cur.x, cur.y, cur.z - bands.Slot);
                if (!probe.InBounds(map) || bands.InGutter(probe))
                {
                    break;
                }
                if (ABBands.ShowsBelow(terrainGrid.TerrainAt(probe)))
                {
                    if (!ABBands.TryResolveVisibleBelow(map, bands, probe,
                            out IntVec3 deeper, out int _))
                    {
                        break;
                    }
                    probe = deeper;
                }
                if (fog.IsFogged(probe))
                {
                    break;
                }
                depthCell[count++] = probe;
                cur = probe;
            }
            return count;
        }

        /// <summary>
        /// DEPTH-ORDERED TERRAIN QUEUE. Draw order between two terrain materials is decided
        /// by render QUEUE, not by altitude or submission order, so a painter's-algorithm
        /// stack cannot simply emit deepest-first and hope: a deep stone (high
        /// renderPrecedence) would still paint over a shallow soil.
        ///
        /// Every level of the stack is therefore re-queued into one controlled band, eight
        /// units apart, with the material's own precedence preserved as a 0-7 tiebreak inside
        /// its level. Depth dominates; precedence still orders terrains within a level; and
        /// the whole band stays inside the terrain family so nothing escapes into the cutout
        /// range where pawns and buildings live.
        ///
        /// A single-level column (the overwhelming majority) is left completely alone - the
        /// unmodified material, byte-identical to before the stack existed.
        /// </summary>
        /// <summary>Terrain family base. Levels are a stride apart, precedence is the
        /// tiebreak inside a level, and 8 levels x 32 keeps the whole band at 2000-2255 -
        /// inside the terrain range, nowhere near the cutout family where pawns live.</summary>
        private const int DepthQueueBase = 2000;

        private const int DepthQueueStride = 32;

        private static readonly Dictionary<Material, Material[]> depthQueued =
            new Dictionary<Material, Material[]>();

        private static Material DepthQueue(Material source, int depth, int levels)
        {
            // ⚠ A SINGLE-LEVEL COLUMN IS LEFT COMPLETELY ALONE. Ordering only ever matters
            // WITHIN a column - two columns never overlap - so a one-level column keeps its
            // untouched material and stays byte-identical to the verified behaviour.
            if (source == null || levels <= 1)
            {
                return source;
            }
            if (!depthQueued.TryGetValue(source, out Material[] byDepth))
            {
                if (depthQueued.Count > 512)
                {
                    depthQueued.Clear();
                }
                byDepth = new Material[8];
                depthQueued[source] = byDepth;
            }
            int slot = Mathf.Clamp(depth, 0, 7);
            if (byDepth[slot] == null)
            {
                // ⚠ DEPTH 0 MUST BE RE-QUEUED TOO. Leaving the nearest level on its natural
                // queue was the inversion bug: a shallow soil sits near 2000 while a deep
                // stone (renderPrecedence 190+) had been re-queued ABOVE it, so the deeper
                // level painted straight over the nearer one. Every level in a stack has to
                // live in the same controlled band or the ordering is only half-imposed.
                int tie = Mathf.Clamp(source.renderQueue - DepthQueueBase, 0, DepthQueueStride - 1);
                byDepth[slot] = new Material(source)
                {
                    renderQueue = DepthQueueBase + ((7 - slot) * DepthQueueStride) + tie
                };
            }
            return byDepth[slot];
        }

        /// <summary>Cardinals before diagonals: a mass edge almost always has an orthogonal
        /// neighbour on the open side, and taking it keeps the backdrop the ground the rock
        /// actually abuts rather than something around a corner.</summary>
        private static readonly IntVec3[] BackdropSearch =
        {
            IntVec3.South, IntVec3.East, IntVec3.North, IntVec3.West,
            IntVec3.South + IntVec3.East, IntVec3.North + IntVec3.East,
            IntVec3.South + IntVec3.West, IntVec3.North + IntVec3.West
        };

        /// <summary>
        /// THE GROUND THE MASS STANDS IN, borrowed from the nearest non-mass neighbour, to be
        /// laid under an eroded rock fill so the retracted corners show continuous ground
        /// instead of a hole.
        ///
        /// Sampled through CellTerrainAt like any other below cell, so it carries that cell's
        /// own snow and pollution and matches what the neighbouring column actually draws.
        /// Ordering against the fill on top is settled by render QUEUE (renderPrecedence),
        /// not altitude - generated stone sits at 190+, above soil and gravel - which is the
        /// same mechanism vanilla uses to stack terrain, so no altitude bias is wanted here.
        ///
        /// Returns false when every neighbour is mass. That is an INTERIOR cell, where the
        /// erosion is a no-op anyway (full coverage), so declining costs nothing and makes a
        /// hole unrepresentable rather than merely unlikely.
        /// </summary>
        private bool TryBackdropTerrain(Map map, TerrainGrid terrainGrid, IntVec3 below,
            out CellTerrain backdrop)
        {
            backdrop = default(CellTerrain);
            for (int i = 0; i < BackdropSearch.Length; i++)
            {
                IntVec3 n = below + BackdropSearch[i];
                if (!n.InBounds(map) || ABBands.InGutter(map, n))
                {
                    continue;
                }
                if (SectionLayer_ABMountainCap.CarriesMass(map, n))
                {
                    continue;
                }
                TerrainDef t = terrainGrid.TerrainAt(n);
                if (t == null || t.dontRender)
                {
                    continue; // see-through or unrendered: no better a backdrop than the hole
                }
                backdrop = CellTerrainAt(map, terrainGrid, n);
                return true;
            }
            return false;
        }

        /// <summary>Vanilla's edge-fade pass: every distinct neighbouring terrain allowed to
        /// bleed over this cell gets a 9-vertex fan whose alpha marks which of the eight
        /// perimeter points it reaches.</summary>
        private void PrintBelowTerrainEdges(Map map, TerrainGrid terrainGrid, IntVec3 below,
            IntVec3 above, CellTerrain self, int depth, int levels)
        {
            edgeSet.Clear();
            IntVec3[] around = GenAdj.AdjacentCellsAroundBottom;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = below + around[i];
                if (!n.InBounds(map))
                {
                    adjTerrain[i] = self;
                    continue;
                }
                CellTerrain ct = CellTerrainAt(map, terrainGrid, n);
                Thing edifice = n.GetEdifice(map);
                if (edifice != null && edifice.def.coversFloor)
                {
                    ct.def = TerrainDefOf.Underwall;
                }
                adjTerrain[i] = ct;
                if (!ct.Equals(self)
                    && ct.def != null
                    && ct.def.edgeType != TerrainDef.TerrainEdgeType.Hard
                    && terrainGrid.FoundationAt(below) == null
                    && terrainGrid.FoundationAt(n) == null
                    && ct.def.renderPrecedence >= self.def.renderPrecedence)
                {
                    edgeSet.Add(ct);
                }
            }
            if (edgeSet.Count == 0)
            {
                return;
            }
            float x = above.x;
            float z = above.z;
            foreach (CellTerrain other in edgeSet)
            {
                // Depth-queued with the base fill it belongs to, or a fade from a DEEP level
                // would out-queue a nearer level's floor and bleed up through it.
                Material mat = DepthQueue(MaterialFor(map, other), depth, levels);
                LayerSubMesh sub = mat != null ? GetSubMesh(mat) : null;
                if (sub == null)
                {
                    continue;
                }
                int count = sub.verts.Count;
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z));
                sub.verts.Add(new Vector3(x, 0f, z));
                sub.verts.Add(new Vector3(x, 0f, z + 0.5f));
                sub.verts.Add(new Vector3(x, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z + 1f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z + 0.5f));
                sub.verts.Add(new Vector3(x + 1f, 0f, z));
                sub.verts.Add(new Vector3(x + 0.5f, 0f, z + 0.5f));
                for (int j = 0; j < 8; j++)
                {
                    edgeReach[j] = false;
                }
                for (int k = 0; k < 8; k++)
                {
                    if (!adjTerrain[k].Equals(other))
                    {
                        continue;
                    }
                    if (k % 2 == 0)
                    {
                        edgeReach[(k - 1 + 8) % 8] = true;
                        edgeReach[k] = true;
                        edgeReach[(k + 1) % 8] = true;
                    }
                    else
                    {
                        edgeReach[k] = true;
                    }
                }
                for (int l = 0; l < 8; l++)
                {
                    sub.colors.Add(edgeReach[l] ? BelowTint : BelowTintClear);
                }
                sub.colors.Add(BelowTintClear);
                for (int m = 0; m < 8; m++)
                {
                    sub.tris.Add(count + m);
                    sub.tris.Add(count + (m + 1) % 8);
                    sub.tris.Add(count + 8);
                }
            }
            edgeSet.Clear();
        }

        /// <summary>The rough terrain of the stone a mass cell stands on, found by walking
        /// down the column - the mass was projected from that rock at generation. Ore is
        /// excluded because ore defs carry no naturalTerrain at all.</summary>
        private static TerrainDef HostRockTerrain(Map map, IntVec3 massCell, int slot)
        {
            IntVec3 probe = massCell;
            for (int step = 0; step < 4; step++)
            {
                if (probe.InBounds(map))
                {
                    Building ed = probe.GetEdifice(map);
                    if (ed != null && ed.def.mineable && ed.def.building != null
                        && !ed.def.building.isResourceRock
                        && ed.def.building.naturalTerrain != null)
                    {
                        return ed.def.building.naturalTerrain;
                    }
                }
                probe = new IntVec3(probe.x, probe.y, probe.z - slot);
                if (probe.z < 0)
                {
                    break;
                }
            }
            return null;
        }

        private static CellTerrain CellTerrainAt(Map map, TerrainGrid terrainGrid, IntVec3 c)
        {
            return new CellTerrain(terrainGrid.TerrainAt(c), c.IsPolluted(map),
                map.snowGrid.GetDepth(c), c.GetSandDepth(map), terrainGrid.ColorAt(c));
        }

        /// <summary>Mirrors SectionLayer_Terrain.GetMaterialFor, which is an instance method
        /// on a class we do not derive from.</summary>
        private static Material MaterialFor(Map map, CellTerrain ct)
        {
            if (ct.def == null)
            {
                return null;
            }
            bool polluted = ct.polluted && ct.snowCoverage < 0.4f && ct.sandCoverage < 0.4f
                && ct.def.graphicPolluted != BaseContent.BadGraphic
                && !WorldComponent_GravshipController.DisableDrawingPollution;
            return map.terrainGrid.GetMaterial(ct.def, polluted, ct.color);
        }

        /// <summary>Multi-cell things print exactly once, from the first occupied cell
        /// (row-major) that has open air above it - not from the root cell, which may sit
        /// under a rooftop while the rest of the body is exposed.</summary>
        private static bool IsPrintAnchor(Thing t, IntVec3 belowCell, Map map, ABBandMap bands,
            TerrainGrid terrainGrid, TerrainDef air, int drop)
        {
            if (t.def.size.x == 1 && t.def.size.z == 1)
            {
                return t.Position.x == belowCell.x && t.Position.z == belowCell.z;
            }
            CellRect rect = t.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    IntVec3 above = new IntVec3(x, 0, z + drop);
                    if (!above.InBounds(map) || bands.InGutter(above))
                    {
                        continue;
                    }
                    if (ABBands.ShowsBelow(terrainGrid.TerrainAt(above)))
                    {
                        return q.x == belowCell.x && q.z == belowCell.z;
                    }
                }
            }
            return false;
        }

        private void SnapshotVertCounts()
        {
            vertCountsBefore.Clear();
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                vertCountsBefore.Add(subs[i].verts.Count);
            }
        }

        /// <summary>
        /// Finishes the vertices the last print emitted: shrink about the thing's own
        /// centre, then translate up to the viewing level by the accumulated descent (which
        /// may be several bands, hence `drop` and not one slot).
        ///
        /// ONE PASS, not two, and in this order. Scaling first means the pivot is the
        /// thing's real TrueCenter rather than a translated copy of it, so there is no
        /// second place for the `- Slot` family of bugs to hide. Altitude (y) is untouched:
        /// it is draw order in this mod, not height.
        ///
        /// Tinting is still deliberately absent - the below view is lit by
        /// SectionLayer_ABBelowLighting from the surface's own glow, and an artificial dim
        /// on top of that is the double-darkening that made V1's sky view murky. SIZE is a
        /// distance cue that costs no brightness, which is why it came back and the tint
        /// did not.
        /// </summary>
        private void FinishNewVerts(int drop, Vector3 centre, float shrink)
        {
            bool scaling = shrink < 0.999f;
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> verts = subs[i].verts;
                int from = i < vertCountsBefore.Count ? vertCountsBefore[i] : 0;
                for (int j = from; j < verts.Count; j++)
                {
                    Vector3 v = verts[j];
                    if (scaling)
                    {
                        v.x = centre.x + (v.x - centre.x) * shrink;
                        v.z = centre.z + (v.z - centre.z) * shrink;
                    }
                    verts[j] = new Vector3(v.x, v.y, v.z + drop);
                }
            }
        }
    }

    /// <summary>
    /// Change propagation. A section only regenerates when its OWN cells are dirtied, so a
    /// wall built on the surface would never repaint the sky band that looks down on it.
    /// Mirroring the dirty flag one band up keeps the see-below view live.
    ///
    /// Terminates naturally: with three bands the mirror walks 0 -> 1 -> 2 and stops, and
    /// the re-entrancy latch makes that guarantee explicit rather than incidental.
    /// </summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty),
        new Type[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    public static class Patch_MapDrawer_ABMirrorDirtyUp
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef =
            AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        private static bool mirroring;

        private static void Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (mirroring)
            {
                return;
            }
            // MIRROR VANILLA'S OWN GUARD. MapDrawer.MapMeshDirty opens with exactly this
            // check and returns, so during map GENERATION every call - vanilla's and ours -
            // does nothing. The postfix ran anyway, and the carve is the worst possible
            // caller: SetTerrain and SetRoof each fire one, across ~36k cells per basement
            // band, and each one then dispatched (bandCount - 1) Harmony-wrapped re-entries
            // into a method whose first line is a return. On a seven-band map that is on the
            // order of 1.3 million dead dispatches per carve, paid entirely inside the
            // generation window this mod has spent three profiling runs shortening.
            if (Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                // Mirror UPWARD, but only as far as the change can actually be SEEN.
                //
                // One step was enough while only one level could look down. With levels
                // stacked, level +2 and +3 see the ground through the open air of the levels
                // between them - but nothing ever dirtied their sections when the ground
                // changed, so they kept whatever they baked the first time they were drawn.
                // Reported as "ground floor mineables disappear on floor 2, and all prior
                // levels' mineables on floor 3": not a masking or translation bug at all,
                // simply a mesh nobody had invalidated since it was built.
                int fromBand = bands.BandOf(loc);
                if (fromBand < 0)
                {
                    return;
                }
                int steps = 0;
                // ⚠ §36c-B1: THE MIRROR SENDS ONLY THE DEDICATED FLAG, NEVER THE VANILLA
                // FLAGS IT RECEIVED. Forwarding vanilla flags rebuilt the above sections'
                // VANILLA layers - ThingsGeneral is the 1.6 atlas bake - for content those
                // layers cannot render; #315 measured that waste at ~15-25 ms per 48-cell
                // burst, and the session's 83-100 ms worst frames were exactly these bursts.
                // Every AB below layer now lists AB_BelowThings in relevantChangeTypes;
                // a below layer that misses the flag goes stale on below changes (the
                // "mineables disappear on floor 2" class), so ADD THE FLAG when adding a
                // layer - the mirror is no longer flag-agnostic.
                ulong mirrorFlag = (ulong)ABDefOf.AB_BelowThings;
                mirroring = true;
                try
                {
                    for (int b = fromBand + 1; b < bands.bandCount; b++)
                    {
                        IntVec3 above = bands.Translate(loc, b);
                        if (!above.InBounds(map) || bands.InGutter(above))
                        {
                            break; // nothing above a gutter column can look down it
                        }
                        map.mapDrawer.MapMeshDirty(above, mirrorFlag);
                        steps++;
                        // ⚠ THE ASCENT RULE - the inverse of ABBands.TryResolveVisibleBelow,
                        // and it was missing here while the descent rule was being enforced
                        // in ten other places. A band can only show this cell if EVERY level
                        // between them is see-through; the first opaque floor, rooftop or
                        // mountain cap hides it from that band AND from every band above.
                        //
                        // Mirroring past that point invalidated sections that provably
                        // cannot display the change. It is the most expensive kind of waste
                        // this codebase has, because the unit is not a cell but a SECTION
                        // REGENERATION: on a seven-band map, one mined rock rebuilt six
                        // section stacks, each running seven of our layers plus vanilla's -
                        // and the lighting layer allocated a fresh Unity Mesh per section on
                        // top. Steady-state fps never showed it; it lands entirely in the
                        // hitches during mining and construction.
                        //
                        // The FIRST step above is deliberately unconditional:
                        // SectionLayer_ABMountainCap derives its cap from the band exactly
                        // one Slot below whether that cell is see-through or not, so band + 1
                        // must always be told regardless of what the ascent rule says.
                        if (!AnyOpenAround(map, bands, above))
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    mirroring = false;
                }
                if (steps > 0)
                {
                    ABPerfStats.NoteMirror(steps);
                }
            }
            catch (Exception e)
            {
                mirroring = false;
                Log.ErrorOnce(ABLog.Tag + " V2: dirty mirror threw: " + e, 762195871);
            }
        }

        /// <summary>
        /// Can ANY column in this cell's 3x3 neighbourhood still see downward?
        ///
        /// Nine cells, not one, and for exactly the reason
        /// SectionLayer_ABBelowShadows.TryResolveDropAround already takes nine: the mirrored
        /// passes are NEIGHBOUR-SAMPLING. Terrain edge fades read the eight adjacent
        /// terrains, the fog fan reads eight neighbours' fog, the snow layer averages a
        /// nine-cell kernel, and the sun-shadow caster mask is defined on neighbours
        /// outright. A column that is opaque itself can still be read as a neighbour by an
        /// adjacent column that is not, so stopping the climb on the centre cell alone would
        /// drop invalidations a boundary pass genuinely needs - and boundary artifacts are
        /// the single hardest class of bug to attribute in this codebase.
        ///
        /// Nine terrain reads per band is array indexing plus two reference compares. It buys
        /// back whole section regenerations, so the trade is not close.
        /// </summary>
        private static bool AnyOpenAround(Map map, ABBandMap bands, IntVec3 c)
        {
            TerrainGrid terrain = map.terrainGrid;
            for (int i = 0; i < 9; i++)
            {
                IntVec3 n = i == 8 ? c : c + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || bands.InGutter(n))
                {
                    continue;
                }
                if (ABBands.ShowsBelow(terrain.TerrainAt(n)))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
