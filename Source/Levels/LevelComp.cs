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

        /// <summary>Fills the buffer with every spawned stairwell across the
        /// whole column (this comp being the ground controller). The utility
        /// bridges run once from the ground comp yet must reach pairs that live
        /// entirely off the ground map - e.g. a link between +1 and +2 when the
        /// player raised the sky-level cap. Allocation free against a
        /// caller-owned buffer.</summary>
        public void CollectColumnStairs(List<Building_ABStairs> buffer)
        {
            buffer.Clear();
            if (mapByLevel != null && mapByLevel.Count > 0)
            {
                foreach (KeyValuePair<int, Map> kvp in mapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    List<Building_ABStairs> s = m.Levels()?.Stairs;
                    if (s == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < s.Count; i++)
                    {
                        if (s[i] != null)
                        {
                            buffer.Add(s[i]);
                        }
                    }
                }
            }
            else if (stairsList != null)
            {
                // No multi-level registry (ground-only): just our own stairs.
                for (int i = 0; i < stairsList.Count; i++)
                {
                    if (stairsList[i] != null)
                    {
                        buffer.Add(stairsList[i]);
                    }
                }
            }
        }

        public void DeregisterStairs(Building_ABStairs stairs)
        {
            stairsList?.Remove(stairs);
        }

        public bool HasMultiLevels => mapByLevel != null && mapByLevel.Count > 1;

        /// <summary>New list of every live map in this column except this comp's
        /// own map. For the infrequent column-wide aggregations (storyteller
        /// wealth and pawns, trade beacons, settlement-loss headcount) that must
        /// see every level, not just the two adjacent maps - a small allocation
        /// off the hot path is fine. Returns empty when no levels are linked.</summary>
        public List<Map> LinkedLevelMaps()
        {
            List<Map> result = new List<Map>();
            if (mapByLevel != null)
            {
                foreach (KeyValuePair<int, Map> kvp in mapByLevel)
                {
                    Map m = kvp.Value;
                    if (m != null && !m.Disposed && m != map)
                    {
                        result.Add(m);
                    }
                }
            }
            return result;
        }

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
            if (level > 0)
            {
                NoteSkyLevel(1);
            }
            TrySubscribeSync();
            if (level > 0)
            {
                // Self-heal any air/rooftop drift against the live roof grid;
                // a no-op when the event path kept everything in sync. Every
                // sky level reconciles against the level directly below it.
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

        /// <summary>Sky comps sync weather from the level directly below them;
        /// the ground comp drives pipe network bridging for every stairwell pair
        /// in the whole column (enumerated via CollectColumnStairs, so pairs
        /// living entirely above the ground - e.g. +1<->+2 - are covered too).
        /// Visual mirroring
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
                if (level > 0)
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
            if (level > 0)
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
                // The bridges enumerate the whole column, so keep driving them
                // whenever any level exists even if the ground map itself holds
                // no stairs (e.g. only a +1<->+2 utility shaft remains).
                if ((stairsList == null || stairsList.Count == 0) && !HasMultiLevels)
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
            if (level > 0)
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
            if (level > 0)
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
            if (level > 0)
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
