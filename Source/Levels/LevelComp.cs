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
        }

        private const int WeatherSyncInterval = 150;

        private const int PipeBridgeInterval = 250;

        /// <summary>Sky comps sync weather from the ground; the ground comp drives
        /// pipe network bridging for every stairwell pair (each pair has one end
        /// on the ground map under the three-level cap).</summary>
        public override void MapComponentTick()
        {
            if (level == 1)
            {
                if (!ABGuard.On(ABGuard.Weather)
                    || (Find.TickManager.TicksGame + (map.uniqueID % WeatherSyncInterval)) % WeatherSyncInterval != 0)
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
            else if (level == 0)
            {
                if (stairsList == null || stairsList.Count == 0
                    || (Find.TickManager.TicksGame + (map.uniqueID % PipeBridgeInterval)) % PipeBridgeInterval != 0)
                {
                    return;
                }
                if (ABGuard.On(ABGuard.Climate))
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
                if (ABGuard.On(ABGuard.Pipes))
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
