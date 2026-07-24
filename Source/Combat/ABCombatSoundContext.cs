using System;
using HarmonyLib;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// "One map" audio for the sky &lt;-&gt; surface column.
    ///
    /// Verse.SoundDefHelper.CorrectContextNow hard-silences ANY in-map sound whose
    /// sourceMap != Find.CurrentMap. Because a cross-level fight lives on two linked
    /// maps at once, that silences almost all of it: from the sky you never hear the
    /// surface impacts / hits / pain / death / return fire, and from the surface you
    /// never hear the gunfight above. This prefix treats the current column's paired
    /// sky/surface level as the SAME audio context, so the whole column sounds like
    /// one map.
    ///
    /// Scope guards keep it safe and cheap:
    ///   - ONE-SHOTS ONLY (def.sustain == false). Sustainers stay vanilla-gated so we
    ///     never double a paired level's ambient loops (wind, rain, fire crackle).
    ///     Every combat sound - gunshots, projectile impacts, bullet cracks, flesh
    ///     hits, pain, death - is a one-shot, so combat audio is fully covered.
    ///   - Only fires for a sound on the current map's paired sky/surface level; every
    ///     other case (same-map, mapless, unpaired) falls straight through to vanilla,
    ///     so the hot path (same-map sounds) pays two reference compares.
    ///   - World view and the see-below kill switch both defer to vanilla.
    /// Fails open (returns true -> vanilla decides) on anything unexpected.
    /// </summary>
    [HarmonyPatch(typeof(SoundDefHelper), nameof(SoundDefHelper.CorrectContextNow))]
    internal static class Patch_SoundDefHelper_CorrectContextNow
    {
        private static bool Prefix(SoundDef def, Map sourceMap, ref bool __result)
        {
            try
            {
                // Sustainers stay vanilla (no doubled ambience); mapless + same-map
                // sounds are already handled correctly by vanilla's first guard.
                if (def == null || def.sustain || sourceMap == null)
                {
                    return true;
                }
                Map cur = Find.CurrentMap;
                if (cur == null || sourceMap == cur)
                {
                    return true;
                }
                if (WorldRendererUtility.WorldSelected || !ABGuard.On(ABGuard.Rendering))
                {
                    return true;
                }
                ABSettings s = ABMod.Settings;
                if (s == null || !s.showLiveBelow || !IsLinkedColumnLevel(cur, sourceMap))
                {
                    return true;
                }
                // sourceMap is the current column's paired level: the vanilla first
                // guard would have passed for a same-map sound, so evaluate the def's
                // own context exactly as vanilla does past that guard.
                switch (def.context)
                {
                    case SoundContext.MapOnly:
                        __result = Current.ProgramState == ProgramState.Playing && WorldRendererUtility.DrawingMap;
                        break;
                    case SoundContext.WorldOnly:
                        __result = false; // WorldSelected is false here
                        break;
                    default: // SoundContext.Any
                        __result = true;
                        break;
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "cross level sound context");
                return true;
            }
        }

        /// <summary>True when <paramref name="other"/> is the sky/surface partner of
        /// the viewed level. Both directions count: view the sky and hear the surface
        /// below, view the surface and hear the sky above - the column is one space.</summary>
        private static bool IsLinkedColumnLevel(Map cur, Map other)
        {
            LevelComp c = cur.Levels();
            if (c == null)
            {
                return false;
            }
            if (c.level == 1 && c.lowerMap == other)
            {
                return true;
            }
            return c.level == 0 && c.upperMap == other;
        }
    }
}
