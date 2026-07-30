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

        private readonly CellTerrain[] adjTerrain = new CellTerrain[8];

        private readonly bool[] edgeReach = new bool[8];

        private readonly HashSet<CellTerrain> edgeSet = new HashSet<CellTerrain>();

        public SectionLayer_ABBelowV2(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)MapMeshFlagDefOf.FogOfWar;
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

                    // DESCEND until something is actually visible, rather than looking
                    // exactly one band down.
                    //
                    // With more than one level stacked above the surface, the level directly
                    // below an open-air cell is usually ALSO open air - so a single-step look
                    // found `dontRender` void and painted the air mask, and level +3 showed a
                    // dark slab instead of the ground far below. Walking down while each
                    // level is see-through lands on the first real floor, which is what
                    // "looking down a shaft" should mean at any height.
                    IntVec3 below = c;
                    int drop = 0;
                    bool inBounds = false;
                    while (true)
                    {
                        IntVec3 next = new IntVec3(below.x, below.y, below.z - slot);
                        if (!next.InBounds(map) || bands.InGutter(next))
                        {
                            inBounds = false;
                            break;
                        }
                        below = next;
                        drop += slot;
                        inBounds = true;
                        if (!ABBands.ShowsBelow(terrainGrid.TerrainAt(below)))
                        {
                            break; // opaque: this is the level you see
                        }
                        if (bands.BandOf(below) <= 0)
                        {
                            break; // bottom level; nothing further down to find
                        }
                    }
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
                            }
                            printed = true;
                        }
                        continue;
                    }

                    if (ABV2Debug.DrawBelowTerrain
                        && !PrintBelowTerrain(map, terrainGrid, below, c, terrainAlt)
                        && ABV2Debug.DrawBelowAirMask)
                    {
                        // Below terrain is itself dontRender: still needs a backdrop.
                        AddQuad(GetSubMesh(AirMaskMat), c, maskAlt, OpaqueWhite);
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
                        try
                        {
                            SnapshotVertCounts();
                            t.Print(this);
                            TranslateNewVerts(drop);
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
            IntVec3 above, float altitude)
        {
            TerrainDef def = terrainGrid.TerrainAt(below);
            if (def == null || def.dontRender)
            {
                return false;
            }
            CellTerrain self = CellTerrainAt(map, terrainGrid, below);
            Material baseMat = MaterialFor(map, self);
            LayerSubMesh sub = baseMat != null ? GetSubMesh(baseMat) : null;
            if (sub == null)
            {
                return false;
            }
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

            PrintBelowTerrainEdges(map, terrainGrid, below, above, self);
            return true;
        }

        /// <summary>Vanilla's edge-fade pass: every distinct neighbouring terrain allowed to
        /// bleed over this cell gets a 9-vertex fan whose alpha marks which of the eight
        /// perimeter points it reaches.</summary>
        private void PrintBelowTerrainEdges(Map map, TerrainGrid terrainGrid, IntVec3 below,
            IntVec3 above, CellTerrain self)
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
                Material mat = MaterialFor(map, other);
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

        /// <summary>Translates the vertices the last print emitted up to the viewing level -
        /// by the accumulated descent, which may be several bands. Altitude (y) is left
        /// alone, and nothing is scaled or tinted: the below level is drawn exactly as it is,
        /// which is the whole point of ONE BIG MAP.</summary>
        private void TranslateNewVerts(int slot)
        {
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> verts = subs[i].verts;
                int from = i < vertCountsBefore.Count ? vertCountsBefore[i] : 0;
                for (int j = from; j < verts.Count; j++)
                {
                    Vector3 v = verts[j];
                    verts[j] = new Vector3(v.x, v.y, v.z + slot);
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
            try
            {
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                IntVec3 above = new IntVec3(loc.x, loc.y, loc.z + bands.Slot);
                if (!above.InBounds(map) || bands.InGutter(above))
                {
                    return;
                }
                mirroring = true;
                try
                {
                    map.mapDrawer.MapMeshDirty(above, dirtyFlags);
                }
                finally
                {
                    mirroring = false;
                }
            }
            catch (Exception e)
            {
                mirroring = false;
                Log.ErrorOnce(ABLog.Tag + " V2: dirty mirror threw: " + e, 762195871);
            }
        }
    }
}
