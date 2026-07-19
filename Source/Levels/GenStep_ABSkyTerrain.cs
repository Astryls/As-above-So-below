using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lays out the sky level from a snapshot of the ground map:
    /// - mineable edifice OR thick natural roof below: the mountain continues
    ///   upward as solid mineable rock (aligned exactly with the rock face below),
    ///   under its own thick rock roof, fogged like unexplored mountain
    /// - otherwise thin natural roof below: bare walkable rock rim (the overhang)
    /// - otherwise constructed roof below: walkable, buildable rooftop
    /// - no roof below: open air (impassable, invisible, shows the level below)
    /// After generation, LevelSync keeps rooftops in step with live roof changes.
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
            List<IntVec3> fogCells = new List<IntVec3>();

            foreach (IntVec3 c in map.AllCells)
            {
                RoofDef roof = null;
                Building edifice = null;
                if (ground != null && c.InBounds(ground))
                {
                    roof = ground.roofGrid.RoofAt(c);
                    edifice = ground.edificeGrid[c];
                }
                bool rockHere = (edifice != null && edifice.def.mineable)
                    || (roof != null && roof.isNatural && roof.isThickRoof);
                if (rockHere)
                {
                    ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                    GenSpawn.Spawn(rock, c, map);
                    map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                    fogCells.Add(c);
                }
                else if (roof == null)
                {
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
                else if (roof.isNatural)
                {
                    ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                }
                else
                {
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
            }

            // Fog exactly the rock interior so it reads as unexplored mountain and
            // defogs naturally as it gets mined or seen.
            FogGrid fog = map.fogGrid;
            for (int i = 0; i < fogCells.Count; i++)
            {
                IntVec3 c = fogCells[i];
                fog.Refog(new CellRect(c.x, c.z, 1, 1));
            }
        }
    }
}
