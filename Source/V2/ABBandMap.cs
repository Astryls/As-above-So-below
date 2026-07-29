using System.Collections.Generic;
using RimWorld;
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
        /// <summary>Minimum impassable rows between adjacent bands. Two is enough to stop
        /// any 1-cell adjacency or region span crossing the seam; the real gutter is
        /// usually wider because Slot is rounded up (see SlotAlignment).</summary>
        public const int MinGutter = 2;

        /// <summary>Slot is rounded UP to a multiple of this.
        ///
        /// Not arbitrary: RimWorld's terrain shaders sample their texture from WORLD
        /// POSITION, so drawing the surface's terrain one Slot higher samples a different
        /// phase of the tiling texture and the ground appears to "randomise" between the
        /// level and its see-below view. Terrain textures tile over a power-of-two number
        /// of cells, so making the vertical offset a multiple of 64 lands the sampling on
        /// exactly the same phase and the two views match.</summary>
        public const int SlotAlignment = 64;

        /// <summary>Rows consumed per band including its gutter, for a given band height.</summary>
        public static int SlotFor(int bandHeight)
        {
            int min = bandHeight + MinGutter;
            return Mathf.CeilToInt(min / (float)SlotAlignment) * SlotAlignment;
        }

        /// <summary>1 means "not banded" - an ordinary vanilla map.</summary>
        public int bandCount = 1;

        /// <summary>Playable rows per band (excludes the gutter).</summary>
        public int bandHeight;

        /// <summary>Which band index is the surface (level 0).</summary>
        public int surfaceBand;

        /// <summary>The biome the basement was carved as, or null for plain solid rock.
        ///
        /// This is V2's stand-in for V1's <c>map.pocketTileInfo.PrimaryBiome</c>. V1 got
        /// persistence for free because vanilla deep-scribes the pocket tile; here the
        /// choice has to be scribed ourselves or a reloaded save would quietly revert the
        /// basement to solid rock - plant regrowth, wildlife and ambience all silently
        /// changing behaviour on load, which is exactly the sort of bug that only shows up
        /// a week later. Read through ABBandEnv.BiomeOf, never directly.</summary>
        public BiomeDef basementBiome;

        /// <summary>Which band the camera is currently looking at. -1 means "not chosen",
        /// which resolves to the surface.
        ///
        /// DELIBERATELY NOT SCRIBED, and deliberately not static. It used to live in a
        /// static Dictionary keyed by map.uniqueID in ABBandView, which leaked across
        /// games: uniqueID restarts at 0 for every new game, so starting or loading a
        /// colony inherited the band the PREVIOUS colony was last viewed at, and the player
        /// opened their new map looking at the sky (or at black, for an unopened band).
        /// Per-map state belongs on the map component.
        ///
        /// Not scribed because loading a save should always put you back on the ground
        /// floor, which is what FinalizeInit enforces below.</summary>
        public int viewBand = -1;

        /// <summary>Bands the player has actually opened (stairs built into them).
        /// The surface is always open. Unopened bands exist physically but are fogged
        /// and inert.</summary>
        /// <summary>A List rather than a HashSet: it holds at most bandCount entries, so
        /// linear scan beats hashing, and Scribe_Collections handles it without ceremony.</summary>
        private List<int> openedBands = new List<int>();

        public ABBandMap(Map map) : base(map)
        {
        }

        /// <summary>Fires after the map is fully constructed on both new-game and load, which
        /// is where the wormhole re-arm handler is attached. Registration is idempotent and
        /// only meaningful on a banded map - an ordinary map has no synthetic links to lose.</summary>
        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (!Banded)
            {
                return;
            }
            ABWormholeRearmHook.Register(map);

            // Always open on the ground floor. Runs for a new colony AND for a loaded save,
            // so a save taken while looking at the sky or the basement still comes back to
            // the surface. The camera has to be moved explicitly rather than left to the
            // band clamp: with free camera panning enabled there IS no clamp, so a restored
            // camera position one band away would otherwise leave the player staring at
            // black space with no obvious way back.
            viewBand = surfaceBand;
            try
            {
                CameraDriver cam = Find.CameraDriver;
                if (cam == null)
                {
                    return;
                }
                Vector3 p = cam.MapPosition.ToVector3();
                IntVec3 look = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.z));
                if (!look.InBounds(map) || BandOf(look) == surfaceBand)
                {
                    return;
                }
                IntVec3 moved = Translate(look, surfaceBand);
                if (moved.InBounds(map))
                {
                    cam.SetRootPosAndSize(new Vector3(moved.x + 0.5f, 0f, moved.z + 0.5f),
                        cam.ZoomRootSize);
                }
            }
            catch
            {
                // Camera positioning is cosmetic; never let it break map init.
            }
        }

        public bool Banded => bandCount > 1 && bandHeight > 0;

        /// <summary>Rows consumed per band including its gutter.</summary>
        public int Slot => SlotFor(bandHeight);

        /// <summary>Actual gutter height after slot alignment.</summary>
        public int GutterRows => Slot - bandHeight;

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

        /// <summary>Drop the per-band biome memo so an abandoned map is not pinned alive by
        /// it (and can never be answered from a stale component).</summary>
        public override void MapRemoved()
        {
            base.MapRemoved();
            Patch_MixedBiome_ABBandBiomeAt.Forget();
            ABBands.ForgetMemo();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref bandCount, "AB2_bandCount", 1);
            Scribe_Values.Look(ref bandHeight, "AB2_bandHeight", 0);
            Scribe_Values.Look(ref surfaceBand, "AB2_surfaceBand", 0);
            Scribe_Defs.Look(ref basementBiome, "AB2_basementBiome");
            Scribe_Collections.Look(ref openedBands, "AB2_openedBands", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && openedBands == null)
            {
                openedBands = new List<int>();
            }
        }
    }
}
