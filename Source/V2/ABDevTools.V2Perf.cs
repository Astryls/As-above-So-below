using System;
using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Stress instruments for the perf counters (ABPerfProbe). Each action produces one
    /// ISOLATED load so the counters carry causality by construction:
    ///
    ///   * pathfind bursts    - per-request cost on THIS map size (calcGrid clears + door
    ///                          job), no gameplay side effects, fixed RNG seed.
    ///   * dirty storm        - MapMeshDirty with NO content change: pure regen machinery,
    ///                          repeatable. Every above-band regen it triggers is
    ///                          mirror-caused, which is the §B1 number.
    ///   * spawn below pawns  - populates the band under the camera so the below dynamic
    ///                          pass has real work (serial three-phase draws).
    ///   * mine burst         - real end-to-end burst: MapMeshDirty + region rebuild +
    ///                          island refloods + path grid dirties, like player mining.
    ///
    /// Recommended flow per experiment: reset perf counters -> ONE stress action -> let it
    /// settle a few seconds -> render report / pathing report. One action per experiment;
    /// two at once and the counters can no longer say which caused what.
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: render report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2RenderReport()
        {
            Log.Warning(ABLog.Tag + " " + ABPerfStats.RenderReport());
            Messages.Message("AB2: render report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: reset perf counters", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ResetPerfCounters()
        {
            // ResetStats resets the §32/§35 pathing counters AND the path-side perf extras;
            // the render side is its own reset.
            ABPathBandScope.ResetStats();
            ABPerfStats.ResetRender();
            Messages.Message("AB2: all perf counters reset (path + render).",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Fixed seed so two runs on the same map are comparable. 40 pairs is
        /// enough for a stable average and short enough that the deliberate main-thread
        /// hitch stays under a second.</summary>
        [DebugAction("As above", "AB2: stress pathfind 40 same-band", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressPathfindSameBand()
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int band = ABBandView.CurrentBand(map);
            List<IntVec3> cells = SampleStandable(map, bands, band, 80, 762195);
            if (cells.Count < 8)
            {
                Messages.Message("AB2: not enough standable cells on band " + band + ".",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            int pairs = cells.Count / 2;
            int found = 0;
            long total = 0;
            long worst = 0;
            for (int i = 0; i < pairs; i++)
            {
                long t0 = ABPerfStats.Now();
                // ⚠ THE using MATTERS: PawnPath is pooled, and 40 undisposed paths would
                // trip the "Leak suspected in object pool" warning and poison the run's log.
                using (PawnPath p = map.pathFinder.FindPathNow(cells[i * 2], cells[i * 2 + 1],
                    tp, null, PathEndMode.OnCell, null))
                {
                    if (p.Found)
                    {
                        found++;
                    }
                }
                long e = ABPerfStats.Now() - t0;
                total += e;
                if (e > worst)
                {
                    worst = e;
                }
            }
            string msg = "AB2 STRESS pathfind same-band: " + pairs + " requests on band " + band
                + ", " + found + " found; total " + ABPerfStats.MsOf(total).ToString("0.0")
                + " ms, avg " + ABPerfStats.MsOf(total / pairs).ToString("0.00")
                + " ms, worst " + ABPerfStats.MsOf(worst).ToString("0.00") + " ms";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Every pair straddles a band, so every request should be rejected by the
        /// §32 guard at ~zero cost. If avg is not near zero, the guard has stopped firing -
        /// that is the regression this action exists to catch.</summary>
        [DebugAction("As above", "AB2: stress pathfind 30 cross-band", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressPathfindCrossBand()
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int bandA = ABBandView.CurrentBand(map);
            int bandB = bands.BandExists(bandA - 1) ? bandA - 1 : bandA + 1;
            if (!bands.BandExists(bandB))
            {
                Messages.Message("AB2: no second band to test against.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            List<IntVec3> a = SampleStandable(map, bands, bandA, 30, 762196);
            List<IntVec3> b = SampleStandable(map, bands, bandB, 30, 762197);
            int pairs = Math.Min(a.Count, b.Count);
            if (pairs < 4)
            {
                Messages.Message("AB2: not enough standable cells on both bands.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            int rejectedBefore = ABPathBandScope.rejectedSync;
            TraverseParms tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            long total = 0;
            for (int i = 0; i < pairs; i++)
            {
                long t0 = ABPerfStats.Now();
                using (PawnPath p = map.pathFinder.FindPathNow(a[i], b[i], tp, null,
                    PathEndMode.OnCell, null))
                {
                }
                total += ABPerfStats.Now() - t0;
            }
            int rejected = ABPathBandScope.rejectedSync - rejectedBefore;
            string msg = "AB2 STRESS pathfind cross-band: " + pairs + " requests band " + bandA
                + " -> " + bandB + "; " + rejected + " rejected by the guard; total "
                + ABPerfStats.MsOf(total).ToString("0.00") + " ms (should be near zero)";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Dirties 48 cells one band below the camera with the Things flag and NO
        /// content change: identical meshes get rebuilt, so the report afterwards is pure
        /// regen machinery + mirror cost. Reset counters first, wait ~2 s, then render
        /// report.</summary>
        [DebugAction("As above", "AB2: stress dirty storm below", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressDirtyStorm()
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            if (viewBand <= 0)
            {
                Messages.Message("AB2: view a band above ground first (nothing below band 0).",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            List<IntVec3> cells = SampleBelowCamera(map, bands, viewBand, 48, 762198,
                requireStandable: false);
            for (int i = 0; i < cells.Count; i++)
            {
                map.mapDrawer.MapMeshDirty(cells[i], (ulong)MapMeshFlagDefOf.Things,
                    regenAdjacentCells: false, regenAdjacentSections: false);
            }
            string msg = "AB2 STRESS dirty storm: " + cells.Count + " cells dirtied on band "
                + (viewBand - 1) + " under the camera. Wait ~2 s, then run the render report.";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Real colonists (full render trees, apparel) so the below dynamic pass
        /// carries worst-case work. Generation itself hitches for a second or two - that
        /// hitch is the GENERATOR, not the draw path; only the counters afterwards speak
        /// for the draw path.</summary>
        [DebugAction("As above", "AB2: stress spawn 20 below pawns", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressSpawnBelowPawns()
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            if (!bands.BandExists(viewBand - 1))
            {
                Messages.Message("AB2: no band below the current view.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            List<IntVec3> cells = SampleBelowCamera(map, bands, viewBand, 20, 762199,
                requireStandable: true);
            int spawned = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                try
                {
                    Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    GenSpawn.Spawn(p, cells[i], map);
                    spawned++;
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " stress spawn failed: " + e.Message, 762195901);
                }
            }
            string msg = "AB2 STRESS: spawned " + spawned + " colonists on band "
                + (viewBand - 1) + " under the camera. Stay on band " + viewBand
                + " looking down, reset counters, wait ~10 s, render report.";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: stress spawn 80 below pawns (armed)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressSpawn80Armed()
        {
            SpawnBelowCrowd(80, 762200, disarm: false);
        }

        [DebugAction("As above", "AB2: stress spawn 80 below pawns (unarmed)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressSpawn80Unarmed()
        {
            SpawnBelowCrowd(80, 762201, disarm: true);
        }

        /// <summary>
        /// The render-cache crowd. ARMED vs UNARMED is the controlled variable: the gear
        /// veto is the one gate that depends on what the pawns are carrying, so running both
        /// and diffing the veto breakdown separates "the cache does not engage" from "the
        /// cache correctly refuses to engage".
        ///
        /// ⚠ EVERY PAWN IS PARKED ON A WAIT JOB. Spawning 80 pawns that all immediately ask
        /// for a path blows past RimWorld's ~39-entry PawnPath pool and fills the log with
        /// "Leak suspected in object pool for PawnPaths" - not a real leak, but it is noise
        /// that costs a test run to re-explain. Draft them by hand when movement is wanted.
        /// </summary>
        private static void SpawnBelowCrowd(int count, int seed, bool disarm)
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            if (!bands.BandExists(viewBand - 1))
            {
                Messages.Message("AB2: no band below the current view.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            List<IntVec3> cells = SampleBelowCamera(map, bands, viewBand, count, seed,
                requireStandable: true);
            int spawned = 0;
            int disarmed = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                try
                {
                    Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    GenSpawn.Spawn(p, cells[i], map);
                    spawned++;
                    if (disarm && p.equipment != null && p.equipment.Primary != null)
                    {
                        p.equipment.DestroyAllEquipment();
                        disarmed++;
                    }
                    p.jobs?.StartJob(JobMaker.MakeJob(JobDefOf.Wait), JobCondition.InterruptForced);
                }
                catch (Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " stress crowd spawn failed: " + e.Message,
                        762195904);
                }
            }
            string msg = "AB2 STRESS: spawned " + spawned + (disarm ? " UNARMED" : " ARMED")
                + " colonists on band " + (viewBand - 1) + " under the camera"
                + (disarm ? " (" + disarmed + " disarmed)" : "")
                + ". Stay on band " + viewBand
                + " looking down, reset counters, sweep the zoom, then render report.";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>The end-to-end burst: destroys up to 30 mineable rocks one band down,
        /// firing everything real mining fires (mesh dirties + mirror, region/room rebuild,
        /// island refloods, path grid). Compare against the dirty storm to separate regen
        /// machinery from the region/island side.</summary>
        [DebugAction("As above", "AB2: stress mine 30 below", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2StressMineBelow()
        {
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Messages.Message("AB2: not a banded map.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            if (viewBand <= 0)
            {
                Messages.Message("AB2: view a band above ground first.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            int slot = bands.Slot;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            view.ClipInsideMap(map);
            CellRect below = view.MovedBy(new IntVec3(0, 0, -slot));
            below.ClipInsideMap(map);
            var rocks = new List<Thing>();
            foreach (IntVec3 c in below)
            {
                if (bands.BandOf(c) != viewBand - 1 || bands.InGutter(c))
                {
                    continue;
                }
                Building e = c.GetEdifice(map);
                if (e != null && e.def.mineable && !rocks.Contains(e))
                {
                    rocks.Add(e);
                    if (rocks.Count >= 30)
                    {
                        break;
                    }
                }
            }
            if (rocks.Count == 0)
            {
                Messages.Message("AB2: no mineable rock under the camera one band down.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            long t0 = ABPerfStats.Now();
            for (int i = 0; i < rocks.Count; i++)
            {
                if (!rocks[i].Destroyed)
                {
                    rocks[i].Destroy(DestroyMode.Vanish);
                }
            }
            long e2 = ABPerfStats.Now() - t0;
            string msg = "AB2 STRESS mine: destroyed " + rocks.Count + " rocks on band "
                + (viewBand - 1) + " in " + ABPerfStats.MsOf(e2).ToString("0.0")
                + " ms (synchronous part). Wait ~2 s, then render + pathing reports.";
            Log.Warning(ABLog.Tag + " " + msg);
            Messages.Message(msg, MessageTypeDefOf.TaskCompletion, false);
        }

        // ---- shared sampling helpers ----------------------------------------

        /// <summary>Random cells of one band. Fixed seed per caller so repeat runs hit the
        /// same cells and their numbers are comparable.</summary>
        private static List<IntVec3> SampleStandable(Map map, ABBandMap bands, int band,
            int want, int seed)
        {
            var result = new List<IntVec3>();
            CellRect rect = ABBands.RectOfBand(map, band);
            var rng = new System.Random(seed);
            int attempts = 0;
            while (result.Count < want && attempts++ < want * 60)
            {
                var c = new IntVec3(rect.minX + rng.Next(rect.Width), 0,
                    rect.minZ + rng.Next(rect.Height));
                if (c.InBounds(map) && !bands.InGutter(c) && c.Standable(map))
                {
                    result.Add(c);
                }
            }
            return result;
        }

        /// <summary>Random cells of the band below the view, restricted to the CAMERA's
        /// column so the sections involved are in view and regenerate immediately - an
        /// out-of-view dirty section regenerates lazily (one per frame) and would smear
        /// the burst the stress is trying to produce.</summary>
        private static List<IntVec3> SampleBelowCamera(Map map, ABBandMap bands, int viewBand,
            int want, int seed, bool requireStandable)
        {
            var result = new List<IntVec3>();
            int slot = bands.Slot;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            view.ClipInsideMap(map);
            CellRect below = view.MovedBy(new IntVec3(0, 0, -slot));
            below.ClipInsideMap(map);
            if (below.Width <= 0 || below.Height <= 0)
            {
                return result;
            }
            var rng = new System.Random(seed);
            int attempts = 0;
            while (result.Count < want && attempts++ < want * 80)
            {
                var c = new IntVec3(below.minX + rng.Next(below.Width), 0,
                    below.minZ + rng.Next(below.Height));
                if (!c.InBounds(map) || bands.BandOf(c) != viewBand - 1 || bands.InGutter(c))
                {
                    continue;
                }
                if (requireStandable && !c.Standable(map))
                {
                    continue;
                }
                if (!result.Contains(c))
                {
                    result.Add(c);
                }
            }
            return result;
        }
    }
}
