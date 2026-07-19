using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lays out the sky level from a snapshot of the ground map.
    /// The mountain mass (mineable edifice or thick natural roof below) rises one
    /// step contracted: its outer ring stays an open walkable rock ledge, and the
    /// eroded core is solid mineable rock under thick rock roof, FULLY fogged -
    /// exactly a vanilla unexplored mountain (playtest spec: fog fills the whole
    /// mass; faces reveal as pawns approach). Constructed roofs below become
    /// corrugated rooftop; natural thin roofs (the overhang strip) become the
    /// VANILLA rough stone terrain of the local rock so the mountain edge is
    /// sealed and looks native; only roofless cells are open air showing the
    /// level below. Ore lumps scatter into the mass so it is worth mining.
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
            CellIndices indices = map.cellIndices;
            int cellCount = indices.NumGridCells;

            // Pass 1: the solid mountain mass, projected from below.
            bool[] solid = new bool[cellCount];
            foreach (IntVec3 c in map.AllCells)
            {
                RoofDef roof = null;
                Building edifice = null;
                if (ground != null && c.InBounds(ground))
                {
                    roof = ground.roofGrid.RoofAt(c);
                    edifice = ground.edificeGrid[c];
                }
                solid[indices.CellToIndex(c)] = (edifice != null && edifice.def.mineable)
                    || (roof != null && roof.isNatural && roof.isThickRoof);
            }

            // Pass 2: erode by one. The ring becomes ledge, the core becomes walls.
            bool[] wall = new bool[cellCount];
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                wall[idx] = solid[idx] && AllNeighbors(map, indices, solid, c);
            }

            // Pass 3: terrain, walls, roofs; collect the fog core (walls eroded again)
            // and the wall cells that can host ore lumps.
            List<IntVec3> fogCells = new List<IntVec3>();
            List<IntVec3> oreCells = new List<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                int idx = indices.CellToIndex(c);
                if (solid[idx])
                {
                    ThingDef rock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, rock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                    if (wall[idx])
                    {
                        // The whole mass: solid rock under thick roof, fogged like a
                        // vanilla unexplored mountain. Faces unfog as pawns approach.
                        GenSpawn.Spawn(rock, c, map);
                        oreCells.Add(c);
                        map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                        fogCells.Add(c);
                    }
                    // Ledge ring: open walkable rock, no wall, no roof, never fogged.
                    continue;
                }
                RoofDef roofBelow = null;
                if (ground != null && c.InBounds(ground))
                {
                    roofBelow = ground.roofGrid.RoofAt(c);
                }
                if (roofBelow == null)
                {
                    // No roof below: open air, the ground stays visible.
                    grid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
                else if (roofBelow.isNatural)
                {
                    // Thin-roof overhang strip outside the mass: the top of the
                    // mountain's edge, sealed with the vanilla rough stone of the
                    // local rock so it reads native.
                    ThingDef rimRock = rocks[ABRockGen.PickIndex(noises, c)];
                    grid.SetTerrain(c, rimRock.building?.naturalTerrain ?? TerrainDefOf.Gravel);
                }
                else
                {
                    grid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
            }

            // Pass 4: fog only the deep interior; the outer wall row stays visible.
            FogGrid fog = map.fogGrid;
            for (int i = 0; i < fogCells.Count; i++)
            {
                IntVec3 c = fogCells[i];
                fog.Refog(new CellRect(c.x, c.z, 1, 1));
            }

            // Pass 5: ore lumps inside the mass walls.
            ABOreGen.ScatterOres(map, oreCells, OreLumpsPer10kCells);
        }

        private const float OreLumpsPer10kCells = 6f;

        /// <summary>True when all 8 neighbors are set; cells beyond the map edge
        /// count as set so masses touching the border stay solid there.</summary>
        private static bool AllNeighbors(Map map, CellIndices indices, bool[] grid, IntVec3 c)
        {
            IntVec3[] adjacent = GenAdj.AdjacentCells;
            for (int i = 0; i < adjacent.Length; i++)
            {
                IntVec3 n = c + adjacent[i];
                if (!n.InBounds(map))
                {
                    continue;
                }
                if (!grid[indices.CellToIndex(n)])
                {
                    return false;
                }
            }
            return true;
        }

    }
}
