using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-genstep timing for BANDED map generation.
    ///
    /// "Map gen is ~3x slower" has sat in the notes unprofiled since V2 began, and this
    /// session's rule held every time it was tested: every unmeasured hot-spot guess was
    /// wrong. Before touching generation ordering or scoping any genstep, this captures
    /// where the time actually goes.
    ///
    /// HOW: vanilla already brackets every genstep with
    ///     DeepProfiler.Start("GenStep - " + def) ... DeepProfiler.End()
    /// inside MapGenerator.GenerateContentsIntoMap. Those calls run whether or not the
    /// profiler itself is enabled, so postfixing Start/End and pairing the OUTERMOST
    /// bracket per genstep gives exact per-step wall time with no genstep touched.
    ///
    /// Details that matter:
    ///  - ARMED ONLY during banded generation. Outside the window the patches cost one
    ///    static bool check; vanilla maps never arm.
    ///  - DEPTH COUNTER, not naive pairing: gensteps call DeepProfiler themselves, so
    ///    nested Start/End must not close the genstep's bracket early. Only depth 0->1
    ///    starts a measurement and 1->0 ends it.
    ///  - THREAD-PINNED to the generation thread. DeepProfiler is a global; another
    ///    thread's Start during the window would corrupt the depth count.
    ///  - Reported via ONE self-contained Log.Warning (the monitor folds separate calls),
    ///    emitted after carve so the table includes our own costs alongside vanilla's.
    /// </summary>
    public static class ABGenProfile
    {
        private static bool armed;

        private static int genThreadId;

        private static int depth;

        private static string currentLabel;

        private static readonly Stopwatch stepWatch = new Stopwatch();

        private static readonly Stopwatch totalWatch = new Stopwatch();

        private static readonly List<KeyValuePair<string, double>> entries =
            new List<KeyValuePair<string, double>>();

        /// <summary>Phase timings from inside the carve itself, plus operation counts.
        ///
        /// Added after the first A/B attempt failed: comparing carve totals across two
        /// generations proved meaningless because tile content varies wildly (Plants was
        /// 87.8 ms on one tile and 1,078.3 ms on the next - 12x - and everything vanilla
        /// spawns in the doomed bands is something ClearCellHard must destroy). Per-phase
        /// numbers and op counts interpret a SINGLE run on its own terms instead.</summary>
        internal static readonly List<KeyValuePair<string, double>> carvePhases =
            new List<KeyValuePair<string, double>>();

        internal static int thingsDestroyed;

        internal static int rocksSpawned;

        /// <summary>WHAT the carve destroyed, by def.
        ///
        /// "74,584 things" is a magnitude, not a diagnosis. Attributing them to defs is
        /// what turns "almost certainly rock" into a fact, and this codebase has a
        /// documented history of confident sentences that were wrong (the snow line sat
        /// dead for months behind one). It also doubles as the pass signal for the
        /// doomed-band scoping work: as each mass spawner is scoped, its row here should
        /// vanish.</summary>
        internal static readonly Dictionary<string, int> destroyedByDef =
            new Dictionary<string, int>();

        internal static void NoteDestroyed(ThingDef def)
        {
            string key = def != null ? def.defName : "(null)";
            destroyedByDef.TryGetValue(key, out int n);
            destroyedByDef[key] = n + 1;
        }

        /// <summary>Top N rows of a def census, largest first.</summary>
        private static void AppendCensus(StringBuilder sb, string title,
            Dictionary<string, int> census, int top)
        {
            if (census.Count == 0)
            {
                sb.AppendLine("              " + title + ": none");
                return;
            }
            var rows = new List<KeyValuePair<string, int>>(census);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));
            int total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Value;
            }
            sb.AppendLine("              " + title + " (" + total.ToString("N0") + " over "
                + rows.Count + " defs):");
            for (int i = 0; i < rows.Count && i < top; i++)
            {
                sb.AppendLine("                " + rows[i].Value.ToString("N0").PadLeft(9)
                    + "  " + rows[i].Key);
            }
        }

        internal static void Phase(string label, double ms)
        {
            carvePhases.Add(new KeyValuePair<string, double>(label, ms));
        }

        /// <summary>FinalizeInit cost, captured separately: it is where regions, rooms and
        /// path costs are built, and it is the number that decides whether carving BEFORE
        /// it (single region build) is worth the reordering risk.</summary>
        internal static double finalizeInitMs = -1;

        internal static void Arm()
        {
            armed = true;
            genThreadId = Thread.CurrentThread.ManagedThreadId;
            depth = 0;
            currentLabel = null;
            entries.Clear();
            carvePhases.Clear();
            thingsDestroyed = 0;
            rocksSpawned = 0;
            destroyedByDef.Clear();
            ABDoomedBands.Reset();
            finalizeInitMs = -1;
            totalWatch.Restart();
        }

        internal static void Disarm()
        {
            armed = false;
            totalWatch.Stop();
        }

        internal static bool Armed => armed;

        internal static void OnStart(string label)
        {
            if (!armed || Thread.CurrentThread.ManagedThreadId != genThreadId)
            {
                return;
            }
            depth++;
            if (depth == 1)
            {
                currentLabel = label ?? "(unlabelled)";
                stepWatch.Restart();
            }
        }

        internal static void OnEnd()
        {
            if (!armed || Thread.CurrentThread.ManagedThreadId != genThreadId)
            {
                return;
            }
            if (depth > 0)
            {
                depth--;
                if (depth == 0 && currentLabel != null)
                {
                    entries.Add(new KeyValuePair<string, double>(
                        currentLabel, stepWatch.Elapsed.TotalMilliseconds));
                    currentLabel = null;
                }
            }
        }

        /// <summary>Called from the banded-generation postfix once carving is done.</summary>
        internal static void Report(Map map, double carveMs, double startSpotMs)
        {
            double total = totalWatch.Elapsed.TotalMilliseconds;
            entries.Sort((a, b) => b.Value.CompareTo(a.Value));
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("V2 banded generation profile (map " + map.Size + "):");
            sb.AppendLine("  TILE  " + DescribeTile(map));
            sb.AppendLine("  PLAN  " + DescribePlan(map));
            double shown = 0;
            int count = 0;
            for (int i = 0; i < entries.Count && count < 25; i++, count++)
            {
                sb.AppendLine("  " + entries[i].Value.ToString("0.0").PadLeft(9) + " ms  "
                    + entries[i].Key);
                shown += entries[i].Value;
            }
            if (entries.Count > count)
            {
                double rest = 0;
                for (int i = count; i < entries.Count; i++)
                {
                    rest += entries[i].Value;
                }
                sb.AppendLine("  " + rest.ToString("0.0").PadLeft(9) + " ms  ("
                    + (entries.Count - count) + " further gensteps)");
            }
            sb.AppendLine("  ---------");
            sb.AppendLine("  " + total.ToString("0.0").PadLeft(9) + " ms  all gensteps (wall)");
            sb.AppendLine("  " + carveMs.ToString("0.0").PadLeft(9) + " ms  AB band carve, of which:");
            for (int i = 0; i < carvePhases.Count; i++)
            {
                sb.AppendLine("  " + carvePhases[i].Value.ToString("0.0").PadLeft(9) + " ms      "
                    + carvePhases[i].Key);
            }
            sb.AppendLine("              (" + thingsDestroyed.ToString("N0") + " things destroyed, "
                + rocksSpawned.ToString("N0") + " rocks spawned)");
            sb.AppendLine("              SCOPING: skipRockInSkyBands="
                + ABDoomedBands.SkipRockInSkyBands);
            AppendCensus(sb, "DESTROYED by def", destroyedByDef, 10);
            AppendCensus(sb, "SUPPRESSED by def (never spawned)", ABDoomedBands.suppressed, 10);
            sb.AppendLine("  " + startSpotMs.ToString("0.0").PadLeft(9) + " ms  AB start-spot fix + rescue");
            sb.AppendLine("  " + (finalizeInitMs < 0 ? "      n/a" : finalizeInitMs.ToString("0.0").PadLeft(9))
                + " ms  Map.FinalizeInit (regions/rooms/path costs - built AFTER gensteps, RE-dirtied by carve)");
            Log.Warning(ABLog.Tag + " " + sb);
        }

        /// <summary>
        /// The tile stamp, without which a carve timing means nothing.
        ///
        /// Two runs of the SAME size and band count came in 7.4x apart (2.3 s on a bog,
        /// 17.1 s on a mountainous tile), because biome content - not cell count - drives
        /// the cost: everything vanilla spawns into a doomed band is something
        /// ClearCellHard must then destroy. Comparing two profiles without knowing what
        /// the tiles were is comparing noise, and the notes have already recorded four
        /// carve timings that way.
        ///
        /// Mutators are listed because they are the largest single lever - a river, a
        /// cave or a mixed biome changes what the gensteps have to place.
        /// </summary>
        private static string DescribeTile(Map map)
        {
            try
            {
                RimWorld.Planet.Tile t = map.TileInfo;
                if (t == null)
                {
                    return "(no tile info)";
                }
                string mutators = "none";
                if (t.Mutators != null && t.Mutators.Count > 0)
                {
                    var names = new List<string>();
                    foreach (RimWorld.TileMutatorDef m in t.Mutators)
                    {
                        names.Add(m.defName);
                    }
                    mutators = string.Join(", ", names.ToArray());
                }
                return "tile " + map.Tile
                    + " | biome " + (t.PrimaryBiome != null ? t.PrimaryBiome.defName : "?")
                    + " | hilliness " + t.hilliness
                    + " (elevGen " + t.HillinessForElevationGen + ")"
                    + " | elev " + t.elevation.ToString("0")
                    + " | temp " + t.temperature.ToString("0.0")
                    + " | rain " + t.rainfall.ToString("0")
                    + " | swamp " + t.swampiness.ToString("0.00")
                    + " | coastal " + t.IsCoastal
                    + " | mutators: " + mutators;
            }
            catch (Exception e)
            {
                return "(tile describe failed: " + e.Message + ")";
            }
        }

        /// <summary>Band layout, so a profile records how many DOOMED bands it paid for.
        /// Read from the live component, which the GenerateMap postfix sets up before
        /// Report runs.</summary>
        private static string DescribePlan(Map map)
        {
            try
            {
                ABBandMap b = ABBands.CompOf(map);
                if (b == null || !b.Banded)
                {
                    return "unbanded (single level) - this is the VANILLA CONTROL";
                }
                int doomed = b.bandCount - 1;
                return b.bandCount + " bands of " + b.bandHeight
                    + " (slot " + b.Slot + "), surface band " + b.surfaceBand
                    + " | " + (b.bandCount - 1 - b.surfaceBand) + " up / " + b.surfaceBand + " down"
                    + " | " + doomed + " DOOMED bands = "
                    + (doomed * b.bandHeight * map.Size.x).ToString("N0") + " cells generated then erased";
            }
            catch (Exception e)
            {
                return "(plan describe failed: " + e.Message + ")";
            }
        }
    }

    [HarmonyPatch(typeof(DeepProfiler), nameof(DeepProfiler.Start))]
    public static class Patch_DeepProfiler_ABGenStart
    {
        private static void Postfix(string label)
        {
            ABGenProfile.OnStart(label);
        }
    }

    [HarmonyPatch(typeof(DeepProfiler), nameof(DeepProfiler.End))]
    public static class Patch_DeepProfiler_ABGenEnd
    {
        private static void Postfix()
        {
            ABGenProfile.OnEnd();
        }
    }

    /// <summary>Arms the recorder for exactly the genstep window, banded maps only.</summary>
    [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.GenerateContentsIntoMap))]
    public static class Patch_GenerateContents_ABGenProfile
    {
        private static void Prefix(Map map)
        {
            try
            {
                if (ABBandedGeneration.TryPendingSurfaceRect(map, out _, out _))
                {
                    ABGenProfile.Arm();
                }
            }
            catch
            {
            }
        }

        private static void Postfix()
        {
            ABGenProfile.Disarm();
        }
    }

    /// <summary>Times FinalizeInit for the map being generated - the region/room/path-cost
    /// build whose double execution is the suspected cost of carving after it.</summary>
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Patch_MapFinalizeInit_ABGenProfile
    {
        [ThreadStatic]
        private static Stopwatch watch;

        private static void Prefix(Map __instance)
        {
            if (MapGenerator.mapBeingGenerated != __instance)
            {
                return;
            }
            (watch ?? (watch = new Stopwatch())).Restart();
        }

        private static void Postfix(Map __instance)
        {
            if (MapGenerator.mapBeingGenerated != __instance || watch == null || !watch.IsRunning)
            {
                return;
            }
            watch.Stop();
            ABGenProfile.finalizeInitMs = watch.Elapsed.TotalMilliseconds;
        }
    }
}
