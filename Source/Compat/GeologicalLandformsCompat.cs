using System;
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
    /// complaint. One piece does not, and it is fixed here.
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
    /// The genuinely open item is that <c>Landform.Prepare(map)</c> snapshots the STACKED
    /// dimensions, so landform grids are computed for a 126x896 field rather than a 126x126
    /// one - the NOISE SPACE row of the slicing rule, the same shape as §6d. That needs its
    /// own investigation against <c>MapSpaceToNodeSpaceFactor</c> and is NOT guessed at here.
    /// </summary>
    public static class GeologicalLandformsCompat
    {
        public const string PackageId = "m00nl1ght.GeologicalLandforms";

        private const string RocksPatchType = "GeologicalLandforms.Patches.Patch_RimWorld_GenStep_RocksFromGrid";

        private static bool resolved;

        private static MethodBase setRoofsFromLandform;

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            try
            {
                Type t = AccessTools.TypeByName(RocksPatchType);
                setRoofsFromLandform = t == null
                    ? null
                    : AccessTools.DeclaredMethod(t, "SetRoofsFromLandform", new[] { typeof(Map) });
                ABLog.Dev("Geological Landforms compat: roofPass="
                    + (setRoofsFromLandform != null ? "FOUND" : "absent"));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " GL compat resolve threw: " + e, 762195895);
            }
        }

        internal static MethodBase SetRoofsFromLandformTarget
        {
            get
            {
                Resolve();
                return setRoofsFromLandform;
            }
        }

        /// <summary>True for the duration of GL's landform roof sweep.
        ///
        /// ThreadStatic because map generation runs off the main thread, and a stray true
        /// leaking onto another thread would silently suppress legitimate roof writes
        /// elsewhere in the game.</summary>
        [ThreadStatic]
        internal static bool InLandformRoofPass;
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
    /// GL's sweep is currently running, and inert on unbanded maps.</summary>
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
