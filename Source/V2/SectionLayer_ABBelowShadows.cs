using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE reason below-shadows were invisible, and it was never in our own layers.
    ///
    /// SectionLayer_Terrain does this:
    ///     GetSubMesh(terrainDef.dontRender ? MatBases.ShadowMask : GetMaterialFor(...))
    ///
    /// ShadowMask is the material MapDrawLayer_ExteriorLightingOverlay stamps over the void
    /// OUTSIDE the map - it suppresses shadow and lighting rendering. The only vanilla
    /// terrain that is dontRender is Odyssey's Space, where that behaviour is exactly
    /// right: no shadows should fall on the void.
    ///
    /// AB_OpenAir is also dontRender (that is what makes it see-through), so vanilla was
    /// stamping a shadow-suppressing mask over EVERY open-air cell - precisely the cells
    /// the see-below view draws into. The shadow geometry was present the whole time
    /// (verified: 220 verts, finalized, render queue 3175, above terrain and plants) and
    /// was being masked out at composite time.
    ///
    /// Diagnosis note: this survived five rounds of fixes because every symptom pointed
    /// inward. It was only isolated by toggling all of our own below layers off and finding
    /// shadows STILL absent, which cleared our code entirely.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_Terrain), nameof(SectionLayer_Terrain.Regenerate))]
    public static class Patch_SectionLayer_Terrain_ABUnmaskShadows
    {
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static void Postfix(SectionLayer_Terrain __instance)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                Map map = SectionRef(__instance)?.map;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                List<LayerSubMesh> subs = __instance.subMeshes;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (subs[i].material == MatBases.ShadowMask)
                    {
                        subs[i].disabled = true;
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: shadow-unmask postfix threw: " + e, 762195874);
            }
        }
    }

    /// <summary>
    /// V2 see-below: the sun's cast shadows from the band underneath.
    ///
    /// Vanilla's SectionLayer_SunShadows is `internal`, so it cannot be subclassed from a
    /// mod - but its geometry rule is short and public-API-only, so it is reproduced here
    /// against the band below. Buildings with a staticSunShadowHeight emit a cell quad plus
    /// skirt strips on the sides that face open ground; the SunShadow material's shader
    /// does the stretching and rotation from the sun vector, which is why this layer is
    /// DYNAMIC - it re-emits as the sun moves rather than baking a fixed shadow.
    ///
    /// Masking matches the rest of the see-below stack: a shadow is emitted only where the
    /// cell one band up is open air, so shadows never bleed onto rooftops or mountain caps
    /// that are opaque from above.
    /// </summary>
    public class SectionLayer_ABBelowShadows : SectionLayer_Dynamic
    {
        private static readonly Color32 LowVertexColor = new Color32(0, 0, 0, 0);

        public SectionLayer_ABBelowShadows(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible
        {
            get
            {
                if (!ABGuard.On(ABGuard.Rendering) || !DebugViewSettings.drawShadows)
                {
                    return false;
                }
                return section.map?.Biome?.disableShadows != true;
            }
        }

        public override bool ShouldDrawDynamic(CellRect view)
        {
            return section.CellRect.Overlaps(view);
        }

        /// <summary>Whole map, mirroring vanilla's SunShadows layer. The shadow shader
        /// DISPLACES vertices along the sun vector, so geometry routinely ends up far
        /// outside the section that emitted it; a section-sized boundary would let Unity
        /// cull shadows that should still be on screen.</summary>
        public override CellRect GetBoundaryRect()
        {
            return new CellRect(0, 0, section.map.Size.x, section.map.Size.z);
        }

        /// <summary>Also vanilla's SunShadows behaviour: bounds must be refreshed every
        /// draw because the displaced geometry moves as the sun does.</summary>
        public override void DrawLayer()
        {
            RefreshSubMeshBounds();
            base.DrawLayer();
        }

        public override void Regenerate()
        {
            if (!MatBases.SunShadow.shader.isSupported || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            // ClearSubMeshes, not sub.Clear: this layer ends up owning TWO submeshes -
            // SunShadow for the staticSunShadowHeight geometry built here, and SunShadowFade
            // which Printer_Shadow creates for shadowData things. FinalizeMesh finalizes
            // every submesh, so clearing only one leaves the other already-finalized on the
            // next regeneration, logging "Finalizing mesh which is already finalized".
            ClearSubMeshes(MeshParts.All);
            LayerSubMesh sub = GetSubMesh(MatBases.SunShadow);
            try
            {
                FogGrid fog = map.fogGrid;
                Building[] edifices = map.edificeGrid.InnerArray;
                CellIndices indices = map.cellIndices;
                float y = AltitudeLayer.Shadows.AltitudeFor();

                CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
                rect.ClipInsideMap(map);
                bool emitted = false;

                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 here = new IntVec3(x, 0, z);
                        if (bands.BandOf(here) <= 0 || bands.InGutter(here))
                        {
                            continue;
                        }
                        // Masked on "is this cell, or any neighbour, see-through".
                        //
                        // Neither extreme works. Requiring the CASTER's own cell to be
                        // see-through kills every mountain shadow, because mountain rock
                        // sits under an opaque cap while the shadow it throws lands on open
                        // ground. Dropping the mask entirely makes every rock face inside
                        // the mass cast too - most visibly the walls of CAVES, whose skirts
                        // then hatch diagonal streaks all over the mountain cap.
                        //
                        // The neighbour test splits them correctly: rock at the mountain's
                        // outer edge touches open ground and casts, while cave walls and
                        // deep interior rock are surrounded by cap and stay silent. It also
                        // matches how the geometry behaves - AddSkirt only emits a side
                        // whose neighbour is shorter, so a caster with no see-through
                        // neighbour has nothing visible to contribute anyway.
                        //
                        // The neighbour scan also supplies the DESCENT. This layer used to
                        // step `z - slot` unconditionally, which is the one-descent bug for
                        // the sixth time: from level +1 that single step lands on the opaque
                        // surface and works, but from +2 or +3 it lands in the OPEN AIR of
                        // the level between, which holds no edifices - so shadows appeared
                        // on the first upper level and nowhere above it. See
                        // ABBands.TryResolveVisibleBelow for the one definition.
                        if (!TryResolveDropAround(map, bands, here, out int drop))
                        {
                            continue;
                        }
                        IntVec3 below = new IntVec3(x, 0, z - drop);
                        if (!below.InBounds(map) || bands.InGutter(below) || fog.IsFogged(below))
                        {
                            continue;
                        }
                        Building b = edifices[indices.CellToIndex(below)];
                        if (b == null || !(b.def.staticSunShadowHeight > 0f))
                        {
                            continue;
                        }
                        float height = b.def.staticSunShadowHeight;
                        Color32 tall = new Color32(0, 0, 0, (byte)(255f * height));

                        // Emitted at the ABOVE cell's coordinates - no post-translation.
                        int baseIdx = sub.verts.Count;
                        sub.verts.Add(new Vector3(x, y, z));
                        sub.verts.Add(new Vector3(x, y, z + 1));
                        sub.verts.Add(new Vector3(x + 1, y, z + 1));
                        sub.verts.Add(new Vector3(x + 1, y, z));
                        sub.colors.Add(LowVertexColor);
                        sub.colors.Add(LowVertexColor);
                        sub.colors.Add(LowVertexColor);
                        sub.colors.Add(LowVertexColor);
                        sub.tris.Add(baseIdx);
                        sub.tris.Add(baseIdx + 1);
                        sub.tris.Add(baseIdx + 2);
                        sub.tris.Add(baseIdx);
                        sub.tris.Add(baseIdx + 2);
                        sub.tris.Add(baseIdx + 3);
                        emitted = true;

                        // Side skirts, ported VERBATIM from vanilla SectionLayer_SunShadows.
                        //
                        // Vanilla emits THREE skirts - west, east and south - and each uses a
                        // DIFFERENT triangle winding. An earlier generic AddSkirt helper
                        // applied the west winding to all four directions (and invented a
                        // north skirt vanilla never emits), which produced malformed tris
                        // and the jagged sawtooth peaks. Kept as explicit blocks so it stays
                        // obvious that the windings are not interchangeable.
                        Building n;

                        // west
                        if (below.x > 0)
                        {
                            n = edifices[indices.CellToIndex(below.x - 1, below.z)];
                            if (n == null || n.def.staticSunShadowHeight < height)
                            {
                                int c3 = sub.verts.Count;
                                sub.verts.Add(new Vector3(x, y, z));
                                sub.verts.Add(new Vector3(x, y, z + 1));
                                sub.colors.Add(tall);
                                sub.colors.Add(tall);
                                sub.tris.Add(baseIdx + 1);
                                sub.tris.Add(baseIdx);
                                sub.tris.Add(c3);
                                sub.tris.Add(c3);
                                sub.tris.Add(c3 + 1);
                                sub.tris.Add(baseIdx + 1);
                            }
                        }

                        // east
                        if (below.x < map.Size.x - 1)
                        {
                            n = edifices[indices.CellToIndex(below.x + 1, below.z)];
                            if (n == null || n.def.staticSunShadowHeight < height)
                            {
                                int c4 = sub.verts.Count;
                                sub.verts.Add(new Vector3(x + 1, y, z + 1));
                                sub.verts.Add(new Vector3(x + 1, y, z));
                                sub.colors.Add(tall);
                                sub.colors.Add(tall);
                                sub.tris.Add(baseIdx + 2);
                                sub.tris.Add(c4);
                                sub.tris.Add(c4 + 1);
                                sub.tris.Add(c4 + 1);
                                sub.tris.Add(baseIdx + 3);
                                sub.tris.Add(baseIdx + 2);
                            }
                        }

                        // south
                        if (below.z > 0)
                        {
                            n = edifices[indices.CellToIndex(below.x, below.z - 1)];
                            if (n == null || n.def.staticSunShadowHeight < height)
                            {
                                int c5 = sub.verts.Count;
                                sub.verts.Add(new Vector3(x, y, z));
                                sub.verts.Add(new Vector3(x + 1, y, z));
                                sub.colors.Add(tall);
                                sub.colors.Add(tall);
                                sub.tris.Add(baseIdx);
                                sub.tris.Add(baseIdx + 3);
                                sub.tris.Add(c5);
                                sub.tris.Add(baseIdx + 3);
                                sub.tris.Add(c5 + 1);
                                sub.tris.Add(c5);
                            }
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
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below shadows");
            }
        }

        /// <summary>
        /// Is this cell, or any of its eight neighbours, see-through - and if so, how far
        /// down does the view actually reach?
        ///
        /// The mask and the descent are answered together deliberately. A caster at a
        /// mountain's outer edge sits under an OPAQUE cap, so it can never resolve a descent
        /// of its own; the level it is casting onto is the one its see-through neighbours
        /// look down at. The SMALLEST drop among the nine is taken: that is the highest
        /// visible floor, which is the surface most of the surrounding view is showing, so
        /// the shadow stays attached to the mass that threw it instead of being drawn
        /// against a floor two levels further down that happens to peek through one corner.
        /// </summary>
        /// <remarks>The TerrainGrid parameter is gone: it had never been read inside this
        /// method, and its only caller was fetching map.terrainGrid once per section purely
        /// to hand it over. The shared gate resolves terrain itself.</remarks>
        private static bool TryResolveDropAround(Map map, ABBandMap bands,
            IntVec3 c, out int drop)
        {
            drop = 0;
            bool any = false;
            for (int i = 0; i < 9; i++)
            {
                IntVec3 n = i == 8 ? c : c + GenAdj.AdjacentCells[i];
                // Fog is deliberately NOT required: a mountain's outer rock casts onto ground
                // whose exploration state is not the caster's business, and vanilla shadows
                // fall on fogged cells too.
                if (!ABBands.TryResolveVisibleFrom(map, bands, n, requireUnfogged: false,
                        out IntVec3 _, out int d)
                    || d <= 0)
                {
                    continue;
                }
                if (!any || d < drop)
                {
                    drop = d;
                    any = true;
                }
            }
            return any;
        }

    }

    /// <summary>
    /// V2 see-below: vanilla's ambient edge shadows for the band underneath.
    ///
    /// The below view prints walls and rock faithfully, but the soft dark border vanilla
    /// draws around every castEdgeShadows edifice (SectionLayer_EdgeShadows,
    /// MatBases.EdgeShadow, 0.45-cell reach) regenerates at the edifice's OWN cells - one
    /// band down, where the camera never looks. Without it the printed rock sits flat on
    /// the below terrain, which is half of the "missing vanilla border" look (the other
    /// half was the fog fan). This is that layer's geometry, sampled one band down and
    /// emitted at the above cell's coordinates, masked by ShowsBelow like the rest of the
    /// see-below stack so nothing paints onto rooftops or mountain caps.
    ///
    /// The corner emission is ported VERBATIM from vanilla - same GenAdj direction
    /// tables, same non-symmetric per-corner blocks, same 0.45 reach. Generalizing the
    /// four blocks into a loop is exactly the trap the sun-shadow port above already
    /// documented (each block's winding and offset signs differ).
    /// </summary>
    public class SectionLayer_ABBelowEdgeShadows : SectionLayer
    {
        private readonly bool[] cornerShadowed = new bool[4];

        private readonly bool[] cardinalCaster = new bool[4];

        private readonly bool[] diagonalOnly = new bool[4];

        public SectionLayer_ABBelowEdgeShadows(Section section) : base(section)
        {
            // Buildings: casters below appear and disappear. Terrain: the ShowsBelow mask
            // above changes when rooftops and caps are laid or removed.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible
        {
            get
            {
                if (!ABGuard.On(ABGuard.Rendering) || !DebugViewSettings.drawShadows)
                {
                    return false;
                }
                return section.map?.Biome?.disableShadows != true;
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
                Building[] edifices = map.edificeGrid.InnerArray;
                CellIndices indices = map.cellIndices;
                float y = AltitudeLayer.Shadows.AltitudeFor();
                CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
                rect.ClipInsideMap(map);
                LayerSubMesh sm = GetSubMesh(MatBases.EdgeShadow);
                bool[] corner = cornerShadowed;
                bool[] cardinal = cardinalCaster;
                bool[] diagOnly = diagonalOnly;

                for (int i = rect.minX; i <= rect.maxX; i++)
                {
                    for (int j = rect.minZ; j <= rect.maxZ; j++)
                    {
                        IntVec3 above = new IntVec3(i, 0, j);
                        // Was a single `j - slot` step - the one-descent bug's SEVENTH
                        // appearance, and the other half of "shadows show on upper 1 but not
                        // upper 2 or 3": two stacked see-through levels put that step in the
                        // void. Now THE shared gate, so this pass cannot drift away from the
                        // terrain it is shading. Fog is deliberately not required: an ambient
                        // edge shadow under a fog skirt is what vanilla draws too.
                        if (!ABBands.TryResolveVisibleFrom(map, bands, above,
                                requireUnfogged: false, out IntVec3 below, out _))
                        {
                            continue;
                        }
                        Thing thing = edifices[indices.CellToIndex(below)];
                        if (thing != null && thing.def.castEdgeShadows)
                        {
                            // The caster's own cell: vanilla's full ambient quad.
                            ABEdgeShadowGeometry.EmitCasterCell(sm, y, i, j);
                            continue;
                        }

                        for (int k = 0; k < 4; k++)
                        {
                            corner[k] = false;
                            cardinal[k] = false;
                            diagOnly[k] = false;
                        }
                        IntVec3[] cardinals = GenAdj.CardinalDirectionsAround;
                        for (int k = 0; k < 4; k++)
                        {
                            if (Caster(map, bands, edifices, indices, below + cardinals[k]))
                            {
                                cardinal[k] = true;
                                corner[(k + 3) % 4] = true;
                                corner[k] = true;
                            }
                        }
                        IntVec3[] diagonals = GenAdj.DiagonalDirectionsAround;
                        for (int l = 0; l < 4; l++)
                        {
                            if (corner[l])
                            {
                                continue;
                            }
                            if (Caster(map, bands, edifices, indices, below + diagonals[l]))
                            {
                                corner[l] = true;
                                diagOnly[l] = true;
                            }
                        }

                        ABEdgeShadowGeometry.EmitCorners(sm, y, i, j, corner, cardinal, diagOnly);
                    }
                }
                if (sm.verts.Count > 0)
                {
                    sm.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below edge shadows");
            }
        }

        /// <summary>An edge-shadow caster at this below cell. Off-map and gutter cells
        /// cast nothing, so no phantom border appears along the band seam.</summary>
        private static bool Caster(Map map, ABBandMap bands, Building[] edifices,
            CellIndices indices, IntVec3 belowNeighbor)
        {
            if (!belowNeighbor.InBounds(map) || bands.InGutter(belowNeighbor))
            {
                return false;
            }
            Thing t = edifices[indices.CellToIndex(belowNeighbor)];
            return t != null && t.def.castEdgeShadows;
        }
    }

    /// <summary>
    /// Vanilla SectionLayer_EdgeShadows' per-cell geometry, extracted VERBATIM so the
    /// below-view port above keeps one authoritative copy of the four asymmetric corner
    /// blocks (they are NOT interchangeable - windings and offset signs differ per
    /// corner). Callers prepare the corner/cardinal/diagOnly arrays from their own caster
    /// rule and coordinate mapping; this emits at (i, j).
    ///
    /// A second consumer once lived here: a sky-mass-scoped replacement of vanilla's own
    /// EdgeShadows and SunShadows layers, which suppressed shadows cast INSIDE the
    /// mountain mass. It was retired along with the wall-sprite suppression it existed to
    /// support - with wall sprites visible again those shadows are correct vanilla
    /// shading, and the redundant-outline problem is fixed in the generator instead.
    /// </summary>
    internal static class ABEdgeShadowGeometry
    {
        internal const float InDist = 0.45f;

        internal static readonly Color32 Shadowed = new Color32(195, 195, 195, byte.MaxValue);

        internal static readonly Color32 Lit = new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue);

        /// <summary>Vanilla's full ambient quad over a caster's own cell.</summary>
        internal static void EmitCasterCell(LayerSubMesh sm, float y, int i, int j)
        {
            sm.verts.Add(new Vector3(i, y, j));
            sm.verts.Add(new Vector3(i, y, j + 1));
            sm.verts.Add(new Vector3(i + 1, y, j + 1));
            sm.verts.Add(new Vector3(i + 1, y, j));
            sm.colors.Add(Shadowed);
            sm.colors.Add(Shadowed);
            sm.colors.Add(Shadowed);
            sm.colors.Add(Shadowed);
            int count = sm.verts.Count;
            sm.tris.Add(count - 4);
            sm.tris.Add(count - 3);
            sm.tris.Add(count - 2);
            sm.tris.Add(count - 4);
            sm.tris.Add(count - 2);
            sm.tris.Add(count - 1);
        }

        internal static void EmitCorners(LayerSubMesh sm, float y, int i, int j,
            bool[] corner, bool[] cardinal, bool[] diagOnly)
        {
            void ConnectCorner(int idx)
            {
                sm.tris.Add(sm.verts.Count - 2);
                sm.tris.Add(idx);
                sm.tris.Add(sm.verts.Count - 1);
                sm.tris.Add(sm.verts.Count - 1);
                sm.tris.Add(idx);
                sm.tris.Add(idx + 1);
            }
            void CloseCornerTri()
            {
                sm.colors.Add(Shadowed);
                sm.colors.Add(Lit);
                sm.colors.Add(Lit);
                sm.tris.Add(sm.verts.Count - 3);
                sm.tris.Add(sm.verts.Count - 2);
                sm.tris.Add(sm.verts.Count - 1);
            }
            float dx;
            float dz;
            int count2 = sm.verts.Count;
            if (corner[0])
            {
                if (cardinal[0] || cardinal[1])
                {
                    dx = 0f;
                    dz = 0f;
                    if (cardinal[0])
                    {
                        dz = InDist;
                    }
                    if (cardinal[1])
                    {
                        dx = InDist;
                    }
                    sm.verts.Add(new Vector3(i, y, j));
                    sm.colors.Add(Shadowed);
                    sm.verts.Add(new Vector3(i + dx, y, j + dz));
                    sm.colors.Add(Lit);
                    if (corner[1] && !diagOnly[1])
                    {
                        ConnectCorner(sm.verts.Count);
                    }
                }
                else
                {
                    sm.verts.Add(new Vector3(i, y, j));
                    sm.verts.Add(new Vector3(i, y, j + InDist));
                    sm.verts.Add(new Vector3(i + InDist, y, j));
                    CloseCornerTri();
                }
            }
            if (corner[1])
            {
                if (cardinal[1] || cardinal[2])
                {
                    dx = 0f;
                    dz = 0f;
                    if (cardinal[1])
                    {
                        dx = InDist;
                    }
                    if (cardinal[2])
                    {
                        dz = 0f - InDist;
                    }
                    sm.verts.Add(new Vector3(i, y, j + 1));
                    sm.colors.Add(Shadowed);
                    sm.verts.Add(new Vector3(i + dx, y, j + 1 + dz));
                    sm.colors.Add(Lit);
                    if (corner[2] && !diagOnly[2])
                    {
                        ConnectCorner(sm.verts.Count);
                    }
                }
                else
                {
                    sm.verts.Add(new Vector3(i, y, j + 1));
                    sm.verts.Add(new Vector3(i + InDist, y, j + 1));
                    sm.verts.Add(new Vector3(i, y, j + 1 - InDist));
                    CloseCornerTri();
                }
            }
            if (corner[2])
            {
                if (cardinal[2] || cardinal[3])
                {
                    dx = 0f;
                    dz = 0f;
                    if (cardinal[2])
                    {
                        dz = 0f - InDist;
                    }
                    if (cardinal[3])
                    {
                        dx = 0f - InDist;
                    }
                    sm.verts.Add(new Vector3(i + 1, y, j + 1));
                    sm.colors.Add(Shadowed);
                    sm.verts.Add(new Vector3(i + 1 + dx, y, j + 1 + dz));
                    sm.colors.Add(Lit);
                    if (corner[3] && !diagOnly[3])
                    {
                        ConnectCorner(sm.verts.Count);
                    }
                }
                else
                {
                    sm.verts.Add(new Vector3(i + 1, y, j + 1));
                    sm.verts.Add(new Vector3(i + 1, y, j + 1 - InDist));
                    sm.verts.Add(new Vector3(i + 1 - InDist, y, j + 1));
                    CloseCornerTri();
                }
            }
            if (!corner[3])
            {
                return;
            }
            if (cardinal[3] || cardinal[0])
            {
                dx = 0f;
                dz = 0f;
                if (cardinal[3])
                {
                    dx = 0f - InDist;
                }
                if (cardinal[0])
                {
                    dz = InDist;
                }
                sm.verts.Add(new Vector3(i + 1, y, j));
                sm.colors.Add(Shadowed);
                sm.verts.Add(new Vector3(i + 1 + dx, y, j + dz));
                sm.colors.Add(Lit);
                if (corner[0] && !diagOnly[0])
                {
                    ConnectCorner(count2);
                }
            }
            else
            {
                sm.verts.Add(new Vector3(i + 1, y, j));
                sm.verts.Add(new Vector3(i + 1 - InDist, y, j));
                sm.verts.Add(new Vector3(i + 1, y, j + InDist));
                CloseCornerTri();
            }
        }
    }
}
