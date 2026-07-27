using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - the band layout for one map, persisted.
    ///
    /// A banded map is a single Map sized (w, 1, bandCount * Slot). Bands stack along
    /// +z so that a band occupies a CONTIGUOUS range of CellIndices (row-major
    /// z * sizeX + x) - that is why +z was chosen over +x, and it makes BandOf a single
    /// integer divide on the hot path.
    ///
    /// Between bands sits a Gutter of impassable open-air rows. The gutter guarantees
    /// no region, room, temperature zone, or section mesh ever spans two bands by
    /// accident: bands can only ever be joined by an explicit wormhole (see ABWormhole).
    ///
    /// Band index vs level: level 0 is the surface, +1 sky, -1 basement.
    /// band = surfaceBand + level. Higher band = higher z = higher level, so the map
    /// reads bottom-to-top as basement / surface / sky.
    /// </summary>
    public class ABBandMap : MapComponent
    {
        /// <summary>Impassable rows between adjacent bands. Two is enough to stop any
        /// 1-cell adjacency or region span crossing the seam.</summary>
        public const int Gutter = 2;

        /// <summary>1 means "not banded" - an ordinary vanilla map.</summary>
        public int bandCount = 1;

        /// <summary>Playable rows per band (excludes the gutter).</summary>
        public int bandHeight;

        /// <summary>Which band index is the surface (level 0).</summary>
        public int surfaceBand;

        /// <summary>Bands the player has actually opened (stairs built into them).
        /// The surface is always open. Unopened bands exist physically but are fogged
        /// and inert.</summary>
        /// <summary>A List rather than a HashSet: it holds at most bandCount entries, so
        /// linear scan beats hashing, and Scribe_Collections handles it without ceremony.</summary>
        private List<int> openedBands = new List<int>();

        public ABBandMap(Map map) : base(map)
        {
        }

        public bool Banded => bandCount > 1 && bandHeight > 0;

        /// <summary>Rows consumed per band including its gutter.</summary>
        public int Slot => bandHeight + Gutter;

        public int BandOf(IntVec3 c)
        {
            if (!Banded)
            {
                return 0;
            }
            return Mathf.Clamp(c.z / Slot, 0, bandCount - 1);
        }

        /// <summary>True for the impassable seam rows at the top of each band's slot.</summary>
        public bool InGutter(IntVec3 c)
        {
            if (!Banded)
            {
                return false;
            }
            return c.z % Slot >= bandHeight;
        }

        public int LevelOf(IntVec3 c) => BandOf(c) - surfaceBand;

        public int BandForLevel(int level) => surfaceBand + level;

        public bool BandExists(int band) => Banded && band >= 0 && band < bandCount;

        public CellRect RectOfBand(int band)
        {
            if (!Banded)
            {
                return CellRect.WholeMap(map);
            }
            return new CellRect(0, band * Slot, map.Size.x, bandHeight);
        }

        /// <summary>Same (x, in-band z), translated to another band. This is what keeps
        /// the column aligned 1:1: a stairwell's far end sits directly above/below it.</summary>
        public IntVec3 Translate(IntVec3 c, int toBand)
        {
            if (!Banded)
            {
                return c;
            }
            int within = c.z % Slot;
            return new IntVec3(c.x, c.y, toBand * Slot + within);
        }

        public bool IsOpen(int band) => band == surfaceBand || (openedBands != null && openedBands.Contains(band));

        public void Open(int band)
        {
            if (!BandExists(band) || band == surfaceBand)
            {
                return;
            }
            if (openedBands == null)
            {
                openedBands = new List<int>();
            }
            if (!openedBands.Contains(band))
            {
                openedBands.Add(band);
                ABLog.Dev("Band " + band + " (level " + (band - surfaceBand) + ") opened on map " + map.uniqueID + ".");
            }
        }

        public void Setup(int bandCount, int bandHeight, int surfaceBand)
        {
            this.bandCount = bandCount;
            this.bandHeight = bandHeight;
            this.surfaceBand = surfaceBand;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref bandCount, "AB2_bandCount", 1);
            Scribe_Values.Look(ref bandHeight, "AB2_bandHeight", 0);
            Scribe_Values.Look(ref surfaceBand, "AB2_surfaceBand", 0);
            Scribe_Collections.Look(ref openedBands, "AB2_openedBands", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && openedBands == null)
            {
                openedBands = new List<int>();
            }
        }
    }
}
