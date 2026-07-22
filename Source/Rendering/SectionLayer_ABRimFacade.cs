using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Ascending rim facades (height-language rework, 2026-07-22). Where a
    /// walkable slab cell (rooftop, built sky floor, landing platform) has
    /// open air directly SOUTH, the slab's own southern strip renders as its
    /// side face: a vertical gradient band rising from a dark contact line at
    /// the boundary to a bright lit top corner. This is vanilla's own height
    /// grammar - a mountain's south face is the wall sprite drawn INSIDE the
    /// wall's cell, never painted onto the floor in front of it - and it is
    /// the piece that flips the see-below read from "hole sunk into the slab
    /// plane" to "slab standing on the ground below" (mockups height_1/2 and
    /// stackwalls_1, user-selected 2026-07-22). The ground through the gap
    /// stays bright and PLUMB; the skirt adds only a narrow contact shadow at
    /// this band's foot.
    ///
    /// Deliberate exclusions:
    ///  - Mountain-cap terrain and mined rock floors: the cap layer's atlas
    ///    edge tiles already draw the vanilla lip there (locked run-17
    ///    aesthetic); banding them would double the edge.
    ///  - Rim cells holding an edifice: the wall/door sprite plus its vanilla
    ///    south drape fringe IS the face (stacked-walls "plumb" treatment);
    ///    a band under an opaque sprite is wasted geometry.
    ///  - Rim cells whose supporting below-wall feeds the wall-top reveal
    ///    strip (drawWallReveal on): the reveal already dresses that strip
    ///    with the real wall texture; two painters would fight.
    ///
    /// Band tint: the below supporting edifice's draw color when one stands
    /// under the rim (granite face under a granite-supported roof edge), else
    /// the sky terrain's own def color, else neutral stone grey. Pure vertex
    /// colors on the shared white VertexColor material - no textures, the
    /// mountain-cap fade skirt's exact machinery - in the proven
    /// over-terrain-under-cutout queue window, so pawns, items, and walls on
    /// the rim cell always draw over the band. Kill switch: Rendering.
    /// </summary>
    public class SectionLayer_ABRimFacade : SectionLayer
    {
        public SectionLayer_ABRimFacade(Section section) : base(section)
        {
            // Terrain: rims move when floors or air change. Buildings: a wall
            // built on (or mined off) a rim cell toggles its band.
            // AB_BelowThings: below support walls change tint and the reveal
            // handoff; the settings sliders also dirty this flag.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)ABDefOf.AB_BelowThings;
        }

        public override bool Visible =>
            ABGuard.On(ABGuard.Rendering) && (ABMod.Settings?.drawWallFacade ?? true);

        // --- queue: above every walkable terrain family, under cutout ---
        private static int lowQueue;

        private static bool queueReady;

        private static void EnsureQueue()
        {
            if (queueReady)
            {
                return;
            }
            int terrain = 2000;
            Material soil = TerrainDefOf.Soil?.graphic?.MatSingle;
            if (soil != null)
            {
                terrain = soil.renderQueue > 0 ? soil.renderQueue
                    : (soil.shader != null ? soil.shader.renderQueue : 2000);
            }
            int shadow = MatBases.EdgeShadow != null ? MatBases.EdgeShadow.renderQueue : terrain;
            int cutout = ShaderDatabase.Cutout != null ? ShaderDatabase.Cutout.renderQueue : terrain + 450;
            lowQueue = Mathf.Clamp(Mathf.Max(terrain, shadow) + 1, terrain + 1, cutout - 1);
            queueReady = true;
        }

        private static Material facadeMatCached;

        private static Material FacadeMat()
        {
            if (facadeMatCached == null)
            {
                facadeMatCached = SolidColorMaterials.NewSolidColorMaterial(Color.white, ShaderDatabase.VertexColor);
                // +1 like the cap fade skirt: above the terrain families and
                // the cap's atlas clones, still under the cutout family. The
                // two layers never share a cell (cap cells are excluded here),
                // so the equal-queue overlap case cannot arise.
                facadeMatCached.renderQueue = lowQueue + 1;
            }
            return facadeMatCached;
        }

        /// <summary>Fractions of the band height given to the bright lit top
        /// corner and the dark contact foot (mockup height_1 proportions).</summary>
        private const float LitLineFrac = 0.12f;

        private const float ContactLineFrac = 0.10f;

        /// <summary>Above the terrain/cap quads at FloorEmplacement, mirroring
        /// the cap layer's own bias stack; queues do the real ordering.</summary>
        private const float AltBias = 0.03f;

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
                ABSettings settings = ABMod.Settings;
                if (settings != null && !settings.drawWallFacade)
                {
                    return;
                }
                EnsureQueue();
                Map lower = map.LowerMap();
                bool lowerOk = lower != null && !lower.Disposed;
                TerrainGrid grid = map.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                bool revealOn = settings?.drawWallReveal ?? true;
                float h = Mathf.Clamp(settings?.rimFacadeHeight ?? 0.55f, 0.25f, 1f);
                float y = AltitudeLayer.FloorEmplacement.AltitudeFor() + AltBias;
                bool emitted = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    TerrainDef t = grid.TerrainAt(c);
                    if (t == null || t == air || t == cap)
                    {
                        continue;
                    }
                    // Mined rock floors continue the mountain mass; the cap
                    // layer's atlas edge already draws their lip.
                    if (LevelSync.TryGetMinedRockDef(t, out _))
                    {
                        continue;
                    }
                    IntVec3 s = c + IntVec3.South;
                    if (!s.InBounds(map) || grid.TerrainAt(s) != air)
                    {
                        continue;
                    }
                    if (map.edificeGrid[c] != null)
                    {
                        // Wall or door on the rim: its sprite + drape is the
                        // face (stacked-walls "plumb" treatment).
                        continue;
                    }
                    Building support = null;
                    if (lowerOk && c.InBounds(lower))
                    {
                        Building ed = lower.edificeGrid[c];
                        if (ABRimPrint.QualifiesAsSupport(ed))
                        {
                            support = ed;
                        }
                    }
                    if (support != null && revealOn
                        && (support.def.seeThroughFog || !lower.fogGrid.IsFogged(support.Position)))
                    {
                        // The wall-top reveal dresses this strip already.
                        continue;
                    }
                    EmitBand(c, h, y, FacadeTone(t, support));
                    emitted = true;
                }
                if (emitted)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "rim facade layer");
            }
        }

        /// <summary>Band tint: supporting below wall's draw color, else the
        /// sky terrain's own def color, else neutral stone grey.</summary>
        private static Color FacadeTone(TerrainDef t, Building support)
        {
            if (support != null)
            {
                Color wc = support.DrawColor;
                if (wc.a > 0.05f)
                {
                    return wc;
                }
            }
            if (t.color != Color.white)
            {
                return t.color;
            }
            return new Color(0.45f, 0.44f, 0.41f);
        }

        private void EmitBand(IntVec3 c, float h, float y, Color tone)
        {
            LayerSubMesh sub = GetSubMesh(FacadeMat());
            float x0 = c.x;
            float x1 = c.x + 1f;
            float zBase = c.z;
            float contactTop = zBase + h * ContactLineFrac;
            float litBottom = zBase + h * (1f - LitLineFrac);
            float zTop = zBase + h;
            Color32 contact = Shade(tone, 0.26f);
            Color32 bodyLow = Shade(tone, 0.55f);
            Color32 bodyHigh = Shade(tone, 1.02f);
            Color32 lit = Shade(tone, 1.38f);
            // Foot to top: dark contact line, rising gradient body, lit corner.
            AddQuad(sub, x0, zBase, x1, contactTop, y, contact, contact, contact, contact);
            AddQuad(sub, x0, contactTop, x1, litBottom, y, bodyLow, bodyHigh, bodyHigh, bodyLow);
            AddQuad(sub, x0, litBottom, x1, zTop, y, lit, lit, lit, lit);
        }

        private static Color32 Shade(Color tone, float mul)
        {
            return new Color32(
                (byte)Mathf.Clamp(tone.r * mul * 255f, 0f, 255f),
                (byte)Mathf.Clamp(tone.g * mul * 255f, 0f, 255f),
                (byte)Mathf.Clamp(tone.b * mul * 255f, 0f, 255f),
                byte.MaxValue);
        }

        /// <summary>Vertex-colored quad; order (x0,z0), (x0,z1), (x1,z1),
        /// (x1,z0), colors (c00, c01, c11, c10). UVs sample the material
        /// center; the +0.01 north tilt mirrors Printer_Plane's seam rule.</summary>
        private const float NorthAltBias = 0.01f;

        private static void AddQuad(LayerSubMesh sub, float x0, float z0, float x1, float z1, float y,
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
    }
}
