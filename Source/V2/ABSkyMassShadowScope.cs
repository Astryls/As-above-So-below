using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Scope vanilla's ambient shadow layers OUT of the sky-band mountain mass interior.
    ///
    /// The mass-unification cover (SectionLayer_ABMountainCap) turns fill ring, wall band
    /// and fogged interior into one continuous rock field - but vanilla still emits
    /// EdgeShadow rims and SunShadow skirts from the now-invisible walls onto adjacent
    /// open fill cells, redrawing the very cluster outlines the cover erased ("texture
    /// outline errors" screenshot: nested dark rims tracing every wall cluster).
    ///
    /// Both layers' Regenerate are replaced ON BANDED MAPS ONLY with verbatim vanilla
    /// geometry plus one filter: a sky-band natural-rock caster contributes nothing
    /// toward a cell that is itself part of the mass. Shadows at the true silhouette
    /// (onto meadow, rooftop or open air) are kept - a mountain still shades the ground
    /// beside it - and every non-sky-band cell gets byte-identical vanilla output.
    ///
    /// Non-banded maps take the vanilla path untouched (prefix returns true), and any
    /// exception falls back to vanilla for that section.
    /// </summary>
    internal static class ABSkyMassShadowScope
    {
        internal static bool MassCell(Map map, ABBandMap bands, int skyBand, IntVec3 c)
        {
            if (!c.InBounds(map) || bands.BandOf(c) != skyBand)
            {
                return false;
            }
            return SectionLayer_ABMountainCap.IsMassCell(map, map.terrainGrid, ABDefOf.AB_MountainTop, c);
        }

        /// <summary>A natural-rock edifice standing in the sky band - the casters the
        /// unification cover conceals.</summary>
        internal static bool IsSkyMassRock(Map map, ABBandMap bands, int skyBand, Thing t)
        {
            if (t == null || t.def == null)
            {
                return false;
            }
            if (!t.def.mineable && (t.def.building == null || !t.def.building.isNaturalRock))
            {
                return false;
            }
            IntVec3 p = t.Position;
            return p.InBounds(map) && bands.BandOf(p) == skyBand;
        }
    }

    [HarmonyPatch(typeof(SectionLayer_EdgeShadows), nameof(SectionLayer_EdgeShadows.Regenerate))]
    public static class Patch_SectionLayer_EdgeShadows_ABSkyMassScope
    {
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static bool Prefix(SectionLayer_EdgeShadows __instance)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Rendering))
                {
                    return true;
                }
                Section section = SectionRef(__instance);
                Map map = section?.map;
                ABBandMap bands = map != null ? ABBands.CompOf(map) : null;
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                RegenerateFiltered(__instance, section, map, bands);
                return false;
            }
            catch (Exception e)
            {
                // Vanilla clears the submesh at the top of its own Regenerate, so a
                // partial build here is safely discarded by the fallback.
                Log.WarningOnce(ABLog.Tag + " sky-mass edge-shadow scope failed, vanilla fallback: "
                    + e.Message, 762195890);
                return true;
            }
        }

        /// <summary>Vanilla SectionLayer_EdgeShadows.Regenerate with the sky-mass caster
        /// filter. Geometry lives in ABEdgeShadowGeometry (shared with the below-view
        /// port).</summary>
        private static void RegenerateFiltered(SectionLayer_EdgeShadows layer, Section section,
            Map map, ABBandMap bands)
        {
            Building[] edifices = map.edificeGrid.InnerArray;
            int skyBand = bands.surfaceBand + 1;
            float y = AltitudeLayer.Shadows.AltitudeFor();
            CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
            rect.ClipInsideMap(map);
            LayerSubMesh sm = layer.GetSubMesh(MatBases.EdgeShadow);
            sm.Clear(MeshParts.All);
            sm.verts.Capacity = rect.Area * 4;
            sm.colors.Capacity = rect.Area * 4;
            sm.tris.Capacity = rect.Area * 8;
            bool[] corner = new bool[4];
            bool[] cardinal = new bool[4];
            bool[] diagOnly = new bool[4];
            CellIndices indices = map.cellIndices;
            for (int i = rect.minX; i <= rect.maxX; i++)
            {
                for (int j = rect.minZ; j <= rect.maxZ; j++)
                {
                    IntVec3 here = new IntVec3(i, 0, j);
                    bool hereIsMass = ABSkyMassShadowScope.MassCell(map, bands, skyBand, here);
                    Thing thing = edifices[indices.CellToIndex(i, j)];
                    if (thing != null && thing.def.castEdgeShadows)
                    {
                        // A sky-mass rock's own ambient quad sits under the unification
                        // cover and would only resurface as a rim at partial overlaps.
                        if (!(hereIsMass && ABSkyMassShadowScope.IsSkyMassRock(map, bands, skyBand, thing)))
                        {
                            ABEdgeShadowGeometry.EmitCasterCell(sm, y, i, j);
                        }
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
                        IntVec3 c = here + cardinals[k];
                        if (c.InBounds(map))
                        {
                            thing = edifices[indices.CellToIndex(c)];
                            if (Casts(map, bands, skyBand, thing, hereIsMass))
                            {
                                cardinal[k] = true;
                                corner[(k + 3) % 4] = true;
                                corner[k] = true;
                            }
                        }
                    }
                    IntVec3[] diagonals = GenAdj.DiagonalDirectionsAround;
                    for (int l = 0; l < 4; l++)
                    {
                        if (corner[l])
                        {
                            continue;
                        }
                        IntVec3 c = here + diagonals[l];
                        if (c.InBounds(map))
                        {
                            thing = edifices[indices.CellToIndex(c)];
                            if (Casts(map, bands, skyBand, thing, hereIsMass))
                            {
                                corner[l] = true;
                                diagOnly[l] = true;
                            }
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

        private static bool Casts(Map map, ABBandMap bands, int skyBand, Thing t, bool receiverIsMass)
        {
            if (t == null || !t.def.castEdgeShadows)
            {
                return false;
            }
            if (!receiverIsMass)
            {
                return true;
            }
            return !ABSkyMassShadowScope.IsSkyMassRock(map, bands, skyBand, t);
        }
    }

    /// <summary>SectionLayer_SunShadows is internal, hence TargetMethod by name.</summary>
    [HarmonyPatch]
    public static class Patch_SectionLayer_SunShadows_ABSkyMassScope
    {
        private static readonly Color32 LowVertexColor = new Color32(0, 0, 0, 0);

        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method("Verse.SectionLayer_SunShadows:Regenerate");
        }

        private static bool Prefix(SectionLayer __instance)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Rendering))
                {
                    return true;
                }
                Section section = SectionRef(__instance);
                Map map = section?.map;
                ABBandMap bands = map != null ? ABBands.CompOf(map) : null;
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                RegenerateFiltered(__instance, section, map, bands);
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " sky-mass sun-shadow scope failed, vanilla fallback: "
                    + e.Message, 762195891);
                return true;
            }
        }

        /// <summary>Vanilla SectionLayer_SunShadows.Regenerate (base quad + west/east/south
        /// skirts, each with its own winding) with one filter: a sky-mass rock caster
        /// emits no skirt toward a mass cell. Its base quad is kept - alpha-0 verts do
        /// not displace, and the unification cover conceals it - so skirt indices stay
        /// exactly vanilla's.</summary>
        private static void RegenerateFiltered(SectionLayer layer, Section section, Map map,
            ABBandMap bands)
        {
            if (!MatBases.SunShadow.shader.isSupported)
            {
                return;
            }
            Building[] edifices = map.edificeGrid.InnerArray;
            int skyBand = bands.surfaceBand + 1;
            float y = AltitudeLayer.Shadows.AltitudeFor();
            CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, 17, 17);
            rect.ClipInsideMap(map);
            LayerSubMesh sm = layer.GetSubMesh(MatBases.SunShadow);
            sm.Clear(MeshParts.All);
            sm.verts.Capacity = rect.Area * 2;
            sm.tris.Capacity = rect.Area * 4;
            sm.colors.Capacity = rect.Area * 2;
            CellIndices indices = map.cellIndices;
            for (int i = rect.minX; i <= rect.maxX; i++)
            {
                for (int j = rect.minZ; j <= rect.maxZ; j++)
                {
                    Building building = edifices[indices.CellToIndex(i, j)];
                    if (building == null || !(building.def.staticSunShadowHeight > 0f))
                    {
                        continue;
                    }
                    bool scoped = ABSkyMassShadowScope.IsSkyMassRock(map, bands, skyBand, building);
                    float height = building.def.staticSunShadowHeight;
                    Color32 tall = new Color32(0, 0, 0, (byte)(255f * height));
                    int count = sm.verts.Count;
                    sm.verts.Add(new Vector3(i, y, j));
                    sm.verts.Add(new Vector3(i, y, j + 1));
                    sm.verts.Add(new Vector3(i + 1, y, j + 1));
                    sm.verts.Add(new Vector3(i + 1, y, j));
                    sm.colors.Add(LowVertexColor);
                    sm.colors.Add(LowVertexColor);
                    sm.colors.Add(LowVertexColor);
                    sm.colors.Add(LowVertexColor);
                    int count2 = sm.verts.Count;
                    sm.tris.Add(count2 - 4);
                    sm.tris.Add(count2 - 3);
                    sm.tris.Add(count2 - 2);
                    sm.tris.Add(count2 - 4);
                    sm.tris.Add(count2 - 2);
                    sm.tris.Add(count2 - 1);

                    // west
                    if (i > 0)
                    {
                        Building n = edifices[indices.CellToIndex(i - 1, j)];
                        if ((n == null || n.def.staticSunShadowHeight < height)
                            && !(scoped && ABSkyMassShadowScope.MassCell(map, bands, skyBand,
                                new IntVec3(i - 1, 0, j))))
                        {
                            int count3 = sm.verts.Count;
                            sm.verts.Add(new Vector3(i, y, j));
                            sm.verts.Add(new Vector3(i, y, j + 1));
                            sm.colors.Add(tall);
                            sm.colors.Add(tall);
                            sm.tris.Add(count + 1);
                            sm.tris.Add(count);
                            sm.tris.Add(count3);
                            sm.tris.Add(count3);
                            sm.tris.Add(count3 + 1);
                            sm.tris.Add(count + 1);
                        }
                    }

                    // east
                    if (i < map.Size.x - 1)
                    {
                        Building n = edifices[indices.CellToIndex(i + 1, j)];
                        if ((n == null || n.def.staticSunShadowHeight < height)
                            && !(scoped && ABSkyMassShadowScope.MassCell(map, bands, skyBand,
                                new IntVec3(i + 1, 0, j))))
                        {
                            int count4 = sm.verts.Count;
                            sm.verts.Add(new Vector3(i + 1, y, j + 1));
                            sm.verts.Add(new Vector3(i + 1, y, j));
                            sm.colors.Add(tall);
                            sm.colors.Add(tall);
                            sm.tris.Add(count + 2);
                            sm.tris.Add(count4);
                            sm.tris.Add(count4 + 1);
                            sm.tris.Add(count4 + 1);
                            sm.tris.Add(count + 3);
                            sm.tris.Add(count + 2);
                        }
                    }

                    // south
                    if (j > 0)
                    {
                        Building n = edifices[indices.CellToIndex(i, j - 1)];
                        if ((n == null || n.def.staticSunShadowHeight < height)
                            && !(scoped && ABSkyMassShadowScope.MassCell(map, bands, skyBand,
                                new IntVec3(i, 0, j - 1))))
                        {
                            int count5 = sm.verts.Count;
                            sm.verts.Add(new Vector3(i, y, j));
                            sm.verts.Add(new Vector3(i + 1, y, j));
                            sm.colors.Add(tall);
                            sm.colors.Add(tall);
                            sm.tris.Add(count);
                            sm.tris.Add(count + 3);
                            sm.tris.Add(count5);
                            sm.tris.Add(count + 3);
                            sm.tris.Add(count5 + 1);
                            sm.tris.Add(count5);
                        }
                    }
                }
            }
            if (sm.verts.Count > 0)
            {
                sm.FinalizeMesh(MeshParts.Verts | MeshParts.Tris | MeshParts.Colors);
                // Vanilla inflates the bounds because the shader displaces geometry
                // along the sun vector; without this Unity culls moving shadows.
                sm.mesh.bounds = new Bounds(Vector3.zero, new Vector3(1000f, 1000f, 1000f));
            }
        }
    }
}
