using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Geological Landforms (m00nl1ght.GeologicalLandforms).
    ///
    /// GL is the deepest GENERATION-time neighbour we have: 44 landforms behind a single
    /// worker, a node-graph terrain pipeline, and around 30 Harmony patch classes reaching
    /// into GenStep_RocksFromGrid, GenStep_Scatterer, CellFinder, TerrainPatchMaker,
    /// MapGenerator and RoofCollapseUtility. Most of that composes with us without
    /// complaint. What does not is fixed here (§56).
    ///
    /// ⚠ WHAT IS DELIBERATELY *NOT* PATCHED HERE, because the audit that proposed it was
    /// wrong on the facts:
    ///
    ///   - "GL's AdjustMineables clobbers our per-level ore multiplier." It does not. Their
    ///     transpiler overwrites <c>GenStep_Scatterer.countPer10kCellsRange</c>, which drives
    ///     VANILLA's surface mineable scatter. §7's ×(1 + 0.45·(depth−1)) is applied to
    ///     <c>basementOreDensity</c> and consumed by our OWN <c>ABOreGen.ScatterOres</c> in
    ///     the basement carve. Two unrelated systems that both mean "how much ore".
    ///
    ///   - "GL outranks our map-gen prefix at HarmonyPriority(800)." There is no race. GL
    ///     prefixes <c>GenerateContentsIntoMap</c>; our size change is a prefix on
    ///     <c>GenerateMap</c>, which has already run, and our carve is a POSTFIX on
    ///     GenerateContentsIntoMap, which runs after theirs. Ordering is already correct.
    ///
    ///   - GL's WALKABLE EDGE CACHE. <c>GetOrBuildEdgeCacheForMap</c> opens with
    ///     <c>if (map.Size.x != map.Size.z) return Array.Empty</c>, and a banded map is never
    ///     square, so the whole exit-spot optimisation disables itself on our maps and
    ///     vanilla's finder runs. That is the correct outcome and it costs us nothing to
    ///     leave alone: their <c>EdgeCellIdxToVec</c> decodes all four sides off
    ///     <c>Size.x</c>, so on a stacked map it would decode the "north" side into a row
    ///     mid-column. Do not be tempted to "restore" this optimisation for banded maps.
    /// </summary>
    public static class GeologicalLandformsCompat
    {
        public const string PackageId = "m00nl1ght.GeologicalLandforms";

        private const string RocksPatchType = "GeologicalLandforms.Patches.Patch_RimWorld_GenStep_RocksFromGrid";
        private const string LandformType = "GeologicalLandforms.GraphEditor.Landform";
        private const string MutatorWorkerType = "GeologicalLandforms.TileMutatorWorker_Landform";
        private const string BiomeGridType = "GeologicalLandforms.BiomeGrid";
        private const string CellFinderPatchType = "GeologicalLandforms.Patches.Patch_RimWorld_CellFinder";

        private static bool resolved;

        private static MethodBase setRoofsFromLandform;
        private static MethodBase landformPrepare;
        private static MethodBase mutatorPostElevationFertility;
        private static MethodBase mutatorPostTerrain;
        private static MethodBase openGroundFractionUpdate;
        private static MethodBase unroofedCacheBuild;

        private static Type biomeGrid;
        private static MethodInfo biomeGridBiomeAt;
        private static MethodInfo biomeGridSetBiome;
        private static MethodInfo openGroundFractionFor;
        private static PropertyInfo openGroundFractionProp;

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            try
            {
                Type rocks = AccessTools.TypeByName(RocksPatchType);
                setRoofsFromLandform = rocks == null
                    ? null
                    : AccessTools.DeclaredMethod(rocks, "SetRoofsFromLandform", new[] { typeof(Map) });

                // Prepare has two overloads. We want the THREE-argument one, because it is
                // the one that actually writes GeneratingMapSize - Prepare(Map) is a thin
                // wrapper that resolves the tile info and seed and then calls it. Matching
                // on argument COUNT keeps the foreign IWorldTileInfo out of our code
                // entirely (§14: no foreign type in any signature we author).
                Type landform = AccessTools.TypeByName(LandformType);
                if (landform != null)
                {
                    foreach (MethodInfo m in landform.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "Prepare")
                        {
                            continue;
                        }
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 3 && ps[1].ParameterType == typeof(IntVec2) && ps[2].ParameterType == typeof(int))
                        {
                            landformPrepare = m;
                            break;
                        }
                    }
                }

                Type worker = AccessTools.TypeByName(MutatorWorkerType);
                if (worker != null)
                {
                    mutatorPostElevationFertility =
                        AccessTools.DeclaredMethod(worker, "GeneratePostElevationFertility", new[] { typeof(Map) });
                    mutatorPostTerrain =
                        AccessTools.DeclaredMethod(worker, "GeneratePostTerrain", new[] { typeof(Map) });
                }

                biomeGrid = AccessTools.TypeByName(BiomeGridType);
                if (biomeGrid != null)
                {
                    // Both of these take and return VANILLA types only, which is why the
                    // transplant uses them instead of the Entry-based SetEntry/EntryAt pair.
                    biomeGridBiomeAt = AccessTools.DeclaredMethod(biomeGrid, "BiomeAt", new[] { typeof(IntVec3) });
                    biomeGridSetBiome = AccessTools.DeclaredMethod(biomeGrid, "SetBiome", new[] { typeof(IntVec3), typeof(BiomeDef) });
                    openGroundFractionUpdate = AccessTools.DeclaredMethod(biomeGrid, "UpdateOpenGroundFraction");
                    openGroundFractionFor = AccessTools.DeclaredMethod(biomeGrid, "GetOpenGroundFractionFor",
                        new[] { typeof(IntVec3), typeof(bool), typeof(bool) });
                    openGroundFractionProp = AccessTools.DeclaredProperty(biomeGrid, "OpenGroundFraction");
                }

                Type cellFinder = AccessTools.TypeByName(CellFinderPatchType);
                unroofedCacheBuild = cellFinder == null
                    ? null
                    : AccessTools.DeclaredMethod(cellFinder, "GetOrBuildUnroofedCacheForMap", new[] { typeof(Map) });

                ABLog.Dev("Geological Landforms compat: roofPass="
                    + (setRoofsFromLandform != null ? "FOUND" : "absent")
                    + " prepare=" + (landformPrepare != null ? "FOUND" : "absent")
                    + " mutator=" + (mutatorPostElevationFertility != null ? "FOUND" : "absent")
                    + " biomeGrid=" + (biomeGridSetBiome != null ? "FOUND" : "absent")
                    + " unroofed=" + (unroofedCacheBuild != null ? "FOUND" : "absent"));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " GL compat resolve threw: " + e, 762195895);
            }
        }

        internal static MethodBase SetRoofsFromLandformTarget
        {
            get { Resolve(); return setRoofsFromLandform; }
        }

        internal static MethodBase LandformPrepareTarget
        {
            get { Resolve(); return landformPrepare; }
        }

        internal static MethodBase MutatorPostElevationFertilityTarget
        {
            get { Resolve(); return mutatorPostElevationFertility; }
        }

        internal static MethodBase MutatorPostTerrainTarget
        {
            get { Resolve(); return mutatorPostTerrain; }
        }

        internal static MethodBase OpenGroundFractionTarget
        {
            get { Resolve(); return openGroundFractionUpdate; }
        }

        internal static MethodBase UnroofedCacheTarget
        {
            get { Resolve(); return unroofedCacheBuild; }
        }

        /// <summary>
        /// Armed for the duration of GL's terrain pass; set true if GL wrote ANY terrain
        /// cell anywhere on the map.
        ///
        /// ⚠⚠ §56m - THE POLLUTED DESTINATION. The obvious question "which cells did GL
        /// author?" is the WRONG one, and answering it precisely (a per-cell write mask over
        /// the anchor rows) made the bug worse, not better. GL's
        /// <c>ApplyBuffered(map.Size, …)</c> paints the ENTIRE STACKED COLUMN, so it has
        /// already overwritten the surface band with the landform sampled far OUT OF RANGE -
        /// which for an Island or Coast resolves to open ocean. Lifting only the authored
        /// cells therefore leaves GL's out-of-range ocean sitting in every cell it declined:
        /// measured at run #40, the anchor rows held 13,001 open land cells and the surface
        /// band received 4,191, losing almost exactly the ~9,000 cells GL had not authored.
        ///
        /// The destination is not neutral ground that we are patching - it is garbage that
        /// must be REPLACED WHOLESALE. So the only question worth asking is "did GL's terrain
        /// pass run at all", which is one bool, and the answer drives a whole-slice lift.
        /// </summary>
        [ThreadStatic]
        internal static bool TerrainWriteArmed;

        [ThreadStatic]
        internal static bool TerrainWriteSeen;

        /// <summary>True for the duration of GL's landform roof sweep.
        ///
        /// ThreadStatic because map generation runs off the main thread, and a stray true
        /// leaking onto another thread would silently suppress legitimate roof writes
        /// elsewhere in the game.</summary>
        [ThreadStatic]
        internal static bool InLandformRoofPass;

        // ------------------------------------------------------------------
        // §56.2  THE TRANSPLANT
        // ------------------------------------------------------------------

        /// <summary>
        /// Band geometry for the transplant, or false when there is nothing to move.
        ///
        /// Returns false when the surface band starts at z=0 (a level plan with nothing
        /// below it), because then GL's own anchor is already the surface band and copying
        /// a region onto itself is pure cost.
        /// </summary>
        internal static bool TryTransplantGeometry(Map map, out int z0, out int h)
        {
            z0 = 0;
            h = 0;
            if (map == null || !ABGuard.On(ABGuard.LevelGen))
            {
                return false;
            }
            if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot) || slot <= 0)
            {
                return false;
            }
            if (surface.minZ <= 0 || surface.Height <= 0 || surface.maxZ >= map.Size.z)
            {
                return false;
            }
            z0 = surface.minZ;
            h = surface.Height;
            return true;
        }

        /// <summary>
        /// What the anchor rows held BEFORE GL's worker ran, so the postfix can tell which
        /// grids GL actually authored. Passed between prefix and postfix as Harmony's
        /// <c>__state</c> rather than a ThreadStatic: map generation runs off the main
        /// thread and __state is per-invocation by construction.
        /// </summary>
        internal sealed class AnchorState
        {
            internal int z0;
            internal int h;
            internal int w;
            internal float[] elevation;
            internal float[] fertility;
            internal float[] caves;
            internal TerrainDef[] terrain;
            internal BiomeDef[] biome;
            internal object flowRef;
            internal Func<IntVec3, BiomeDef> biomeRead;
            internal Action<IntVec3, BiomeDef> biomeWrite;

            internal int Index(int x, int j)
            {
                return x * h + j;
            }
        }

        /// <summary>Bind fast delegates to GL's biome grid on this map.
        ///
        /// Uses BiomeAt/SetBiome rather than EntryAt/SetEntry: the Entry-based pair would
        /// preserve variant LAYERS, but it also drags GL's <c>Entry</c> type through our
        /// signatures, and it is not needed - <c>GenStep_BiomeVariants</c> runs at order 225,
        /// i.e. AFTER the post-elevation mutator pass that this transplant follows, so no
        /// variant layer exists yet to lose.
        ///
        /// Delegates rather than <c>MethodInfo.Invoke</c> because the snapshot reads and the
        /// transplant writes are both once per cell per landform layer - about 72,000 calls
        /// on a 190-wide band, which is the difference between a millisecond and a visible
        /// hitch.</summary>
        internal static bool TryBiomeAccessors(Map map, out Func<IntVec3, BiomeDef> read,
            out Action<IntVec3, BiomeDef> write)
        {
            read = null;
            write = null;
            Resolve();
            if (map == null || biomeGrid == null || biomeGridBiomeAt == null || biomeGridSetBiome == null)
            {
                return false;
            }
            object grid = null;
            List<MapComponent> comps = map.components;
            for (int i = 0; i < comps.Count; i++)
            {
                if (comps[i] != null && biomeGrid.IsInstanceOfType(comps[i]))
                {
                    grid = comps[i];
                    break;
                }
            }
            if (grid == null)
            {
                return false;
            }
            try
            {
                read = (Func<IntVec3, BiomeDef>)Delegate.CreateDelegate(
                    typeof(Func<IntVec3, BiomeDef>), grid, biomeGridBiomeAt);
                write = (Action<IntVec3, BiomeDef>)Delegate.CreateDelegate(
                    typeof(Action<IntVec3, BiomeDef>), grid, biomeGridSetBiome);
                return true;
            }
            catch
            {
                read = null;
                write = null;
                return false;
            }
        }

        /// <summary>
        /// ASSERT ALWAYS, NARRATE ON REQUEST (§57i).
        ///
        /// The narration - censuses, authored-grid lists, the genstep audit - is behind
        /// <c>verboseLogging</c> only. It was briefly on <c>Prefs.DevMode</c> too, because
        /// run #39 was wasted with the identifying lines behind a setting nobody had turned
        /// on (§56k) - but dev mode is ordinary play for a large slice of players, and the
        /// publish checklist is explicit that generation-time narration must not land in
        /// their logs. The debugging lesson is preserved as a habit (turn verbose logging ON
        /// before investigating generation) rather than as shipped log spam.
        ///
        /// What is NOT gated: the invariant assertions in <c>ProbeSurfacePhase</c> and the
        /// band-overlap check. Those are `Log.Error` and always fire, because they only
        /// speak when something is genuinely broken - and they are what caught §57.
        ///
        /// ⚠ CALLERS MUST GATE EXPENSIVE ARGUMENTS ON <see cref="DiagEnabled"/>. C# evaluates
        /// arguments before the call, so `Diag("x" + CensusOf(...))` walks the whole surface
        /// band whether or not anything is logged. That shipped-cost trap is exactly what the
        /// checklist's log-hygiene section exists to catch.
        /// </summary>
        internal static bool DiagEnabled
        {
            get { return ABMod.Settings != null && ABMod.Settings.verboseLogging; }
        }

        internal static void Diag(string msg)
        {
            if (DiagEnabled)
            {
                Log.Message(ABLog.Tag + " " + msg);
            }
        }

        /// <summary>Terrain-level census of one band-height slice starting at z0.
        ///
        /// Counted at the END of the terrain mutator pass, i.e. BEFORE GenStep_RocksFromGrid
        /// has spawned anything, so "edifice" here is whatever already stood there and the
        /// interesting figure is water vs open. Comparing the ANCHOR slice against the
        /// SURFACE slice is the whole experiment: identical censuses mean the lift is
        /// faithful and the landform really is that wet, divergent ones mean the lift is
        /// dropping land.</summary>
        internal static string CensusOf(Map map, int z0, int h)
        {
            int water = 0;
            int solid = 0;
            int open = 0;
            int w = map.Size.x;
            for (int x = 0; x < w; x++)
            {
                for (int j = 0; j < h; j++)
                {
                    IntVec3 c = new IntVec3(x, 0, z0 + j);
                    TerrainDef t = map.terrainGrid.TerrainAt(c);
                    if (t != null && t.IsWater)
                    {
                        water++;
                    }
                    else if (c.GetEdifice(map) != null)
                    {
                        solid++;
                    }
                    else
                    {
                        open++;
                    }
                }
            }
            return "water=" + water + " edifice=" + solid + " open=" + open;
        }

        /// <summary>Name the things actually standing in a slice, commonest first.
        ///
        /// §56.9 probe. A cell census says "10,711 edifices"; this says WHAT they are, which
        /// is the difference between suspecting a scatterer and knowing one. Pairs with the
        /// genstep audit: the def names the thing, the audit names who spawned it.</summary>
        internal static string TopEdificesIn(Map map, int z0, int h, int top = 6)
        {
            var counts = new Dictionary<string, int>();
            int total = 0;
            int w = map.Size.x;
            for (int x = 0; x < w; x++)
            {
                for (int j = 0; j < h; j++)
                {
                    Building b = new IntVec3(x, 0, z0 + j).GetEdifice(map);
                    if (b == null || b.def == null)
                    {
                        continue;
                    }
                    total++;
                    string key = b.def.defName + (b.def.building != null && b.def.building.isResourceRock
                        ? " (resourceRock)"
                        : "");
                    counts.TryGetValue(key, out int n);
                    counts[key] = n + 1;
                }
            }
            if (total == 0)
            {
                return "none";
            }
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            sb.Append(total).Append(" total:");
            for (int i = 0; i < ordered.Count && i < top; i++)
            {
                sb.Append(" ").Append(ordered[i].Key).Append("=").Append(ordered[i].Value);
            }
            return sb.ToString();
        }

        /// <summary>Count edifices in a rect. §56.11 probe primitive.</summary>
        internal static int CountEdifices(Map map, CellRect rect)
        {
            int n = 0;
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) && c.GetEdifice(map) != null)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>Assert that a carve phase left the surface band alone, and say so loudly
        /// if it did not. Errors rather than Diag: a phase writing into the surface band is
        /// a correctness failure, not a diagnostic curiosity, and it must surface even for a
        /// player who never opens dev mode.</summary>
        internal static void ProbeSurfacePhase(Map map, CellRect surf, ref int watch, string phase)
        {
            int now = CountEdifices(map, surf);
            if (now != watch)
            {
                Log.Error(ABLog.Tag + " V2: carve phase '" + phase
                    + "' changed surface-band edifices " + watch + " -> " + now
                    + ". THE SURFACE BAND MUST NOT BE TOUCHED BY THE CARVE.");
                watch = now;
            }
        }

        // ---- snapshot / compare / lift primitives -------------------------

        internal static void Snapshot(MapGenFloatGrid grid, AnchorState s, float[] into)
        {
            for (int x = 0; x < s.w; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    into[s.Index(x, j)] = grid[new IntVec3(x, 0, j)];
                }
            }
        }

        /// <summary>Did GL write this grid at the anchor rows? If so lift the WHOLE slice.
        ///
        /// Whole-slice rather than changed-cells-only on purpose: GL's ApplyBuffered writes
        /// every cell when its module is non-null, so a cell whose landform value happens to
        /// equal what vanilla already had is still landform output and must travel with the
        /// rest. Lifting only the differing cells would leave that cell showing the SURFACE
        /// band's own unrelated vanilla value - a speckle that is invisible in testing and
        /// impossible to explain later.</summary>
        internal static bool LiftIfAuthored(MapGenFloatGrid grid, AnchorState s, float[] before)
        {
            bool authored = false;
            for (int x = 0; x < s.w && !authored; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    if (grid[new IntVec3(x, 0, j)] != before[s.Index(x, j)])
                    {
                        authored = true;
                        break;
                    }
                }
            }
            if (!authored)
            {
                return false;
            }
            for (int x = 0; x < s.w; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    grid[new IntVec3(x, 0, s.z0 + j)] = grid[new IntVec3(x, 0, j)];
                }
            }
            return true;
        }

        /// <summary>Recompute open-ground fraction over the surface band only, reusing GL's
        /// own per-cell arithmetic so the two can never drift.</summary>
        internal static bool TryRescopeOpenGroundFraction(object grid, Map map)
        {
            Resolve();
            if (grid == null || map == null || openGroundFractionFor == null || openGroundFractionProp == null)
            {
                return false;
            }
            if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot) || slot <= 0)
            {
                // Not generating: fall back to the live band layout so the FinalizeInit
                // call on a LOADED save is scoped too.
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return false;
                }
                surface = bands.RectOfBand(bands.surfaceBand);
            }
            bool caveBiome = false;
            try
            {
                object primary = AccessTools.DeclaredProperty(biomeGrid, "Primary")?.GetValue(grid, null);
                object biomeBase = primary == null
                    ? null
                    : AccessTools.DeclaredProperty(primary.GetType(), "BiomeBase")?.GetValue(primary, null);
                caveBiome = biomeBase is BiomeDef b && b.wildPlantsAreCavePlants;
            }
            catch
            {
                // Leave caveBiome false - it only softens the score for unwalkable cells.
            }
            bool waterPassable = TerrainDefOf.WaterDeep.passability != Traversability.Impassable;

            double sum = 0.0;
            object[] args = new object[3];
            foreach (IntVec3 c in surface)
            {
                args[0] = c;
                args[1] = caveBiome;
                args[2] = waterPassable;
                sum += (float)openGroundFractionFor.Invoke(grid, args);
            }
            float area = surface.Area;
            if (area <= 0f)
            {
                return false;
            }
            openGroundFractionProp.SetValue(grid, UnityEngine.Mathf.Clamp01((float)(sum / area)), null);
            return true;
        }
    }

    /// <summary>
    /// §56.1  TELL GL THE MAP IS ONE BAND TALL.
    ///
    /// <c>Landform.Prepare</c> snapshots the map dimensions into <c>GeneratingMapSize</c>,
    /// and on a banded map that is the STACKED size - 250 x 1792 rather than 250 x 250.
    /// Three separate things read it and all three are wrong by the same cause:
    ///
    ///   - <c>GeneratingMapSizeMin = min(x, z)</c> drives
    ///     <c>MapSpaceToNodeSpaceFactor</c>, i.e. the SCALE of every landform grid. On a
    ///     square colony the stacked z is never the minimum so this survives by luck; on a
    ///     RECTANGULAR one (250x150 banded to 250x1064) vanilla would have used 150 and GL
    ///     uses 250, stretching the entire landform by a third.
    ///   - <c>GeneratingGridFullSize = LandformGridSize.Apply(mapSize)</c> is an extension
    ///     point other mods may bind to map size.
    ///   - the graph nodes that ask where the map edge is - NodeValueMapSize,
    ///     NodeGridTransformByMapSize, NodeGridRotateToMapSides - plus
    ///     <c>OutputBiomeGrid.ApplyBiomeTransitions(tile, mapSize, …)</c>, which positions a
    ///     biome transition band relative to it.
    ///
    /// This is the NOISE SPACE row of the slicing rule (§1), and correcting the input at the
    /// single point where GL records it is the whole fix - no per-node patching.
    ///
    /// ⚠ The three-argument overload is the target, not <c>Prepare(Map)</c>. Prepare(Map)
    /// resolves the tile info and seed and delegates, so patching the delegate catches BOTH
    /// the real generation path and Map Preview, which calls the three-argument form
    /// directly with its own size. The map comes from <c>MapGenerator.mapBeingGenerated</c>
    /// (the same source the coast remap uses) because the overload is not handed one.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABLandformMapSize
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.LandformPrepareTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.LandformPrepareTarget;
        }

        /// <summary>Named to match GL's parameter. Harmony binds prefix arguments by NAME,
        /// so this never has to name the foreign IWorldTileInfo sitting in slot 0.</summary>
        private static void Prefix(ref IntVec2 mapSize)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                Map map = MapGenerator.mapBeingGenerated;
                if (map == null)
                {
                    return;
                }
                if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot) || slot <= 0)
                {
                    return; // ordinary map - GL's own snapshot is correct
                }
                int h = surface.Height;
                if (h <= 0 || h >= mapSize.z)
                {
                    return;
                }
                mapSize = new IntVec2(mapSize.x, h);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform map size patch threw: " + e, 762195897);
            }
        }
    }

    /// <summary>
    /// §56.2  TRANSPLANT THE LANDFORM INTO THE SURFACE BAND (elevation / fertility / caves
    /// / biome).
    ///
    /// With §56.1 applied, GL now generates a correctly scaled landform - but it is anchored
    /// at z=0, which on a banded map is the DEEPEST BASEMENT, not the surface. GL's
    /// <c>ApplyBuffered(map.Size, …)</c> walks the whole stack and evaluates the map-space
    /// grid at every row, so rows [0, h) hold exactly the landform an ordinary h-tall map
    /// would have had, and everything above it is that same function sampled far outside its
    /// intended domain.
    ///
    /// So we copy rows [0, h) up into the surface band. This is the row of §1 that has no
    /// clean upstream fix: the alternative is to offset the grid functions themselves at
    /// <c>Landform.TransformIntoMapSpace&lt;T&gt;</c>, which is the single choke point every
    /// consumer funnels through - but it is GENERIC, and the CLR shares one native body
    /// across all reference-type instantiations, so patching &lt;TerrainDef&gt; also patches
    /// &lt;BiomeDef&gt; and a TargetMethods list naming both would apply the offset TWICE
    /// with no way to tell. A row copy is O(w·h), runs once, and cannot double-apply.
    ///
    /// ⚠ COPY, NOT MOVE. The source rows are deliberately left intact:
    ///   - a landform LAYER that runs after this one re-evaluates
    ///     <c>MapGenerator.Fertility[c]</c> and <c>Caves[c]</c> at rows [0, h) while building
    ///     its terrain, and it must find the same data there that it would have found on an
    ///     unbanded map;
    ///   - the carve destroys everything outside the surface band anyway, so clearing the
    ///     source would be work nobody reads.
    ///
    /// ⚠ Runs as a POSTFIX on the worker, so it fires once per landform layer, after each
    /// layer has finished writing. Transplanting after every layer rather than once at the
    /// end is what keeps a multi-layer landform consistent.
    ///
    /// ⚠⚠ ONLY LIFT WHAT GL ACTUALLY WROTE (§56g). The first cut of this patch copied all
    /// four grids unconditionally and produced a colony whose entire surface level was rock
    /// and ocean - 24,863 cells blocked by an edifice, 10,837 water, ZERO standable. GL
    /// writes each grid only when that landform authored the matching output:
    ///     if (elevationModule != null) ApplyBuffered(...);
    /// and a Coast authors terrain and caves but NO elevation. So the unconditional copy was
    /// overwriting the surface band's elevation with BAND 0's vanilla elevation - a different
    /// slice of the same noise - and GenStep_RocksFromGrid then turned every cell above 0.7
    /// into granite. The fix is to snapshot the anchor rows in a prefix and lift a grid only
    /// when it changed, which needs no knowledge of which outputs the landform declared and
    /// covers the early-return case for free (a postfix still runs when the original
    /// returned early, so "GL ran" is never a safe assumption).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABLandformTransplantElevation
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.MutatorPostElevationFertilityTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.MutatorPostElevationFertilityTarget;
        }

        private static void Prefix(Map map, out GeologicalLandformsCompat.AnchorState __state)
        {
            __state = null;
            try
            {
                if (!GeologicalLandformsCompat.TryTransplantGeometry(map, out int z0, out int h))
                {
                    return;
                }
                var s = new GeologicalLandformsCompat.AnchorState
                {
                    z0 = z0,
                    h = h,
                    w = map.Size.x
                };
                int n = s.w * s.h;
                s.elevation = new float[n];
                s.fertility = new float[n];
                s.caves = new float[n];
                GeologicalLandformsCompat.Snapshot(MapGenerator.Elevation, s, s.elevation);
                GeologicalLandformsCompat.Snapshot(MapGenerator.Fertility, s, s.fertility);
                GeologicalLandformsCompat.Snapshot(MapGenerator.Caves, s, s.caves);
                if (GeologicalLandformsCompat.TryBiomeAccessors(map,
                        out Func<IntVec3, BiomeDef> read, out Action<IntVec3, BiomeDef> write))
                {
                    s.biomeRead = read;
                    s.biomeWrite = write;
                    s.biome = new BiomeDef[n];
                    for (int x = 0; x < s.w; x++)
                    {
                        for (int j = 0; j < s.h; j++)
                        {
                            s.biome[s.Index(x, j)] = read(new IntVec3(x, 0, j));
                        }
                    }
                }
                __state = s;
            }
            catch (Exception e)
            {
                __state = null;
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform anchor snapshot threw: " + e, 762195902);
            }
        }

        private static void Postfix(Map map, GeologicalLandformsCompat.AnchorState __state)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                string moved = "";
                if (GeologicalLandformsCompat.LiftIfAuthored(MapGenerator.Elevation, __state, __state.elevation))
                {
                    moved += " elevation";
                }
                if (GeologicalLandformsCompat.LiftIfAuthored(MapGenerator.Fertility, __state, __state.fertility))
                {
                    moved += " fertility";
                }
                if (GeologicalLandformsCompat.LiftIfAuthored(MapGenerator.Caves, __state, __state.caves))
                {
                    moved += " caves";
                }
                if (LiftBiomesIfAuthored(__state))
                {
                    moved += " biome";
                }
                GeologicalLandformsCompat.Diag("V2: GL landform pass (elevation) [rows 0.."
                    + (__state.h - 1) + " -> " + __state.z0 + ".." + (__state.z0 + __state.h - 1)
                    + "], grids authored by GL:" + (moved.Length == 0 ? " NONE" : moved) + ".");
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform elevation transplant threw: " + e, 762195898);
            }
        }

        private static bool LiftBiomesIfAuthored(GeologicalLandformsCompat.AnchorState s)
        {
            if (s.biome == null || s.biomeRead == null || s.biomeWrite == null)
            {
                return false;
            }
            bool authored = false;
            for (int x = 0; x < s.w && !authored; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    if (s.biomeRead(new IntVec3(x, 0, j)) != s.biome[s.Index(x, j)])
                    {
                        authored = true;
                        break;
                    }
                }
            }
            if (!authored)
            {
                return false;
            }
            for (int x = 0; x < s.w; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    BiomeDef b = s.biomeRead(new IntVec3(x, 0, j));
                    if (b != null)
                    {
                        s.biomeWrite(new IntVec3(x, 0, s.z0 + j), b);
                    }
                }
            }
            return true;
        }
    }

    /// <summary>
    /// §56.2 continued - the same transplant for the TERRAIN pass and the river flow map.
    ///
    /// Terrain is copied top-only. GL's worker calls <c>terrainGrid.SetTerrain</c> and never
    /// touches under-terrain, so lifting <c>TerrainAt</c> reproduces its output exactly;
    /// copying under-terrain as well would drag along whatever vanilla's own terrain genstep
    /// had put at the anchor rows and is not GL's to give.
    ///
    /// The river flow map is a flat <c>List&lt;float&gt;</c> of 2 floats per cell indexed
    /// <c>(x * Size.z + z) * 2</c>, written by GL's ApplyWaterFlow from the same
    /// zero-anchored functions. Left untransplanted, a river drawn in the surface band would
    /// carry the flow vectors of a row three bands down: water that visually runs the wrong
    /// way, or does not run at all.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABLandformTransplantTerrain
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.MutatorPostTerrainTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.MutatorPostTerrainTarget;
        }

        private static void Prefix(Map map, out GeologicalLandformsCompat.AnchorState __state)
        {
            __state = null;
            try
            {
                if (!GeologicalLandformsCompat.TryTransplantGeometry(map, out int z0, out int h))
                {
                    return;
                }
                var s = new GeologicalLandformsCompat.AnchorState
                {
                    z0 = z0,
                    h = h,
                    w = map.Size.x
                };
                s.terrain = new TerrainDef[s.w * s.h];
                TerrainGrid grid = map.terrainGrid;
                for (int x = 0; x < s.w; x++)
                {
                    for (int j = 0; j < s.h; j++)
                    {
                        s.terrain[s.Index(x, j)] = grid.TerrainAt(new IntVec3(x, 0, j));
                    }
                }
                s.flowRef = map.waterInfo?.riverFlowMap;
                GeologicalLandformsCompat.TerrainWriteSeen = false;
                GeologicalLandformsCompat.TerrainWriteArmed = true;
                __state = s;
            }
            catch (Exception e)
            {
                __state = null;
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform terrain snapshot threw: " + e, 762195903);
            }
        }

        private static void Postfix(Map map, GeologicalLandformsCompat.AnchorState __state)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                int lifted = LiftTerrainSlice(map, __state);
                string moved = lifted > 0 ? (" terrain(" + lifted + " cells)") : "";
                if (LiftFlowIfAuthored(map, __state))
                {
                    moved += " riverFlow";
                }
                // Gated, not just the log call: CensusOf walks the whole band twice.
                if (GeologicalLandformsCompat.DiagEnabled)
                {
                    GeologicalLandformsCompat.Diag("V2: GL landform pass (terrain), grids authored"
                        + " by GL:" + (moved.Length == 0 ? " NONE" : moved)
                        + " | ANCHOR rows 0.." + (__state.h - 1) + " ["
                        + GeologicalLandformsCompat.CensusOf(map, 0, __state.h) + "]"
                        + " | SURFACE rows " + __state.z0 + ".." + (__state.z0 + __state.h - 1) + " ["
                        + GeologicalLandformsCompat.CensusOf(map, __state.z0, __state.h) + "]");
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform terrain transplant threw: " + e, 762195899);
            }
            finally
            {
                // ⚠ Cleared in a finally: a recorder left armed would keep observing writes
                // for the rest of the session on this thread.
                GeologicalLandformsCompat.TerrainWriteArmed = false;
                GeologicalLandformsCompat.TerrainWriteSeen = false;
            }
        }

        /// <summary>Replace the whole surface slice with the anchor slice when GL's terrain
        /// pass ran (§56m).
        ///
        /// "Ran" is detected by the SetTerrain recorder; if that patch is missing we fall
        /// back to "did anything at the anchor rows change", which is the same question asked
        /// less reliably. When GL's landform declares no terrain output at all, ApplyBuffered
        /// is never called, nothing is polluted, and we correctly leave the surface alone.</summary>
        private static int LiftTerrainSlice(Map map, GeologicalLandformsCompat.AnchorState s)
        {
            TerrainGrid grid = map.terrainGrid;
            bool ran = GeologicalLandformsCompat.TerrainWriteSeen;
            if (!ran)
            {
                for (int x = 0; x < s.w && !ran; x++)
                {
                    for (int j = 0; j < s.h; j++)
                    {
                        if (grid.TerrainAt(new IntVec3(x, 0, j)) != s.terrain[s.Index(x, j)])
                        {
                            ran = true;
                            break;
                        }
                    }
                }
            }
            if (!ran)
            {
                return 0;
            }
            int lifted = 0;
            for (int x = 0; x < s.w; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    TerrainDef def = grid.TerrainAt(new IntVec3(x, 0, j));
                    if (def == null)
                    {
                        continue;
                    }
                    grid.SetTerrain(new IntVec3(x, 0, s.z0 + j), def);
                    lifted++;
                }
            }
            return lifted;
        }

        /// <summary>GL replaces <c>riverFlowMap</c> wholesale with a freshly allocated list
        /// (<c>waterInfo.riverFlowMap = new List&lt;float&gt;(array)</c>), so a REFERENCE
        /// comparison is an exact test for "did ApplyWaterFlow run this pass" - no per-cell
        /// scan needed.</summary>
        private static bool LiftFlowIfAuthored(Map map, GeologicalLandformsCompat.AnchorState s)
        {
            List<float> flow = map.waterInfo?.riverFlowMap;
            if (flow == null || ReferenceEquals(flow, s.flowRef))
            {
                return false;
            }
            int sizeZ = map.Size.z;
            if (flow.Count < map.Size.x * sizeZ * 2)
            {
                return false;
            }
            for (int x = 0; x < s.w; x++)
            {
                for (int j = 0; j < s.h; j++)
                {
                    int srcIdx = (x * sizeZ + j) * 2;
                    int dstIdx = (x * sizeZ + s.z0 + j) * 2;
                    flow[dstIdx] = flow[srcIdx];
                    flow[dstIdx + 1] = flow[srcIdx + 1];
                }
            }
            return true;
        }
    }

    /// <summary>
    /// §56.9  AUDIT THE GENSTEP LIST AS ACTUALLY ASSEMBLED.
    ///
    /// GL has a THIRD write path besides the two mutator passes: its
    /// <c>GenerateContentsIntoMap</c> prefix appends each landform's
    /// <c>NodeRunGenStep.GenStepDef</c> to the list, at whatever order the landform author
    /// chose. GL's own preview guard reads <c>item.Order &lt; 230.0</c>, which says those
    /// orders routinely land AFTER our terrain lift at 220 - i.e. after everything this
    /// compat layer corrects, writing straight into the surface band with the landform
    /// sampled out of range.
    ///
    /// Runs at LOWEST priority so GL's priority-800 prefix has already appended its
    /// additions by the time we look. Read-only and DevMode-only.
    ///
    /// ⚠ The parameter is taken BY VALUE, not by ref, and re-enumerated into a local list.
    /// The chain is LINQ over defs and is safely re-enumerable, but never mutate it here -
    /// this is a probe, not a policy.
    /// </summary>
    [HarmonyPatch(typeof(MapGenerator), "GenerateContentsIntoMap")]
    public static class Patch_MapGenerator_ABGenStepAudit
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(IEnumerable<GenStepWithParams> genStepDefs)
        {
            // verboseLogging only: this dumps ~40 lines per generation, which is precisely
            // the shape of the ABGenProfile.Report offender the checklist calls out.
            if (!GeologicalLandformsCompat.DiagEnabled || genStepDefs == null)
            {
                return;
            }
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("V2: genstep order audit (steps at order > 220 run AFTER the GL"
                    + " terrain lift):");
                foreach (GenStepWithParams g in genStepDefs)
                {
                    GenStepDef d = g.def;
                    if (d == null)
                    {
                        continue;
                    }
                    sb.Append("\n    " + d.order.ToString("0000") + "  " + d.defName
                        + "  [" + (d.genStep == null ? "null" : d.genStep.GetType().Name) + "]"
                        + (d.generated ? "  (injected)" : ""));
                }
                Log.Message(ABLog.Tag + " " + sb);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: genstep audit probe threw: " + e.Message);
            }
        }
    }

    /// <summary>
    /// §56m  DID GL'S TERRAIN PASS RUN AT ALL?
    ///
    /// Armed only for the duration of <c>TileMutatorWorker_Landform.GeneratePostTerrain</c>,
    /// so on every other SetTerrain call in the entire game this is a single ThreadStatic
    /// bool test.
    ///
    /// Deliberately records nothing about WHICH cell. An earlier version kept a per-cell mask
    /// over the anchor rows and used it to lift only authored cells - which was precise,
    /// well-tested, and wrong, because GL has already overwritten the surface band with
    /// out-of-range landform output and the unauthored cells are exactly where that garbage
    /// survives. See the §56m note on <c>TerrainWriteArmed</c>.
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_TerrainGrid_ABLandformWriteProbe
    {
        private static void Prefix()
        {
            if (GeologicalLandformsCompat.TerrainWriteArmed)
            {
                GeologicalLandformsCompat.TerrainWriteSeen = true;
            }
        }
    }

    /// <summary>
    /// §56.3  OPEN GROUND FRACTION IS A MAP-WIDE SCALAR (§1, rule 4: count populations).
    ///
    /// <c>BiomeGrid.UpdateOpenGroundFraction</c> averages a per-cell openness score over
    /// <c>map.AllCells</c> divided by <c>NumGridCells</c>. On a seven-band map six sevenths
    /// of those cells are not colony ground at all: basement rock scores 0.35 (or 0.75 in a
    /// cave biome) and the open-air sky bands score whatever their terrain says. The result
    /// feeds GL's <c>AnimalDensityFactor</c> extension point and is refreshed from their
    /// <c>GenStep_Animals</c> prefix, so a banded colony gets wildlife density computed from
    /// its basements.
    ///
    /// The postfix recomputes over the surface band by calling GL's OWN private per-cell
    /// scorer through reflection. That is the point: we change the DOMAIN of the average and
    /// borrow the arithmetic, so a future change to their formula is picked up for free and
    /// the two can never silently disagree.
    ///
    /// Fails open - if any of the reflection is missing we leave their value alone, because
    /// a wrong-but-plausible density is strictly better than a zero one.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABOpenGroundFraction
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.OpenGroundFractionTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.OpenGroundFractionTarget;
        }

        private static void Postfix(object __instance, Map ___map)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                GeologicalLandformsCompat.TryRescopeOpenGroundFraction(__instance, ___map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL open-ground rescope threw: " + e, 762195900);
            }
        }
    }

    /// <summary>
    /// §56.4  GL's UNROOFED-CELL CACHE MUST NOT OFFER THE SKY (§1, rule 1: clamp at
    /// selection).
    ///
    /// <c>GetOrBuildUnroofedCacheForMap</c> takes the first 50 unroofed cells out of
    /// <c>map.cellsInRandomOrder</c> and hands them to the postfixes on
    /// <c>CellFinderLoose.TryGetRandomCellWith</c> and
    /// <c>TryFindRandomNotEdgeCellWith</c> as a last-chance fallback. On a banded map the
    /// overwhelming majority of unroofed cells are SKY BAND and gutter rows - open air is
    /// unroofed by definition - so the fallback that exists to rescue a failed placement
    /// would instead place it in the void, and it is a fallback precisely when nothing else
    /// worked, i.e. exactly when nobody is checking.
    ///
    /// Filtering the returned array is enough and needs no write-back: GL caches the
    /// unfiltered array on the BiomeGrid and returns it on every later call, and this
    /// postfix filters that return too. The filter is idempotent and at most 50 cells wide.
    ///
    /// An empty result after filtering is the correct answer, not a failure - GL's consumer
    /// then reports false and vanilla's original negative result stands (fail closed: this
    /// net decides where things are PLACED).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABUnroofedCacheBand
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.UnroofedCacheTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.UnroofedCacheTarget;
        }

        private static void Postfix(Map map, ref IntVec3[] __result)
        {
            try
            {
                if (__result == null || __result.Length == 0 || map == null)
                {
                    return;
                }
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                CellRect surface;
                if (bands != null && bands.Banded)
                {
                    surface = bands.RectOfBand(bands.surfaceBand);
                }
                else if (!ABBandedGeneration.TryPendingSurfaceRect(map, out surface, out int slot) || slot <= 0)
                {
                    return; // ordinary map
                }
                List<IntVec3> kept = null;
                for (int i = 0; i < __result.Length; i++)
                {
                    if (surface.Contains(__result[i]))
                    {
                        (kept ?? (kept = new List<IntVec3>(__result.Length))).Add(__result[i]);
                    }
                }
                if (kept == null)
                {
                    __result = Array.Empty<IntVec3>();
                }
                else if (kept.Count != __result.Length)
                {
                    __result = kept.ToArray();
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL unroofed cache scope threw: " + e, 762195901);
            }
        }
    }

    /// <summary>
    /// Scope GL's landform roof sweep, which is otherwise applied to the whole column.
    ///
    /// Their <c>GenStep_RocksFromGrid</c> transpiler inserts a call to
    /// <c>SetRoofsFromLandform</c>, whose body is
    /// <c>foreach (IntVec3 c in map.AllCells) map.roofGrid.SetRoof(c, grid.ValueAt(c.x, c.z))</c>.
    /// The grid is the SURFACE tile's landform roof output, and <c>map.AllCells</c> on a
    /// seven-band map is about 800,000 cells - so the surface's roof pattern is stamped
    /// verbatim onto every sky band, every basement band and both gutters, and each write
    /// also drags our own <c>RoofGrid.SetRoof</c> postfix (<c>ABSkySync</c>) behind it.
    ///
    /// This is the MAP-WIDE SCALAR row of the slicing rule: one grid computed for the map as
    /// a whole, consumed as though every cell belonged to it.
    ///
    /// SUPPRESS THE CALLEE, NOT THE CALLER (§9d). The alternative was to prefix
    /// <c>SetRoofsFromLandform</c> and re-run a band-scoped copy of its loop, which would
    /// mean reimplementing a foreign body that calls <c>Landform.GetFeatureScaled</c> - a
    /// type we must not name. Gating the individual roof write instead leaves GL's own
    /// arithmetic entirely intact and costs one ThreadStatic bool test per call.
    ///
    /// ⚠ Note on ordering: a prefix returning false skips the ORIGINAL, but Harmony still
    /// runs postfixes, so <c>Patch_RoofGrid_ABSyncAbove</c> still fires for a suppressed
    /// cell. That is harmless - it mirrors a roof that was not changed - but it is the reason
    /// this is a suppression of the WRITE rather than of the sweep.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_GL_ABLandformRoofScope
    {
        private static bool Prepare()
        {
            return GeologicalLandformsCompat.SetRoofsFromLandformTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return GeologicalLandformsCompat.SetRoofsFromLandformTarget;
        }

        private static void Prefix()
        {
            GeologicalLandformsCompat.InLandformRoofPass = true;
        }

        /// <summary>FINALIZER, not a postfix. §18a: Harmony skips postfixes when the original
        /// throws, and a latch left true here would suppress roof writes for the rest of the
        /// session on this thread.</summary>
        private static void Finalizer()
        {
            GeologicalLandformsCompat.InLandformRoofPass = false;
        }
    }

    /// <summary>Drop landform roof writes that fall outside the surface band. Inert unless
    /// GL's sweep is currently running, and inert on unbanded maps.
    ///
    /// ⚠ This stays a SUPPRESSION even after §56.2 gave the landform a transplant, because
    /// the roof sweep is not part of the mutator pass - it is spliced into
    /// GenStep_RocksFromGrid, long after the transplant has run, and it reads the roof grid
    /// function directly at map coordinates. Scoping it is the whole fix; there is nothing
    /// to lift.</summary>
    [HarmonyPatch(typeof(RoofGrid), nameof(RoofGrid.SetRoof))]
    public static class Patch_RoofGrid_ABLandformSurfaceOnly
    {
        private static readonly AccessTools.FieldRef<RoofGrid, Map> MapRef =
            AccessTools.FieldRefAccess<RoofGrid, Map>("map");

        private static bool Prefix(RoofGrid __instance, IntVec3 c)
        {
            try
            {
                if (!GeologicalLandformsCompat.InLandformRoofPass)
                {
                    return true; // the overwhelmingly common case: one bool test
                }
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return true;
                }
                Map map = MapRef(__instance);
                if (map == null
                    || !ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot)
                    || slot <= 0)
                {
                    return true; // not a banded map - GL's sweep is correct as written
                }
                return surface.Contains(c);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL landform roof scope threw: " + e, 762195896);
                return true;
            }
        }
    }
}
