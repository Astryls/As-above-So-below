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

        /// <summary>
        /// THE DUAL OF <see cref="Cover"/>, for a fan the cell OWNS rather than one
        /// reaching into it from outside.
        ///
        /// <see cref="Cover"/> is a DILATION - a union, where any matching neighbour claims
        /// its points, so a border grows inward from whoever qualifies. This is an EROSION -
        /// an intersection, where a perimeter point survives only if everything touching it
        /// continues the shape. The centre always survives: it is the cell being drawn.
        ///
        /// Use this whenever a cell's own fill must RETRACT from its open sides instead of
        /// ending on the cell boundary. A full-cell quad under a linked-atlas tile is the
        /// case that motivated it: the atlas art is transparent outside the rock outline
        /// (rounded corners, wavy lip), so an opaque quad beneath fills those gaps back in
        /// and the stylised silhouette reads as a square. Eroding the fill to the same
        /// neighbour rule the tile's link mask uses hides it under the art exactly.
        ///
        /// ⚠ Passing Cover's coverage here by mistake is not a compile error and looks
        /// almost right - it dissolves the middle of each open edge while keeping the
        /// corners, i.e. scalloped rather than retracted.
        /// </summary>
        internal static void CoverInterior(bool[] covered, bool north, bool south, bool east,
            bool west, bool southWest, bool northWest, bool northEast, bool southEast)
        {
            covered[0] = south && west && southWest; // SW corner
            covered[1] = west;                       // W mid
            covered[2] = north && west && northWest; // NW corner
            covered[3] = north;                      // N mid
            covered[4] = north && east && northEast; // NE corner
            covered[5] = east;                       // E mid
            covered[6] = south && east && southEast; // SE corner
            covered[7] = south;                      // S mid
            covered[8] = true;                       // centre: the cell itself
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
            Color32[] c = TwoToneScratch;
            for (int i = 0; i < 9; i++)
            {
                c[i] = covered[i] ? coveredColor : clearColor;
            }
            AddFan(sub, x, z, y, c);
        }

        /// <summary>Scratch for the two-tone overload. [ThreadStatic] on principle: map
        /// generation runs layer code off the main thread and a shared static here would be
        /// a silent cross-thread colour scramble rather than an exception.</summary>
        [System.ThreadStatic]
        private static Color32[] twoToneScratch;

        private static Color32[] TwoToneScratch => twoToneScratch ?? (twoToneScratch = new Color32[9]);

        /// <summary>The geometry, once. Per-vertex colours in the index space documented on
        /// the class (0=SW, 1=W mid, 2=NW, 3=N mid, 4=NE, 5=E mid, 6=SE, 7=S mid, 8=centre)
        /// so a caller can carry a gradient - the cliff-face ramp - across the fan instead of
        /// being limited to one tone plus transparent.</summary>
        internal static void AddFan(LayerSubMesh sub, int x, int z, float y, Color32[] colors)
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
                sub.colors.Add(colors[i]);
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
