using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vanilla's 9-vertex cell fan - the exact geometry SectionLayerGeometryMaker_Solid
    /// bakes for fog of war and SectionLayer_Terrain hand-rolls for edge fades: four
    /// corners, four edge midpoints and a center, eight triangles, per-vertex color
    /// carrying the "how far does the border reach into this cell" information.
    ///
    /// Shared by the see-below fog port (soft fog boundary instead of hard per-cell
    /// squares), the mountain-cap meadow fade (the real terrain fade mechanic at the
    /// plateau boundary) and the drop-off shadow ring (EdgeShadow contact line where the
    /// mass meets open air). One geometry, three materials.
    ///
    /// Vertex index space is vanilla FogOfWar's: 0=SW corner, 1=W mid, 2=NW, 3=N mid,
    /// 4=NE, 5=E mid, 6=SE, 7=S mid, 8=center. The coverage rule is also vanilla's: a
    /// matching CARDINAL neighbour claims its edge's three perimeter points, a matching
    /// DIAGONAL neighbour claims only its corner, and the center belongs to the cell
    /// itself (CoverAll).
    ///
    /// Deliberately NO uvs: the fog and edge-shadow materials do not sample them, and
    /// terrain materials sample WORLD POSITION - writing 0..1 uvs per quad is the
    /// documented "muddy smear" trap.
    /// </summary>
    internal static class ABNineFan
    {
        /// <summary>Coverage from the eight neighbours, vanilla FogOfWar rules.</summary>
        internal static void Cover(bool[] covered, bool north, bool south, bool east,
            bool west, bool southWest, bool northWest, bool northEast, bool southEast)
        {
            for (int i = 0; i < 9; i++)
            {
                covered[i] = false;
            }
            if (north)
            {
                covered[2] = true;
                covered[3] = true;
                covered[4] = true;
            }
            if (south)
            {
                covered[6] = true;
                covered[7] = true;
                covered[0] = true;
            }
            if (east)
            {
                covered[4] = true;
                covered[5] = true;
                covered[6] = true;
            }
            if (west)
            {
                covered[0] = true;
                covered[1] = true;
                covered[2] = true;
            }
            if (southWest)
            {
                covered[0] = true;
            }
            if (northWest)
            {
                covered[2] = true;
            }
            if (northEast)
            {
                covered[4] = true;
            }
            if (southEast)
            {
                covered[6] = true;
            }
        }

        /// <summary>The cell itself qualifies: every point covered, center included.</summary>
        internal static void CoverAll(bool[] covered)
        {
            for (int i = 0; i < 9; i++)
            {
                covered[i] = true;
            }
        }

        internal static bool Any(bool[] covered)
        {
            for (int i = 0; i < 9; i++)
            {
                if (covered[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>One cell's fan at (x..x+1, z..z+1): 9 verts, 8 tris (vanilla's exact
        /// triangulation), covered verts get coveredColor, the rest clearColor.</summary>
        internal static void AddFan(LayerSubMesh sub, int x, int z, float y, bool[] covered,
            Color32 coveredColor, Color32 clearColor)
        {
            int n = sub.verts.Count;
            sub.verts.Add(new Vector3(x, y, z));
            sub.verts.Add(new Vector3(x, y, z + 0.5f));
            sub.verts.Add(new Vector3(x, y, z + 1));
            sub.verts.Add(new Vector3(x + 0.5f, y, z + 1));
            sub.verts.Add(new Vector3(x + 1, y, z + 1));
            sub.verts.Add(new Vector3(x + 1, y, z + 0.5f));
            sub.verts.Add(new Vector3(x + 1, y, z));
            sub.verts.Add(new Vector3(x + 0.5f, y, z));
            sub.verts.Add(new Vector3(x + 0.5f, y, z + 0.5f));
            for (int i = 0; i < 9; i++)
            {
                sub.colors.Add(covered[i] ? coveredColor : clearColor);
            }
            sub.tris.Add(n + 7);
            sub.tris.Add(n);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 2);
            sub.tris.Add(n + 3);
            sub.tris.Add(n + 3);
            sub.tris.Add(n + 4);
            sub.tris.Add(n + 5);
            sub.tris.Add(n + 5);
            sub.tris.Add(n + 6);
            sub.tris.Add(n + 7);
            sub.tris.Add(n + 7);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 8);
            sub.tris.Add(n + 1);
            sub.tris.Add(n + 3);
            sub.tris.Add(n + 8);
            sub.tris.Add(n + 3);
            sub.tris.Add(n + 5);
            sub.tris.Add(n + 8);
            sub.tris.Add(n + 5);
            sub.tris.Add(n + 7);
            sub.tris.Add(n + 8);
        }
    }
}
