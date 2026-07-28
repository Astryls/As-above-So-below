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
                        if (!AnySeeThroughAround(map, bands, terrain, air, here))
                        {
                            continue;
                        }
                        IntVec3 below = new IntVec3(x, 0, z - slot);
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

        /// <summary>True when this cell or any of its eight neighbours is open air on this
        /// band - i.e. somewhere the shadow could actually be seen from up here.</summary>
        private static bool AnySeeThroughAround(Map map, ABBandMap bands, TerrainGrid terrain,
            TerrainDef air, IntVec3 c)
        {
            for (int i = 0; i < 9; i++)
            {
                IntVec3 n = i == 8 ? c : c + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || bands.InGutter(n) || bands.BandOf(n) <= 0)
                {
                    continue;
                }
                if (terrain.TerrainAt(n) == air)
                {
                    return true;
                }
            }
            return false;
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
