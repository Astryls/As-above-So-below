using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
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
            LayerSubMesh sub = GetSubMesh(MatBases.SunShadow);
            sub.Clear(MeshParts.All);
            try
            {
                int slot = bands.Slot;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainGrid terrain = map.terrainGrid;
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
                        if (terrain.TerrainAt(here) != air)
                        {
                            continue; // opaque from up here
                        }
                        IntVec3 below = new IntVec3(x, 0, z - slot);
                        if (!below.InBounds(map) || bands.InGutter(below) || fog.IsFogged(below))
                        {
                            continue;
                        }
                        // Things carrying their own shadowData (trees, bushes, most items)
                        // cast via Printer_Shadow rather than the staticSunShadowHeight
                        // path below. Emit them here, at the translated centre.
                        EmitThingShadows(map, below, slot, ref emitted);

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

                        // Side skirts, only where the neighbour below casts a shorter (or
                        // no) shadow - otherwise adjacent blocks double up their edges.
                        AddSkirt(sub, map, edifices, indices, below, IntVec3.West, height, tall,
                            baseIdx + 1, baseIdx, new Vector3(x, y, z), new Vector3(x, y, z + 1));
                        AddSkirt(sub, map, edifices, indices, below, IntVec3.East, height, tall,
                            baseIdx + 2, baseIdx + 3, new Vector3(x + 1, y, z + 1), new Vector3(x + 1, y, z));
                        AddSkirt(sub, map, edifices, indices, below, IntVec3.South, height, tall,
                            baseIdx, baseIdx + 3, new Vector3(x, y, z), new Vector3(x + 1, y, z));
                        AddSkirt(sub, map, edifices, indices, below, IntVec3.North, height, tall,
                            baseIdx + 1, baseIdx + 2, new Vector3(x, y, z + 1), new Vector3(x + 1, y, z + 1));
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

        /// <summary>Re-emits shadowData shadows for the things one band down.
        ///
        /// Vanilla prints these from inside each thing's own Print (Plant.Print ends with a
        /// Printer_Shadow call, Graphic.Print does the same via ShadowGraphic), which is
        /// why they land in whichever layer printed the thing. Doing it explicitly here
        /// keeps below shadows in the dedicated shadow layer where the whole-map boundary
        /// and per-draw bounds refresh apply to them.</summary>
        private void EmitThingShadows(Map map, IntVec3 below, int slot, ref bool emitted)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(below);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                ShadowData shadow = t.def?.graphicData?.shadowData;
                if (shadow == null || t.Position != below)
                {
                    continue;
                }
                DrawerType drawer = t.def.drawerType;
                if (drawer != DrawerType.MapMeshOnly && drawer != DrawerType.MapMeshAndRealTime)
                {
                    continue;
                }
                // Plants scale their shadow with growth, exactly as Plant.Print does.
                float scale = 1f;
                if (t is Plant plant && plant.def.plant != null)
                {
                    scale = plant.def.plant.visualSizeRange.LerpThroughRange(plant.Growth);
                }
                Vector3 center = t.TrueCenter() + shadow.offset * scale;
                center.z += slot;
                center.y = AltitudeLayer.Shadows.AltitudeFor();
                // PrintShadow takes the LAYER (it resolves the SunShadowFade submesh
                // itself), not the submesh we built the staticSunShadowHeight geometry in.
                Printer_Shadow.PrintShadow(this, center, shadow.volume * scale, Rot4.North);
                emitted = true;
            }
        }

        private static void AddSkirt(LayerSubMesh sub, Map map, Building[] edifices,
            CellIndices indices, IntVec3 belowCell, IntVec3 dir, float height, Color32 tall,
            int cornerA, int cornerB, Vector3 vertA, Vector3 vertB)
        {
            IntVec3 n = belowCell + dir;
            if (!n.InBounds(map))
            {
                return;
            }
            Building nb = edifices[indices.CellToIndex(n)];
            if (nb != null && nb.def.staticSunShadowHeight >= height)
            {
                return;
            }
            int idx = sub.verts.Count;
            sub.verts.Add(vertA);
            sub.verts.Add(vertB);
            sub.colors.Add(tall);
            sub.colors.Add(tall);
            sub.tris.Add(cornerA);
            sub.tris.Add(cornerB);
            sub.tris.Add(idx);
            sub.tris.Add(idx);
            sub.tris.Add(idx + 1);
            sub.tris.Add(cornerA);
        }
    }
}
