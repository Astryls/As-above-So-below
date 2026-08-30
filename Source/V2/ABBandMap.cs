using System;
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

        /// <summary>Freeze the current settings onto this map. Called once, beside Setup.
        ///
        /// ⚠ ALL-ZERO TEMPERATURE TABLES ARE NOT SNAPSHOTTED. Altitude temperature now
        /// ships OFF (all offsets 0), and a zero snapshot would WIN over live settings
        /// forever - a player who generates a colony at the defaults and later enables
        /// offsets would see nothing happen and no reason why. Leaving the snapshot null
        /// makes such a colony follow live settings (so enabling works), while a colony
        /// generated WITH a climate keeps the no-retro-climate protection the snapshot
        /// exists for. Wind always snapshots; its defaults are nonzero and unchanged.</summary>
        public void SnapshotClimate(ABSettings s)
        {
            if (s == null)
            {
                return;
            }
            s.EnsureClimateLists();
            climateSky = AnyNonZero(s.skyTempOffsets) ? new List<float>(s.skyTempOffsets) : null;
            climateDeep = AnyNonZero(s.deepTempOffsets) ? new List<float>(s.deepTempOffsets) : null;
            climateWind = new List<float>(s.skyWindFactors);
        }

        private static bool AnyNonZero(List<float> list)
        {
            if (list == null)
            {
                return false;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != 0f)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- see-below resolve cache (session-only, never scribed) ----------------
        //
        // Memoizes ABBands.TryResolveVisibleBelow per cell: value array holds
        // belowIndex + 1 (0 = "resolves to nothing"), stamp array holds the version the
        // entry was computed under. Invalidation is one Interlocked increment from the
        // terrain-write funnel (ABSeeBelowDirty); nothing is ever cleared or scanned.
        // Cost: two int[] of map.Area, lazily allocated on first banded resolve only
        // (~1.2 MB on a 7-level map - the price of turning regen-burst re-resolves and
        // the four-layers-same-frame redundancy into array reads).

        private int[] seeBelowCache;

        private int[] seeBelowStamp;

        private int seeBelowVersion = 1;

        private readonly object seeBelowGate = new object();

        public int SeeBelowVersion => System.Threading.Volatile.Read(ref seeBelowVersion);

        /// <summary>O(1) whole-map invalidation. Terrain writes are rare; re-resolves
        /// amortize onto next touch. Safe from any thread.</summary>
        public void DirtySeeBelowCache()
        {
            System.Threading.Interlocked.Increment(ref seeBelowVersion);
        }

        // ---- below-view fog gate (session-only, never scribed) --------------------
        //
        // Per-band verdict: does this band contain ANY unfogged cell? Every per-frame
        // below pass (pawns, realtime things, thing overlays, forbidden markers)
        // rejects fogged cells per thing, so when every band below the view is fully
        // fogged those whole-map walks provably produce nothing - the gate skips them
        // outright. Maintained EVENT-DRIVEN, zero Harmony: 1.6 raises
        // MapEvents.CellFogChanged from Unfog/Refog and MapEvents.MapFogged from
        // SetAllFogged, and the audit (2026-08-29) confirmed every writer funnels
        // through them - FloodFillerFog's unsafe ref is READ-only (PassCheck); its
        // Processor calls fogGrid.Unfog per cell. Only a foreign mod writing
        // FogGrid_Unsafe directly can bypass this, which would break vanilla's own
        // event consumers too; the debug assertion below catches that in test runs.
        //
        // Verdicts: 0 = unknown (scan on next ask), 1 = fully fogged, 2 = has
        // unfogged. An unfog event landing on a verdict-1 band IS proof it opened -
        // flip to 2, no scan. Scans therefore run once per band per session (lazily,
        // on the first below-view frame) plus after rare refog-class events. A refog
        // resets to unknown rather than trying to count. Fog only ever shrinks in
        // ordinary play, so a band that opens stays open for the save.

        private byte[] fogVerdict;

        private bool fogEventsHooked;

        private int lastFogAssertFrame = -1;

        /// <summary>Idempotent; called from FinalizeInit. The delegate and this
        /// component share the map's lifetime, so there is nothing to unhook.</summary>
        public void HookFogEvents()
        {
            if (fogEventsHooked || map?.events == null)
            {
                return;
            }
            fogEventsHooked = true;
            map.events.CellFogChanged += OnCellFogChanged;
            map.events.MapFogged += OnMapFogged;
        }

        private void OnCellFogChanged(IntVec3 c, bool fogged)
        {
            byte[] v = fogVerdict;
            if (v == null || !Banded)
            {
                return; // nothing asked yet: first ask scans fresh truth
            }
            int band = BandOf(c);
            if (band < 0 || band >= v.Length)
            {
                return; // gutter or malformed - no band owns it
            }
            if (fogged)
            {
                v[band] = 0; // refog: unknown; rescan on next ask
            }
            else if (v[band] == 1)
            {
                v[band] = 2; // the event is itself proof the band opened
            }
        }

        private void OnMapFogged()
        {
            byte[] v = fogVerdict;
            if (v != null)
            {
                Array.Clear(v, 0, v.Length);
            }
        }

        /// <summary>The gate. True when any band strictly below <paramref name="viewBand"/>
        /// contains at least one unfogged cell - i.e. a below pass could draw something.
        /// Fails OPEN on any doubt: an open gate merely pays the old cost.</summary>
        public bool AnyUnfoggedBelow(int viewBand)
        {
            if (!Banded)
            {
                return true;
            }
            byte[] v = fogVerdict ?? (fogVerdict = new byte[bandCount]);
            int lo = Math.Min(viewBand, v.Length);
            for (int b = 0; b < lo; b++)
            {
                byte verdict = v[b];
                if (verdict == 2)
                {
                    return true;
                }
                if (verdict == 0)
                {
                    if (!ScanBandFullyFogged(b))
                    {
                        v[b] = 2;
                        return true;
                    }
                    v[b] = 1;
                }
            }
            // Debug-only drift tripwire (LogTransit is #if DEBUG): the gate is about to
            // suppress the below passes, so periodically re-derive the verdicts from the
            // grid itself. A mismatch means a fog write path escaped the event audit.
            if (ABV2Debug.LogTransit && UnityEngine.Time.frameCount - lastFogAssertFrame > 600)
            {
                lastFogAssertFrame = UnityEngine.Time.frameCount;
                for (int b = 0; b < lo; b++)
                {
                    if (!ScanBandFullyFogged(b))
                    {
                        Log.Error(ABLog.Tag + " V2 fog-gate DRIFT: band " + b
                            + " has unfogged cells but verdict said fully fogged."
                            + " A fog write path bypassed MapEvents - report this.");
                        v[b] = 2;
                    }
                }
            }
            return false;
        }

        private bool ScanBandFullyFogged(int band)
        {
            FogGrid fog = map.fogGrid;
            foreach (IntVec3 c in RectOfBand(band))
            {
                if (!fog.IsFogged(c))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Lazy alloc under a gate (cold path, once per session per map). The
        /// stamp array is published LAST so a racing reader that sees it non-null sees a
        /// fully constructed pair; entries start 0, i.e. stale against version 1.</summary>
        public void GetSeeBelowCache(out int[] cache, out int[] stamp)
        {
            int[] s = seeBelowStamp;
            if (s != null)
            {
                cache = seeBelowCache;
                stamp = s;
                return;
            }
            lock (seeBelowGate)
            {
                if (seeBelowStamp == null)
                {
                    int n = map.cellIndices.NumGridCells;
                    seeBelowCache = new int[n];
                    System.Threading.Thread.MemoryBarrier();
                    seeBelowStamp = new int[n];
                }
                cache = seeBelowCache;
                stamp = seeBelowStamp;
            }
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
            // A banded map just became live (fresh generation or load) - give the patch
            // lifecycle a chance to apply the band temperature postfix before gameplay
            // reads temperatures. Self-defers to the main thread when this runs on the
            // loader thread.
            ABPatchLifecycle.Recheck("map-finalize");
            // Subscribe the below-view fog gate to vanilla's fog events. Verdicts stay
            // lazy, so anything that happened before this line is simply scanned fresh
            // on first use.
            HookFogEvents();
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
