using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mountain rivers (2026-07-24): where the ground map's river tunnels under
    /// a mountain (vanilla keeps thick rock roof over rivers narrower than 20
    /// cells), the sky level's mountain mass carries the same watercourse
    /// across its top - the exact surface cells projected plumb, so parity
    /// with the tile's original generation is true by construction.
    ///
    /// Flow: the ground map's riverFlowMap is a per-cell float-pair list and
    /// both maps share dimensions, so a straight copy plus a cloned riverGraph
    /// gives the sky map pixel-exact vanilla water flow (WaterInfo.SetTextures
    /// rebuilds the flow texture from riverFlowMap and the map's own river
    /// terrain - no reflection into TileMutatorWorker needed).
    ///
    /// Ledges: sky river cells bordering open air become waterfall lips.
    /// Downstream lips (flow pointing off the edge, read from the real flow
    /// map) get the full treatment - foam, falling spray, sound - and a
    /// silent WaterfallBase spawns on the surface river below the drop for
    /// mist and rumble at ground level. Upstream lips (flow arriving from the
    /// air, i.e. the stream "starts" at the cliff) get foam and quiet sound
    /// only. Runs at order 205: after the sky terrain classifier, before the
    /// landmark mutators (whose structure placement avoids water natively).
    /// Fails soft: any exception logs and leaves the sky level riverless.
    /// </summary>
    public class GenStep_ABSkyRivers : GenStep
    {
        public override int SeedPart => 762195901;

        private const float LipFlowDot = 0.35f;

        public override void Generate(Map map, GenStepParams parms)
        {
            ABSettings settings = ABMod.Settings;
            if (settings != null && !settings.mountainRivers)
            {
                return;
            }
            if (!ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            try
            {
                GenerateInt(map);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Sky river generation failed (sky level continues without rivers): " + e);
            }
        }

        private static void GenerateInt(Map map)
        {
            Map ground = map.GroundMap();
            if (ground == null || ground.Disposed || ground.Size != map.Size)
            {
                return;
            }
            WaterInfo groundWater = ground.waterInfo;
            if (groundWater == null || groundWater.riverFlowMap == null
                || groundWater.riverFlowMap.Count == 0 || groundWater.riverGraph.NullOrEmpty())
            {
                return;
            }

            TerrainGrid groundTerrain = ground.terrainGrid;
            TerrainGrid skyTerrain = map.terrainGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;
            List<IntVec3> carved = new List<IntVec3>();

            foreach (IntVec3 c in map.AllCells)
            {
                TerrainDef below = groundTerrain.BaseTerrainAt(c);
                if (below == null || !below.IsRiver)
                {
                    continue;
                }
                TerrainDef top = skyTerrain.TerrainAt(c);
                if (top == null || top == air || top == ABDefOf.AB_RoofSurface
                    || top == ABDefOf.AB_Skylight)
                {
                    // Open air already shows the surface river below; built
                    // rooftops are never flooded.
                    continue;
                }
                // Carve the channel through the mass: drop the rock wall, the
                // roof, and the fog, then lay the same water the surface has.
                c.GetEdifice(map)?.Destroy();
                map.roofGrid.SetRoof(c, null);
                map.fogGrid.Unfog(c);
                skyTerrain.SetTerrain(c, below);
                carved.Add(c);
            }

            if (carved.Count == 0)
            {
                return;
            }

            // The river MOVES to the mountain top - it does not duplicate
            // (live report 2026-07-24: "rivers on each level"). The tunneled
            // stretch below the carved channel dries to the local rock's
            // natural floor, exactly what vanilla generates under a mountain
            // absent a river; the falls then feed the still-wet surface river
            // beyond the mass edge, so the watercourse is hydrologically
            // continuous: over the top, off the ledge, onward at ground level.
            int dried = DryTunnelStretch(ground, carved);

            // Cut the channel like a vanilla river, not a slit: a walkable
            // bank of the rock's rough floor on every mass-rock neighbor
            // (ore veins stay standing - an exposed seam by the stream), and
            // an unfogged rim ring so no gray fog skirt creeps over the banks
            // (live report 2026-07-24: "gray fog of war shadow on its banks").
            List<IntVec3> banks = new List<IntVec3>();
            CarveBanks(map, carved, banks);
            UnfogSolidNeighbors(map, carved);
            UnfogSolidNeighbors(map, banks);

            // Flow data: clone the graph, copy the per-cell flow list.
            WaterInfo skyWater = map.waterInfo;
            skyWater.riverGraph = new List<RiverNode>();
            for (int i = 0; i < groundWater.riverGraph.Count; i++)
            {
                RiverNode src = groundWater.riverGraph[i];
                skyWater.riverGraph.Add(new RiverNode
                {
                    start = src.start,
                    end = src.end,
                    width = src.width
                });
            }
            skyWater.riverFlowMap = new List<float>(groundWater.riverFlowMap);

            // Ledges.
            List<IntVec3> basesPlaced = new List<IntVec3>();
            for (int i = 0; i < carved.Count; i++)
            {
                IntVec3 c = carved[i];
                Vector3 flow = groundWater.GetWaterMovement(c.ToVector3Shifted());
                bool hasFlow = flow.sqrMagnitude > 0.0001f;
                Vector3 flowDir = hasFlow ? flow.normalized : FallbackFlowDir(skyWater);
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 dir = GenAdj.CardinalDirections[d];
                    IntVec3 n = c + dir;
                    if (!n.InBounds(map) || skyTerrain.TerrainAt(n) != air)
                    {
                        continue;
                    }
                    float dot = Vector3.Dot(flowDir, dir.ToVector3());
                    if (dot > LipFlowDot)
                    {
                        SpawnLip(map, c, Rot4.FromIntVec3(dir), inflow: false);
                        TrySpawnBase(ground, n, basesPlaced);
                        break;
                    }
                    if (dot < -LipFlowDot)
                    {
                        SpawnLip(map, c, Rot4.FromIntVec3(dir), inflow: true);
                        break;
                    }
                }
            }
            ABLog.Dev("Sky rivers: carved " + carved.Count + " water cells, "
                + banks.Count + " bank cells, dried " + dried + " tunnel cells below, "
                + map.listerThings.ThingsOfDef(ABDefOf.AB_Waterfall).Count + " waterfall lips, "
                + basesPlaced.Count + " bases.");
        }

        /// <summary>Replaces the ground map's river cells under the carved sky
        /// channel with the local rock's natural rough floor. Cells built or
        /// bridged over (TerrainAt != BaseTerrainAt) are left untouched.
        /// Returns the number of cells dried. KNOWN GAP: 1.6 water-body
        /// (fishing) data for the dried stretch may keep stale cells until the
        /// tracker's own refresh; cosmetic, noted in the schematic.</summary>
        private static int DryTunnelStretch(Map ground, List<IntVec3> carved)
        {
            TerrainGrid gt = ground.terrainGrid;
            int dried = 0;
            for (int i = 0; i < carved.Count; i++)
            {
                IntVec3 c = carved[i];
                TerrainDef baseT = gt.BaseTerrainAt(c);
                if (baseT == null || !baseT.IsRiver || gt.TerrainAt(c) != baseT)
                {
                    continue;
                }
                gt.SetTerrain(c, DryBedTerrain(ground, c));
                dried++;
            }
            return dried;
        }

        /// <summary>The natural rough floor of the rock flanking the tunnel;
        /// falls back to the tile's dominant rock, then gravel.</summary>
        private static TerrainDef DryBedTerrain(Map ground, IntVec3 c)
        {
            for (int d = 0; d < 8; d++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[d];
                if (!n.InBounds(ground))
                {
                    continue;
                }
                ThingDef rock = n.GetEdifice(ground)?.def;
                if (rock?.building != null && rock.building.isNaturalRock
                    && rock.building.naturalTerrain != null)
                {
                    return rock.building.naturalTerrain;
                }
            }
            foreach (ThingDef worldRock in Find.World.NaturalRockTypesIn(ground.Tile))
            {
                if (worldRock?.building?.naturalTerrain != null)
                {
                    return worldRock.building.naturalTerrain;
                }
            }
            return TerrainDefOf.Gravel;
        }

        /// <summary>One walkable bank cell for every plain-rock neighbor of the
        /// water: wall down, roof off, fog off, the rock's rough floor laid.
        /// Ore veins are NOT consumed - they stand as exposed seams and the
        /// rim unfog makes them visible.</summary>
        private static void CarveBanks(Map map, List<IntVec3> water, List<IntVec3> banks)
        {
            TerrainGrid skyTerrain = map.terrainGrid;
            for (int i = 0; i < water.Count; i++)
            {
                for (int d = 0; d < 8; d++)
                {
                    IntVec3 n = water[i] + GenAdj.AdjacentCells[d];
                    if (!n.InBounds(map))
                    {
                        continue;
                    }
                    Building ed = n.GetEdifice(map);
                    if (ed == null || ed.def.building == null || !ed.def.building.isNaturalRock)
                    {
                        continue;
                    }
                    TerrainDef bank = ed.def.building.naturalTerrain ?? TerrainDefOf.Gravel;
                    ed.Destroy();
                    map.roofGrid.SetRoof(n, null);
                    map.fogGrid.Unfog(n);
                    skyTerrain.SetTerrain(n, bank);
                    banks.Add(n);
                }
            }
        }

        /// <summary>Reveals the ring of still-solid cells around the carved
        /// corridor so walls and seams render instead of a fog skirt - the
        /// same one-ring reveal vanilla map gen gives open areas.</summary>
        private static void UnfogSolidNeighbors(Map map, List<IntVec3> cells)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                for (int d = 0; d < 8; d++)
                {
                    IntVec3 n = cells[i] + GenAdj.AdjacentCells[d];
                    if (n.InBounds(map) && map.fogGrid.IsFogged(n))
                    {
                        map.fogGrid.Unfog(n);
                    }
                }
            }
        }

        private static Vector3 FallbackFlowDir(WaterInfo water)
        {
            if (!water.riverGraph.NullOrEmpty())
            {
                Vector3 v = water.riverGraph[0].end - water.riverGraph[0].start;
                if (v.sqrMagnitude > 0.001f)
                {
                    return v.normalized;
                }
            }
            return Vector3.forward;
        }

        private static void SpawnLip(Map map, IntVec3 c, Rot4 facing, bool inflow)
        {
            if (map.thingGrid.ThingAt(c, ABDefOf.AB_Waterfall) != null)
            {
                return;
            }
            Thing_ABWaterfall lip = (Thing_ABWaterfall)ThingMaker.MakeThing(ABDefOf.AB_Waterfall);
            lip.inflow = inflow;
            GenSpawn.Spawn(lip, c, map, facing);
        }

        /// <summary>Silent mist-and-rumble marker on the surface river where the
        /// water lands. Thinned to one per fall cluster.</summary>
        private static void TrySpawnBase(Map ground, IntVec3 c, List<IntVec3> placed)
        {
            if (!c.InBounds(ground) || !ground.terrainGrid.BaseTerrainAt(c).IsRiver)
            {
                return;
            }
            for (int i = 0; i < placed.Count; i++)
            {
                if ((placed[i] - c).LengthHorizontalSquared <= 16f)
                {
                    return;
                }
            }
            if (ground.thingGrid.ThingAt(c, ABDefOf.AB_WaterfallBase) != null)
            {
                return;
            }
            GenSpawn.Spawn(ThingMaker.MakeThing(ABDefOf.AB_WaterfallBase), c, ground);
            placed.Add(c);
        }
    }
}
