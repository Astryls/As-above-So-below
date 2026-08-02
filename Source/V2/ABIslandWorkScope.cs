using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// SAME-ISLAND-FIRST CANDIDATE SELECTION: pawns prefer work and haulables on their own
    /// walkable island, and only cross plateaus when there is genuinely nothing local.
    ///
    /// WHY SELECTION AND NOT MOVEMENT. §33c and §34 both taught the same rule the hard way:
    /// a trip must be declined BEFORE the job is taken, or not at all. Any refusal at
    /// StartPath / TrySegment recreates the `CanReach=True` + `path=NOT FOUND` re-issue stall
    /// that §34 exists to eliminate. This file therefore biases WHICH target gets picked and
    /// never touches whether a picked target is pursued.
    ///
    /// WHERE THE BIAS BELONGS. `GenClosest.ClosestThingReachable` is the funnel nearly every
    /// work giver and hauling scan goes through, and it has TWO branches with different
    /// honesty:
    ///  - the regionwise BFS expands in REGION HOPS from the pawn. Region hops go through the
    ///    wormhole chain, so a cross-plateau candidate is genuinely far in the metric it
    ///    uses - this branch is island-aware for free and needs nothing from us;
    ///  - the global fallback (`ClosestThing_Global`, taken when the BFS sees 30 regions
    ///    without a hit, or the thing group is not region-findable) ranks by STRAIGHT-LINE
    ///    distance with only a `CanReach` gate. That is the liar: a plateau 15 cells away
    ///    euclidean beats a same-island target 60 cells away that is 400 cells of actual
    ///    walking through two stairwells.
    /// Wrapping the validator at the ClosestThingReachable level covers both branches
    /// uniformly rather than patching the global ranker's internals.
    ///
    /// ⚠ SCOPE: ONLY SAME-BAND, DIFFERENT-ISLAND CANDIDATES ARE DEMOTED. Cross-BAND
    /// candidates are untouched - down-across-up logistics between levels is the mod's core
    /// feature (§30, hauling free from vanilla), and demoting it would starve cross-level
    /// stockpiles. Unknown islands (-1: held things resolving oddly, cells with no region)
    /// are untouched too - §34's three-valued lesson, unknown must mean LEAVE IT ALONE.
    ///
    /// ⚠ DETECT-THEN-REDO WITH A LATCH, THE §30-POWER SHAPE. The prefix re-enters the method
    /// it patches for pass 1; without the [ThreadStatic] latch that is unbounded recursion,
    /// and a StackOverflowException in .NET is UNCATCHABLE. Pass 2 is vanilla running
    /// completely untouched (we return true), so the fallback behaviour is exact, not
    /// reconstructed.
    ///
    /// ⚠ THE COST IS A DOUBLED SCAN WHEN PASS 1 MISSES, and the gates exist to make that
    /// rare and cheap: banded map, root's band actually fragmented (one cached bool, §34),
    /// root island known. Our island filter runs BEFORE the caller's validator inside the
    /// wrapped predicate - ours is two array reads, theirs can be arbitrarily expensive.
    /// ⚠ NOT COVERED: callers that go straight to `ClosestThing_Global_Reachable` (notably
    /// WorkGiver_DoBill ingredient search). Deliberate first scope; extend only with evidence.
    /// </summary>
    [HarmonyPatch(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable))]
    public static class Patch_GenClosest_ABSameIslandFirst
    {
        [ThreadStatic]
        private static bool inLocalPass;

        // ⚠ A GUARD THAT SILENTLY EARLY-RETURNS IS INDISTINGUISHABLE FROM AN UNIMPLEMENTED
        // FEATURE (§14). Counted, and reported by `AB2: pathing report`.
        public static int localHits;

        public static int fallbacks;

        private static bool Prefix(ref Thing __result, IntVec3 root, Map map,
            ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams,
            float maxDistance, Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin,
            int searchRegionsMax, bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes, bool ignoreEntirelyForbiddenRegions,
            bool lookInHaulSources)
        {
            if (inLocalPass)
            {
                return true; // we are pass 1; run the vanilla body
            }
            try
            {
                if (map == null || !ABBands.Banded(map)
                    || !ABBandComponents.FragmentedBandAt(map, root))
                {
                    return true; // the common case: one bool per query on a settled band
                }
                int rootIsland = ABBandComponents.ComponentOf(map, root);
                if (rootIsland < 0)
                {
                    return true;
                }

                Predicate<Thing> wrapped = delegate (Thing t)
                {
                    if (!IslandAllows(map, root, rootIsland, t))
                    {
                        return false;
                    }
                    return validator == null || validator(t);
                };

                Thing local;
                inLocalPass = true;
                try
                {
                    local = GenClosest.ClosestThingReachable(root, map, thingReq, peMode,
                        traverseParams, maxDistance, wrapped, customGlobalSearchSet,
                        searchRegionsMin, searchRegionsMax, forceAllowGlobalSearch,
                        traversableRegionTypes, ignoreEntirelyForbiddenRegions,
                        lookInHaulSources);
                }
                finally
                {
                    inLocalPass = false;
                }

                if (local != null)
                {
                    localHits++;
                    __result = local;
                    return false; // a local candidate exists; take it
                }
                fallbacks++;
                return true; // nothing local: vanilla runs unrestricted, cross-island allowed
            }
            catch
            {
                // Candidate selection must never be broken by a bias. Vanilla takes over.
                inLocalPass = false;
                return true;
            }
        }

        /// <summary>True when this candidate survives pass 1. Cross-band and unknown are
        /// deliberately allowed - only a same-band different-island candidate is demoted.</summary>
        private static bool IslandAllows(Map map, IntVec3 root, int rootIsland, Thing t)
        {
            if (t == null)
            {
                return true;
            }
            IntVec3 pos = t.PositionHeld;
            if (!pos.IsValid || !pos.InBounds(map) || !ABBands.SameBand(map, root, pos))
            {
                return true;
            }
            int isle = ABBandComponents.ComponentOf(map, pos);
            return isle < 0 || isle == rootIsland;
        }
    }
}
