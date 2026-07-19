using System.Linq;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lays out the sky level from a snapshot of the ground map's roofs:
    /// no roof below = open air (impassable), natural rock roof below = walkable
    /// rock surface (mountain top), constructed roof below = walkable rooftop.
    /// Live re-sync when roofs change on the ground map arrives in tranche T1.
    /// </summary>
    public class GenStep_ABSkyTerrain : GenStep
    {
        public override int SeedPart => 762195842;

        public override void Generate(Map map, GenStepParams parms)
        {
            Map ground = map.GroundMap();
            TerrainDef rockTerrain = ResolveRockTerrain(map);
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
                    grid.SetTerrain(c, rockTerrain);
                }
                else
                {
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
            }
        }

        private static TerrainDef ResolveRockTerrain(Map map)
        {
            ThingDef rock = Find.World.NaturalRockTypesIn(map.Tile).FirstOrDefault();
            return rock?.building?.naturalTerrain ?? TerrainDefOf.Gravel;
        }
    }
}
