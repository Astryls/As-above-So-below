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
    /// step contracted: its outer ring becomes an open walkable rock ledge, the
    /// eroded core becomes solid mineable rock under a thick rock roof, and only
    /// the core's interior (one more erosion in) is fogged, so the rock face ring
    /// stays visible exactly like a vanilla mountain. Constructed roofs below
    /// become corrugated rooftop, natural thin roofs (the mountain overhang
    /// strip) become walkable rock roof, and only roofless cells are open air
    /// showing the level below - so the mountain edge is sealed with no
    /// see-through slivers under the rock edge sprites. Ore lumps scatter into
    /// the mass so the sky mountain is worth mining.
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
                        GenSpawn.Spawn(rock, c, map);
                        oreCells.Add(c);
                        if (AllWithinRadius(map, indices, wall, c, 2))
                        {
                            // Deep interior only: roofed mountain, fogged like vanilla
                            // unexplored rock.
                            map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
                            fogCells.Add(c);
                        }
                        // The two visible face rows stay unroofed AND unfogged so their
                        // rock texture renders fully lit, matching Z-Levels beta's edge.
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
                    // mountain's edge, walkable rock roof.
                    grid.SetTerrain(c, ABDefOf.AB_RockRoofSurface);
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

        /// <summary>True when every cell within the square radius is set; cells
        /// beyond the map edge count as set.</summary>
        private static bool AllWithinRadius(Map map, CellIndices indices, bool[] grid, IntVec3 c, int radius)
        {
            foreach (IntVec3 n in CellRect.CenteredOn(c, radius))
            {
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
