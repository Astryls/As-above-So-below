using System;
using System.Collections.Generic;
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
        public int level;
        public Map upperMap;
        public Map lowerMap;
        public Map groundMap;

        private Dictionary<int, Map> mapByLevel;
        private List<int> tmpLevels;
        private List<Map> tmpMaps;

        public Dictionary<int, Map> MapByLevel => mapByLevel ?? (mapByLevel = new Dictionary<int, Map>());

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
            TrySubscribeSync();
        }

        private const int WeatherSyncInterval = 150;

        /// <summary>The sky level adopts the ground map's weather so rain, snow, and
        /// storms stay linked between floors. Cheap: one check every 150 ticks.</summary>
        public override void MapComponentTick()
        {
            if (level != 1 || !ABGuard.On(ABGuard.Weather))
            {
                return;
            }
            if ((Find.TickManager.TicksGame + (map.uniqueID % WeatherSyncInterval)) % WeatherSyncInterval != 0)
            {
                return;
            }
            try
            {
                Map ground = lowerMap ?? groundMap;
                if (ground == null || ground.Disposed)
                {
                    return;
                }
                WeatherDef target = ground.weatherManager.curWeather;
                if (target != null && map.weatherManager.curWeather != target)
                {
                    map.weatherManager.TransitionTo(target);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Weather, e, "weather sync");
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
