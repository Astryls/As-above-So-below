using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - A STRUCTURE IS NOT A CELL.
    ///
    /// §53a said a cell-local invariant cannot express a level-local mistake. This file is
    /// that lesson one size up: a generated STRUCTURE is an indivisible rect, and no
    /// per-cell guard can protect one. ABAirSpawnGuard can relocate a wreck because a wreck
    /// is a thing; it cannot relocate a vault, because moving a vault one wall at a time
    /// produces a demolished vault. The only correct unit of relocation is the whole rect.
    ///
    /// THE BUG THIS EXISTS FOR. Vanilla and modded generators express "where does my
    /// structure go" in one of three ways:
    ///
    ///   1. a scatterer with a declared margin  - already fixed, band-locally, by
    ///      Patch_GenGrid_ABBandCloseToEdge and Patch_GenStep_Scatterer_ABBandSeam;
    ///   2. the player start spot               - already fixed by
    ///      Patch_GenStep_FindPlayerStartSpot_ABSurfaceBand;
    ///   3. "the middle of the map"             - fixed by Patch_Map_ABSurfaceCenter below,
    ///      and by nothing before it.
    ///
    /// Category 3 asks exactly one question and takes the answer literally, so it slips past
    /// every net we had. The worked example is a scenario that lays a vault down with
    /// <c>CellRect.CenteredOn(map.Center, layout.Sizes)</c> and no validator of any kind.
    ///
    /// ⚠ AND THE ARITHMETIC IS A TRAP, because the naive test passes. Map centre is
    /// <c>Size.z / 2</c> = <c>bandCount * Slot / 2</c>. Write the plan as U levels up and L
    /// down, so bandCount = U + L + 1 and surfaceBand = L:
    ///
    ///   U == L (symmetric)  -> centre = L*Slot + Slot/2  -> the MIDDLE of the surface band.
    ///                          Correct, and correct entirely by accident.
    ///   U + L odd           -> bandCount even -> centre = (bandCount/2)*Slot exactly, which
    ///                          is the FIRST ROW of a band. A structure centred there is
    ///                          sawn precisely in half by the seam below it.
    ///   otherwise           -> centre lands mid-band, in the WRONG band, and the carve
    ///                          replaces that whole band with rock or sky. The structure is
    ///                          not damaged, it is erased.
    ///
    /// So the default symmetric plan hides the bug completely and every asymmetric plan
    /// exposes it. That is why this reads as "works for me" on one colony and "the vault is
    /// cut in half" on the next, with no setting in between that looks relevant.
    ///
    /// TWO LAYERS, in the file's usual order of desperation:
    ///   ANCHOR  - Map.Center means the surface band's centre, during generation. Fixes the
    ///             placement before it happens, for every generator that asks the question.
    ///   RESCUE  - before the carve, any registered structure rect not wholly inside the
    ///             surface band is SLID into it, contents and all. This one works on the
    ///             RESULT rather than on which helper the mod happened to call, so it covers
    ///             generators we have never heard of and never named.
    ///
    /// ⚠ AND A THIRD THING THE RESCUE OWES: THE GRIDS THAT ARE NOT IN THE CELL. See
    /// RefogRelocation. Moving a structure moves its terrain, its roofs, its things and its
    /// pawns, and every one of those is visible in the destination cell - so a slide LOOKS
    /// complete the moment those four are carried. The fog grid is a parallel grid derived
    /// once, by GenStep_Fog, from the walls that stood at that moment, and it is not carried
    /// by anything. §58.
    /// </summary>
    public static class ABStructureFit
    {
        /// <summary>Both spans a rect must reach before it is treated as a structure.
        ///
        /// The overwhelming majority of UsedRects entries are NOT structures:
        /// GenStep_ScatterThings registers <c>thing.OccupiedRect()</c> for every single
        /// chunk and slag it scatters, which on a seven-band map is thousands of 1x1 and 2x1
        /// rects, most of them in bands the carve is about to replace anyway. Rescuing those
        /// would carpet the surface with the debris of all seven levels - strictly worse than
        /// the bug. Eight is comfortably above any scattered thing and comfortably below any
        /// authored room.</summary>
        private const int MinStructureSpan = 8;

        /// <summary>Artificial-edifice cells a rect must contain before it is worth moving.
        ///
        /// The span gate alone would still accept a large EMPTY claim - a reserved area that
        /// a generator registered and then declined to build in. Moving one of those
        /// relocates nothing and overwrites a piece of the surface for it. A real structure
        /// has walls; the ring of a modest 8x8 room is already 28 cells, so eight is a floor
        /// that no genuine building can fail and no empty claim can pass.</summary>
        private const int MinArtificialCells = 8;

        // -------------------------------------------------------------------
        // ANCHOR
        // -------------------------------------------------------------------

        /// <summary>
        /// "The middle of the map" means the middle of the LEVEL, while generating.
        ///
        /// Same ANCHOR move as Patch_CellFinder_ABRandomCell: a cell chosen from the whole
        /// map is remapped into the surface band, at the finder, so the caller never sees a
        /// stacked answer at all.
        ///
        /// ⚠ SCOPED TO GENERATION, DELIBERATELY, and this is the one thing not to relax.
        /// Most runtime readers of Map.Center would actually be IMPROVED by the redirect
        /// (GenAI's fallback cell, CameraJumper targets, letter targets), but SkyOverlay
        /// positions a quad that is scaled to the FULL map size at Map.Center - move the
        /// centre without resizing the quad and the weather overlay slides off half the
        /// stack. There is no gain here worth that, so the patch answers normally the
        /// instant generation is over.
        ///
        /// The gate is <c>mapBeingGenerated</c> rather than ProgramState because a colony
        /// settled mid-game generates while ProgramState is still Playing. Map Preview,
        /// which runs gensteps without ever entering MapGenerator.GenerateMap, therefore
        /// falls through to vanilla - correct, because the preview renders terrain and
        /// terrain gensteps do not consult Map.Center.
        /// </summary>
        [HarmonyPatch(typeof(Map), "get_Center")]
        public static class Patch_Map_ABSurfaceCenter
        {
            private static bool Prefix(Map __instance, ref IntVec3 __result)
            {
                try
                {
                    if (MapGenerator.mapBeingGenerated != __instance)
                    {
                        return true;
                    }
                    if (!ABBandedGeneration.TryPendingSurfaceRect(__instance, out CellRect surface, out _))
                    {
                        return true; // not a banded generation
                    }
                    __result = surface.CenterCell;
                    return false;
                }
                catch
                {
                    return true;
                }
            }
        }

        // -------------------------------------------------------------------
        // RESCUE
        // -------------------------------------------------------------------

        private sealed class Candidate
        {
            public CellRect rect;
            public string sampleDef;
            public int group;
        }

        private struct CellState
        {
            public TerrainDef top;
            public TerrainDef under;
            public RoofDef roof;
        }

        private struct Carried
        {
            public Thing thing;
            public IntVec3 offset;
            public Rot4 rot;
        }

        /// <summary>
        /// Slide every generated structure that is not wholly inside the surface band into
        /// it, before the carve gets a chance to destroy it.
        ///
        /// ⚠ RUNS BEFORE RescueStrandedColonists, AND THE ORDER IS LOAD-BEARING. A scenario
        /// that starts the colony sealed inside a vault places its pawns INSIDE the rect, so
        /// moving the structure first carries them with it and leaves the pawn rescue with
        /// nothing to do. Run the pawn rescue first and it would drag the colonists out to
        /// open ground, and they would then watch their own vault arrive around them a
        /// moment later, empty.
        ///
        /// Reads MapGenerator's own "UsedRects" registry, which is the closest thing the
        /// engine has to a list of "here is a structure I built". Vanilla's resolvers, the
        /// KCSG layout generators used by most Vanilla Expanded content, and any mod
        /// following the same convention all publish into it - so this is generic without
        /// naming a single packageId.
        /// </summary>
        internal static void RescueStraddlingStructures(Map map, ABBandMap bands)
        {
            if (map == null || bands == null || !bands.Banded)
            {
                return;
            }
            if (!MapGenerator.TryGetVar("UsedRects", out List<CellRect> used)
                || used == null || used.Count == 0)
            {
                return;
            }

            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            List<Candidate> candidates = Gather(map, used, surface);
            if (candidates.Count == 0)
            {
                return;
            }
            GroupContiguous(candidates);

            // Per-op sky sync is waste here for the same reason it is during the carve:
            // ABSkyBandGen rebuilds every sky band from final state immediately after.
            ABSkySync.Suspended = true;
            try
            {
                MoveGroups(map, bands, surface, candidates, used);
            }
            finally
            {
                ABSkySync.Suspended = false;
            }
        }

        /// <summary>Which registered rects are structures worth moving, and are misplaced.</summary>
        private static List<Candidate> Gather(Map map, List<CellRect> used, CellRect surface)
        {
            var found = new List<Candidate>();
            for (int i = 0; i < used.Count; i++)
            {
                CellRect r = used[i].ClipInsideMap(map);
                if (r.Width < MinStructureSpan || r.Height < MinStructureSpan)
                {
                    continue;
                }
                // Wholly inside the surface band: vanilla's own placement, nothing to do.
                if (r.minZ >= surface.minZ && r.maxZ <= surface.maxZ)
                {
                    continue;
                }
                if (!HasSubstance(map, r, out string sample))
                {
                    continue;
                }
                found.Add(new Candidate { rect = r, sampleDef = sample, group = found.Count });
            }
            return found;
        }

        /// <summary>Does anything actually stand in this rect? Counts CELLS carrying an
        /// artificial edifice rather than distinct buildings, because a wall is a wall
        /// whether the generator spawned it as fifty 1x1 pieces or one long segment.</summary>
        private static bool HasSubstance(Map map, CellRect r, out string sampleDef)
        {
            sampleDef = null;
            int count = 0;
            foreach (IntVec3 c in r)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                Building e = c.GetEdifice(map);
                if (e == null || e.def == null || e.def.building == null
                    || e.def.building.isNaturalRock)
                {
                    continue;
                }
                if (sampleDef == null)
                {
                    sampleDef = e.def.defName;
                }
                if (++count >= MinArtificialCells)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A structure SET is one structure.
        ///
        /// Generators that tile several layouts into a complex register one rect per module,
        /// laid edge to edge. Giving each module its own delta would shear the complex apart
        /// along the seam - modules landing at different offsets, corridors ending in walls -
        /// which is a worse outcome than the sawing this file exists to prevent. Rects that
        /// touch (expanded by one, so edge-to-edge counts) are therefore merged and move as
        /// a unit. O(n^2) over a handful of survivors; the gates already threw away the
        /// thousands.
        /// </summary>
        private static void GroupContiguous(List<Candidate> cands)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < cands.Count; i++)
                {
                    for (int j = i + 1; j < cands.Count; j++)
                    {
                        if (cands[i].group == cands[j].group)
                        {
                            continue;
                        }
                        if (!cands[i].rect.ExpandedBy(1).Overlaps(cands[j].rect))
                        {
                            continue;
                        }
                        int from = cands[j].group;
                        int to = cands[i].group;
                        for (int k = 0; k < cands.Count; k++)
                        {
                            if (cands[k].group == from)
                            {
                                cands[k].group = to;
                            }
                        }
                        changed = true;
                    }
                }
            }
        }

        private static void MoveGroups(Map map, ABBandMap bands, CellRect surface,
            List<Candidate> cands, List<CellRect> used)
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < cands.Count; i++)
            {
                int g = cands[i].group;
                if (!seen.Add(g))
                {
                    continue;
                }
                CellRect bound = cands[i].rect;
                string sample = cands[i].sampleDef;
                for (int j = i + 1; j < cands.Count; j++)
                {
                    if (cands[j].group != g)
                    {
                        continue;
                    }
                    CellRect o = cands[j].rect;
                    bound = new CellRect(
                        Mathf.Min(bound.minX, o.minX), Mathf.Min(bound.minZ, o.minZ),
                        Mathf.Max(bound.maxX, o.maxX) - Mathf.Min(bound.minX, o.minX) + 1,
                        Mathf.Max(bound.maxZ, o.maxZ) - Mathf.Min(bound.minZ, o.minZ) + 1);
                    sample = sample ?? cands[j].sampleDef;
                }

                if (bound.Height > surface.Height)
                {
                    // ⚠ NOT recoverable, and not worth faking a recovery for. Sparing the
                    // carve inside the rect would leave one room spanning two levels through
                    // the seam - a hole straight through the band model, which is a far worse
                    // failure than a missing structure. Name it loudly enough that whoever
                    // reads the log can act: the def identifies the mod, the numbers identify
                    // the setting that would fix it (a taller level plan, or fewer levels).
                    Log.Warning(ABLog.Tag + " V2: a generated structure is taller than one"
                        + " level and cannot be rescued from the carve. Rect " + bound
                        + " is " + bound.Width + "x" + bound.Height + " but a level is only "
                        + surface.Height + " tall"
                        + (sample != null ? "; it contains " + sample : "")
                        + ". Raise the map size or reduce the number of levels if this"
                        + " structure matters.");
                    continue;
                }

                int newMinZ = Mathf.Clamp(bound.minZ, surface.minZ, surface.maxZ - bound.Height + 1);
                int delta = newMinZ - bound.minZ;
                if (delta == 0)
                {
                    continue;
                }
                if (Slide(map, bands, bound, delta))
                {
                    Retarget(used, bound, delta);
                    ABLog.Dev("Structure rescue: moved " + bound.Width + "x" + bound.Height
                        + " structure at " + bound.CenterCell + " by z" + (delta > 0 ? "+" : "")
                        + delta + " into the surface band"
                        + (sample != null ? " (" + sample + ")" : "") + ".");
                }
            }
        }

        /// <summary>
        /// Move a rect's entire contents - terrain, under-terrain, roof and things - by a z
        /// delta.
        ///
        /// ⚠ SNAPSHOT FIRST, WRITE SECOND. Source and destination overlap whenever the delta
        /// is smaller than the rect (which is the common case, a structure nudged off a
        /// seam), so writing as we walk would feed each row its own already-overwritten
        /// predecessor and smear the structure across the band. Lifting the whole rect into
        /// arrays before a single write makes the overlap irrelevant and removes the need to
        /// reason about iteration direction at all.
        /// </summary>
        private static bool Slide(Map map, ABBandMap bands, CellRect src, int delta)
        {
            CellRect dst = new CellRect(src.minX, src.minZ + delta, src.Width, src.Height);
            if (dst.minZ < 0 || dst.maxZ >= map.Size.z)
            {
                return false;
            }

            int w = src.Width;
            int h = src.Height;
            var state = new CellState[w * h];
            TerrainGrid terrain = map.terrainGrid;
            RoofGrid roofs = map.roofGrid;

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    IntVec3 c = new IntVec3(src.minX + x, 0, src.minZ + z);
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    state[z * w + x] = new CellState
                    {
                        top = terrain.TerrainAt(c),
                        under = terrain.UnderTerrainAt(c),
                        roof = roofs.RoofAt(c)
                    };
                }
            }

            // Lift the contents out before anything is cleared, so neither clear pass can
            // destroy the very things being rescued.
            var carried = new List<Carried>();
            foreach (IntVec3 c in src)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> here = c.GetThingList(map);
                for (int i = here.Count - 1; i >= 0; i--)
                {
                    Thing t = here[i];
                    // Position, not occupancy: a multi-cell building is listed in every cell
                    // it covers and must be carried exactly once.
                    if (t == null || t.Destroyed || !t.Spawned || t.Position != c)
                    {
                        continue;
                    }
                    carried.Add(new Carried
                    {
                        thing = t,
                        offset = t.Position - src.Min,
                        rot = t.Rotation
                    });
                    t.DeSpawn(DestroyMode.Vanish);
                }
            }

            // Anything standing where the structure is going. Pawns are displaced rather
            // than destroyed - the destination is inside the surface band, so this is where
            // a scenario's colonists are most likely to be standing, and a colonist deleted
            // here would vanish with no error exactly like the bug RescueStrandedColonists
            // was written for.
            var displaced = new List<Pawn>();
            foreach (IntVec3 c in dst)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                List<Thing> here = c.GetThingList(map);
                for (int i = here.Count - 1; i >= 0; i--)
                {
                    if (here[i] is Pawn p && p.Spawned)
                    {
                        displaced.Add(p);
                        p.DeSpawn(DestroyMode.Vanish);
                    }
                }
                ABBandedGeneration.ClearCellHard(map, c);
            }

            // Vacated ground: strip the structure's floor so it does not leave a ghost
            // footprint, without leavings - this is generation, nobody deconstructed
            // anything and dropping steel here would be inventing loot.
            foreach (IntVec3 c in src)
            {
                if (!c.InBounds(map) || dst.Contains(c))
                {
                    continue;
                }
                ABBandedGeneration.ClearCellHard(map, c);
                if (terrain.CanRemoveTopLayerAt(c))
                {
                    terrain.RemoveTopLayer(c, doLeavings: false);
                }
                roofs.SetRoof(c, null);
            }

            // Terrain before things: a thing landing on a cell whose terrain has not been
            // written yet can trip the air-spawn guard, which reads the terrain under it.
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    IntVec3 d = new IntVec3(dst.minX + x, 0, dst.minZ + z);
                    if (!d.InBounds(map))
                    {
                        continue;
                    }
                    CellState s = state[z * w + x];
                    if (s.top == null)
                    {
                        continue;
                    }
                    if (s.under != null)
                    {
                        terrain.SetUnderTerrain(d, s.under);
                    }
                    terrain.SetTerrain(d, s.top);
                    roofs.SetRoof(d, s.roof);
                }
            }

            for (int i = 0; i < carried.Count; i++)
            {
                Carried c = carried[i];
                if (c.thing == null || c.thing.Destroyed)
                {
                    continue;
                }
                IntVec3 to = dst.Min + c.offset;
                if (!to.InBounds(map))
                {
                    continue;
                }
                GenSpawn.Spawn(c.thing, to, map, c.rot, WipeMode.Vanish);
            }

            for (int i = 0; i < displaced.Count; i++)
            {
                Pawn p = displaced[i];
                if (p == null || p.Destroyed || p.Spawned)
                {
                    continue;
                }
                if (ABBandSafety.TryFindSurfaceCell(map, bands, p.Position, true,
                        out IntVec3 safe))
                {
                    GenSpawn.Spawn(p, safe, map, Rot4.South, WipeMode.Vanish);
                }
                else
                {
                    GenSpawn.Spawn(p, dst.CenterCell, map, Rot4.South, WipeMode.Vanish);
                }
            }

            // ⚠ THE START SPOT TRAVELS WITH THE VAULT. A scenario that seals the colony
            // inside a structure sets PlayerStartSpot to a cell within it, and
            // FixPlayerStartSpot runs after this. Leave the spot behind and that clamp does
            // exactly its job on a now-meaningless cell, depositing the colonists on open
            // ground while their sealed vault sits elsewhere with the door shut.
            if (MapGenerator.PlayerStartSpotValid)
            {
                IntVec3 spot = MapGenerator.PlayerStartSpot;
                if (src.Contains(spot))
                {
                    MapGenerator.PlayerStartSpot = spot + new IntVec3(0, 0, delta);
                }
            }

            // LAST, and after the start spot has moved, because the repair seeds from it.
            RefogRelocation(map, bands, src, dst);
            return true;
        }

        /// <summary>
        /// Re-derive the fog of war over the cells the slide rewrote.
        ///
        /// ⚠ THE BUG THIS FIXES: A SEALED ANCIENT DANGER ARRIVING ALREADY REVEALED. Slide
        /// carries terrain, under-terrain, roofs, things, pawns and the start spot - six
        /// things, all of which you can SEE in the destination cell, which is exactly why the
        /// seventh went unnoticed for six windows. Fog is not stored in the cell's contents;
        /// it is a parallel grid that vanilla derives ONCE, in GenStep_Fog, by flooding out
        /// from the start spot through whatever walls existed at that instant. A shrine slid
        /// off the seam lands on cells that were open ground a moment ago and are therefore
        /// already unfogged, so it arrives with its wall ring closed, its roof on, its
        /// insects asleep - and its interior and its loot in plain view from turn one.
        /// Nothing about the cell looks wrong, and no error is raised. That is rule 9 (§53)
        /// one grid over: SOME MISPLACEMENTS ARE NOT VISIBLE IN THE CELL.
        ///
        /// THE REPAIR IS A LOCAL RE-RUN OF GenStep_Fog, not a special case for shrines:
        /// refog everything the slide touched, then flood back in from the outside world.
        /// Reachable ground reopens, a sealed interior stays dark, and neither outcome is
        /// hardcoded - both fall out of the geometry, so a ruin with a hole in it reveals
        /// through the hole with no extra code.
        ///
        /// ⚠ THE VACATED FOOTPRINT NEEDS IT TOO, and in the opposite direction. The cells the
        /// structure LEFT keep the structure's interior fog, and they are now bare ground the
        /// player can walk onto - a black rectangle sitting in the open. One rect covering
        /// both ends fixes both, because the flood does not care which direction a cell's
        /// error pointed.
        ///
        /// ⚠ CLAMPED TO THE SURFACE BAND, TWICE (rule 1, and ABFogReveal's lesson 2). This
        /// runs BEFORE the carve, so the bands above and below are still full of vanilla's
        /// terrain and are fogged wholesale; an unclamped flood would happily walk out of the
        /// rect, across the not-yet-cut gutter and into the next level, revealing content the
        /// carve is about to delete and costing a full-map flood to do it. Both the seed ring
        /// and the wall-face pass therefore refuse to look at a cell outside the surface
        /// band. Everything outside it is the carve's business a few milliseconds from now:
        /// basements get Refog'd wholesale, sky bands get unfogged wholesale.
        /// </summary>
        private static void RefogRelocation(Map map, ABBandMap bands, CellRect src, CellRect dst)
        {
            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            // ASSERT, unconditionally (§57d). MoveGroups clamps the destination into the
            // surface band and the taller-than-a-level case is refused before we get here, so
            // this cannot fire today - but it is the one precondition the whole repair rests
            // on, and a future caller passing its own delta would otherwise refog a band the
            // carve has already finished with.
            if (dst.minZ < surface.minZ || dst.maxZ > surface.maxZ)
            {
                Log.Error(ABLog.Tag + " V2: structure rescue moved " + src + " to " + dst
                    + ", which is not inside the surface band " + surface
                    + ". Fog repair skipped.");
                return;
            }

            int minZ = Mathf.Max(surface.minZ, Mathf.Min(src.minZ, dst.minZ));
            int maxZ = Mathf.Min(surface.maxZ, Mathf.Max(src.maxZ, dst.maxZ));
            if (maxZ < minZ)
            {
                return;
            }
            CellRect repair = new CellRect(dst.minX, minZ, dst.Width, maxZ - minZ + 1)
                .ClipInsideMap(map);
            if (repair.Area <= 0)
            {
                return;
            }

            FogGrid fog = map.fogGrid;
            CellIndices indices = map.cellIndices;
            fog.Refog(repair);

            var queued = new HashSet<int>();
            var frontier = new Queue<IntVec3>();

            // "Open" in the fog sense only: FloodFillerFog's PassCheck tests MakeFog and
            // nothing else, so a door, a plant or an unwalkable-but-see-through cell all
            // spread. Matching it exactly is what makes this a re-run rather than a guess.
            bool Open(IntVec3 c)
            {
                Building e = c.GetEdifice(map);
                return e == null || e.def == null || !e.def.MakeFog;
            }

            void Seed(IntVec3 c)
            {
                if (!c.InBounds(map) || !repair.Contains(c) || !fog.IsFogged(c) || !Open(c))
                {
                    return;
                }
                if (queued.Add(indices.CellToIndex(c)))
                {
                    frontier.Enqueue(c);
                }
            }

            // SEED 1 - the boundary. A cell inside the rect that is connected to the known
            // world must cross the edge of the rect somewhere, and fog spreads 4-way, so
            // every such connection is a cardinal pair on the border. The OUTSIDE half must
            // itself be unfogged AND open: an unfogged cell that is a wall is just a revealed
            // rock face, not a way in, and seeding off one would light up sealed interiors
            // through their own outer wall.
            foreach (IntVec3 c in repair.EdgeCells)
            {
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[i];
                    if (repair.Contains(n) || !n.InBounds(map) || !surface.Contains(n))
                    {
                        continue;
                    }
                    if (!fog.IsFogged(n) && Open(n))
                    {
                        Seed(c);
                        break;
                    }
                }
            }

            // SEED 2 - vanilla's own roots, which is what makes the SEALED VAULT SCENARIO
            // come out right. A scenario that starts the colony inside a closed structure has
            // no boundary connection at all by definition, and GenStep_Fog reveals its
            // interior only because it floods from the start spot - which the slide has just
            // carried into the rect. Drop the roots and the mod's own rescue would post the
            // colony into a black box it cannot see out of.
            if (MapGenerator.PlayerStartSpotValid)
            {
                Seed(MapGenerator.PlayerStartSpot);
            }
            List<IntVec3> roots = MapGenerator.rootsToUnfog;
            for (int i = 0; roots != null && i < roots.Count; i++)
            {
                Seed(roots[i]);
            }

            int opened = 0;
            while (frontier.Count > 0)
            {
                IntVec3 c = frontier.Dequeue();
                fog.Unfog(c);
                opened++;
                for (int i = 0; i < 4; i++)
                {
                    Seed(c + GenAdj.CardinalDirections[i]);
                }
            }

            // Wall faces, copying FloodFillerFog's expansion pass: a fogged blocker touching
            // revealed open space is itself revealed, so the player sees the structure's
            // outside wall instead of the black edge of the fog. No cascade is possible even
            // though this mutates as it walks - the neighbour test demands an OPEN cell and
            // this pass only ever unfogs blockers, so a cell it reveals can never authorise
            // the next one.
            int faces = 0;
            foreach (IntVec3 c in repair)
            {
                if (!c.InBounds(map) || !fog.IsFogged(c) || Open(c))
                {
                    continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[i];
                    if (!n.InBounds(map) || !surface.Contains(n) || fog.IsFogged(n) || !Open(n))
                    {
                        continue;
                    }
                    fog.Unfog(c);
                    faces++;
                    break;
                }
            }

            ABLog.Dev("Structure rescue: refogged " + repair + " (" + repair.Area
                + " cells), reopened " + opened + " from outside, revealed " + faces
                + " wall face(s), left " + (repair.Area - opened - faces) + " dark.");
        }

        /// <summary>Move the registry entries too, so anything reading UsedRects after this
        /// (the dev-mode overlay, a late genstep, another mod's postfix) sees where the
        /// structure actually IS rather than where it was built.</summary>
        private static void Retarget(List<CellRect> used, CellRect bound, int delta)
        {
            for (int i = 0; i < used.Count; i++)
            {
                CellRect r = used[i];
                if (r.minZ < bound.minZ || r.maxZ > bound.maxZ
                    || r.minX < bound.minX || r.maxX > bound.maxX)
                {
                    continue;
                }
                used[i] = new CellRect(r.minX, r.minZ + delta, r.Width, r.Height);
            }
        }
    }
}
