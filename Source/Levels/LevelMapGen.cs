using System;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Creates level maps as vanilla pocket maps aligned 1:1 with the ground map.
    /// Hard cap: one level up (+1), one level down (-1).
    /// </summary>
    public static class LevelMapGen
    {
        public class Context
        {
            public Map groundMap;
            public int levelToGenerate;
            /// <summary>The map directly beneath the one being generated. For a
            /// sky level this is the level it rises from, so the ledge erodes
            /// the mountain inward one more ring per level up. Links are wired
            /// after generation, so the sky genstep cannot read LowerMap() yet
            /// and takes the below map from here.</summary>
            public Map belowMap;
        }

        /// <summary>Set only while a level map is generating; consumed by LevelComp's constructor.</summary>
        public static Context CurrentContext;

        public static Map GetOrGenerate(Map currentMap, int destLevel, MapGeneratorDef generatorDef, out bool generated)
        {
            generated = false;
            if (!ABGuard.On(ABGuard.LevelGen) || currentMap == null || generatorDef == null)
            {
                return null;
            }
            int maxUpper = ABMod.Settings?.MaxUpper ?? 1;
            if (destLevel < -1 || destLevel > maxUpper || destLevel == 0)
            {
                ABLog.Dev("Rejected level generation request for level " + destLevel + " (cap is " + maxUpper + " up, one down).");
                return null;
            }
            try
            {
                Map ground = currentMap.Level() == 0 ? currentMap : currentMap.GroundMap();
                LevelComp controller = ground?.Levels();
                if (controller == null)
                {
                    return null;
                }
                if (controller.MapByLevel.TryGetValue(destLevel, out Map existing) && existing != null && !existing.Disposed)
                {
                    return existing;
                }

                Map newMap = Generate(currentMap, ground, destLevel, generatorDef);
                if (newMap == null)
                {
                    return null;
                }

                if (!controller.MapByLevel.ContainsKey(0))
                {
                    controller.AddLevel(0, ground);
                }
                controller.AddLevel(destLevel, newMap);

                LevelComp newComp = newMap.Levels();
                LevelComp curComp = currentMap.Levels();
                if (destLevel > currentMap.Level())
                {
                    newComp.lowerMap = currentMap;
                    curComp.upperMap = newMap;
                }
                else
                {
                    newComp.upperMap = currentMap;
                    curComp.lowerMap = newMap;
                }

                generated = true;
                ABLog.Dev("Generated level " + destLevel + " map " + newMap.uniqueID + " for ground map " + ground.uniqueID + ".");
                return newMap;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "level map generation");
                return null;
            }
        }

        private static Map Generate(Map sourceMap, Map groundMap, int destLevel, MapGeneratorDef generatorDef)
        {
            PocketMapParent parent = (PocketMapParent)WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.PocketMap);
            parent.sourceMap = sourceMap;
            parent.mapGenerator = generatorDef;
            parent.Tile = sourceMap.Tile;
            CurrentContext = new Context
            {
                groundMap = groundMap,
                levelToGenerate = destLevel,
                // sourceMap is the level the player is climbing from, i.e. the
                // map directly below the new one for any sky generation.
                belowMap = sourceMap
            };
            try
            {
                Map map = MapGenerator.GenerateMap(sourceMap.Size, parent, generatorDef, null, null, true);
                Find.World.pocketMaps.Add(parent);
                ABApi.NotifyLevelCreated(map);
                return map;
            }
            finally
            {
                CurrentContext = null;
            }
        }
    }
}
