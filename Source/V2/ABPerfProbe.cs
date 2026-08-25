using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// PERF COUNTERS for the two survey-identified unknowns (§32f request count, §34f flood
    /// cost) plus the render-side burst axis (mirrored regens, below pawn pass). Everything
    /// here is OBSERVE-ONLY: no counter may sit on a decision path, and no patch in this
    /// file targets a per-frame method - only per-regen, per-search and per-request ones.
    ///
    /// ⚠ WHY THERE IS NO CanReach COUNTER. CanReach is the one genuinely hot method in the
    /// set, and a Harmony patch costs its dispatch even when the body is one increment
    /// (§14: a guarded patch still costs full dispatch). The funnels ABOVE it - the BFS
    /// worker, the bill helper, the request entries - are counted instead; between them
    /// they bound what CanReach can be doing.
    ///
    /// ⚠ COUNTERS ARE PLAIN FIELDS, MAXES ARE RACY ON PURPOSE. Regenerate can run inside a
    /// long-event thread during load; a torn max costs one lost sample of a diagnostic,
    /// which is cheaper than putting Interlocked on paths that are main-thread 99% of the
    /// time. Totals use Interlocked where a tear would corrupt (longs on x86 don't tear on
    /// aligned writes in practice, but the add itself can lose increments).
    /// </summary>
    public static class ABPerfStats
    {
        public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

        public static double MsOf(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static string Ms(long ticks) => MsOf(ticks).ToString("0.00");

        private static string Avg(long ticks, long n) => n == 0 ? "-" : MsOf(ticks / n).ToString("0.000");

        // ---- path side -------------------------------------------------------

        public static int syncRequests;

        public static int asyncRequests;

        private static int reqTick = -1;

        private static int reqThisTick;

        public static int reqMaxPerTick;

        /// <summary>Every path request seen on a banded map, from both entries. Called at
        /// the TOP of the two request patches, before the cross-band decision, so rejected
        /// and permitted requests are both counted.</summary>
        public static void NoteRequest(Map map, bool sync)
        {
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            if (sync)
            {
                syncRequests++;
            }
            else
            {
                asyncRequests++;
            }
            int t = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (t != reqTick)
            {
                reqTick = t;
                reqThisTick = 0;
            }
            reqThisTick++;
            if (reqThisTick > reqMaxPerTick)
            {
                reqMaxPerTick = reqThisTick;
            }
        }

        // BFS funnel (GenClosest.RegionwiseBFSWorker).
        public static int bfsCalls;

        public static int bfsHits;

        /// <summary>Null result with regionsSeen under the cap: the reachable set was
        /// exhausted, vanilla SKIPS the global fallback. The cheap kind of miss.</summary>
        public static int bfsExhausted;

        /// <summary>Null result AT the cap: vanilla proceeds to ClosestThing_Global with a
        /// CanReach gate per candidate. The expensive kind of miss - §B4(b)'s number.</summary>
        public static int bfsCapped;

        public static long bfsRegionsSum;

        // Bill ingredient search (WorkGiver_DoBill.TryFindBestIngredientsHelper).
        public static int billCalls;

        public static int billFails;

        public static int billUnbounded;

        public static long billTicksTotal;

        public static long billTicksMax;

        // Island floods (ABBandComponents.Build), fed inline from BandFor.
        public static int floodCount;

        public static long floodTicksTotal;

        public static long floodTicksMax;

        public static void NoteFlood(long elapsed)
        {
            floodCount++;
            floodTicksTotal += elapsed;
            if (elapsed > floodTicksMax)
            {
                floodTicksMax = elapsed;
            }
        }

        public static string PathReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB2 PATH PERF EXTRAS");
            sb.AppendLine("  requests (banded maps): " + syncRequests + " sync + "
                + asyncRequests + " async; max " + reqMaxPerTick + " in one tick");
            long avgRegions = bfsCalls == 0 ? 0 : bfsRegionsSum / bfsCalls;
            sb.AppendLine("  work-scan BFS: " + bfsCalls + " calls, " + bfsHits + " hits, "
                + bfsExhausted + " exhausted-under-cap (global SKIPPED), "
                + bfsCapped + " cap-hits (global fallback RAN); avg regions seen " + avgRegions);
            sb.AppendLine("  bill ingredient search: " + billCalls + " calls ("
                + billUnbounded + " unbounded-radius), " + billFails + " failed; "
                + Ms(billTicksTotal) + " ms total, worst " + Ms(billTicksMax)
                + ", avg " + Avg(billTicksTotal, billCalls));
            sb.AppendLine("  island floods: " + floodCount + " rebuilds, "
                + Ms(floodTicksTotal) + " ms total, worst " + Ms(floodTicksMax)
                + ", avg " + Avg(floodTicksTotal, floodCount));
            return sb.ToString();
        }

        public static void ResetPath()
        {
            syncRequests = 0;
            asyncRequests = 0;
            reqTick = -1;
            reqThisTick = 0;
            reqMaxPerTick = 0;
            bfsCalls = 0;
            bfsHits = 0;
            bfsExhausted = 0;
            bfsCapped = 0;
            bfsRegionsSum = 0;
            billCalls = 0;
            billFails = 0;
            billUnbounded = 0;
            billTicksTotal = 0;
            billTicksMax = 0;
            floodCount = 0;
            floodTicksTotal = 0;
            floodTicksMax = 0;
        }

        // ---- render side -----------------------------------------------------

        // Below dynamic pass (ABBelowDynamicDraw.DrawBelowPawns), fed inline.
        public static int belowFrames;

        public static long belowTicksTotal;

        public static long belowTicksMax;

        public static long belowConsideredSum;

        public static long belowDrawnSum;

        public static long belowRealtimeSum;

        public static int belowDrawnMaxPerFrame;

        public static void NoteBelowPass(int considered, int drawn, int realtime, long elapsed)
        {
            belowFrames++;
            belowConsideredSum += considered;
            belowDrawnSum += drawn;
            belowRealtimeSum += realtime;
            belowTicksTotal += elapsed;
            if (elapsed > belowTicksMax)
            {
                belowTicksMax = elapsed;
            }
            if (drawn > belowDrawnMaxPerFrame)
            {
                belowDrawnMaxPerFrame = drawn;
            }
        }

        // Below overlay pass (Patch_ThingOverlays_ABBelow), fed inline.
        public static int overlayPasses;

        public static long overlayTicksTotal;

        public static long overlayTicksMax;

        public static long overlayScannedSum;

        public static void NoteOverlay(int scanned, long elapsed)
        {
            overlayPasses++;
            overlayScannedSum += scanned;
            overlayTicksTotal += elapsed;
            if (elapsed > overlayTicksMax)
            {
                overlayTicksMax = elapsed;
            }
        }

        // Dirty mirror (Patch_MapDrawer_ABMirrorDirtyUp), fed inline.
        public static int mirrorCalls;

        public static long mirrorSteps;

        private static int mirrorTick = -1;

        private static int mirrorStepsThisTick;

        public static int mirrorMaxStepsPerTick;

        public static void NoteMirror(int steps)
        {
            mirrorCalls++;
            mirrorSteps += steps;
            int t = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (t != mirrorTick)
            {
                mirrorTick = t;
                mirrorStepsThisTick = 0;
            }
            mirrorStepsThisTick += steps;
            if (mirrorStepsThisTick > mirrorMaxStepsPerTick)
            {
                mirrorMaxStepsPerTick = mirrorStepsThisTick;
            }
        }

        // Section layer regenerations, attributed per layer type by the Harmony patch below.
        public const int SlotThingsGeneral = 0;

        public const int SlotThingsOther = 1;

        public const int SlotTerrainVanilla = 2;

        public const int SlotBelowV2 = 3;

        public const int SlotMountainCap = 4;

        public const int SlotLighting = 5;

        public const int SlotShadows = 6;

        public const int SlotEdgeShadows = 7;

        public const int SlotSnow = 8;

        public const int SlotBelowWatergen = 9;

        public const int LayerSlotCount = 10;

        public static readonly string[] LayerNames =
        {
            "ThingsGeneral (vanilla atlas)",
            "Things other (vanilla)",
            "Terrain (vanilla, incl. watergen)",
            "AB BelowV2",
            "AB MountainCap",
            "AB BelowLighting",
            "AB BelowShadows",
            "AB BelowEdgeShadows",
            "AB BelowSnow",
            "AB BelowWatergen",
        };

        public static readonly long[] layerRegens = new long[LayerSlotCount];

        public static readonly long[] layerTicksTotal = new long[LayerSlotCount];

        public static readonly long[] layerTicksMax = new long[LayerSlotCount];

        public static void NoteLayerRegen(int slot, long elapsed)
        {
            if (slot < 0 || slot >= LayerSlotCount)
            {
                return;
            }
            layerRegens[slot]++;
            layerTicksTotal[slot] += elapsed;
            if (elapsed > layerTicksMax[slot])
            {
                layerTicksMax[slot] = elapsed;
            }
        }

        public static string RenderReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB2 RENDER PERF REPORT");
            sb.AppendLine("  below dynamic pass: " + belowFrames + " frames, "
                + Ms(belowTicksTotal) + " ms total, avg " + Avg(belowTicksTotal, belowFrames)
                + ", worst frame " + Ms(belowTicksMax));
            sb.AppendLine("    pawns below-band considered " + belowConsideredSum
                + ", drawn " + belowDrawnSum + " (max " + belowDrawnMaxPerFrame
                + " in one frame), realtime things drawn " + belowRealtimeSum);
            // ⚠ READ THESE THREE LINES TOGETHER, IN ORDER.
            //   blitted   = the cache actually fired (measured at the draw call).
            //   permitted = we allowed it; permitted MINUS blitted is VANILLA's veto share
            //               (zoom threshold, carrying, crawling, animation, hediff material)
            //               and is the only way to see whether the 4K zoom gate is binding.
            //   vetoed    = our own gates, broken out by reason below. None of them are
            //               failures: gear and stair animation are fidelity decisions, and
            //               the budget lines are deliberate spike control.
            long verdicts = ABBelowRenderCache.permittedSum + ABBelowRenderCache.vetoedSum;
            sb.AppendLine("    below pawn cache: " + ABBelowRenderCache.blittedSum
                + " blitted, " + ABBelowRenderCache.permittedSum + " permitted, "
                + ABBelowRenderCache.vetoedSum + " vetoed by us"
                + (verdicts > 0
                    ? " (" + ((float)ABBelowRenderCache.blittedSum / verdicts).ToStringPercent()
                        + " of all below draws blitted)"
                    : ""));
            sb.AppendLine("      vanilla vetoes after we permitted: "
                + Math.Max(0L, ABBelowRenderCache.permittedSum - ABBelowRenderCache.blittedSum)
                + "  (zoom threshold / carrying / crawling / animation)");
            sb.AppendLine("      our vetoes: gear " + ABBelowRenderCache.vetoGear
                + ", non-humanlike " + ABBelowRenderCache.vetoNonHumanlike
                + ", stair anim " + ABBelowRenderCache.vetoStairAnim
                + ", cold-fill budget " + ABBelowRenderCache.vetoColdBudget
                + ", rebake budget " + ABBelowRenderCache.vetoRebakeBudget
                + ", disabled " + ABBelowRenderCache.vetoOff);
            sb.AppendLine("  below overlay pass: " + overlayPasses + " repaints, scanned "
                + overlayScannedSum + " overlay things, " + Ms(overlayTicksTotal)
                + " ms total, worst " + Ms(overlayTicksMax));
            // The THIRD emitter (ABBelowFlecks). `considered` counts flecks that survived the
            // view-band gate; `mirrored` counts those actually visible from above. considered
            // staying 0 while looking DOWN a level means the gate is wrong, not that the map
            // is quiet - that distinction is the whole reason both numbers are printed.
            sb.AppendLine("  below flecks: " + Patch_FleckManager_ABMirrorBelow.mirrored
                + " mirrored of " + Patch_FleckManager_ABMirrorBelow.considered
                + " considered (0/0 = never viewed a band from above)");
            sb.AppendLine("  dirty mirror: " + mirrorCalls + " mirrored dirties, "
                + mirrorSteps + " section-cells stepped, max " + mirrorMaxStepsPerTick
                + " steps in one tick");
            sb.AppendLine("  steady-effects melt patch (§36e-C1): "
                + (ABBandSnowPatchLifetime.Installed ? "INSTALLED" : "not installed")
                + " (installs " + ABBandSnowPatchLifetime.installs
                + ", uninstalls " + ABBandSnowPatchLifetime.uninstalls + ")");
            sb.AppendLine("  layer regenerations (banded maps only):");
            for (int i = 0; i < LayerSlotCount; i++)
            {
                sb.AppendLine("    " + LayerNames[i].PadRight(34)
                    + " n=" + layerRegens[i]
                    + "  total " + Ms(layerTicksTotal[i]) + " ms"
                    + "  avg " + Avg(layerTicksTotal[i], layerRegens[i])
                    + "  worst " + Ms(layerTicksMax[i]));
            }
            return sb.ToString();
        }

        public static void ResetRender()
        {
            belowFrames = 0;
            belowTicksTotal = 0;
            belowTicksMax = 0;
            belowConsideredSum = 0;
            belowDrawnSum = 0;
            belowRealtimeSum = 0;
            belowDrawnMaxPerFrame = 0;
            overlayPasses = 0;
            overlayTicksTotal = 0;
            overlayTicksMax = 0;
            overlayScannedSum = 0;
            mirrorCalls = 0;
            mirrorSteps = 0;
            mirrorTick = -1;
            mirrorStepsThisTick = 0;
            mirrorMaxStepsPerTick = 0;
            // The cache verdict counters live on ABBelowRenderCache but reset with the rest
            // of the render side: a split reset is how you end up comparing a fresh blit
            // count against a cumulative veto count and concluding something false.
            ABBelowRenderCache.permittedSum = 0;
            ABBelowRenderCache.blittedSum = 0;
            ABBelowRenderCache.vetoedSum = 0;
            ABBelowRenderCache.vetoOff = 0;
            ABBelowRenderCache.vetoNonHumanlike = 0;
            ABBelowRenderCache.vetoStairAnim = 0;
            ABBelowRenderCache.vetoGear = 0;
            ABBelowRenderCache.vetoColdBudget = 0;
            ABBelowRenderCache.vetoRebakeBudget = 0;
            for (int i = 0; i < LayerSlotCount; i++)
            {
                layerRegens[i] = 0;
                layerTicksTotal[i] = 0;
                layerTicksMax[i] = 0;
            }
        }
    }

    /// <summary>
    /// Counts the regionwise BFS funnel's outcomes. RegionwiseBFSWorker is called per
    /// work-scan per pawn think - frequent but nowhere near per-frame, and the postfix is
    /// four increments behind a banded check.
    ///
    /// ⚠ regionsSeen IS AN OUT PARAM ON THE ORIGINAL and is bound here BY VALUE, which
    /// Harmony resolves to "the value after the original ran" - exactly what we want.
    /// </summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.RegionwiseBFSWorker))]
    public static class Patch_GenClosest_ABPerfBFS
    {
        private static void Postfix(Map map, int maxRegions, int regionsSeen, Thing __result)
        {
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            ABPerfStats.bfsCalls++;
            ABPerfStats.bfsRegionsSum += regionsSeen;
            if (__result != null)
            {
                ABPerfStats.bfsHits++;
            }
            else if (regionsSeen >= maxRegions)
            {
                ABPerfStats.bfsCapped++;
            }
            else
            {
                ABPerfStats.bfsExhausted++;
            }
        }
    }

    /// <summary>
    /// Times the bill ingredient search - the one selection funnel that runs its own region
    /// BFS with a 99999 cap, so an unsatisfiable bill floods every region of the connected
    /// component (all bands, through the wormholes). Vanilla throttles the re-search per
    /// bill (nextTickToSearchForIngredients), so what this measures is the cost per flood
    /// and how often they actually happen - the §B6 decision input.
    ///
    /// ⚠ THE UNBOUNDED TEST MIRRORS VANILLA'S OWN: the helper treats a radius within 1 of
    /// 999 as "no radius" (`Math.Abs(999f - searchRadius) >= 1f` guards the bounded branch),
    /// so this uses the identical comparison rather than a >= that would misclassify 998.5.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_WorkGiverDoBill_ABPerfBills
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper");
        }

        private static void Prefix(out long __state)
        {
            __state = ABPerfStats.Now();
        }

        private static void Postfix(bool __result, Pawn pawn, float searchRadius, long __state)
        {
            Map map = pawn?.MapHeld;
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            long elapsed = ABPerfStats.Now() - __state;
            ABPerfStats.billCalls++;
            if (!__result)
            {
                ABPerfStats.billFails++;
            }
            if (Math.Abs(999f - searchRadius) < 1f)
            {
                ABPerfStats.billUnbounded++;
            }
            ABPerfStats.billTicksTotal += elapsed;
            if (elapsed > ABPerfStats.billTicksMax)
            {
                ABPerfStats.billTicksMax = elapsed;
            }
        }
    }

    /// <summary>
    /// Times section layer regenerations - vanilla's Things (the 1.6 atlas bake) and
    /// Terrain, plus every AB below layer. This is the §B1 decision input: how much of the
    /// regen volume on a banded map is vanilla layers rebuilt by MIRRORED dirties for
    /// content they cannot render. The stress actions isolate causality (a dirty storm on
    /// an untouched band means every above-band regen it triggers is mirror-caused); these
    /// counters supply magnitude.
    ///
    /// ⚠ Regenerate IS NOT A HOT METHOD - it runs per dirty section, not per frame - so a
    /// Harmony prefix/postfix pair here is cheap. Do not extend this pattern to DrawLayer,
    /// which runs per section per frame.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SectionLayers_ABPerfRegen
    {
        private static readonly AccessTools.FieldRef<SectionLayer, Section> SectionRef =
            AccessTools.FieldRefAccess<SectionLayer, Section>("section");

        private static IEnumerable<MethodBase> TargetMethods()
        {
            // Vanilla: the concrete Regenerate declarations. Subclasses that do not
            // override (ThingsGeneral, ThingsPowerGrid, vanilla Watergen) route through
            // their base's declared method, so two patches cover the whole family.
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_Things), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_Terrain), "Regenerate");
            // Ours: every below layer declares its own.
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowV2), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABMountainCap), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowLighting), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowShadows), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowEdgeShadows), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowSnow), "Regenerate");
            yield return AccessTools.DeclaredMethod(typeof(SectionLayer_ABBelowWatergen), "Regenerate");
        }

        private static int SlotFor(SectionLayer layer)
        {
            switch (layer)
            {
                case SectionLayer_ABBelowV2 _: return ABPerfStats.SlotBelowV2;
                case SectionLayer_ABMountainCap _: return ABPerfStats.SlotMountainCap;
                case SectionLayer_ABBelowLighting _: return ABPerfStats.SlotLighting;
                case SectionLayer_ABBelowShadows _: return ABPerfStats.SlotShadows;
                case SectionLayer_ABBelowEdgeShadows _: return ABPerfStats.SlotEdgeShadows;
                case SectionLayer_ABBelowSnow _: return ABPerfStats.SlotSnow;
                case SectionLayer_ABBelowWatergen _: return ABPerfStats.SlotBelowWatergen;
                case SectionLayer_ThingsGeneral _: return ABPerfStats.SlotThingsGeneral;
                case SectionLayer_Things _: return ABPerfStats.SlotThingsOther;
                case SectionLayer_Terrain _: return ABPerfStats.SlotTerrainVanilla;
                default: return -1;
            }
        }

        private static void Prefix(out long __state)
        {
            __state = ABPerfStats.Now();
        }

        private static void Postfix(SectionLayer __instance, long __state)
        {
            try
            {
                Map map = SectionRef(__instance)?.map;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                ABPerfStats.NoteLayerRegen(SlotFor(__instance), ABPerfStats.Now() - __state);
            }
            catch
            {
                // A diagnostic must never break a regeneration.
            }
        }
    }
}
