using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Static def handles.
    ///
    /// SLIMMED FOR V2. The V1 version held 19 entries - job defs for cross-level hauling and
    /// capture, the AB_Basement/AB_Sky pocket-map generators, a custom MapMeshFlagDef, and
    /// the camera-lock keybind. Every one of those defs has been deleted along with the code
    /// that used them, and a [DefOf] field whose def no longer exists throws at startup
    /// ("Could not resolve cross-reference" / a null static that fails much later), so the
    /// class has to shrink in lockstep with the XML.
    ///
    /// What survives is exactly what V2 reads: the three terrain defs the band renderer and
    /// generators key off. The two view keybinds are deliberately NOT here - ABBandInput
    /// resolves them with GetNamedSilentFail so a missing keybind degrades to "hotkey does
    /// nothing" rather than a hard startup failure.
    /// </summary>
    [DefOf]
    public static class ABDefOf
    {
        /// <summary>The see-through terrain. Load-bearing: it is `dontRender`, and the whole
        /// band renderer keys visibility off "is the cell above this one AB_OpenAir".</summary>
        public static TerrainDef AB_OpenAir;

        /// <summary>Walkable rooftop surface in a band above a roofed structure.</summary>
        public static TerrainDef AB_RoofSurface;

        /// <summary>Peak surface generated where a mountain rises into the sky band.</summary>
        public static TerrainDef AB_MountainTop;

        /// <summary>Dirty flag in SectionLayer_ABMountainCap's relevantChangeTypes, so a
        /// below-content change can repaint the cap without dirtying every other layer.</summary>
        public static MapMeshFlagDef AB_BelowThings;

        static ABDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ABDefOf));
        }
    }
}
