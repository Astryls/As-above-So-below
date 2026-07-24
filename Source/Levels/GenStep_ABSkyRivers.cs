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

        /// <summary>Every run records its outcome (or the reason it bailed)
        /// here; the "AB: river diagnostic" dev tool prints it, so generation
        /// behavior is inspectable in-session without verbose logging.</summary>
        internal static string LastSummary = "never ran";

        /// <summary>How far (cells) to look for mountain mass on each side of
        /// a river when testing whether it crosses the massif as an open
        /// CANYON. Vanilla strips the rock roof over rivers wider than ~20
        /// cells (live finding 2026-07-24, round 8: the user's rivers are wide
        /// canyons, so the roofed-tunnel predicate never engaged) - a canyon
        /// cell has no roof and no sky mass above it, only walls flanking it.
        /// Must exceed half the widest expected canyon.</summary>
        private const int CanyonFlankScan = 16;

        /// <summary>Mountain mass within CanyonFlankScan cells on BOTH sides
        /// of any of the four axes: the cell sits in a cut through the massif.
        /// isMass tests one cell for mountain mass (sky terrain in the
        /// genstep, thick roof in the pre-sky qualifier).</summary>
        private static bool FlankedByMass(Map map, IntVec3 c, Func<IntVec3, bool> isMass)
        {
            for (int axis = 0; axis < 4; axis++)
            {
                IntVec3 dir = axis switch
                {
                    0 => IntVec3.North,
                    1 => IntVec3.East,
                    2 => new IntVec3(1, 0, 1),
                    _ => new IntVec3(1, 0, -1)
                };
                if (ScanForMass(map, c, dir, isMass) && ScanForMass(map, c, new IntVec3(-dir.x, 0, -dir.z), isMass))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ScanForMass(Map map, IntVec3 from, IntVec3 dir, Func<IntVec3, bool> isMass)
        {
            IntVec3 c = from;
            for (int i = 0; i < CanyonFlankScan; i++)
            {
                c += dir;
                if (!c.InBounds(map))
                {
                    return false;
                }
                if (isMass(c))
                {
                    return true;
                }
            }
            return false;
        }

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
            LastSummary = "ran, bailed: no linked ground map";
            Map ground = map.GroundMap();
            if (ground == null || ground.Disposed || ground.Size != map.Size)
            {
                return;
            }
            // FLOW DATA IS OPTIONAL (live finding 2026-07-24, run #123: a
            // quicktest map carried a visible river with an EMPTY
            // riverFlowMap - 1.6 populates it per river variant and does not
            // scribe riverGraph at all - and the old hard bail silently
            // no-opped the whole feature; the user then read the see-below
            // river through open air as "still duplicating"). Carving, drying,
            // and banks need only TERRAIN data; flow merely animates the sky
            // water and refines waterfall direction, so it degrades gracefully:
            // no flow -> unanimated sky water (matching the ground's own
            // render on such maps) and every air-bordering lip treated as a
            // downstream fall.
            WaterInfo groundWater = ground.waterInfo;
            bool hasFlowData = groundWater?.riverFlowMap != null && groundWater.riverFlowMap.Count > 0;
            bool hasGraph = groundWater != null && !groundWater.riverGraph.NullOrEmpty();
            LastSummary = "ran, bailed: no river cells under sky mass (river may not tunnel here)";

            TerrainGrid groundTerrain = ground.terrainGrid;
            TerrainGrid skyTerrain = map.terrainGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;

            // HYDROLOGY SPEC (user directive 2026-07-24): classify every
            // contiguous under-mountain river stretch by its boundary
            // crossings. Water flowing INTO the mass anywhere -> the stretch
            // is a vanilla tunnel, untouched ("flows through the rock as
            // normal"). A stretch whose crossings are ALL outflow - the river
            // ORIGINATES under the mountain and only flows away - LIFTS: the
            // mountain closes to solid rock at ground level and the river
            // head lives on the sky level instead, pouring off the mass edge
            // as a real waterfall into the still-wet surface river below.
            bool IsSkyMass(IntVec3 mc)
            {
                TerrainDef t = skyTerrain.TerrainAt(mc);
                return t != null && t != air && t != ABDefOf.AB_RoofSurface && !t.IsWater;
            }
            // ANY water terrain, not just IsRiver (round-9 finding: wide 1.6
            // rivers are composites - a moving-water core fringed with still
            // water pools; keying on IsRiver lifted the core and left the
            // fringe wet = "two rivers"). Enclosed mountain lakes stay vanilla
            // through the zero-crossings rule.
            bool IsMassRiver(IntVec3 mc)
            {
                TerrainDef b = groundTerrain.BaseTerrainAt(mc);
                if (b == null || !b.IsWater)
                {
                    return false;
                }
                // TUNNEL: sky mass directly above (narrow roofed river), OR
                // CANYON: open cut with the massif flanking both sides (wide
                // rivers - vanilla strips their roof; round-8 finding).
                return IsSkyMass(mc) || FlankedByMass(map, mc, IsSkyMass);
            }
            List<List<IntVec3>> stretches = CollectStretches(ground, IsMassRiver);
            if (stretches.Count == 0)
            {
                return;
            }
            int lifted = 0;
            int keptVanilla = 0;
            List<IntVec3> carved = new List<IntVec3>();
            for (int s = 0; s < stretches.Count; s++)
            {
                List<IntVec3> stretch = stretches[s];
                if (!ShouldLift(ground, groundWater, hasFlowData, stretch, IsMassRiver))
                {
                    keptVanilla++;
                    continue;
                }
                lifted++;
                for (int i = 0; i < stretch.Count; i++)
                {
                    IntVec3 c = stretch[i];
                    TerrainDef below = groundTerrain.BaseTerrainAt(c);
                    // Carve the sky channel: drop the rock wall, the roof,
                    // and the fog, then lay the same water the surface had.
                    c.GetEdifice(map)?.Destroy();
                    map.roofGrid.SetRoof(c, null);
                    map.fogGrid.Unfog(c);
                    skyTerrain.SetTerrain(c, below);
                    carved.Add(c);
                    // Close the mountain below: solid rock to its boundary.
                    SealGroundCell(ground, c);
                }
            }
            if (carved.Count == 0)
            {
                LastSummary = "ran: " + stretches.Count + " tunnel stretch(es), all kept vanilla"
                    + " (water flows into the rock)";
                return;
            }

            // Cut the channel like a vanilla river, not a slit: a walkable
            // bank of the rock's rough floor on every mass-rock neighbor
            // (ore veins stay standing - an exposed seam by the stream), and
            // an unfogged rim ring so no gray fog skirt creeps over the banks
            // (live report 2026-07-24: "gray fog of war shadow on its banks").
            List<IntVec3> banks = new List<IntVec3>();
            CarveBanks(map, carved, banks);
            UnfogSolidNeighbors(map, carved);
            UnfogSolidNeighbors(map, banks);

            // Flow data: clone the graph and copy the per-cell flow list when
            // the ground actually has them.
            WaterInfo skyWater = map.waterInfo;
            if (hasGraph)
            {
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
            }
            if (hasFlowData)
            {
                skyWater.riverFlowMap = new List<float>(groundWater.riverFlowMap);
            }

            // Ledges.
            List<IntVec3> basesPlaced = new List<IntVec3>();
            for (int i = 0; i < carved.Count; i++)
            {
                IntVec3 c = carved[i];
                Vector3 flow = hasFlowData
                    ? groundWater.GetWaterMovement(c.ToVector3Shifted())
                    : Vector3.zero;
                bool hasFlow = flow.sqrMagnitude > 0.0001f;
                Vector3 flowDir = hasFlow ? flow.normalized
                    : hasGraph ? FallbackFlowDir(skyWater) : Vector3.zero;
                bool anyDirection = flowDir.sqrMagnitude > 0.0001f;
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 dir = GenAdj.CardinalDirections[d];
                    IntVec3 n = c + dir;
                    if (!n.InBounds(map) || skyTerrain.TerrainAt(n) != air)
                    {
                        continue;
                    }
                    if (!anyDirection)
                    {
                        // No flow information at all: every ledge pours.
                        SpawnLip(map, c, Rot4.FromIntVec3(dir), inflow: false);
                        TrySpawnBase(ground, n, basesPlaced);
                        break;
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
            LastSummary = "lifted " + lifted + " mountain-source stretch(es): " + carved.Count
                + " water cells + " + banks.Count + " banks on the sky, ground sealed solid below; kept "
                + keptVanilla + " through-tunnel(s) vanilla; "
                + map.listerThings.ThingsOfDef(ABDefOf.AB_Waterfall).Count + " waterfall lips, "
                + basesPlaced.Count + " bases (sky map " + map.uniqueID
                + (hasFlowData ? ", full flow data" : ", no ground flow data") + ")";
            ABLog.Dev("Sky rivers: " + LastSummary);
        }

        /// <summary>Contiguous (4-way) groups of under-mass river cells.</summary>
        internal static List<List<IntVec3>> CollectStretches(Map ground, Func<IntVec3, bool> isMassRiver)
        {
            List<List<IntVec3>> stretches = new List<List<IntVec3>>();
            HashSet<IntVec3> visited = new HashSet<IntVec3>();
            Queue<IntVec3> open = new Queue<IntVec3>();
            foreach (IntVec3 seed in ground.AllCells)
            {
                if (visited.Contains(seed) || !isMassRiver(seed))
                {
                    continue;
                }
                List<IntVec3> stretch = new List<IntVec3>();
                open.Clear();
                open.Enqueue(seed);
                visited.Add(seed);
                while (open.Count > 0)
                {
                    IntVec3 c = open.Dequeue();
                    stretch.Add(c);
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = c + GenAdj.CardinalDirections[d];
                        if (n.InBounds(ground) && !visited.Contains(n) && isMassRiver(n))
                        {
                            visited.Add(n);
                            open.Enqueue(n);
                        }
                    }
                }
                stretches.Add(stretch);
            }
            return stretches;
        }

        /// <summary>The hydrology verdict for one stretch. Crossings are
        /// stretch cells cardinally adjacent to OPEN river (river outside the
        /// mass); each is classified by the flow vector dotted with the
        /// outward direction. Any inflow -> vanilla tunnel. All outflow ->
        /// lift (mountain-source river). No flow data: a single crossing is a
        /// terminal stretch, assumed source (lift); multiple crossings assume
        /// a through-river (vanilla, the safe default).</summary>
        internal static bool ShouldLift(Map ground, WaterInfo water, bool hasFlowData,
            List<IntVec3> stretch, Func<IntVec3, bool> isMassRiver)
        {
            TerrainGrid gt = ground.terrainGrid;
            int crossings = 0;
            int inflow = 0;
            int outflow = 0;
            for (int i = 0; i < stretch.Count; i++)
            {
                IntVec3 c = stretch[i];
                for (int d = 0; d < 4; d++)
                {
                    IntVec3 dir = GenAdj.CardinalDirections[d];
                    IntVec3 n = c + dir;
                    if (!n.InBounds(ground) || isMassRiver(n))
                    {
                        continue;
                    }
                    TerrainDef nt = gt.BaseTerrainAt(n);
                    if (nt == null || !nt.IsWater)
                    {
                        continue;
                    }
                    crossings++;
                    if (!hasFlowData || water == null)
                    {
                        continue;
                    }
                    Vector3 flow = water.GetWaterMovement(c.ToVector3Shifted());
                    if (flow.sqrMagnitude < 0.0001f)
                    {
                        continue;
                    }
                    float dot = Vector3.Dot(flow.normalized, dir.ToVector3());
                    if (dot > 0.2f)
                    {
                        outflow++;
                    }
                    else if (dot < -0.2f)
                    {
                        inflow++;
                    }
                }
            }
            if (crossings == 0)
            {
                return false; // enclosed water with no open connection: leave it
            }
            if (inflow > 0)
            {
                return false; // water flows INTO the rock: vanilla tunnel
            }
            if (outflow > 0)
            {
                return true; // every known crossing flows away: mountain source
            }
            return crossings == 1; // no flow data: terminal stretch = source
        }

        /// <summary>Closes the mountain at ground level: the river cell
        /// becomes solid rock (rough floor + rock wall + thick roof), matching
        /// the flanking stone. Bridged or built-over cells are left alone.</summary>
        private static void SealGroundCell(Map ground, IntVec3 c)
        {
            TerrainGrid gt = ground.terrainGrid;
            TerrainDef baseT = gt.BaseTerrainAt(c);
            if (baseT == null || !baseT.IsWater || gt.TerrainAt(c) != baseT)
            {
                return;
            }
            ThingDef rock = NeighborRockDef(ground, c);
            gt.SetTerrain(c, rock?.building?.naturalTerrain ?? TerrainDefOf.Gravel);
            if (rock != null && c.GetEdifice(ground) == null)
            {
                GenSpawn.Spawn(rock, c, ground);
            }
            ground.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);
        }

        /// <summary>The rock species flanking the cell; falls back to the
        /// tile's dominant rock, then granite.</summary>
        private static ThingDef NeighborRockDef(Map ground, IntVec3 c)
        {
            for (int d = 0; d < 8; d++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[d];
                if (!n.InBounds(ground))
                {
                    continue;
                }
                ThingDef rock = n.GetEdifice(ground)?.def;
                if (rock?.building != null && rock.building.isNaturalRock)
                {
                    return rock;
                }
            }
            foreach (ThingDef worldRock in Find.World.NaturalRockTypesIn(ground.Tile))
            {
                if (worldRock?.building != null && worldRock.building.isNaturalRock)
                {
                    return worldRock;
                }
            }
            return ThingDefOf.Granite;
        }

        /// <summary>Roof-based qualification for AUTO sky generation, runnable
        /// BEFORE any sky level exists: the mass footprint is the thick-rock
        /// roof, and the same classifier decides whether any stretch lifts.</summary>
        internal static bool GroundQualifiesForAutoSky(Map ground)
        {
            if (ground == null || ground.Disposed)
            {
                return false;
            }
            WaterInfo water = ground.waterInfo;
            bool hasFlowData = water?.riverFlowMap != null && water.riverFlowMap.Count > 0;
            TerrainGrid gt = ground.terrainGrid;
            RoofGrid roofs = ground.roofGrid;
            bool IsThickRoofMass(IntVec3 c)
            {
                RoofDef roof = roofs.RoofAt(c);
                return roof != null && roof.isThickRoof;
            }
            bool IsMassRiver(IntVec3 c)
            {
                TerrainDef b = gt.BaseTerrainAt(c);
                if (b == null || !b.IsWater)
                {
                    return false;
                }
                // Tunnel (thick roof over the water) or canyon (massif
                // flanking both sides) - same union the genstep uses.
                return IsThickRoofMass(c) || FlankedByMass(ground, c, IsThickRoofMass);
            }
            List<List<IntVec3>> stretches = CollectStretches(ground, IsMassRiver);
            for (int i = 0; i < stretches.Count; i++)
            {
                if (ShouldLift(ground, water, hasFlowData, stretches[i], IsMassRiver))
                {
                    return true;
                }
            }
            return false;
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
            if (!c.InBounds(ground) || !ground.terrainGrid.BaseTerrainAt(c).IsWater)
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
