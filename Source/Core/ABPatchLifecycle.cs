using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Demand-driven Harmony patch lifecycle: a patch that is not needed is not applied,
    /// rather than applied-and-early-returning.
    ///
    /// WHY. A guard clause cannot take a patch below its dispatch cost. The band
    /// temperature postfix rides GenTemperature.TryGetTemperatureForCell - 174k-341k
    /// calls per profiler window through Thing.AmbientTemperature alone - and on a save
    /// with no banded map (or no configured offsets) every one of those calls paid
    /// Harmony dispatch for a guaranteed early-out. Unpatching makes them free.
    ///
    /// CURRENT ENROLLMENT: ONLY Patch_GenTemperature_ABBandOffset. Its [HarmonyPatch]
    /// attribute was removed, so HarmonyBoot's PatchAll pass skips it and this class owns
    /// it end to end. Enrolling more patches (GetBiomeAt is the obvious candidate) means
    /// auditing THEIR demand timing first - biome must answer during map generation,
    /// which is a stricter contract than temperature's.
    ///
    /// DEMAND for the temperature patch, mirroring ABBandEnv.FromTable's tier rule via
    /// ABBandEnv.AnyOffsetConfigured (one source of truth - rule 62: derive, don't copy):
    ///   master toggle ON, AND at least one banded map whose effective sky or deep
    ///   offset table contains a nonzero entry.
    ///
    /// TIMING RULES.
    ///  - Patch/unpatch runs ONLY on the main thread, and never from inside a patch
    ///    body. Callers on loader threads (map generation, FinalizeInit) are deferred via
    ///    LongEventHandler.ExecuteWhenFinished, which lands on the main thread before
    ///    gameplay resumes. Cost of the deferral: a handful of generation-time
    ///    temperature reads answer without offsets; at the shipped defaults (all zero)
    ///    that is no difference at all, and with configured offsets it is a one-time
    ///    seeding nuance that the live patch corrects from the first real tick.
    ///  - APPLY is immediate when demand appears (correctness first). UNAPPLY waits for
    ///    two consecutive idle sweeps (hysteresis), so a map transition cannot flap the
    ///    patch - each apply is a re-JIT of the target.
    ///  - A failed APPLY warns once and retries every sweep (a dead temperature feature
    ///    must not be silent - rule 33). A failed UNAPPLY latches the lifecycle broken
    ///    with the patch still applied: the pre-lifecycle state, safe by definition.
    /// </summary>
    public static class ABPatchLifecycle
    {
        private const int SweepIntervalTicks = 250;

        private const int IdleSweepsBeforeUnpatch = 2;

        private static readonly MethodBase TempTarget =
            AccessTools.Method(typeof(GenTemperature),
                nameof(GenTemperature.TryGetTemperatureForCell));

        private static readonly MethodInfo TempPostfix =
            AccessTools.Method(typeof(Patch_GenTemperature_ABBandOffset), "Postfix");

        private static bool applied;

        private static bool unpatchBroken;

        private static bool applyFailWarned;

        private static int consecutiveIdleSweeps;

        private static int nextSweepTick;

        /// <summary>Settings-panel readout (visibility of system status, as ABGuard).</summary>
        public static bool Applied => applied;

        /// <summary>Elapsed-time sweep, not modulo: a time-skip merely delays the next
        /// check instead of sleeping an interval. Registered via [ABGameTick].</summary>
        [ABGameTick(90)]
        public static void Tick()
        {
            int now = Find.TickManager.TicksGame;
            if (now < nextSweepTick && nextSweepTick - now <= SweepIntervalTicks)
            {
                return;
            }
            nextSweepTick = now + SweepIntervalTicks;
            Recheck("sweep");
        }

        /// <summary>Re-evaluate demand and converge the patch state. Safe to call from
        /// any thread; off-main-thread calls are deferred, never dropped.</summary>
        public static void Recheck(string reason)
        {
            if (TempTarget == null || TempPostfix == null)
            {
                return; // resolved never; the boot warning already named it
            }
            if (!UnityData.IsInMainThread)
            {
                LongEventHandler.ExecuteWhenFinished(() => Recheck(reason + "-deferred"));
                return;
            }
            bool want;
            try
            {
                want = DemandExists();
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " patch lifecycle demand check threw; leaving state as-is: " + e,
                    193480217);
                return;
            }
            if (want)
            {
                consecutiveIdleSweeps = 0;
                if (!applied)
                {
                    Apply(reason);
                }
                return;
            }
            if (!applied || unpatchBroken)
            {
                return;
            }
            if (++consecutiveIdleSweeps >= IdleSweepsBeforeUnpatch)
            {
                Unapply(reason);
            }
        }

        /// <summary>True when the band temperature postfix has work anywhere: master
        /// toggle on, and any banded map whose EFFECTIVE offsets (snapshot, else live
        /// settings - ABBandEnv.AnyOffsetConfigured mirrors FromTable exactly) are
        /// nonzero. No game, no maps, no demand.</summary>
        private static bool DemandExists()
        {
            ABSettings s = ABMod.Settings;
            if (s != null && !s.bandTemperatureOffsets)
            {
                return false;
            }
            if (Current.Game == null)
            {
                return false;
            }
            var maps = Find.Maps;
            if (maps == null)
            {
                return false;
            }
            for (int i = 0; i < maps.Count; i++)
            {
                ABBandMap bands = ABBands.CompOf(maps[i]);
                if (bands != null && bands.Banded && ABBandEnv.AnyOffsetConfigured(bands))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Apply(string reason)
        {
            try
            {
                HarmonyBoot.Harmony.Patch(TempTarget, postfix: new HarmonyMethod(TempPostfix));
                applied = true;
                applyFailWarned = false;
                ABLog.Dev("band temperature patch applied (" + reason + ").");
            }
            catch (Exception e)
            {
                // Warn once, retry every sweep: silence here would be a temperature
                // feature that died with no log line to connect to it.
                if (!applyFailWarned)
                {
                    applyFailWarned = true;
                    Log.Warning(ABLog.Tag + " could not apply the band temperature patch"
                        + " (will keep retrying): " + e);
                }
            }
        }

        private static void Unapply(string reason)
        {
            try
            {
                HarmonyBoot.Harmony.Unpatch(TempTarget, TempPostfix);
                applied = false;
                consecutiveIdleSweeps = 0;
                ABLog.Dev("band temperature patch removed - no demand (" + reason + ").");
            }
            catch (Exception e)
            {
                // Latched applied: the patch stays installed and self-gates, which is
                // exactly the pre-lifecycle behaviour. Safe, and said once.
                unpatchBroken = true;
                Log.Warning(ABLog.Tag + " could not remove the idle band temperature patch;"
                    + " it stays applied and self-gates: " + e);
            }
        }
    }
}
