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

        /// <summary>
        /// THE COLONY'S OWN CLIMATE, copied from the settings at generation and scribed.
        ///
        /// Same reasoning as bandCount/bandHeight sitting here rather than being read live:
        /// a generated world does not change shape because a slider moved. Reading the live
        /// settings for temperature would silently re-climate every existing save - a player
        /// tuning the alpine numbers for a NEW colony would melt the snow line and kill the
        /// crops in the one they have been running for three years. Null on a map generated
        /// before this existed, which the resolver in ABBandEnv treats as "fall back to
        /// settings, then defaults".
        /// </summary>
        public List<float> climateSky;

        public List<float> climateDeep;

        public List<float> climateWind;

        /// <summary>Freeze the current settings onto this map. Called once, beside Setup.</summary>
        public void SnapshotClimate(ABSettings s)
        {
            if (s == null)
            {
                return;
            }
            s.EnsureClimateLists();
            climateSky = new List<float>(s.skyTempOffsets);
            climateDeep = new List<float>(s.deepTempOffsets);
            climateWind = new List<float>(s.skyWindFactors);
        }

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
            // FIRST, unconditionally: this instance is the authoritative component, so
            // repair any cache that latched a mid-load placeholder (see RebindAfterLoad).
            ABBands.RebindAfterLoad(map, this);
            Patch_MixedBiome_ABBandBiomeAt.Forget();
            if (!Banded)
            {
                return;
            }
            ABWormholeRearmHook.Register(map);
            // Same event, same reason: RegionsRoomsChanged is the only cheap, truthful signal
            // that regions moved. See the banner in ABBandComponents.
            ABBandComponents.Register(map);

            // REPAIR THE SKY BAND BEFORE ANYTHING LOOKS AT IT.
            //
            // Placed here rather than in a load hook because this is the first point at
            // which the damage for THIS load has already happened: Map.FinalizeLoading
            // respawns every building first, and every one of those re-registers with the
            // edifice grid and re-fires ABSkySync (see the load note in that file). Running
            // earlier would repair a map that is about to be broken again.
            //
            // Map.FinalizeInit calls components LAST, after RebuildAllRegionsAndRooms, so
            // the AB_WallTop -> AB_MountainTop rows change passability after the region
            // build - TerrainGrid.SetTerrain's own change effects handle that incrementally,
            // and the queued RegenerateEverythingNow still runs after this.
            try
            {
                ABSkySync.RepairAfterLoad(map, this);
            }
            catch (System.Exception e)
            {
                Log.Error(ABLog.Tag + " V2: sky-sync repair threw (map left as-is): " + e);
            }

            // Always open on the ground floor. Runs for a new colony AND for a loaded save,
            // so a save taken while looking at the sky or the basement still comes back to
            // the surface. The camera has to be moved explicitly rather than left to the
            // band clamp: with free camera panning enabled there IS no clamp, so a restored
            // camera position one band away would otherwise leave the player staring at
            // black space with no obvious way back.
            viewBand = surfaceBand;

            // The camera move MUST be deferred to the main thread. FinalizeInit runs on a
            // LongEvent WORKER thread during load, and CameraDriver.MapPosition reads the
            // Unity transform - a cross-thread Unity API call that throws. The first
            // version did this inline inside a silent catch, so on every LOAD the exception
            // was swallowed and the camera was never moved: with free panning enabled there
            // is no clamp to rescue it either, so the restored camera sat in the old band's
            // coordinates while viewBand said 'surface', the view-rect clip drew only the
            // surface band, and the player loaded into empty void - reported as "camera
            // clamping breaks on reload". ExecuteWhenFinished runs after load on the main
            // thread, which is the standard pattern for exactly this.
            Map m = map;
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                try
                {
                    if (m == null || Find.Maps == null || !Find.Maps.Contains(m)
                        || Find.CurrentMap != m)
                    {
                        return; // map gone, or not the one on screen - nothing to fix
                    }
                    // Judge by the REMEMBERED camera position, not the live camera. Vanilla
                    // restores map.rememberedCameraPos itself during load, and the ordering
                    // between that restore and this deferred delegate is not ours to rely
                    // on - reading the live camera mid-restore judged a transient position.
                    // rememberedCameraPos is the authoritative answer either way.
                    Vector3 remembered = m.rememberedCameraPos != null
                        ? m.rememberedCameraPos.rootPos
                        : Find.CameraDriver.MapPosition.ToVector3();
                    IntVec3 look = new IntVec3(Mathf.RoundToInt(remembered.x), 0,
                        Mathf.RoundToInt(remembered.z));
                    if (look.InBounds(m) && BandOf(look) == surfaceBand)
                    {
                        return; // saved looking at the surface: vanilla's restore is right
                    }
                    // Saved looking at another band. viewBand has been forced to the
                    // surface, so the restored position would show the wrong band (or void
                    // with free panning). Land on the COLONISTS, like vanilla does at game
                    // start - a translated abstract column meant nothing to the player
                    // (reported: "camera does not land on pawns as expected").
                    Pawn anchor = null;
                    foreach (Pawn p in m.mapPawns.FreeColonistsSpawned)
                    {
                        if (BandOf(p.Position) == surfaceBand)
                        {
                            anchor = p;
                            break;
                        }
                        anchor = anchor ?? p;
                    }
                    if (anchor != null)
                    {
                        ABBandView.JumpTo(m, anchor.Position);
                        return;
                    }
                    // No colonists at all: translate the remembered column into the surface
                    // band so at least the neighbourhood is familiar.
                    IntVec3 moved = look.InBounds(m)
                        ? Translate(look, surfaceBand)
                        : RectOfBand(surfaceBand).CenterCell;
                    if (moved.InBounds(m))
                    {
                        Find.CameraDriver.SetRootPosAndSize(
                            new Vector3(moved.x + 0.5f, 0f, moved.z + 0.5f),
                            Find.CameraDriver.ZoomRootSize);
                    }
                }
                catch
                {
                    // Camera positioning is cosmetic; never let it break load.
                }
            });
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
            Scribe_Collections.Look(ref climateSky, "AB2_climateSky", LookMode.Value);
            Scribe_Collections.Look(ref climateDeep, "AB2_climateDeep", LookMode.Value);
            Scribe_Collections.Look(ref climateWind, "AB2_climateWind", LookMode.Value);
            Scribe_Defs.Look(ref basementBiome, "AB2_basementBiome");
            Scribe_Collections.Look(ref openedBands, "AB2_openedBands", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && openedBands == null)
            {
                openedBands = new List<int>();
            }
        }
    }
}
