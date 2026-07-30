using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// VANILLA'S NO-BUILD MAP EDGE, removed on banded maps.
    ///
    /// Vanilla reserves a 10-cell strip around the map that cannot be built on, drawn as a
    /// dashed rectangle whenever a build designator is active. On a banded map both halves
    /// of that feature are anchored to the STACK rather than to the level, and the result is
    /// incoherent in exactly the way the schematic's slicing rule predicts:
    ///
    ///   FUNCTIONALLY - the strip lands on the deepest basement's floor rows and the top of
    ///     the sky band. Those are two arbitrary levels; every other level, the surface
    ///     included, has no strip at all and can be built right up to its seam.
    ///   VISUALLY - GenDraw.DrawMapEdgeLines takes its rectangle straight from
    ///     Find.CurrentMap.Size, so it draws a box around the whole 190x768 stack. From
    ///     inside a band you see, at best, one stray line crossing the level; usually the
    ///     box is entirely off-screen in another band and the cue simply is not there.
    ///
    /// The rule is REMOVED rather than re-based. Per-band would also have been defensible -
    /// and is one flag away, because Patch_GenGrid_ABBandCloseToEdge already re-bases the
    /// underlying distance test onto the cell's own band, so simply deleting the two
    /// overrides below would give a correct 10-cell strip at every level's edge. It is
    /// removed because a level is not a map: its north and south boundaries are the seam, an
    /// internal structural device the player did not choose and should not have to build
    /// around, and reserving ten cells of every level for it costs real space on a 126-tall
    /// band.
    ///
    /// KNOWN CONSEQUENCE, stated rather than hidden: vanilla's strip is also what stops a
    /// colony sealing its own map edge and starving the pawn-entry finder. Building to the
    /// very edge of the surface band is now possible, and a player who walls the entire
    /// perimeter will make raid and caravan entry harder to satisfy. Flip Removed to false
    /// to get the per-band strip back.
    /// </summary>
    public static class ABNoBuildEdge
    {
        /// <summary>true - no reserved strip on a banded map at all. false - a correct
        /// 10-cell strip measured from each level's own edges (which is what the band-local
        /// CloseToEdge patch produces once these overrides stand down).</summary>
        public const bool Removed = true;

        internal static bool Suppress(Map map)
        {
            return Removed && map != null && ABBands.Banded(map);
        }
    }

    /// <summary>The cell test. Vanilla already short-circuits this for pocket maps and for
    /// world layers that opt out via ignoreNoBuildArea, so returning false for a banded map
    /// is joining an existing list rather than inventing an exemption.</summary>
    [HarmonyPatch(typeof(GenGrid), nameof(GenGrid.InNoBuildEdgeArea))]
    public static class Patch_GenGrid_ABNoBuildEdgeArea
    {
        private static void Postfix(Map map, ref bool __result)
        {
            try
            {
                if (__result && ABNoBuildEdge.Suppress(map))
                {
                    __result = false;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>The rect test - a separate implementation on CellRect that hardcodes the 10
    /// rather than calling the cell version, so it needs its own override or multi-cell
    /// placement (and the monument marker) would still refuse.</summary>
    [HarmonyPatch(typeof(CellRect), nameof(CellRect.InNoBuildEdgeArea))]
    public static class Patch_CellRect_ABNoBuildEdgeArea
    {
        private static void Postfix(Map map, ref bool __result)
        {
            try
            {
                if (__result && ABNoBuildEdge.Suppress(map))
                {
                    __result = false;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>The dashed rectangle, suppressed at the draw call rather than at
    /// DrawMapEdgeLines: the same private helper also serves the map-boundary and no-ZONE
    /// edge overlays, and only this one is being retired.</summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawNoBuildEdgeLines))]
    public static class Patch_GenDraw_ABNoBuildEdgeLines
    {
        private static bool Prefix()
        {
            try
            {
                return !ABNoBuildEdge.Suppress(Find.CurrentMap);
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>
    /// The no-ZONE edge lines, kept but re-aimed.
    ///
    /// This overlay is not being removed - the 5-cell zone rule still applies, and thanks to
    /// the band-local CloseToEdge patch it now applies per level, which is correct. What is
    /// wrong is only the DRAWING: vanilla builds the rectangle from Find.CurrentMap.Size, so
    /// it boxes the whole stack. Suppressing the stray box is better than showing a line
    /// that describes a rule the game is no longer enforcing at that position.
    ///
    /// Drawing a correct per-band box instead would mean reimplementing DrawMapEdgeLines
    /// (it is private and takes only a distance), which is more surface area than the cue is
    /// worth; the designator still refuses correctly, which is the part that matters.
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawNoZoneEdgeLines))]
    public static class Patch_GenDraw_ABNoZoneEdgeLines
    {
        private static bool Prefix()
        {
            try
            {
                Map map = Find.CurrentMap;
                return map == null || !ABBands.Banded(map);
            }
            catch
            {
                return true;
            }
        }
    }
}
