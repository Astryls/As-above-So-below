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
    public partial class LevelComp : MapComponent
    {
        // The cheap global perf gates (AnySkyLevels/AnyBasementLevels/
        // AnyLevelColumns) live in LevelCensus now (refactor R3); this comp only
        // feeds the counts via LevelCensus.NoteLevel in FinalizeInit/MapRemoved.
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
                // Wire this new map's own vertical links NOW. The initial mesh
                // regen (Map.FinalizeInit defers RegenerateEverythingNow via
                // ExecuteWhenFinished) can run before GetOrGenerate's post-
                // generation linking, and the below-things layer then printed
                // every section empty with lowerMap null (run-34 diagnostic:
                // earlyNoLower == exactly the section count). Only THIS map's
                // links are set here; the reverse links on the ground map are
                // still wired after generation completes, so no other system
                // can reach the half-generated map through the column.
                if (level == 1)
                {
                    lowerMap = ctx.groundMap;
                }
                else if (level == -1)
                {
                    upperMap = ctx.groundMap;
                }
            }
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            UnsubscribeSync();
            if (level != 0)
            {
                ABApi.NotifyLevelRemoved(map);
            }
            if (level != 0)
            {
                LevelCensus.NoteLevel(level, -1);
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
