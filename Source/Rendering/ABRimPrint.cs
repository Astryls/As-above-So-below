using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Shared helpers for the two rim-wall print layers (the rooftop wall-top
    /// reveal and the slab-edge wall facade): the supporting-edifice predicate
    /// mirroring LevelSync.CoveredBelow's wall branch, plus print snapshot and
    /// rollback so a mid-print exception can never leave a submesh with
    /// mismatched vert/uv/color counts (FinalizeMesh would then throw on every
    /// rebuild and take the whole section down with it).
    /// </summary>
    internal static class ABRimPrint
    {
        /// <summary>True for the below edifices that count as roof support in
        /// LevelSync.CoveredBelow AND print into map meshes: artificial
        /// impassable buildings (walls, coolers, vents), never natural rock,
        /// stairs, or realtime-only drawers (doors).</summary>
        internal static bool QualifiesAsSupport(Building ed)
        {
            if (ed == null || ed is Building_ABStairs)
            {
                return false;
            }
            ThingDef d = ed.def;
            if (d.passability != Traversability.Impassable || d.mineable)
            {
                return false;
            }
            if (d.building != null && d.building.isNaturalRock)
            {
                return false;
            }
            DrawerType drawer = d.drawerType;
            return drawer == DrawerType.MapMeshOnly || drawer == DrawerType.MapMeshAndRealTime;
        }

        internal static void Snapshot(List<LayerSubMesh> subs, List<int> vertCounts, List<int> triCounts)
        {
            vertCounts.Clear();
            triCounts.Clear();
            for (int i = 0; i < subs.Count; i++)
            {
                vertCounts.Add(subs[i].verts.Count);
                triCounts.Add(subs[i].tris.Count);
            }
        }

        /// <summary>Truncates every submesh back to its snapshot; submeshes
        /// created after the snapshot shrink to empty.</summary>
        internal static void Rollback(List<LayerSubMesh> subs, List<int> vertCounts, List<int> triCounts)
        {
            for (int i = 0; i < subs.Count; i++)
            {
                Truncate(subs[i],
                    i < vertCounts.Count ? vertCounts[i] : 0,
                    i < triCounts.Count ? triCounts[i] : 0);
            }
        }

        /// <summary>True for the sun shadow volume material Printer_Shadow
        /// prints with (Graphic.Print appends the ShadowGraphic after the body
        /// quads). Drawn as plain geometry outside the shadow screen pass it
        /// renders as solid black shapes, so both rim layers drop it.</summary>
        internal static bool IsShadowMaterial(Material m)
        {
            return m != null && m == MatBases.SunShadowFade;
        }

        /// <summary>Drops unsafe geometry a print appended: shadow volumes
        /// (uv-less colored verts with the sun shadow fade material) and any
        /// submesh whose parallel arrays no longer line up - a count mismatch
        /// would poison FinalizeMesh for the whole section layer.</summary>
        internal static void DropUnsafeNewGeometry(List<LayerSubMesh> subs,
            List<int> vertCounts, List<int> triCounts)
        {
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sub = subs[i];
                int vFrom = i < vertCounts.Count ? vertCounts[i] : 0;
                int tFrom = i < triCounts.Count ? triCounts[i] : 0;
                if (sub.verts.Count <= vFrom)
                {
                    continue;
                }
                if (IsShadowMaterial(sub.material)
                    || sub.uvs.Count != sub.verts.Count
                    || sub.colors.Count != sub.verts.Count)
                {
                    Truncate(sub, vFrom, tFrom);
                }
            }
        }

        internal static void Truncate(LayerSubMesh sub, int vertCount, int triCount)
        {
            if (sub.verts.Count > vertCount)
            {
                sub.verts.RemoveRange(vertCount, sub.verts.Count - vertCount);
            }
            if (sub.uvs.Count > vertCount)
            {
                sub.uvs.RemoveRange(vertCount, sub.uvs.Count - vertCount);
            }
            if (sub.colors.Count > vertCount)
            {
                sub.colors.RemoveRange(vertCount, sub.colors.Count - vertCount);
            }
            if (sub.tris.Count > triCount)
            {
                sub.tris.RemoveRange(triCount, sub.tris.Count - triCount);
            }
        }
    }
}
