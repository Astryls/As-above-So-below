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
            if (destLevel < -1 || destLevel > 1 || destLevel == 0)
            {
                ABLog.Dev("Rejected level generation request for level " + destLevel + " (cap is one up, one down).");
                return null;
            }
            try
            {
                // V2 interlock: a banded map already IS the whole column, so growing
                // V1 pocket levels on top of it would produce two competing level
                // models. Everything else in V1 goes inert on a banded map by itself
                // (its machinery keys off upperMap/lowerMap, which stay null), so this
                // single guard is the whole interlock.
                if (ABBands.Banded(currentMap))
                {
                    ABLog.Dev("Refused V1 level generation on a V2 banded map.");
                    return null;
                }
                Map ground = currentMap.Level() == 0 ? currentMap : currentMap.GroundMap();
                LevelComp controller = ground?.Levels();
                if (controller == null)
                {
                    return null;
                }
                // Belt-and-suspenders for the PlaceWorker gate: never grow a
                // column on a foreign special map opted out of z-levels (e.g.
                // an Ancient urban ruins exploration submap). Covers any path
                // that reaches generation without a placement check.
                if (AncientUrbanRuinsCompat.BlocksLevels(ground))
                {
                    ABLog.Dev("Refused level generation on opted-out map " + ground.uniqueID + " (Ancient urban ruins submap).");
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
                levelToGenerate = destLevel
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
