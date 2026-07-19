using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lays out the sky level from a snapshot of the ground map's roofs:
    /// - no roof below: open air (impassable, invisible, shows the level below)
    /// - thick natural roof below: the mountain continues upward as solid mineable
    ///   rock matching surface geology, under a thick rock roof of its own
    /// - thin natural roof below: bare walkable rock surface (the mountain's rim)
    /// - constructed roof below: walkable, buildable rooftop
    /// After generation, LevelSync keeps this in step with live roof changes.
    /// </summary>
    public class GenStep_ABSkyTerrain : GenStep
    {
        public override int SeedPart => 762195842;

        public override void Generate(Map map, GenStepParams parms)
        {
            Map ground = map.GroundMap();
            List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile).ToList();
            if (rocks.Count == 0)
            {
                rocks.Add(ThingDefOf.Sandstone);
            }
            List<Perlin> noises = ABRockGen.MakeNoises(rocks.Count);
            TerrainGrid grid = map.terrainGrid;

            foreach (IntVec3 c in map.AllCells)
            {
                RoofDef roof = null;
                if (ground != null && c.InBounds(ground))
                {
                    roof = ground.roofGrid.RoofAt(c);
                }
                if (roof == null)
                {
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
                else if (roof.isNatural)
                {
                    ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                    if (roof.isThickRoof)
                    {
                        GenSpawn.Spawn(rock, c, map);
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                    }
                }
                else
                {
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
            }
        }
    }
}
