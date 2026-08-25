using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WHERE THINGS ARRIVING FROM THE SKY ARE ALLOWED TO COME DOWN.
    ///
    /// One authority, two rules, and they are not symmetrical:
    ///
    ///   BASEMENTS: NEVER. ABSOLUTE. A sub-surface band is a sealed box of rock with a
    ///   mountain on top of it. Nothing falls into it from orbit, ever - not a raid pod, not
    ///   a trade delivery, not a shuttle, not a quest reward, not an Anomaly arrival. There
    ///   is no exception and no "unless" clause, because every exception anyone could want
    ///   here reads to the player as a bug: cargo materialising inside solid stone.
    ///
    ///   SKY BANDS: ONLY IF STAIRS REACH THEM. An upper level the colony has actually built
    ///   its way up to is a rooftop, and things may land on a rooftop. An upper level with no
    ///   link chain to the surface is a mountain summit the colony has never visited, and a
    ///   trade shuttle putting your silver on it would be a soft-lock. So a sky band becomes
    ///   a legal destination exactly when a chain of vertical links connects it to the
    ///   surface band, and stops being one again the moment that chain is broken.
    ///
    /// \u26a0 WHY THIS IS A CLAMP AT SELECTION AND NOT A CORRECTION AFTER THE FACT (rule 1).
    /// Relocating a skyfaller that has already picked its target is far too late: the pod is
    /// a live Thing with an animation, a shadow, a landing effecter and, for a shuttle, a
    /// whole `TransportShip` job chain keyed to the cell it chose. Rejecting the CELL while
    /// the finder is still choosing costs one band lookup and leaves every downstream system
    /// with an answer it can trust.
    ///
    /// \u26a0 THE TWO CHOKE POINTS ARE `CanPhysicallyDropInto` AND `SkyfallerCanLandAt`, AND
    /// TOGETHER THEY ARE ALMOST THE WHOLE SURFACE AREA. `IsGoodDropSpot` calls the first,
    /// and `TryFindDropSpotNear`, the raid drop centres and the quest/reward droppers all
    /// call `IsGoodDropSpot`. `IsSafeDropSpot` backs the second, and `FindSafeLandingSpot`,
    /// `TryFindSafeLandingSpotCloseToColony`, `GetBestShuttleLandingSpot` and
    /// `TryFindShipLandingArea` all route through it - which is why Odyssey shuttles and
    /// third-party shuttle mods (CeleTech Arsenal - Shuttle Extension and friends) are
    /// covered without naming any of them: they land through vanilla's finders, so they
    /// inherit the policy for free. A mod that hand-rolls its own landing search is the only
    /// thing that can escape, and that is a deliberate, documented gap rather than an
    /// unknown one.
    ///
    /// `RandomDropSpot` and `TradeDropSpot` are the two exceptions that do NOT route through
    /// either choke point - both call `CellFinderLoose.RandomCellWith` with their own
    /// predicate - so they get a result-remapping postfix instead.
    /// </summary>
    public static class ABLandingPolicy
    {
        /// <summary>
        /// How long a served-band answer is reused.
        ///
        /// The scan walks every artificial building on the map to find the links, which is a
        /// real cost on a large colony, and landing queries can come in bursts (a drop-spot
        /// search tests many cells). Caching per map makes a burst cost one scan. Two seconds
        /// of staleness means a staircase finished this second is a legal landing target
        /// almost immediately, which no player can perceive, and it avoids coupling this file
        /// to Building_ABStairs2's spawn/despawn path.
        /// </summary>
        private const int RecomputeInterval = 120;

        private sealed class Cache
        {
            internal int computedTick = int.MinValue;

            internal bool[] served;
        }

        private static readonly ConditionalWeakTable<Map, Cache> caches =
            new ConditionalWeakTable<Map, Cache>();

        /// <summary>Counts so "did this ever refuse anything" is one dev action away instead
        /// of a theory (\u00a714).</summary>
        public static int refusedBasement;

        public static int refusedUnservedSky;

        public static string lastRefusal = "none yet";

        public static void ResetStats()
        {
            refusedBasement = 0;
            refusedUnservedSky = 0;
            lastRefusal = "none yet";
        }

        /// <summary>May something arriving from the sky come down at this cell?
        /// Non-banded maps and anything this system cannot resolve answer YES - an unknown
        /// must never turn into a refusal (\u00a734's three-valued lesson).</summary>
        public static bool AllowsArrival(Map map, IntVec3 c)
        {
            if (map == null || !c.IsValid || !c.InBounds(map))
            {
                return true;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return true;
            }
            if (bands.InGutter(c))
            {
                return false; // the seam is not a place
            }
            int band = bands.BandOf(c);
            if (band < 0)
            {
                return true;
            }
            if (band < bands.surfaceBand)
            {
                refusedBasement++;
                lastRefusal = "basement band " + band + " at " + c;
                return false;
            }
            if (band == bands.surfaceBand)
            {
                return true;
            }
            bool[] served = Served(map, bands);
            if (served != null && band < served.Length && served[band])
            {
                return true;
            }
            refusedUnservedSky++;
            lastRefusal = "sky band " + band + " has no stair chain, at " + c;
            return false;
        }

        /// <summary>True when a chain of vertical links connects the surface band up to
        /// <paramref name="band"/>.</summary>
        public static bool BandIsServed(Map map, int band)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            if (band == bands.surfaceBand)
            {
                return true;
            }
            if (band < bands.surfaceBand)
            {
                return false;
            }
            bool[] served = Served(map, bands);
            return served != null && band < served.Length && served[band];
        }

        /// <summary>Pull a cell that broke the policy back onto the surface band, same column
        /// where possible. Used by the two finders that promise a valid cell to callers which
        /// do not check one.</summary>
        public static void RemapToLegalArrival(Map map, ref IntVec3 cell)
        {
            if (map == null || !cell.IsValid || AllowsArrival(map, cell))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            if (ABBandSafety.TryFindSurfaceCell(map, bands, cell, true, out IntVec3 safe))
            {
                ABLog.Dev("Drop spot remapped off an illegal band: " + cell + " -> " + safe + ".");
                cell = safe;
            }
        }

        private static bool[] Served(Map map, ABBandMap bands)
        {
            Cache cache = caches.GetValue(map, _ => new Cache());
            int now = Find.TickManager?.TicksGame ?? 0;
            if (cache.served != null && cache.served.Length == bands.bandCount
                && now - cache.computedTick < RecomputeInterval
                && now >= cache.computedTick)
            {
                return cache.served;
            }
            cache.served = Compute(map, bands);
            cache.computedTick = now;
            return cache.served;
        }

        /// <summary>
        /// Which bands the stairs actually reach, walked upward from the surface.
        ///
        /// \u26a0 A LINK IS NOT ASSUMED TO SPAN EXACTLY ONE LEVEL. `Building_ABStairs2.LevelDelta`
        /// comes from a def extension and a mod (or a future def of ours) may set it to two.
        /// Collecting real band PAIRS and closing over them handles any span, where indexing
        /// a "link between b and b+1" array would silently drop a two-level shaft and declare
        /// the top unreachable.
        ///
        /// Only UPWARD service is computed. Downward is not an oversight: the basement ban is
        /// absolute, so a link into a basement can never make it landable and there is
        /// nothing to propagate.
        /// </summary>
        private static bool[] Compute(Map map, ABBandMap bands)
        {
            bool[] served = new bool[Mathf.Max(1, bands.bandCount)];
            if (bands.surfaceBand >= 0 && bands.surfaceBand < served.Length)
            {
                served[bands.surfaceBand] = true;
            }

            List<Thing> buildings =
                map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
            List<int> loEnds = new List<int>();
            List<int> hiEnds = new List<int>();
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!(buildings[i] is Building_ABStairs2 link) || !link.Spawned)
                {
                    continue;
                }
                int from = bands.BandOf(link.Position);
                int to = from + link.LevelDelta;
                if (from < 0 || to < 0 || from >= served.Length || to >= served.Length
                    || from == to)
                {
                    continue;
                }
                loEnds.Add(Mathf.Min(from, to));
                hiEnds.Add(Mathf.Max(from, to));
            }

            // Transitive closure. Band counts are tiny (7 at most), so the naive fixed point
            // is cheaper and far more obviously correct than any ordering trick.
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < loEnds.Count; i++)
                {
                    int lo = loEnds[i];
                    int hi = hiEnds[i];
                    if (lo < bands.surfaceBand)
                    {
                        continue; // a basement link can never confer service
                    }
                    if (served[lo] && !served[hi])
                    {
                        served[hi] = true;
                        changed = true;
                    }
                    else if (served[hi] && !served[lo])
                    {
                        served[lo] = true;
                        changed = true;
                    }
                }
            }
            return served;
        }
    }

    /// <summary>The physical-drop gate. `IsGoodDropSpot` is built on this, and nearly every
    /// pod, reward and raid drop is built on `IsGoodDropSpot`.</summary>
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.CanPhysicallyDropInto))]
    public static class Patch_DropCellFinder_ABCanPhysicallyDropInto
    {
        private static void Postfix(ref bool __result, IntVec3 c, Map map)
        {
            if (!__result)
            {
                return; // vanilla already said no; never turn a no into a yes
            }
            if (!ABLandingPolicy.AllowsArrival(map, c))
            {
                __result = false;
            }
        }
    }

    /// <summary>The skyfaller/shuttle gate. Backs `FindSafeLandingSpot`,
    /// `GetBestShuttleLandingSpot`, `TryFindShipLandingArea` and
    /// `TryFindSafeLandingSpotCloseToColony`, so Odyssey shuttles and any shuttle mod using
    /// vanilla's finders inherit the policy without being named.</summary>
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.SkyfallerCanLandAt))]
    public static class Patch_DropCellFinder_ABSkyfallerCanLandAt
    {
        private static void Postfix(ref bool __result, IntVec3 c, Map map)
        {
            if (!__result)
            {
                return;
            }
            if (!ABLandingPolicy.AllowsArrival(map, c))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// The two finders that bypass both choke points.
    ///
    /// Both build their own predicate around `CellFinderLoose.RandomCellWith`, so neither
    /// ever asks `CanPhysicallyDropInto`. They also both promise a valid cell to callers that
    /// do not check, which is why this REMAPS rather than invalidating: handing back
    /// IntVec3.Invalid here would surface as a null-ref somewhere far away, in vanilla code,
    /// with no hint that a mod caused it.
    /// </summary>
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.RandomDropSpot))]
    public static class Patch_DropCellFinder_ABRandomDropSpot
    {
        private static void Postfix(ref IntVec3 __result, Map map)
        {
            ABLandingPolicy.RemapToLegalArrival(map, ref __result);
        }
    }

    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.TradeDropSpot))]
    public static class Patch_DropCellFinder_ABTradeDropSpot
    {
        private static void Postfix(ref IntVec3 __result, Map map)
        {
            ABLandingPolicy.RemapToLegalArrival(map, ref __result);
        }
    }
}
