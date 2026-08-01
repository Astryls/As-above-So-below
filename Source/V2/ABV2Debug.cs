using Verse;

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

        /// <summary>The below-band WATER DEPTH pass (SectionLayer_ABBelowWatergen). Off makes
        /// water seen from an upper level vanish again, which is the confirmation that the
        /// depth pass - not masking or translation - is what was missing.</summary>
        public static bool DrawBelowWater = true;

        // NOTE: BandWaterGlobals lived here - a toggle for republishing the water shader's
        // _MapSize as one Slot instead of the whole stack. It was DISPROVED in one launch
        // (banding the global is what makes water run north-south; vanilla's value is
        // correct) and both the toggle and the patch behind it are gone. The A/B toggle is
        // the reason that cost one launch instead of a release - build the discriminator
        // INTO a change whose mechanism you cannot read.

        /// <summary>Traces every step of a cross-band transit. Off by default (it is noisy
        /// and per-move); the stairs order is the one flow where seeing each step beats
        /// reasoning about it.</summary>
        public static bool LogTransit;

        public static void Transit(string msg)
        {
            if (LogTransit)
            {
                Log.Warning(ABLog.Tag + " TRANSIT: " + msg);
            }
        }

        /// <summary>Traces cross-band shots. Same reason as transit logging: reasoning about
        /// which patch did or did not fire has repeatedly lost to just measuring it.</summary>
        public static bool LogCombat;

        public static void Combat(string msg)
        {
            if (LogCombat)
            {
                Log.Warning(ABLog.Tag + " COMBAT: " + msg);
            }
        }

        public static string StateSummary()
        {
            // massFieldFade lives on the cap layer rather than here, but it is a RENDERING
            // toggle a tester can leave flipped mid-session, and an UNSTAMPED toggle is
            // exactly how a later reading gets poisoned - the reason the bisect flags are
            // stamped at all.
            return "terrain=" + DrawBelowTerrain
                + " things=" + DrawBelowThings
                + " airMask=" + DrawBelowAirMask
                + " lighting=" + DrawBelowLighting
                + " belowWater=" + DrawBelowWater
                + " massFieldFade=" + SectionLayer_ABMountainCap.MassFieldFadeEnabled;
        }
    }
}
