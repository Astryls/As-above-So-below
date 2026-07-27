using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE. The band model: which layer of the single map a cell belongs to.
    ///
    /// In shipping V2 this is pure geometry - the map is (w, h * bandCount), bands
    /// stack along +z, and BandOf is one integer divide. Because CellIndices is
    /// row-major (z * sizeX + x), a band is a CONTIGUOUS index range, which is why
    /// +z was chosen over +x.
    ///
    /// For the spike we also support a rect layout so a wormhole can be tested on an
    /// ordinary map without generating a banded one: the sealed test chamber is
    /// "band 1", everything else is "band 0". The consumer-facing API (BandOf /
    /// SameBand) is identical either way, so the segmentation code the spike
    /// exercises is the code V2 ships.
    /// </summary>
    public sealed class ABBandLayout
    {
        /// <summary>Band height in cells for the stacked (real V2) layout.</summary>
        private readonly int bandHeight;

        /// <summary>Spike-only: cells inside this rect are band 1.</summary>
        private readonly CellRect testRect;

        private readonly bool useRect;

        private ABBandLayout(int bandHeight, CellRect testRect, bool useRect)
        {
            this.bandHeight = bandHeight;
            this.testRect = testRect;
            this.useRect = useRect;
        }

        /// <summary>The real V2 layout: bands stacked along +z.</summary>
        public static ABBandLayout Stacked(int bandHeight)
        {
            return new ABBandLayout(bandHeight, default(CellRect), useRect: false);
        }

        /// <summary>Spike layout: one rect carved out of an ordinary map.</summary>
        public static ABBandLayout TestRect(CellRect rect)
        {
            return new ABBandLayout(0, rect, useRect: true);
        }

        public int BandOf(IntVec3 cell)
        {
            if (useRect)
            {
                return testRect.Contains(cell) ? 1 : 0;
            }
            return bandHeight > 0 ? cell.z / bandHeight : 0;
        }
    }

    public static class ABBands
    {
        private static readonly Dictionary<int, ABBandLayout> layouts = new Dictionary<int, ABBandLayout>();

        /// <summary>Spike-only: runtime registration, not scribed. A reload drops the
        /// layout and the wormhole goes inert - acceptable for a throwaway branch,
        /// and a reminder that V2 must persist the layout on the map itself.</summary>
        public static void Register(Map map, ABBandLayout layout)
        {
            if (map != null)
            {
                layouts[map.uniqueID] = layout;
            }
        }

        public static void Clear(Map map)
        {
            if (map != null)
            {
                layouts.Remove(map.uniqueID);
            }
        }

        public static ABBandLayout LayoutOf(Map map)
        {
            if (map == null)
            {
                return null;
            }
            return layouts.TryGetValue(map.uniqueID, out ABBandLayout l) ? l : null;
        }

        public static bool Banded(Map map) => LayoutOf(map) != null;

        public static int BandOf(Map map, IntVec3 cell)
        {
            ABBandLayout l = LayoutOf(map);
            return l == null ? 0 : l.BandOf(cell);
        }

        public static bool SameBand(Map map, IntVec3 a, IntVec3 b)
        {
            ABBandLayout l = LayoutOf(map);
            return l == null || l.BandOf(a) == l.BandOf(b);
        }
    }
}
