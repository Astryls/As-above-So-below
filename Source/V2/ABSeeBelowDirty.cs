using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Invalidation for the see-below resolve cache (ABBandMap.seeBelowVersion).
    ///
    /// The cached quantity is the GEOMETRIC descent answer of
    /// <c>ABBands.TryResolveVisibleBelow</c>, whose only mutable input is
    /// <c>TerrainGrid.TerrainAt</c> (ShowsBelow tests the terrain def and nothing else;
    /// band geometry is fixed for the life of the map). So the cache is valid exactly as
    /// long as no terrain answer changes.
    ///
    /// ⚠ RULE 16: COUNT THE WRITE PATHS. 1.6's TerrainAt consults THREE grids
    /// (tempGrid, then foundationGrid, then topGrid), written by seven public methods -
    /// SetTerrain, RemoveTopLayer, SetFoundation, RemoveFoundation, SetTempTerrain,
    /// RemoveTempTerrain, SetTerrainColor. Every one of them funnels through the private
    /// <c>DoTerrainChangedEffects</c>, so ONE postfix there covers all seven, present and
    /// future - patching the writers individually would be seven chances to miss the
    /// eighth. The single exception is <c>RemoveGravshipTerrainUnsafe</c>, which writes
    /// grids directly without the funnel; it gets its own postfix below.
    ///
    /// The bump is a per-map version increment (O(1)); nothing is cleared. Cells
    /// re-resolve lazily on next touch. On an unbanded map CompOf returns null and the
    /// postfix is a null test.
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), "DoTerrainChangedEffects")]
    public static class Patch_TerrainGrid_ABSeeBelowDirty
    {
        private static void Postfix(Map ___map)
        {
            ABBands.CompOf(___map)?.DirtySeeBelowCache();
        }
    }

    /// <summary>See above: the one TerrainGrid writer that bypasses
    /// DoTerrainChangedEffects. Gravship moves rewrite terrain wholesale.</summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveGravshipTerrainUnsafe))]
    public static class Patch_TerrainGrid_ABSeeBelowDirtyGravship
    {
        private static void Postfix(Map ___map)
        {
            ABBands.CompOf(___map)?.DirtySeeBelowCache();
        }
    }
}
