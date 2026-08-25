using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §66 AIM-TIME PROBE. Field report: "aiming across levels seems to take longer than it
    /// should", with the pie observed filling ONCE (no visible restart). Warmup arithmetic
    /// is provably distance-free (TryStartCastOn: WarmupTime x AimingDelayFactor, read in
    /// full, 1.6), so if the report is real the stretch must live in one of exactly three
    /// places, and one log line per aim separates them:
    ///
    ///   1. tick delta &gt; expected ticks   -&gt; something inflates the countdown itself
    ///      (engine-side; hunt from the stance),
    ///   2. tick delta == expected, realtime &gt;&gt; ticks at current speed -&gt; the GAME is
    ///      slow while cross-level combat runs (TPS or forced speed), aim is innocent,
    ///   3. both equal -&gt; the aim is exactly as long as the weapon says and the perceived
    ///      delay is BEFORE or AFTER the pie (repositioning, invisible shots), which is a
    ///      different hunt entirely.
    ///
    /// ⚠ RULE 18: NEVER EXONERATE A SUSPECT ON ONE CLEAN SAMPLE. The user watched one pie
    /// fill once; the AIM RESTART line below re-checks that on EVERY aim in the session, so
    /// a rare scrapped warmup cannot hide behind a clean observation.
    ///
    /// ⚠ EVERYTHING IS GATED ON ABV2Debug.LogCombat BEFORE ANY WORK - both patches sit on
    /// hot verbs, and §66b is the receipt for what an ungated diagnostic costs. With the
    /// toggle off each patch is one static bool read.
    ///
    /// Same-band aims are logged too, deliberately: the comparison baseline arrives in the
    /// same log with the same clock, instead of being remembered from a different fight.
    /// </summary>
    public static class ABCombatAimProbe
    {
        public struct AimStart
        {
            public int startTick;

            public float realtime;

            public int expectedTicks;

            public bool crossBand;
        }

        /// <summary>Keyed on the pawn, not the verb: a pawn warms up one verb at a time,
        /// and the pawn id is what both ends of the probe can cheaply reach. Stale entries
        /// (a scrapped aim that never restarts, a pawn that dies mid-warmup) are bounded by
        /// pawn count and overwritten by the next aim, so no sweep is needed.</summary>
        public static readonly Dictionary<int, AimStart> live = new Dictionary<int, AimStart>();
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TryStartCastOn), new[]
    {
        typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool),
        typeof(bool), typeof(bool)
    })]
    public static class Patch_Verb_ABAimProbeStart
    {
        private static void Postfix(Verb __instance, LocalTargetInfo castTarg, bool __result)
        {
            if (!ABV2Debug.LogCombat || !__result)
            {
                return;
            }
            try
            {
                if (__instance == null || !__instance.CasterIsPawn)
                {
                    return;
                }
                Pawn pawn = __instance.CasterPawn;
                if (pawn == null || !pawn.Spawned)
                {
                    return;
                }
                float warmup = __instance.WarmupTime;
                if (warmup <= 0f)
                {
                    return; // instant cast: there is no pie to time
                }
                Map map = pawn.Map;
                ABBandMap bands = map != null ? ABBands.CompOf(map) : null;
                bool cross = bands != null && bands.Banded
                    && bands.BandOf(pawn.Position) != bands.BandOf(castTarg.Cell);
                float factor = pawn.GetStatValue(StatDefOf.AimingDelayFactor);
                int expected = (warmup * factor).SecondsToTicks();
                int now = Find.TickManager.TicksGame;
                if (ABCombatAimProbe.live.TryGetValue(pawn.thingIDNumber,
                        out ABCombatAimProbe.AimStart prev))
                {
                    // A start with no DONE in between means the previous warmup was scrapped
                    // - the flicker §66 first suspected. If this line ever prints, "fills
                    // once" was a clean sample, not the whole story.
                    ABV2Debug.Combat("AIM RESTART " + pawn.LabelShortCap
                        + " - previous aim scrapped after " + (now - prev.startTick) + "/"
                        + prev.expectedTicks + " ticks"
                        + (prev.crossBand ? " (was cross-band)" : " (was same-band)"));
                }
                ABCombatAimProbe.live[pawn.thingIDNumber] = new ABCombatAimProbe.AimStart
                {
                    startTick = now,
                    realtime = Time.realtimeSinceStartup,
                    expectedTicks = expected,
                    crossBand = cross,
                };
                ABV2Debug.Combat("AIM START " + pawn.LabelShortCap
                    + (cross ? " CROSS-BAND " : " same-band ") + pawn.Position + " -> "
                    + castTarg.Cell + ", warmup " + expected + " ticks (" + warmup.ToString("0.00")
                    + "s x factor " + factor.ToString("0.00") + "), speed "
                    + Find.TickManager.TickRateMultiplier.ToString("0.#") + "x");
            }
            catch
            {
                // A probe must never be able to break the thing it measures.
            }
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.WarmupComplete))]
    public static class Patch_Verb_ABAimProbeDone
    {
        private static void Postfix(Verb __instance)
        {
            if (!ABV2Debug.LogCombat)
            {
                return;
            }
            try
            {
                if (__instance == null || !__instance.CasterIsPawn)
                {
                    return;
                }
                Pawn pawn = __instance.CasterPawn;
                if (pawn == null
                    || !ABCombatAimProbe.live.TryGetValue(pawn.thingIDNumber,
                        out ABCombatAimProbe.AimStart s))
                {
                    return;
                }
                ABCombatAimProbe.live.Remove(pawn.thingIDNumber);
                int dt = Find.TickManager.TicksGame - s.startTick;
                float rt = Time.realtimeSinceStartup - s.realtime;
                // What this tick count SHOULD have cost in realtime at the speed the game is
                // running right now. Speed can change mid-aim (combat forces 1x), so this is
                // a reading aid, not a second clock: the verdict fields are dt vs expected
                // and rt vs tickTrue.
                float speed = Mathf.Max(Find.TickManager.TickRateMultiplier, 0.01f);
                float tickTrue = dt / (60f * speed);
                ABV2Debug.Combat("AIM DONE " + pawn.LabelShortCap
                    + (s.crossBand ? " CROSS-BAND" : " same-band") + " - " + dt
                    + " ticks (expected " + s.expectedTicks + "), realtime " + rt.ToString("0.00")
                    + "s (tick-true at current speed would be " + tickTrue.ToString("0.00")
                    + "s)");
            }
            catch
            {
            }
        }
    }
}
