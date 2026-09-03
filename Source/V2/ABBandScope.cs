using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// ⚠⚠ THE BAND SCOPE: "for the duration of this call, the map IS this one band".
    ///
    /// §99 needs to run vanilla (and modded) gensteps a second time, once per non-surface
    /// band, so that a sky plateau and a cavern floor get the same geysers, chunks, boulders
    /// and vents the ground level gets. The obvious way to do that - copy each genstep's
    /// logic into a band-aware rewrite - fails the mod-compat test immediately: ReGrowth 2's
    /// boulders are a TRANSPILER on <c>GenStep_RockChunks</c>, not a def or a genstep of
    /// their own, so the only way to get them is to run vanilla's genstep and let their IL
    /// ride along. Rule 54: search the capability, not the name.
    ///
    /// So instead of rewriting the gensteps, we lie to them about the map. THREE funnels
    /// carry essentially every genstep's idea of "where can I put things":
    ///
    ///   1. <c>Map.AllCells</c>          - the cell-walking gensteps (RockChunks, Plants,
    ///                                     Snow, most terrain mutators, most mod gensteps).
    ///   2. <c>cellsInRandomOrder</c>    - the same, shuffled (GenStep_Plants uses this one).
    ///   3. <c>GenStep_Scatterer.TryFindScatterCell</c> - the whole scatterer family.
    ///
    /// Redirect those three at one rect and an astonishing amount of foreign generation code
    /// becomes band-aware for free, including code that has not been written yet. That is the
    /// same "find the one virtual everything funnels through" move that made a single patch
    /// cover nine coastal mutators (§56), applied to generation instead of terrain.
    ///
    /// ⚠ WHY THE SCATTERER NEEDED ITS OWN FUNNEL RATHER THAN JUST A <c>CanScatterAt</c> VETO.
    /// Gating CanScatterAt alone does work, but it works by REJECTION: vanilla's finder calls
    /// <c>CellFinderLoose.TryFindRandomNotEdgeCellWith(..., 1000 tries)</c> against the whole
    /// map, so on a 7-band stack six of every seven candidate cells are thrown away before
    /// the genstep's own validators even get a look. A geyser with a Buildable(radius 4)
    /// validator and minSpacing 25 does not have 1000 tries to spare, and when it runs out it
    /// calls <c>Log.Warning("Scatterer ... could not find cell to generate at")</c> - an
    /// engine warning, attributed to us, once per band per scatterer. Sampling from the band
    /// rect instead makes every try a real try and makes the warning ours to phrase (rule 33:
    /// a filter that can reject everything must say so, and must say which clause).
    ///
    /// ⚠ THE COUNT NEEDED NO SCALING AT ALL, AND THAT IS NOT LUCK - IT IS A LATENT BUG.
    /// <c>GenStep_Scatterer.CountFromPer10kCells</c> reads <c>map.Size.x</c> and SQUARES it:
    ///
    ///     int num = Mathf.RoundToInt(10000f / countPer10kCells);
    ///     return Mathf.RoundToInt((float)(mapSize * mapSize) / (float)num);
    ///
    /// It never looks at <c>map.Size.z</c> and never looks at <c>map.Area</c>. On our 126x896
    /// map that yields the count a 126x126 map would get - ONE BAND'S WORTH - which vanilla
    /// then spreads uniformly over all seven bands. So the ground level has been receiving
    /// roughly one SEVENTH of its correct geyser/chunk/vent count since V2 existed, and the
    /// other six sevenths were being generated into bands the carve was about to erase. The
    /// user reported the visible half of that ("upper bands devoid of features"); this is the
    /// invisible half. See <c>Patch_GenStep_Scatterer_ABSurfaceCount</c> in ABBandDressing.
    ///
    /// ⚠ SINGLE-SLOT, NOT A STACK, AND IT ASSERTS. Nested band scopes would mean two answers
    /// to "which band am I on" (rule 57: two "which side" answers = one wrong), so Push on a
    /// live scope is a hard error rather than a silent nesting. Depth is still tracked so a
    /// half-taken scope unwinds cleanly - the same discipline as ABGLContextBorrow (§56r).
    ///
    /// ⚠ INERT BY DEFAULT. <c>Active</c> is a plain static bool checked before anything else
    /// in every patch below, so outside the few hundred milliseconds of the dressing pass the
    /// cost of all of this is one static bool read on <c>Map.AllCells</c>. It is never armed
    /// outside the generation window and never armed on the Map Preview thread (the dressing
    /// pass runs inside the carve, which previews never reach).
    /// </summary>
    internal static class ABBandScope
    {
        /// <summary>Hot pre-check. Read before any other member in every patch below, so an
        /// unarmed scope costs exactly one static bool load.</summary>
        internal static bool Active;

        private static Map scopedMap;

        private static CellRect scopedRect;

        private static bool rejectOpenAir;

        private static List<IntVec3> randomOrder;

        private static int depth;

        /// <summary>Cells refused for being open air, so "the sky band generated nothing" is
        /// never an unfalsifiable observation (rule 33).</summary>
        internal static int airRejections;

        internal static CellRect Rect => scopedRect;

        /// <summary>True when a scope is armed AND it is armed for this exact map. The map
        /// identity half is load-bearing: Map Preview generates on a background thread with
        /// its own Map object, and a scope armed for the colony must never touch it (§98.f).
        /// </summary>
        internal static bool AppliesTo(Map map)
        {
            return Active && map != null && ReferenceEquals(map, scopedMap);
        }

        /// <summary>
        /// Arm the scope. <paramref name="rejectAir"/> is set for SKY bands, where most of
        /// the rect is <c>AB_OpenAir</c> and a foreign genstep that only checks affordances
        /// would happily drop a geyser into the void. ABAirSpawnGuard is the backstop, but a
        /// backstop that fires thousands of times is a design that has given up - better to
        /// never offer the cell (rule 14: ask what is at the destination).
        /// </summary>
        internal static void Push(Map map, CellRect rect, bool rejectAir)
        {
            if (depth != 0)
            {
                Log.Error(ABLog.Tag + " ABBandScope.Push called while a scope is already"
                    + " armed (existing " + scopedRect + ", requested " + rect
                    + "). Nested band scopes cannot both be right; refusing the inner one.");
                depth++;
                return;
            }
            scopedMap = map;
            scopedRect = rect;
            rejectOpenAir = rejectAir;
            randomOrder = null;
            depth = 1;
            Active = true;
        }

        internal static void Pop()
        {
            depth--;
            if (depth > 0)
            {
                return; // unwinding a refused nested push
            }
            depth = 0;
            Active = false;
            scopedMap = null;
            randomOrder = null;
        }

        /// <summary>⚠ Rule 15: assert always. A leaked scope would silently confine ordinary
        /// play to one rect, which is exactly the class of bug that hides for versions.
        /// </summary>
        internal static void AssertNoneOutstanding(string where)
        {
            if (Active || depth != 0)
            {
                Log.Error(ABLog.Tag + " ABBandScope LEAKED past " + where + " (depth=" + depth
                    + ", rect=" + scopedRect + "). Force-releasing.");
                depth = 0;
                Active = false;
                scopedMap = null;
                randomOrder = null;
            }
        }

        /// <summary>The one cell verdict. Rule 11 applies in reverse here: this is a CELL
        /// question only - whether the band will accept a thing at all is the dressing pass's
        /// business, not this method's.</summary>
        internal static bool Allows(Map map, IntVec3 c)
        {
            if (!AppliesTo(map))
            {
                return true;
            }
            if (!scopedRect.Contains(c))
            {
                return false;
            }
            if (rejectOpenAir)
            {
                TerrainDef air = ABDefOf.AB_OpenAir;
                if (air != null && map.terrainGrid.TerrainAt(c) == air)
                {
                    airRejections++;
                    return false;
                }
            }
            return true;
        }

        /// <summary>The band's cells, shuffled once per scope. Built lazily because most
        /// scoped gensteps are scatterers and never ask for it.</summary>
        internal static List<IntVec3> RandomOrder()
        {
            if (randomOrder == null)
            {
                randomOrder = new List<IntVec3>(scopedRect.Area);
                foreach (IntVec3 c in scopedRect)
                {
                    randomOrder.Add(c);
                }
                randomOrder.Shuffle();
            }
            return randomOrder;
        }
    }

    /// <summary>
    /// Funnel 1: the cell-walking gensteps.
    ///
    /// <c>Map.AllCells</c> is a compiler-generated iterator, so the getter's whole body is
    /// "new state machine" - replacing <c>__result</c> outright is exact, not an
    /// approximation, and <c>CellRect</c> is already an <c>IEnumerable&lt;IntVec3&gt;</c>
    /// over precisely the cells we want.
    ///
    /// ⚠ THIS IS A BROAD TARGET AND IT IS SCOPED ACCORDINGLY. AllCells is called from live
    /// gameplay in dozens of places; the guard is a single static bool plus a reference
    /// compare, and the scope is only ever armed inside the carve.
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.AllCells), MethodType.Getter)]
    public static class Patch_Map_ABBandScopeAllCells
    {
        private static bool Prefix(Map __instance, ref IEnumerable<IntVec3> __result)
        {
            if (!ABBandScope.Active || !ABBandScope.AppliesTo(__instance))
            {
                return true;
            }
            __result = ABBandScope.Rect;
            return false;
        }
    }

    /// <summary>
    /// Funnel 2: the shuffled walk (<c>GenStep_Plants</c> and friends).
    ///
    /// A postfix rather than a prefix so vanilla's own cached list is still built exactly
    /// once and is still there, untouched, for the rest of the game - we hand back a
    /// different list for the duration of the scope and never write to theirs.
    /// </summary>
    [HarmonyPatch(typeof(MapCellsInRandomOrder), nameof(MapCellsInRandomOrder.GetAll))]
    public static class Patch_MapCellsInRandomOrder_ABBandScope
    {
        private static void Postfix(Map ___map, ref List<IntVec3> __result)
        {
            if (!ABBandScope.Active || !ABBandScope.AppliesTo(___map))
            {
                return;
            }
            __result = ABBandScope.RandomOrder();
        }
    }

    /// <summary>
    /// Funnel 3: the scatterer family's cell search.
    ///
    /// Replaces vanilla's map-wide random search with the identical search restricted to the
    /// band. Everything else about the scatterer is untouched - its own validators, its own
    /// spacing, its own footprint rules all still run, because we call ITS
    /// <c>CanScatterAt</c> virtually rather than reimplementing the predicate (rule 36: run
    /// vanilla's predicate; rule 53: a predicate is closed over its caller's args).
    ///
    /// ⚠ <c>nearPlayerStart</c> AND <c>nearMapCenter</c> ARE DELIBERATELY NOT HANDLED HERE.
    /// Both name a place that only exists on the ground level, so a scatterer that declares
    /// either is colony-anchored content and the dressing pass refuses to run it on another
    /// band at all (see ABBandDressing.Eligible). If one somehow reaches this method we fall
    /// through to vanilla rather than inventing a "band centre" it never asked about.
    ///
    /// ⚠ THE TWO-STAGE SEARCH IS NOT BELT-AND-BRACES. Random sampling is right for the common
    /// case (a wide-open plateau, a big cavern floor) and wrong for the tail: a nearly-full
    /// band can have a handful of legal cells that 1500 random darts will miss, and silently
    /// producing nothing there is exactly the failure the user reported. The shuffled sweep
    /// is exhaustive, so "no legal cell" becomes a fact rather than a guess.
    /// </summary>
    [HarmonyPatch(typeof(GenStep_Scatterer), "TryFindScatterCell")]
    public static class Patch_GenStep_Scatterer_ABBandScopeCell
    {
        private const int RandomTries = 1500;

        private static MethodInfo canScatterAt;

        /// <summary>
        /// ⚠ THE SWEEP IS BOUNDED, AND THE BOUND IS AN ARGUMENT, NOT A GUESS.
        ///
        /// <c>CanScatterAt</c> gets STRICTLY MORE RESTRICTIVE as one <c>Generate</c> call
        /// proceeds, because <c>usedSpots</c> only ever grows and <c>NearUsedSpot</c> is a
        /// veto. So once an exhaustive sweep has proved no legal cell exists, every later
        /// try in that same call is provably hopeless too - and a scatterer whose count is
        /// 10 would otherwise pay ten full sweeps of ~16k reflective predicate calls to
        /// learn the same fact ten times.
        ///
        /// TWO sweeps are allowed rather than one because the predicate can legitimately
        /// change ONCE: vanilla flips <c>useFallback</c> after the first failure and retries
        /// with <c>fallbackValidators</c>, which is a genuinely different question. Two is
        /// the exact number of distinct predicates a scatterer can present (rule 74: name
        /// the denominator).
        /// </summary>
        private static GenStep_Scatterer exhaustedFor;

        private static int exhaustedSweeps;

        /// <summary>Called at the top of every scatterer Generate, so the budget is per-call
        /// rather than per-instance-for-all-time (gensteps are singletons reused across bands
        /// and across colonies).</summary>
        internal static void ResetSweepBudget()
        {
            exhaustedFor = null;
            exhaustedSweeps = 0;
        }

        private static bool Prefix(GenStep_Scatterer __instance, Map map, ref IntVec3 result,
            ref bool __result)
        {
            if (!ABBandScope.Active || !ABBandScope.AppliesTo(map))
            {
                return true;
            }
            if (__instance.nearPlayerStart || __instance.nearMapCenter)
            {
                return true; // colony-anchored; not ours to redirect
            }
            try
            {
                if (canScatterAt == null)
                {
                    canScatterAt = AccessTools.Method(typeof(GenStep_Scatterer), "CanScatterAt",
                        new Type[] { typeof(IntVec3), typeof(Map) });
                }
                if (canScatterAt == null)
                {
                    return true; // vanilla changed shape; leave it alone (rule 62)
                }
                object[] args = new object[2];
                args[1] = map;
                CellRect rect = ABBandScope.Rect;

                for (int i = 0; i < RandomTries; i++)
                {
                    IntVec3 c = rect.RandomCell;
                    args[0] = c;
                    // Invoke on the BASE MethodInfo dispatches virtually, so an override
                    // (ScatterGeysers, ScatterThings, a mod's subclass) is the thing that
                    // actually answers - which is the entire point.
                    if ((bool)canScatterAt.Invoke(__instance, args))
                    {
                        result = c;
                        __result = true;
                        return false;
                    }
                }

                if (!ReferenceEquals(exhaustedFor, __instance))
                {
                    exhaustedFor = null;
                    exhaustedSweeps = 0;
                }
                if (exhaustedSweeps < 2)
                {
                    List<IntVec3> sweep = ABBandScope.RandomOrder();
                    for (int i = 0; i < sweep.Count; i++)
                    {
                        args[0] = sweep[i];
                        if ((bool)canScatterAt.Invoke(__instance, args))
                        {
                            result = sweep[i];
                            __result = true;
                            return false;
                        }
                    }
                    exhaustedFor = __instance;
                    exhaustedSweeps++;
                }

                result = IntVec3.Invalid;
                __result = false;
                // Deliberately ABLog.Dev and not Log.Warning: on a sky band most rects are
                // mostly void and "this scatterer had nowhere to go" is an ordinary, correct
                // outcome. Vanilla's own warning would have been an error report from us.
                ABLog.Dev("Band dressing: " + (__instance.def?.defName ?? __instance.ToString())
                    + " found no legal cell in " + rect + " (" + rect.Area
                    + " cells, exhaustive sweeps used " + exhaustedSweeps + "/2).");
                return false;
            }
            catch (Exception e)
            {
                ABLog.Dev("Band dressing: scatter cell search failed, deferring to vanilla: "
                    + e.Message);
                return true;
            }
        }
    }
}
