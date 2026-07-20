using System.Collections.Generic;
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
