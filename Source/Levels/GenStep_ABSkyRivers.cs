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
            ABLog.Dev("Sky rivers: carved " + carved.Count + " cells, "
                + map.listerThings.ThingsOfDef(ABDefOf.AB_Waterfall).Count + " waterfall lips, "
                + basesPlaced.Count + " bases.");
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
