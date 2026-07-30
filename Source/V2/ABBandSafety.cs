using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - NOTHING MAY EXIST IN THE VOID.
    ///
    /// The gutter (the impassable open-air seam between two bands) is a structural device,
    /// not a place. It has no region, no room, no temperature zone and no path grid worth
    /// the name, so anything that ends up standing in it is stranded: it can be seen by
    /// nothing, reach nothing, and be reached by nothing. This file is the safety net that
    /// keeps pawns out of it and keeps generated structures from straddling it.
    ///
    /// THE SHAPE OF THE BUG IS ALWAYS THE SAME, and it is the schematic's slicing rule
    /// again: vanilla asks a question about "the map" - its edge, its centre, a random cell
    /// in it - and on a banded map the honest answer to that question is about the STACK,
    /// not about the level the player is standing on. Two different fixes follow from that:
    ///
    ///   ANCHOR  - a cell picked from the whole map (random cell, edge cell) is remapped
    ///             into the surface band. Patched at the finder, so the caller never sees a
    ///             void cell at all.
    ///   FIELD   - a distance measured from the map's extent (CloseToEdge) is re-based onto
    ///             the cell's OWN band. One patch fixes every generator that declares a
    ///             "keep away from the edge" margin, because they all funnel through it.
    ///
    /// And behind both, a last-ditch net: a spawn-time relocation and a slow tick sweep, so
    /// a path we have not thought of still cannot leave a pawn in the void.
    /// </summary>
    public static class ABBandSafety
    {
        /// <summary>Floor on how close a scattered structure may be placed to a band's z
        /// seam, in cells, regardless of what the genstep itself declares.
        ///
        /// Most scatterers declare a minEdgeDist and the CloseToEdge patch below makes that
        /// declaration band-correct - which is the real fix. This is for the ones that
        /// declare nothing: on a vanilla map a sketch that overhangs the map edge is simply
        /// clipped off-map and never seen, but a band seam is in the MIDDLE of the stack, so
        /// the same overhang produces a visibly sawn-off building at the top of the level.
        /// Ten matches vanilla's own NoBuildEdgeWidth, which is the number vanilla uses for
        /// "structures do not belong this close to the boundary".</summary>
        /// <remarks>DERIVED, not constant. A flat 10 was 16% of a 126-tall band gone at both
        /// ends versus 10% at 190, and it showed: two different scatterers reported "could
        /// not find cell to generate at" across three test maps once the band-local
        /// CloseToEdge fix had already tightened the legal area on the other axis. Scaling
        /// with the band keeps the intent (structures do not sit astride a seam) without
        /// squeezing small levels until generation starves.</remarks>
        internal static int SeamMarginFor(int bandHeight)
        {
            return Mathf.Clamp(bandHeight / 24, 4, 12);
        }

        /// <summary>How often the sweep looks for pawns in the void. Two seconds: this is a
        /// net for a bug that should never fire, not a gameplay system, and it walks every
        /// spawned pawn on every map when it runs.</summary>
        private const int SweepInterval = 120;

        // ---- geometry ------------------------------------------------------

        /// <summary>The void: outside the map, or in a seam between two bands.</summary>
        public static bool InVoid(Map map, ABBandMap bands, IntVec3 c)
        {
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            return !c.InBounds(map) || bands.InGutter(c);
        }

        /// <summary>
        /// The band rect a cell belongs to, live OR mid-generation.
        ///
        /// The generation case is the one that matters here and it is easy to miss: the band
        /// component is not Setup() until the GenerateMap postfix, so during every genstep
        /// ABBandMap.Banded is still FALSE while the map is already the full stacked height.
        /// Anything that only asks ABBands is therefore silently inert for the entire window
        /// in which scatterers choose their spots - which is exactly the window this file
        /// exists to fix. The pending layout is the authority in that window.
        /// </summary>
        public static bool TryBandRectOf(Map map, IntVec3 c, out CellRect band)
        {
            band = default(CellRect);
            if (map == null)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands != null && bands.Banded)
            {
                band = bands.RectOfBand(bands.BandOf(c));
                return true;
            }
            if (ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot)
                && slot > 0 && surface.Height > 0)
            {
                int index = Mathf.Max(0, c.z / slot);
                band = new CellRect(surface.minX, index * slot, surface.Width, surface.Height);
                return true;
            }
            return false;
        }

        /// <summary>Same column, surface band, in-band offset preserved.
        ///
        /// The modulo is by SLOT, never by band height - taking it by height skews the
        /// offset by a growing multiple of the gutter, which is the same arithmetic slip the
        /// start-spot fix had to be corrected for.</summary>
        public static IntVec3 ToSurface(Map map, ABBandMap bands, IntVec3 c)
        {
            int slot = bands.Slot;
            int within = ((c.z % slot) + slot) % slot;
            within = Mathf.Clamp(within, 0, bands.bandHeight - 1);
            return new IntVec3(
                Mathf.Clamp(c.x, 0, map.Size.x - 1), 0,
                bands.surfaceBand * slot + within);
        }

        /// <summary>
        /// A cell on the SURFACE band near the column that was asked for.
        ///
        /// Always the surface, deliberately: a spawn that landed in the void has already
        /// proven that whatever chose it does not understand bands, so the nearest band is
        /// not evidence of intent - it is an accident of which seam the cell fell into.
        /// The surface is the one level that is always generated, always open, and always
        /// reachable, so it is the only answer that cannot make things worse.
        /// </summary>
        public static bool TryFindSurfaceCell(Map map, ABBandMap bands, IntVec3 hint,
            bool needStandable, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            CellRect surface = bands.RectOfBand(bands.surfaceBand);
            IntVec3 seed = ToSurface(map, bands, hint);
            Predicate<IntVec3> ok = delegate(IntVec3 c)
            {
                if (!c.InBounds(map) || !surface.Contains(c))
                {
                    return false;
                }
                return !needStandable || c.Standable(map);
            };

            if (ok(seed))
            {
                result = seed;
                return true;
            }
            if (CellFinder.TryFindRandomCellNear(seed, map, 24, ok, out result))
            {
                return true;
            }
            if (CellFinder.TryFindRandomCellNear(seed, map, 64, ok, out result))
            {
                return true;
            }
            // Deterministic sweep so this never silently fails on a crowded level.
            foreach (IntVec3 c in surface)
            {
                if (ok(c))
                {
                    result = c;
                    return true;
                }
            }
            // Last resort: somewhere real beats the void even if something is standing there.
            result = surface.CenterCell;
            return result.InBounds(map);
        }

        /// <summary>Perimeter cell of the surface band on one side. The dir overloads of the
        /// vanilla edge finders mean "the north edge of the world", and on a banded map that
        /// is the surface band's north row, not the top of the sky.</summary>
        public static bool TryRandomSurfaceEdgeCell(Map map, Rot4 dir,
            Predicate<IntVec3> validator, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            CellRect r = bands.RectOfBand(bands.surfaceBand);
            for (int attempt = 0; attempt < 200; attempt++)
            {
                IntVec3 c = EdgeOf(r, dir);
                if (c.InBounds(map) && (validator == null || validator(c)))
                {
                    result = c;
                    return true;
                }
            }
            foreach (IntVec3 c in EdgeCells(r, dir))
            {
                if (c.InBounds(map) && (validator == null || validator(c)))
                {
                    result = c;
                    return true;
                }
            }
            return false;
        }

        private static IntVec3 EdgeOf(CellRect r, Rot4 dir)
        {
            if (dir == Rot4.North)
            {
                return new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, r.maxZ);
            }
            if (dir == Rot4.South)
            {
                return new IntVec3(Rand.RangeInclusive(r.minX, r.maxX), 0, r.minZ);
            }
            if (dir == Rot4.East)
            {
                return new IntVec3(r.maxX, 0, Rand.RangeInclusive(r.minZ, r.maxZ));
            }
            return new IntVec3(r.minX, 0, Rand.RangeInclusive(r.minZ, r.maxZ));
        }

        private static IEnumerable<IntVec3> EdgeCells(CellRect r, Rot4 dir)
        {
            if (dir == Rot4.North || dir == Rot4.South)
            {
                int z = dir == Rot4.North ? r.maxZ : r.minZ;
                for (int x = r.minX; x <= r.maxX; x++)
                {
                    yield return new IntVec3(x, 0, z);
                }
                yield break;
            }
            int px = dir == Rot4.East ? r.maxX : r.minX;
            for (int z2 = r.minZ; z2 <= r.maxZ; z2++)
            {
                yield return new IntVec3(px, 0, z2);
            }
        }

        // ---- the sweep -----------------------------------------------------

        /// <summary>
        /// Anything standing in a seam is moved to the surface.
        ///
        /// This is the net UNDER the net: the spawn patch below stops the known routes, but
        /// a pawn can also be put into a cell by teleports, psycasts, another mod's spawner,
        /// or a save written before this fix existed. A pawn in the gutter is not merely in
        /// an odd spot - it is in a cell with no region, so it cannot path, cannot be
        /// reached, and cannot be rescued by any ordinary game mechanism.
        ///
        /// Position + Notify_Teleported is the vanilla teleport idiom: the Position setter
        /// does the thingGrid / map-mesh bookkeeping when the thing is spawned, and the
        /// notify drops the now-impossible job and resets the render tween so the pawn does
        /// not visibly slide across the level.
        /// </summary>
        [ABGameTick(80)]
        public static void SweepVoid()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing
                    || Scribe.mode != LoadSaveMode.Inactive
                    || Find.TickManager == null
                    || Find.TickManager.TicksGame % SweepInterval != 0)
                {
                    return;
                }
                List<Map> maps = Find.Maps;
                if (maps == null)
                {
                    return;
                }
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    ABBandMap bands = ABBands.CompOf(map);
                    if (bands == null || !bands.Banded)
                    {
                        continue;
                    }
                    IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                    for (int j = pawns.Count - 1; j >= 0; j--)
                    {
                        Pawn p = pawns[j];
                        if (p == null || !p.Spawned || p.Destroyed)
                        {
                            continue;
                        }
                        if (!bands.InGutter(p.Position))
                        {
                            continue;
                        }
                        if (TryFindSurfaceCell(map, bands, p.Position, true, out IntVec3 safe))
                        {
                            ABLog.Dev("Void sweep: " + p.LabelShortCap + " was in the seam at "
                                + p.Position + "; moved to " + safe + ".");
                            p.Position = safe;
                            p.Notify_Teleported();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: void sweep threw: " + e, 419772601);
            }
        }
    }

    /// <summary>
    /// The choke point: no pawn is ever SPAWNED into a seam.
    ///
    /// Every overload of GenSpawn.Spawn funnels into this one, so a single prefix covers
    /// raids, traders, wildlife, manhunter packs, quest arrivals, dev-mode spawns and any
    /// mod that spawns through the normal API. Correcting `loc` before vanilla reads it
    /// means nothing downstream ever observes the bad cell - no half-registered pawn, no
    /// region lookup on a cell that has none.
    ///
    /// Scoped to pawns and to the gutter ONLY. A pawn spawning on a non-surface band is
    /// legitimate (stairs, cavern wildlife, our own generation) and must not be dragged to
    /// the surface; the void is the only thing being corrected.
    ///
    /// Deliberately inert during map generation, where ABBandMap.Banded is still false: the
    /// generation window has its own, better-informed net in RescueStrandedColonists, which
    /// runs after vanilla has finished placing things and knows what the carve is about to
    /// delete.
    /// </summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new Type[]
    {
        typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode),
        typeof(bool), typeof(bool)
    })]
    public static class Patch_GenSpawn_ABNoVoidSpawn
    {
        private static void Prefix(Thing newThing, ref IntVec3 loc, Map map, bool respawningAfterLoad)
        {
            try
            {
                if (respawningAfterLoad || map == null || !(newThing is Pawn))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABBandSafety.InVoid(map, bands, loc))
                {
                    return;
                }
                if (ABBandSafety.TryFindSurfaceCell(map, bands, loc, true, out IntVec3 safe))
                {
                    ABLog.Dev("Void spawn intercepted: " + newThing.LabelShortCap + " at " + loc
                        + " -> " + safe + ".");
                    loc = safe;
                }
            }
            catch
            {
                // Never let the safety net be the thing that breaks a spawn.
            }
        }
    }

    /// <summary>
    /// "How far is this cell from the edge of the map" - answered per BAND.
    ///
    /// This is the single highest-leverage patch in the file. CloseToEdge is the one helper
    /// every generator uses to express "do not put me near the boundary", and on a banded
    /// map it measures against the STACK: z==0 is the deepest basement's floor and
    /// z==Size.z-1 is the top of the sky, so the surface band - sitting in the middle -
    /// reports itself as comfortably inland everywhere, including the two rows that butt
    /// directly onto a seam. That is why generated structures sit astride the boundary and
    /// come out sawn in half after the carve.
    ///
    /// Re-basing onto the cell's own band rect makes every caller correct at once, using
    /// each genstep's OWN declared margin rather than a number invented here:
    /// GenStep_Scatterer's minEdgeDist / extraNoBuildEdgeDist / minEdgeDistPct,
    /// DropCellFinder's distToEdge, ruin edifice trimming, and the zone-edge rule all
    /// inherit the fix.
    ///
    /// A prefix rather than a postfix because the vanilla body is the thing being replaced,
    /// and cheap enough to sit in front of it: eleven call sites in the whole game, all of
    /// them generation or cell-finding, none of them per-frame.
    /// </summary>
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.CloseToEdge))]
    public static class Patch_GenGrid_ABBandCloseToEdge
    {
        private static bool Prefix(IntVec3 c, Map map, int edgeDist, ref bool __result)
        {
            try
            {
                if (!ABBandSafety.TryBandRectOf(map, c, out CellRect band))
                {
                    return true; // ordinary map
                }
                __result = c.x < band.minX + edgeDist
                    || c.x > band.maxX - edgeDist
                    || c.z < band.minZ + edgeDist
                    || c.z > band.maxZ - edgeDist;
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// A floor under the seam margin, for gensteps that declare none.
    ///
    /// Every stock scatterer starts its override with `if (!base.CanScatterAt(...)) return
    /// false;`, so postfixing the base reaches the whole family without naming any of them -
    /// the same "find the one virtual everything funnels through" move that made one patch
    /// cover nine coastal mutators.
    ///
    /// Only the Z edges are constrained. Bands stack along +z, so the x edges of a band ARE
    /// the map's x edges and vanilla's own handling of them is already right; narrowing x
    /// too would thin generation along the sides for no reason.
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Scatterer), "CanScatterAt")]
    public static class Patch_GenStep_Scatterer_ABBandSeam
    {
        private static void Postfix(IntVec3 loc, Map map, ref bool __result)
        {
            try
            {
                if (!__result || !ABBandSafety.TryBandRectOf(map, loc, out CellRect band))
                {
                    return;
                }
                int m = ABBandSafety.SeamMarginFor(band.Height);
                if (band.Height <= 2 * m)
                {
                    return; // a band too short to afford the margin keeps vanilla behaviour
                }
                if (loc.z < band.minZ + m || loc.z > band.maxZ - m)
                {
                    __result = false;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>The dir overload of the edge finder. Its sibling (no dir) is already
    /// redirected in ABBandEnv; this one is how pawn groups that insist on entering from a
    /// particular side arrive, and left alone it hands back the top of the sky band or the
    /// basement floor for North/South.</summary>
    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomEdgeCell),
        new Type[] { typeof(Rot4), typeof(Map) })]
    public static class Patch_CellFinder_ABRandomEdgeCellDir
    {
        private static bool Prefix(Rot4 dir, Map map, ref IntVec3 __result)
        {
            if (!ABBandEdges.NeedsRedirect(map))
            {
                return true;
            }
            if (ABBandSafety.TryRandomSurfaceEdgeCell(map, dir, null, out IntVec3 c))
            {
                __result = c;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.TryFindRandomEdgeCellWith),
        new Type[] { typeof(Predicate<IntVec3>), typeof(Map), typeof(Rot4), typeof(float), typeof(IntVec3) },
        new ArgumentType[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
    public static class Patch_CellFinder_ABTryFindRandomEdgeCellWithDir
    {
        private static bool Prefix(Predicate<IntVec3> validator, Map map, Rot4 dir,
            ref IntVec3 result, ref bool __result)
        {
            if (!ABBandEdges.NeedsRedirect(map))
            {
                return true;
            }
            __result = ABBandSafety.TryRandomSurfaceEdgeCell(map, dir, validator, out result);
            return false;
        }
    }

    /// <summary>"A random cell on the map" means a random cell on the LEVEL. Unpatched it
    /// lands in a seam roughly (gutter rows / slot) of the time and in an unrelated band the
    /// rest, which is how strays end up in the void with no edge finder involved.</summary>
    [HarmonyPatch(typeof(CellFinder), nameof(CellFinder.RandomCell))]
    public static class Patch_CellFinder_ABRandomCell
    {
        private static bool Prefix(Map map, ref IntVec3 __result)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return true;
            }
            CellRect s = bands.RectOfBand(bands.surfaceBand);
            __result = new IntVec3(
                Rand.RangeInclusive(s.minX, s.maxX), 0,
                Rand.RangeInclusive(s.minZ, s.maxZ));
            return false;
        }
    }
}
