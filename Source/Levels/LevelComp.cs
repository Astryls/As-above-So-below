using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Present on every map. Stores this map's level (0 ground, +1 sky, -1 basement)
    /// and its vertical links. The ground map's comp doubles as the column controller
    /// and owns MapByLevel.
    /// </summary>
    public class LevelComp : MapComponent
    {
        // Cheap global gate for hot Harmony postfixes (the outdoor temperature
        // getters fire on every temperature read game wide): when no sky level
        // exists in the current game they early out on static reads alone. The
        // count is keyed to the Game via a weak reference because MapRemoved
        // never fires on game unload; a stale count from an abandoned game only
        // ever degrades the optimization, never correctness. Checked count-first
        // so the common no-sky case never touches the weak reference.
        private static WeakReference skyGame;
        private static int skyCount;

        public static bool AnySkyLevels =>
            skyCount > 0 && skyGame != null && skyGame.Target == (object)Current.Game;

        private static void NoteSkyLevel(int delta)
        {
            Game cur = Current.Game;
            if (cur == null)
            {
                return;
            }
            if (skyGame == null || skyGame.Target != (object)cur)
            {
                skyGame = new WeakReference(cur);
                skyCount = 0;
            }
            skyCount = Math.Max(0, skyCount + delta);
        }

        public int level;
        public Map upperMap;
        public Map lowerMap;
        public Map groundMap;

        private Dictionary<int, Map> mapByLevel;
        private List<int> tmpLevels;
        private List<Map> tmpMaps;

        public Dictionary<int, Map> MapByLevel => mapByLevel ?? (mapByLevel = new Dictionary<int, Map>());

        /// <summary>Spawned stairwells on this map. Runtime only, maintained by
        /// Building_ABStairs spawn and despawn.</summary>
        private List<Building_ABStairs> stairsList;

        public List<Building_ABStairs> Stairs => stairsList ?? (stairsList = new List<Building_ABStairs>());

        public void RegisterStairs(Building_ABStairs stairs)
        {
            if (stairs != null && !Stairs.Contains(stairs))
            {
                Stairs.Add(stairs);
            }
        }

        public void DeregisterStairs(Building_ABStairs stairs)
        {
            stairsList?.Remove(stairs);
        }

        public bool HasMultiLevels => mapByLevel != null && mapByLevel.Count > 1;

        public LevelComp(Map map) : base(map)
        {
            // During level generation the context tells the freshly constructed comp
            // which column and level it belongs to, before any content generates.
            LevelMapGen.Context ctx = LevelMapGen.CurrentContext;
            if (ctx != null)
            {
                level = ctx.levelToGenerate;
                groundMap = ctx.groundMap;
            }
        }

        private bool syncSubscribed;
        private Action<IntVec3> roofChangedHandler;
        private Action<IntVec3> terrainChangedHandler;
        private Action<Thing> thingSpawnedHandler;
        private Action<Thing> thingDespawnedHandler;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (level == 0 && groundMap == null)
            {
                groundMap = map;
            }
            if (level == 1)
            {
                NoteSkyLevel(1);
            }
            TrySubscribeSync();
            if (level == 1)
            {
                // Self-heal any air/rooftop drift against the live roof grid;
                // a no-op when the event path kept everything in sync.
                LevelSync.ReconcileRooftops(map);
            }
        }

        private const int WeatherSyncInterval = 150;

        /// <summary>Matches the VEF pipe net tick (100) so each direct
        /// injection covers exactly one net tick window.</summary>
        private const int PipeBridgeInterval = 100;

        /// <summary>Stairwell heat exchange; slower than pipes because the
        /// exchange constant was tuned at this cadence.</summary>
        private const int ClimateExchangeInterval = 250;

        private const int SubscribeRetryInterval = 250;

        /// <summary>Cadence of the stuck-hostile scan on pocket levels. Gated
        /// behind a cheap any-hostiles check, so calm maps pay one lookup.
        /// Never visibility-throttled: NPC behavior is simulation.</summary>
        private const int HostileScanInterval = 250;

        /// <summary>Low-frequency safety net over the event-driven rooftop sync:
        /// a time-sliced whole-map sweep that converges the air/rooftop state
        /// even if roof events misfire for any reason.</summary>
        private const int RooftopSweepInterval = 2000;

        /// <summary>Interval stretch for purely visual mirroring systems
        /// (weather, sweep) while this map is not the one on screen. On-view
        /// catch-up fires immediately, so the player never sees stale state.</summary>
        private const int HiddenWeatherMultiplier = 4;

        private const int HiddenSweepMultiplier = 5;

        /// <summary>Cells reconciled per tick while a rooftop sweep is active;
        /// a full 250x250 map converges in ~16 ticks with no single-tick spike.</summary>
        private const int SweepCellsPerTick = 4096;

        // Elapsed-time scheduling instead of TicksGame modulo: modulo beats are
        // silently missed when a debug time skip, save or load, or another
        // mod's tick throttling makes component ticks non-contiguous, and an
        // interval that stretches while hidden needs a due-tick anyway. The
        // dues are deliberately not scribed; after a load each fires once at
        // its stagger offset and re-seats the cadence.
        private int nextWeatherDue = -1;

        private int nextSweepDue = -1;

        private int nextHostileDue = -1;

        private int nextClimateDue = -1;

        private int nextPipesDue = -1;

        /// <summary>Cursor of the active time-sliced rooftop sweep; -1 = idle.</summary>
        private int sweepCursor = -1;

        private bool wasVisible;

        private static int Stagger(Map map, int interval) => map.uniqueID % interval;

        /// <summary>Lazy-init + elapsed-time due check with a per-map stagger.</summary>
        private bool Due(ref int due, int now, int interval)
        {
            if (due < 0)
            {
                due = now + Stagger(map, interval) + 1;
                return false;
            }
            if (now < due)
            {
                return false;
            }
            due = now + interval;
            return true;
        }

        /// <summary>Sky comps sync weather from the ground; the ground comp drives
        /// pipe network bridging for every stairwell pair (each pair has one end
        /// on the ground map under the three-level cap). Visual mirroring
        /// (weather, rooftop sweep) stretches its cadence while the map is not
        /// on screen and catches up the moment it becomes visible; simulation
        /// (pipes, climate, hostiles) never throttles.</summary>
        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            bool visible = map == Find.CurrentMap;
            if (visible && !wasVisible)
            {
                // The player just switched here: sync the visual mirrors now
                // instead of waiting out a stretched hidden-cadence window.
                if (level == 1)
                {
                    nextWeatherDue = 0;
                    if (sweepCursor < 0)
                    {
                        sweepCursor = 0;
                    }
                }
            }
            wasVisible = visible;
            if (level != 0 && !syncSubscribed && now % SubscribeRetryInterval == 0)
            {
                // FinalizeInit's subscription can bail silently when a link or
                // the events object is not ready yet; retry until it lands so
                // the roof and terrain cascades can never stay dead for a whole
                // session. One bool read per tick once subscribed.
                TrySubscribeSync();
            }
            if (level != 0 && ABGuard.On(ABGuard.HostileMove)
                && Due(ref nextHostileDue, now, HostileScanInterval))
            {
                try
                {
                    HostileDescend.ScanPocketMap(this);
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.HostileMove, e, "hostile descend scan");
                }
            }
            if (level == 1)
            {
                int weatherInterval = visible ? WeatherSyncInterval : WeatherSyncInterval * HiddenWeatherMultiplier;
                if (ABGuard.On(ABGuard.Weather) && Due(ref nextWeatherDue, now, weatherInterval))
                {
                    try
                    {
                        Map ground = lowerMap ?? groundMap;
                        if (ground != null && !ground.Disposed)
                        {
                            WeatherDef target = ground.weatherManager.curWeather;
                            if (target != null && map.weatherManager.curWeather != target)
                            {
                                map.weatherManager.TransitionTo(target);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Weather, e, "weather sync");
                    }
                }
                int sweepInterval = visible ? RooftopSweepInterval : RooftopSweepInterval * HiddenSweepMultiplier;
                if (sweepCursor < 0 && Due(ref nextSweepDue, now, sweepInterval))
                {
                    sweepCursor = 0;
                }
                if (sweepCursor >= 0
                    && !LevelSync.ReconcileRooftopsSlice(map, ref sweepCursor, SweepCellsPerTick))
                {
                    sweepCursor = -1;
                }
            }
            else if (level == 0)
            {
                if (stairsList == null || stairsList.Count == 0)
                {
                    return;
                }
                if (ABGuard.On(ABGuard.Climate) && Due(ref nextClimateDue, now, ClimateExchangeInterval))
                {
                    try
                    {
                        LevelClimate.TickGroundPairs(this);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Climate, e, "stairwell heat exchange");
                    }
                }
                if (ABGuard.On(ABGuard.Pipes) && Due(ref nextPipesDue, now, PipeBridgeInterval))
                {
                    try
                    {
                        ABPipeCompat.TickGroundPairs(this);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Pipes, e, "pipe network bridge");
                    }
                }
            }
        }

        /// <summary>Sky level comps listen to the ground map's roof changes and their
        /// own terrain/spawn events; both sky and basement comps listen for mineable
        /// removal to guarantee the fog reveal.</summary>
        private void TrySubscribeSync()
        {
            if (syncSubscribed || level == 0 || map.events == null)
            {
                return;
            }
            Map self = map;
            if (level == 1)
            {
                Map ground = lowerMap ?? groundMap;
                if (ground == null || ground.events == null)
                {
                    return;
                }
                roofChangedHandler = c => LevelSync.OnGroundRoofChanged(ground, c);
                terrainChangedHandler = c => LevelSync.OnSkyTerrainChanged(self, c);
                thingSpawnedHandler = t => LevelSync.OnSkyThingSpawned(self, t);
                ground.events.RoofChanged += roofChangedHandler;
                map.events.TerrainChanged += terrainChangedHandler;
                map.events.ThingSpawned += thingSpawnedHandler;
            }
            thingDespawnedHandler = t => LevelSync.OnLevelMineableDespawned(self, t);
            map.events.ThingDespawned += thingDespawnedHandler;
            syncSubscribed = true;
            ABLog.Dev("Level sync subscribed for map " + map.uniqueID + " (level " + level + ").");
            // Surface ceiling hints regenerate from the Roofs flag; after a load the
            // surface sections can build before this sky map's links restore, so
            // nudge a one-time whole-map regen. Cosmetic: failure is swallowed.
            if (level == 1)
            {
                try
                {
                    (lowerMap ?? groundMap)?.mapDrawer?.WholeMapChanged(MapMeshFlagDefOf.Roofs);
                }
                catch (Exception e)
                {
                    ABLog.Dev("Ceiling hint load nudge skipped: " + e.Message);
                }
            }
        }

        private void UnsubscribeSync()
        {
            if (!syncSubscribed)
            {
                return;
            }
            Map ground = lowerMap ?? groundMap;
            if (ground?.events != null && roofChangedHandler != null)
            {
                ground.events.RoofChanged -= roofChangedHandler;
            }
            if (map?.events != null)
            {
                if (terrainChangedHandler != null)
                {
                    map.events.TerrainChanged -= terrainChangedHandler;
                }
                if (thingSpawnedHandler != null)
                {
                    map.events.ThingSpawned -= thingSpawnedHandler;
                }
                if (thingDespawnedHandler != null)
                {
                    map.events.ThingDespawned -= thingDespawnedHandler;
                }
            }
            syncSubscribed = false;
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            UnsubscribeSync();
            if (level != 0)
            {
                ABApi.NotifyLevelRemoved(map);
            }
            if (level == 1)
            {
                NoteSkyLevel(-1);
            }
            // Sever stair links cleanly so counterparts on surviving maps read as
            // not connected instead of holding references into a disposed map.
            if (stairsList != null)
            {
                for (int i = 0; i < stairsList.Count; i++)
                {
                    stairsList[i]?.SeverLink();
                }
                stairsList.Clear();
            }
            Map ground = groundMap;
            if (ground != null && !ground.Disposed && ground != map)
            {
                ground.Levels()?.CleanupInvalidMaps();
            }
        }

        public void AddLevel(int lvl, Map newMap)
        {
            if (MapByLevel.ContainsKey(lvl))
            {
                Log.Warning(ABLog.Tag + " Level " + lvl + " already registered on this column, replacing.");
                MapByLevel.Remove(lvl);
            }
            MapByLevel.Add(lvl, newMap);
        }

        public void CleanupInvalidMaps()
        {
            if (mapByLevel != null)
            {
                mapByLevel.RemoveAll(kvp => kvp.Value == null || kvp.Value.Disposed);
            }
            if (upperMap != null && upperMap.Disposed)
            {
                upperMap = null;
            }
            if (lowerMap != null && lowerMap.Disposed)
            {
                lowerMap = null;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref level, "AB_level", 0);
            Scribe_References.Look(ref upperMap, "AB_upperMap");
            Scribe_References.Look(ref lowerMap, "AB_lowerMap");
            Scribe_References.Look(ref groundMap, "AB_groundMap");
            Scribe_Collections.Look(ref mapByLevel, "AB_mapByLevel", LookMode.Value, LookMode.Reference, ref tmpLevels, ref tmpMaps);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                CleanupInvalidMaps();
            }
        }
    }
}
