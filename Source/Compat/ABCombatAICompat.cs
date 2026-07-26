using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// CAI 5000 - Advanced AI + Fog Of War (packageId Krkr.rule56) soft compat.
    ///
    /// CAI is really TWO systems. (1) Advanced AI is ALWAYS on and coexists with
    /// us for free: its Projectile patch class is empty and we spawn our
    /// cross-gap projectiles directly on the target map (never through the verbs
    /// it patches), so our cross-level combat driver and its per-map combat AI
    /// never touch. (2) Fog Of War is OPT-IN (Finder.Settings.FogOfWar_Enabled
    /// defaults false). When on, CAI hides anything the player's pawns do not
    /// currently see, per map, and ORs its fog into vanilla FogGrid.IsFogged -
    /// which our see-below gate already reads everywhere, so the below view
    /// HONORS CAI fog automatically (user decision: option B).
    ///
    /// This module adds the ONE thing that does not come for free: cross-level
    /// VISION. A pawn on the sky level looking down through open air should keep
    /// the surface below revealed (and reciprocally), so option B does not black
    /// out the surface whenever the colony is up top. We do that by calling CAI's
    /// own public, line-of-sight-correct reveal:
    ///     MapComponent_FogGrid.RevealSpot(IntVec3 cell, float radius, int duration, bool applyRangeMultiplier)
    /// on the OTHER level's fog grid at the looker's plumb cell. "Cross-level
    /// sight" here means fog-of-war VISION reveal only; we deliberately do NOT
    /// feed CAI's threat-AI enemy flags across the gap (our own cross-level
    /// combat already handles that, and that path is far more coupled).
    ///
    /// Discipline: REFLECTION ONLY. No CombatAI type ever appears in a field,
    /// base type, or method signature here (CombatAI.dll is not even a compile
    /// reference), so nothing forces those types to resolve when CAI is absent.
    /// Every entry point is gated on Active and fails open.
    /// </summary>
    internal static class ABCombatAICompat
    {
        private const string PackageId = "Krkr.rule56";

        private static bool? active;

        /// <summary>CAI 5000 loaded? Cached, postfix-insensitive (a local copy
        /// with a _steam suffix still counts).</summary>
        internal static bool Active
        {
            get
            {
                if (!active.HasValue)
                {
                    active = ABDetect.Active(PackageId);
                }
                return active.Value;
            }
        }

        // --- Resolved once, on first use, only when CAI is active. ---
        private static bool resolved;
        private static bool ready;

        private static Type fogGridType;                 // CombatAI.MapComponent_FogGrid
        private static MethodInfo revealSpotMethod;      // RevealSpot(IntVec3, float, int, bool)
        private static MethodInfo settingsGetter;        // static CombatAI.Finder.Settings getter
        private static FieldInfo settingsField;          // fallback if Settings is a field
        private static FieldInfo fogEnabledField;        // CombatAI.Settings.FogOfWar_Enabled

        /// <summary>Resolve the reflection surface once. Sets <see cref="ready"/>
        /// only when every member we need was found; a partial resolve leaves us
        /// inert (fail open). Never throws.</summary>
        private static void EnsureInit()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            if (!Active)
            {
                return;
            }
            try
            {
                fogGridType = AccessTools.TypeByName("CombatAI.MapComponent_FogGrid");
                if (fogGridType != null)
                {
                    revealSpotMethod = AccessTools.Method(fogGridType, "RevealSpot",
                        new[] { typeof(IntVec3), typeof(float), typeof(int), typeof(bool) });
                }

                Type finderType = AccessTools.TypeByName("CombatAI.Finder");
                Type settingsType = AccessTools.TypeByName("CombatAI.Settings");
                if (finderType != null)
                {
                    settingsGetter = AccessTools.PropertyGetter(finderType, "Settings");
                    if (settingsGetter == null)
                    {
                        settingsField = AccessTools.Field(finderType, "Settings");
                    }
                }
                if (settingsType != null)
                {
                    fogEnabledField = AccessTools.Field(settingsType, "FogOfWar_Enabled");
                }

                ready = fogGridType != null && revealSpotMethod != null
                    && (settingsGetter != null || settingsField != null) && fogEnabledField != null;

                if (!ready)
                {
                    ABLog.Dev("CAI 5000 detected but its fog API did not fully resolve; cross-level vision inert. "
                        + "fogGridType=" + (fogGridType != null) + " revealSpot=" + (revealSpotMethod != null)
                        + " settings=" + (settingsGetter != null || settingsField != null)
                        + " fogEnabledField=" + (fogEnabledField != null));
                }
                else
                {
                    ABLog.Dev("CAI 5000 (Krkr.rule56) detected; cross-level fog-of-war vision bridge ready.");
                }
            }
            catch (Exception e)
            {
                ready = false;
                ABLog.Dev("CAI 5000 fog API resolution threw (ignored, bridge inert): " + e.Message);
            }
        }

        /// <summary>True when CAI is active AND its fog API resolved. Cheap after
        /// the first call.</summary>
        internal static bool Ready
        {
            get
            {
                EnsureInit();
                return ready;
            }
        }

        private static object GetSettings()
        {
            try
            {
                if (settingsGetter != null)
                {
                    return settingsGetter.Invoke(null, null);
                }
                return settingsField?.GetValue(null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Is CAI's Fog Of War turned on right now? (It is opt-in and
        /// off by default.) The cross-level vision pass only runs when this is
        /// true; when it is false CAI does no hiding and there is nothing to
        /// reveal. Returns false on any failure.</summary>
        internal static bool FogEnabled
        {
            get
            {
                if (!Ready)
                {
                    return false;
                }
                try
                {
                    object s = GetSettings();
                    return s != null && (bool)fogEnabledField.GetValue(s);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Does <paramref name="map"/> carry CAI's fog component? (CAI
        /// adds it to every map, ours included.) Diagnostic-only.</summary>
        internal static bool HasFogComp(Map map)
        {
            if (!Ready || map == null)
            {
                return false;
            }
            try
            {
                return map.GetComponent(fogGridType) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reveal a line-of-sight cone around <paramref name="cell"/> on
        /// <paramref name="map"/> in CAI's fog, for <paramref name="durationTicks"/>
        /// ticks. This is how a looker on the OTHER level lights this map's
        /// surface: pass the looker's plumb cell and its sight radius. CAI shadow-
        /// casts the reveal itself (walls on this map block it), so the reveal is
        /// physically correct for what a plunging viewpoint at that cell can see.
        ///
        /// applyRangeMultiplier is passed false so the radius means exactly what
        /// we set (CAI's 1.8x fog range multiplier is for its own muzzle reveals).
        /// Returns true when the reveal was queued. Fails open + trips the guard
        /// on error so a bad frame cannot spam.
        /// </summary>
        internal static bool RevealOnMap(Map map, IntVec3 cell, float radius, int durationTicks)
        {
            if (!Ready || map == null || map.Disposed)
            {
                return false;
            }
            try
            {
                object comp = map.GetComponent(fogGridType);
                if (comp == null)
                {
                    return false;
                }
                revealSpotMethod.Invoke(comp, new object[] { cell, radius, durationTicks, false });
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.CombatAI, e, "CAI cross-level fog reveal");
                return false;
            }
        }

        /// <summary>Compact one-line status for the settings panel / self-test.</summary>
        internal static string StatusLine()
        {
            if (!Active)
            {
                return "CAI 5000: not loaded";
            }
            if (!Ready)
            {
                return "CAI 5000: loaded, fog API unresolved (inert)";
            }
            return "CAI 5000: ready, Fog Of War " + (FogEnabled ? "ON" : "off");
        }
    }
}
