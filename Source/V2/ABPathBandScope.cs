using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// PATHING COST CONTROL ON A BANDED MAP.
    ///
    /// ⚠ THE THING EVERY EARLIER NOTE IN THIS PROJECT GOT WRONG. ABMapSizeLimit, ABSettings
    /// and the schematic all say the cell budget exists because "1.6's path grid is an
    /// IJobParallelFor over EVERY cell, on a hot per-request path". Half of that is false and
    /// it matters, because it pointed optimisation work at the wrong place for a long time:
    ///
    ///  - PathGridJob is NOT per request. PathFinder dedupes it through gridJobLookup, keyed
    ///    on MapGridRequest, so it runs once per DISTINCT VARIANT per tick and it is Burst
    ///    compiled and parallel. A colony with four allowed areas in use pays four of these
    ///    a tick, not one per pawn.
    ///  - What IS per request, and map sized, is the first line of PathFinderJob.Execute:
    ///    `calcGrid.Clear()`. CalcNode is 20 bytes, so that is a 2.9 MB memset per path
    ///    request at 4 x 190, plus another 438 KB from PathGridDoorsBlockedJob clearing
    ///    providerCost and blocked.
    ///
    /// ⚠ AND NEITHER OF THOSE IS PATCHABLE. Both jobs carry
    /// [BurstCompile(CompileSynchronously = true)]. Burst replaces the job system's dispatch
    /// pointer with native code, so a Harmony patch on the managed IL is dead weight and
    /// silently does nothing. The only lever on those two costs is the total cell count,
    /// which the budget already pulls. Do not go looking for a patch there again.
    ///
    /// ⚠ AND OPEN AIR IS ALREADY FREE, SO DO NOT "OPTIMISE" THE GUTTER. AB_OpenAir is
    /// Traversability.Impassable, which puts it on the cheap branch in all three consumers:
    /// PathGridJob.Execute writes 10000 and skips CostForCell entirely, PathFinderJob skips
    /// it at `if (num5 >= 10000) continue` before it can ever enter the frontier, and
    /// PathGrid.CalculatedCostAt returns 10000 on its first line. The gutter is also only 2
    /// rows in a 128 row slot, 1.6% of cells at every tier, so a perfect elimination would be
    /// noise. It is not the cost.
    ///
    /// So this file attacks the two things that ARE reachable, both of which are about
    /// WASTED work rather than per-cell cost:
    ///
    ///  1. CROSS BAND REQUESTS. A path from one band to another is impossible BY
    ///     CONSTRUCTION, because the gutter is a full width impassable row. A* does not know
    ///     that, so it drains the entire band pocket before returning NotFound: roughly 16k
    ///     expansions at 126 and 36k at 190, against a few hundred for an ordinary path.
    ///     Worse, eight vanilla call sites reach this through PathFinder.FindPathNow, which
    ///     is SYNCHRONOUS ON THE MAIN THREAD. Rejecting up front is not a behaviour change:
    ///     the request already fails, it just fails 50 to 100 times more expensively.
    ///
    ///  2. WHOLE MAP DOOR AND PAWN SWEEPS. PathGridDoorsBlockedJob walks every door and
    ///     every spawned pawn on the map, once per request. On a seven band colony six
    ///     sevenths of those are on bands the path cannot reach.
    ///
    /// Plus a tuning lever (DetermineHeuristicStrength) which is neither, and is documented
    /// on its own patch below.
    ///
    /// ⚠ WHY THE GUARD IS SAFE FOR FLYING PAWNS, WHICH IS THE ONE CASE THAT COULD HAVE
    /// BROKEN IT. PathGridDef Flying does not make impassable terrain walkable outright; it
    /// exempts only terrain with forcePassableByFlyingPawns=true (see the first branch of
    /// PathGrid.CalculatedCostAt). AB_OpenAir does not set that field, so the flying grid
    /// scores the gutter 10000 exactly like the normal grid, and a flying pawn cannot cross
    /// a band either. If AB_OpenAir ever gains forcePassableByFlyingPawns, THIS GUARD MUST
    /// GAIN A def.flying EXEMPTION or flying pawns stop pathing between levels.
    /// </summary>
    public static class ABPathBandScope
    {
        // ---- diagnostics ----------------------------------------------------
        //
        // ⚠ A GUARD CLAUSE THAT SILENTLY EARLY RETURNS IS INDISTINGUISHABLE FROM AN
        // UNIMPLEMENTED FEATURE (§14, learned the hard way on the Dubwise merge). Every
        // early return below is counted, and the last rejection carries its own reason
        // string, so "did this ever fire" is one dev action away instead of a theory.

        public static int rejectedSync;

        public static int rejectedAsync;

        public static int guardCalls;

        public static int filterCalls;

        public static long doorsKept;

        public static long doorsDropped;

        public static long pawnsKept;

        public static long pawnsDropped;

        public static long providersKept;

        public static long providersDropped;

        public static string lastReject = "none yet";

        public static void ResetStats()
        {
            rejectedSync = 0;
            rejectedAsync = 0;
            guardCalls = 0;
            filterCalls = 0;
            doorsKept = 0;
            doorsDropped = 0;
            pawnsKept = 0;
            pawnsDropped = 0;
            providersKept = 0;
            providersDropped = 0;
            lastReject = "none yet";
            ABPerfStats.ResetPath();
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("AB2 PATHING REPORT");
            sb.AppendLine("  cross band guard: " + guardCalls + " requests inspected, "
                + rejectedSync + " rejected sync (FindPathNow), "
                + rejectedAsync + " rejected async (PushRequest)");
            sb.AppendLine("  last rejection: " + lastReject);
            sb.AppendLine("  door/pawn band filter: " + filterCalls + " requests scoped");
            sb.AppendLine("    doors     kept " + doorsKept + " dropped " + doorsDropped
                + Share(doorsKept, doorsDropped));
            sb.AppendLine("    pawns     kept " + pawnsKept + " dropped " + pawnsDropped
                + Share(pawnsKept, pawnsDropped));
            sb.AppendLine("    providers kept " + providersKept + " dropped " + providersDropped
                + Share(providersKept, providersDropped));
            sb.AppendLine("  heuristic multiplier: " + HeuristicBoost().ToString("0.00")
                + (Mathf.Approximately(HeuristicBoost(), 1f) ? " (off, vanilla accuracy)" : "")
                + "; transit-leg floor " + TransitLegHeuristic.ToString("0.00"));
            sb.AppendLine("  same-island-first: "
                + Patch_GenClosest_ABSameIslandFirst.localHits + " local picks, "
                + Patch_GenClosest_ABSameIslandFirst.fallbacks + " cross-island fallbacks");
            sb.AppendLine("  mech command range: "
                + Patch_MechanitorTracker_ABBandLocalRange.rescued
                + " cross-band orders permitted (0 with no mechanitor, or if no order was "
                + "ever aimed at another level)");
            sb.Append(ABPerfStats.PathReport());
            return sb.ToString();
        }

        private static string Share(long kept, long dropped)
        {
            long total = kept + dropped;
            return total == 0 ? "" : "  (" + (100f * dropped / total).ToString("0.0") + "% skipped)";
        }

        internal static float HeuristicBoost()
        {
            ABSettings s = ABMod.Settings;
            return s == null ? 1f : Mathf.Clamp(s.pathHeuristic, MinHeuristic, MaxHeuristic);
        }

        public const float MinHeuristic = 1f;

        public const float MaxHeuristic = 2.5f;

        /// <summary>
        /// Heuristic floor for the LEGS OF A SEGMENTED CROSS-ISLAND TRIP, applied even when
        /// the player's slider is at 1.00 (off).
        ///
        /// ⚠ WHY A FLOOR AND NOT A CHANGED DEFAULT. Turning the slider's shipped default up
        /// would silently make ALL pathing greedier for every user; this instead prices only
        /// the trips §34 newly enabled - long down-across-up hauls where a slightly longer
        /// route is invisible but the A* expansion count is not. Vanilla precedent for the
        /// magnitude: 1.5 for colonists in darkness, 1.75 for animals; 1.35 is milder than
        /// both.
        ///
        /// ⚠ COMPOSED BY MAX(), NEVER MULTIPLIED, with the slider. A player at 2.5 already
        /// gets more than the floor asks for; 2.5 x 1.35 = 3.4 would be a different (and
        /// unrequested) trade.
        /// </summary>
        public const float TransitLegHeuristic = 1.35f;

        // ---- the cross band test --------------------------------------------

        /// <summary>
        /// True when this request can never produce a path because start and destination sit
        /// in different bands.
        ///
        /// ⚠ EVERY EARLY RETURN HERE IS "LET VANILLA HANDLE IT", NEVER "REJECT". A guard that
        /// is wrong in the permissive direction costs a slow path; wrong in the other
        /// direction it silently strands a pawn, and that failure mode looks exactly like the
        /// three unrelated stairwell bugs already recorded on ABWormholePather. So:
        ///
        ///  - Not banded, which includes EVERY MAP DURING GENERATION (⚠ ABBands.Banded is
        ///    FALSE while gensteps run): permit. This is load bearing for GenStep_Roads,
        ///    which calls FindPathNow across the whole stack and must not be interfered with.
        ///  - Either endpoint out of bounds or invalid: permit.
        ///  - Either endpoint IN THE GUTTER: permit. BandOf has to return some band for a
        ///    gutter cell, and which side it picks is an implementation detail we must not
        ///    build a rejection on. A destination on a band edge reached with
        ///    PathEndMode.Touch is the realistic case.
        /// </summary>
        internal static bool CrossBand(Map map, IntVec3 start, LocalTargetInfo target, out string why)
        {
            why = null;
            if (map == null || !ABBands.Banded(map))
            {
                return false;
            }
            guardCalls++;
            if (!target.IsValid)
            {
                return false;
            }
            IntVec3 dest = target.Cell;
            if (!start.IsValid || !start.InBounds(map) || !dest.IsValid || !dest.InBounds(map))
            {
                return false;
            }
            if (ABBands.InGutter(map, start) || ABBands.InGutter(map, dest))
            {
                return false;
            }
            if (ABBands.SameBand(map, start, dest))
            {
                return false;
            }
            why = start + " band " + ABBands.BandOf(map, start)
                + " -> " + dest + " band " + ABBands.BandOf(map, dest);
            return true;
        }

        // ---- per band door / pawn / provider buckets -------------------------
        //
        // ⚠ WHY REUSING THE LISTS IS SAFE, AND WHAT WOULD MAKE IT UNSAFE. These lists are
        // handed to PathGridDoorsBlockedJob, which runs on a WORKER THREAD. Refilling a list
        // while a job still reads it would be a data race. It cannot happen, because
        // PathFinderTick opens with ForceCompleteScheduledJobs() and FindPathNow does the
        // same before it parameterizes: by the time this code runs on tick N, every job from
        // tick N-1 has already been joined. That is the whole invariant. If a future RimWorld
        // ever lets a path job straddle a tick boundary, THIS MUST BECOME DOUBLE BUFFERED.
        //
        // Rebuild is keyed on (map, tick), so two banded maps pathing in the same tick simply
        // rebuild twice. The source lists are vanilla's own once-per-tick caches, so a
        // rebuild is a single pass over doors + pawns + cost providers and allocates nothing
        // after warmup.

        private static Map bucketMap;

        private static int bucketTick = -1;

        private static List<Thing>[] doorsByBand;

        private static List<Pawn>[] pawnsByBand;

        private static List<IPathFindCostProvider>[] providersByBand;

        private static void EnsureBuckets(Map map, IReadOnlyList<Thing> doors,
            IReadOnlyList<Pawn> pawns, IReadOnlyList<IPathFindCostProvider> providers)
        {
            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (bucketMap == map && bucketTick == tick)
            {
                return;
            }
            bucketMap = map;
            bucketTick = tick;

            int bands = Mathf.Max(1, ABBands.BandCount(map));
            if (doorsByBand == null || doorsByBand.Length != bands)
            {
                doorsByBand = new List<Thing>[bands];
                pawnsByBand = new List<Pawn>[bands];
                providersByBand = new List<IPathFindCostProvider>[bands];
                for (int i = 0; i < bands; i++)
                {
                    doorsByBand[i] = new List<Thing>();
                    pawnsByBand[i] = new List<Pawn>();
                    providersByBand[i] = new List<IPathFindCostProvider>();
                }
            }
            else
            {
                for (int i = 0; i < bands; i++)
                {
                    doorsByBand[i].Clear();
                    pawnsByBand[i].Clear();
                    providersByBand[i].Clear();
                }
            }

            if (doors != null)
            {
                for (int i = 0; i < doors.Count; i++)
                {
                    Thing t = doors[i];
                    int b = BandIndex(map, t?.Position ?? IntVec3.Invalid, bands);
                    if (b >= 0)
                    {
                        doorsByBand[b].Add(t);
                    }
                }
            }
            if (pawns != null)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    int b = BandIndex(map, p?.Position ?? IntVec3.Invalid, bands);
                    if (b >= 0)
                    {
                        pawnsByBand[b].Add(p);
                    }
                }
            }
            if (providers != null)
            {
                for (int i = 0; i < providers.Count; i++)
                {
                    IPathFindCostProvider p = providers[i];
                    IntVec3 at = p is Thing t ? t.Position : IntVec3.Invalid;
                    int b = BandIndex(map, at, bands);
                    if (b >= 0)
                    {
                        providersByBand[b].Add(p);
                    }
                }
            }
        }

        /// <summary>Band of a cell, or -1 when it cannot be established. ⚠ -1 means DROP
        /// FROM EVERY BUCKET, which is only correct because the caller falls back to the
        /// unfiltered vanilla list whenever the requesting pawn's own band is unknown.</summary>
        private static int BandIndex(Map map, IntVec3 c, int bands)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return -1;
            }
            int b = ABBands.BandOf(map, c);
            return b >= 0 && b < bands ? b : -1;
        }

        internal static void ScopeDoorJob(PathGridDoorsBlockedJob job, PathRequest request)
        {
            if (job == null || request == null)
            {
                return;
            }
            Map map = request.map;
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            int bands = Mathf.Max(1, ABBands.BandCount(map));
            if (bands < 2)
            {
                return;
            }
            // ⚠ SCOPE ON THE REQUEST'S START, NOT ON THE PAWN'S CURRENT POSITION. The job's
            // own `start` field is request.Start, and a pawn mid transit can be standing in a
            // band the request was not issued from.
            int band = BandIndex(map, request.Start, bands);
            if (band < 0 || ABBands.InGutter(map, request.Start))
            {
                return;
            }

            EnsureBuckets(map, job.doors, job.pawns, job.providers);
            filterCalls++;

            int wasDoors = job.doors?.Count ?? 0;
            int wasPawns = job.pawns?.Count ?? 0;
            int wasProviders = job.providers?.Count ?? 0;

            job.doors = doorsByBand[band];
            job.pawns = pawnsByBand[band];
            job.providers = providersByBand[band];

            doorsKept += job.doors.Count;
            doorsDropped += wasDoors - job.doors.Count;
            pawnsKept += job.pawns.Count;
            pawnsDropped += wasPawns - job.pawns.Count;
            providersKept += job.providers.Count;
            providersDropped += wasProviders - job.providers.Count;
        }
    }

    // =====================================================================================
    // 1. CROSS BAND REQUESTS
    // =====================================================================================

    /// <summary>
    /// The synchronous entry, and the expensive one. FindPathNow calls job.Run(), so the
    /// exhaustive failure search happens ON THE MAIN THREAD, inside whatever JobGiver asked.
    ///
    /// ⚠ ONE PATCH COVERS BOTH OVERLOADS. FindPathNow(start, target, Pawn, ...) does nothing
    /// but resolve bashing flags and delegate to the TraverseParms overload, so patching the
    /// TraverseParms one catches every caller. The vanilla call sites that reach here with a
    /// target on another band are JobGiver_AISapper, JobGiver_Manhunter, JobGiver_ShamblerFight,
    /// JobGiver_AIWaitAmbush, JobGiver_RevenantEscape, JobGiver_Duel, LordToil_EntitySwarm,
    /// PrisonBreakUtility, RCellFinder and RoyalTitleUtility.
    ///
    /// ⚠ PawnPath.NotFound IS THE CORRECT RETURN AND IT DOES NOT NEED DISPOSING. Vanilla
    /// FindPathNow already returns exactly this singleton on three of its own paths, and its
    /// callers wrap the result in `using`, so the contract is unchanged.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_PathFinder_ABRejectCrossBandSync
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PathFinder), nameof(PathFinder.FindPathNow), new[]
            {
                typeof(IntVec3),
                typeof(LocalTargetInfo),
                typeof(TraverseParms),
                typeof(PathFinderCostTuning?),
                typeof(PathEndMode),
                typeof(PathRequest.IPathGridCustomizer)
            });
        }

        private static bool Prefix(Map ___map, IntVec3 start, LocalTargetInfo target,
            ref PawnPath __result)
        {
            ABPerfStats.NoteRequest(___map, sync: true);
            if (!ABPathBandScope.CrossBand(___map, start, target, out string why))
            {
                return true;
            }
            ABPathBandScope.rejectedSync++;
            ABPathBandScope.lastReject = "sync " + why;
            __result = PawnPath.NotFound;
            return false;
        }
    }

    /// <summary>
    /// The asynchronous entry. Resolving the request in place is exactly what
    /// FinalizeRecyclePathJobData does for a search that came back empty, so the requester
    /// sees the same result one tick earlier and the work queue never grows an entry that
    /// can only fail.
    ///
    /// ⚠ THIS DOES NOT TIGHTEN A RETRY LOOP. A failed path ends the job and the think tree
    /// re-runs next tick either way; the retry cadence is the tick, not the search cost.
    /// In practice ABWormholePather has already segmented ordinary pawn movement at
    /// StartPath, so anything reaching here is a third party or a vanilla helper pushing a
    /// request directly.
    /// </summary>
    [HarmonyPatch(typeof(PathFinder), nameof(PathFinder.PushRequest))]
    public static class Patch_PathFinder_ABRejectCrossBandAsync
    {
        private static bool Prefix(PathRequest request)
        {
            if (request == null || request.ResultIsReady || request.Cancelled)
            {
                return true;
            }
            ABPerfStats.NoteRequest(request.map, sync: false);
            if (!ABPathBandScope.CrossBand(request.map, request.Start, request.Target, out string why))
            {
                return true;
            }
            ABPathBandScope.rejectedAsync++;
            ABPathBandScope.lastReject = "async " + why;
            request.Resolve(null);
            return false;
        }
    }

    // =====================================================================================
    // 2. DOOR / PAWN / COST PROVIDER BAND SCOPE
    // =====================================================================================

    /// <summary>
    /// PathGridDoorsBlockedJob walks every door, every cost provider and every spawned pawn
    /// on the map ONCE PER REQUEST, writing providerCost and blocked entries at their cells.
    /// On a banded map all but one band's worth of those writes land on cells A* can never
    /// reach, because the gutter seals each band.
    ///
    /// ⚠ THIS IS THE ONE MAP SIZED PIECE OF THE PATHFINDER THAT CAN BE PATCHED AT ALL, AND
    /// THE REASON IS ITS FIELD TYPES. It holds IReadOnlyList&lt;Thing&gt;, Map and Pawn, which
    /// are managed references, so Burst can never compile it: it is a plain managed IJob
    /// class while PathGridJob and PathFinderJob are Burst structs. Anything else worth
    /// optimising in this subsystem is behind that same wall.
    ///
    /// ⚠ PATCHING Parameterize RATHER THAN Execute IS DELIBERATE. Execute is where the cost
    /// is, but the lists arrive as fields, so replacing the DATA is both cheaper and safer
    /// than replacing the READER (§14: wrap the data instead of the reader). It also keeps
    /// vanilla's own per-tick door and pawn caching intact; we bucket its output rather than
    /// re-querying listerThings.
    ///
    /// ⚠ THE THIRD PARAMETER IS A PRIVATE NESTED STRUCT AND IS SIMPLY OMITTED. The original
    /// is ParameterizeDoorBlockedJob(PathGridDoorsBlockedJob, PathRequest, ref PathUniqueState);
    /// PathUniqueState is private to PathFinder and must never appear in a patch signature.
    /// Harmony binds by NAME, so taking only `job` and `request` is correct and stable.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_PathFinder_ABDoorPawnBandScope
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PathFinder), "ParameterizeDoorBlockedJob");
        }

        private static void Postfix(PathGridDoorsBlockedJob job, PathRequest request)
        {
            ABPathBandScope.ScopeDoorJob(job, request);
        }
    }

    // =====================================================================================
    // 3. HEURISTIC STRENGTH
    // =====================================================================================

    /// <summary>
    /// A* expansion count is governed by how greedy the heuristic is. PathFinderJob computes
    /// hCost as `octileDistance * 13 * heuristicStrength`, and 13 is exactly
    /// DefaultMoveTicksCardinal, so strength 1.0 is admissible for an ordinary pawn on
    /// cost-free terrain: optimal paths, maximum expansions. Above 1.0 the search is greedier,
    /// visits far fewer cells and may return a slightly longer route.
    ///
    /// ⚠ THIS IS A VANILLA TRADE, NOT A NEW ONE. DetermineHeuristicStrength already returns
    /// 1.75 for every animal and a distance scaled curve for AI pawns, and 1.5 for colonists
    /// under unnatural darkness. The slider extends the same dial to colonists.
    ///
    /// ⚠ GATED ON A BANDED MAP ON PURPOSE. The mod has no business changing pathfinding on a
    /// player's ordinary maps, and pawn == null (map generation, GenStep_Roads, world work)
    /// keeps vanilla's flat 1.0 because there is no map to test.
    /// </summary>
    [HarmonyPatch(typeof(PathFinder), "DetermineHeuristicStrength")]
    public static class Patch_PathFinder_ABHeuristicStrength
    {
        private static void Postfix(Pawn pawn, ref float __result)
        {
            if (pawn == null)
            {
                return;
            }
            Map map = pawn.Map;
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            float boost = ABPathBandScope.HeuristicBoost();
            // A leg of an in-flight segmented trip gets the floor even with the slider off.
            // HasPending is a dictionary probe gated behind a plain count read, and a pending
            // record exists for exactly the legs that belong to a cross-island journey:
            // TrySegment creates it BEFORE the pather builds the leg's path.
            if (ABWormholePather.AnyPending && ABWormholePather.HasPending(pawn))
            {
                boost = Mathf.Max(boost, ABPathBandScope.TransitLegHeuristic);
            }
            if (boost <= 1f)
            {
                return;
            }
            __result *= boost;
        }
    }
}
