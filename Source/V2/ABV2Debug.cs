namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 render bisect switches.
    ///
    /// The below-shadow geometry is provably present (finalized SunShadowFade submesh, 220
    /// verts, render queue 3175 - above terrain at ~2340 and plants at 2900) and still does
    /// not draw. Emission, ordering and bounds are therefore all ruled out, which leaves
    /// something else in the see-below stack occluding it.
    ///
    /// These let the pieces be switched off independently at runtime so one launch
    /// identifies the occluder instead of another round of theorising.
    /// </summary>
    public static class ABV2Debug
    {
        public static bool DrawBelowTerrain = true;

        public static bool DrawBelowThings = true;

        public static bool DrawBelowAirMask = true;

        public static bool DrawBelowLighting = true;

        public static string StateSummary()
        {
            return "terrain=" + DrawBelowTerrain
                + " things=" + DrawBelowThings
                + " airMask=" + DrawBelowAirMask
                + " lighting=" + DrawBelowLighting;
        }
    }
}
