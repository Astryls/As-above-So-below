using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// ARRIVING AND LEAVING VIA OTHER LEVELS - raids over the ridge, visitors down the
    /// tunnel, and the config that governs both.
    ///
    /// ⚠ WHAT WAS TRUE BEFORE THIS FILE, because half the job is bringing EXISTING behaviour
    /// under control rather than adding new behaviour:
    ///   * ARRIVALS were surface-only BY ACCIDENT, not by rule. Every walk-in funnels through
    ///     RCellFinder.TryFindRandomPawnEntryCell, whose validator demands standable +
    ///     CanReachColony edge cells - sky-band edges are open air and the basement edge is
    ///     solid rock, so the surface always won. Carve a tunnel to the map edge, though, and
    ///     nothing in vanilla would ever USE it.
    ///   * DEPARTURES already leaked across levels. TryFindBestExitSpot projects the pawn's
    ///     position to the nearest edge IN RAW STACK COORDINATES and validates with
    ///     CanReach - so a visitor who wandered upstairs already left via the sky band's
    ///     edge, uncontrolled and unconfigurable.
    ///
    /// THE SHAPE: one entry chokepoint, two exit chokepoints, and a [ThreadStatic] category
    /// latch around the call sites that know WHO is arriving (the finder itself does not).
    /// Unlatched callers - Anomaly emergences, creep joiners, sappers' PassAllDestroyable
    /// fallback - stay vanilla by construction: participation is an allowlist.
    ///
    /// ⚠⚠ EVERY PATH OUT OF THIS FILE FALLS BACK TO VANILLA. An incident that cannot find an
    /// off-surface entry cell arrives on the surface; a pawn whose allowed exits are all
    /// unreachable keeps the exit vanilla chose, even if its band is disallowed. Flavor must
    /// never break a raid or trap a pawn on the map - the fallback is load-bearing, the same
    /// way the §41c deny rule's inverse is.
    ///
    /// ⚠ BAND CHOICE IS CAPACITY-PROPORTIONAL (user's call): each eligible band's weight is
    /// its count of standable, unfogged edge cells, surface included. Mountain worlds trend
    /// toward ridge arrivals, flat worlds trend toward ~0 naturally, and there is no tuning
    /// slider to get wrong. Eligibility per direction and per category comes from the
    /// Arrivals settings tab; reachability-to-colony stays enforced per candidate cell, so
    /// an unconnected level never hosts an arrival no matter what the toggles say.
    /// </summary>
    public static class ABBandArrivals
    {
        public enum Category
        {
            Raider,
            Friendly,
            Animal
        }

        // Observe-only counters, surfaced in `AB2: combat report`.
        public static int offSurfaceArrivals;

        public static int arrivalFallbacks;

        public static int exitsRedirected;

        public static int exitsKeptDisallowed;

        public static void ResetCounters()
        {
            offSurfaceArrivals = 0;
            arrivalFallbacks = 0;
            exitsRedirected = 0;
            exitsKeptDisallowed = 0;
        }

        public static string CounterReport()
        {
            return "arrivals: offSurface=" + offSurfaceArrivals + " fellBack="
                + arrivalFallbacks + " | exits: redirected=" + exitsRedirected
                + " keptDisallowed=" + exitsKeptDisallowed;
        }

        /// <summary>The category the CURRENT entry-cell search is being run for. Null means
        /// "nobody we know" and the search stays vanilla. Set only by the latch patches
        /// below, cleared in their Finalizers (§18a: prefix state releases in a Finalizer).</summary>
        [ThreadStatic]
        internal static Category? current;

        internal static Category ClassifyFaction(Faction f)
        {
            if (f == null)
            {
                return Category.Friendly; // quest refugees and the like
            }
            Faction player = Faction.OfPlayerSilentFail;
            return player != null && f.HostileTo(player) ? Category.Raider : Category.Friendly;
        }

        internal static Category ClassifyPawn(Pawn p)
        {
            if (p.Faction == null || p.RaceProps == null || p.RaceProps.Animal)
            {
                return Category.Animal;
            }
            return ClassifyFaction(p.Faction);
        }

        internal static bool ArriveAllowed(Category c, bool upper)
        {
            ABSettings s = ABMod.Settings;
            if (s == null || !s.crossLevelTravel)
            {
                return false;
            }
            switch (c)
            {
                case Category.Raider: return upper ? s.raiderArriveUpper : s.raiderArriveLower;
                case Category.Animal: return upper ? s.animalArriveUpper : s.animalArriveLower;
                default: return upper ? s.friendlyArriveUpper : s.friendlyArriveLower;
            }
        }

        internal static bool LeaveAllowed(Category c, bool upper)
        {
            ABSettings s = ABMod.Settings;
            if (s == null || !s.crossLevelTravel)
            {
                return false;
            }
            switch (c)
            {
                case Category.Raider: return upper ? s.raiderLeaveUpper : s.raiderLeaveLower;
                case Category.Animal: return upper ? s.animalLeaveUpper : s.animalLeaveLower;
                default: return upper ? s.friendlyLeaveUpper : s.friendlyLeaveLower;
            }
        }

        // ---- edge capacity ---------------------------------------------------

        private sealed class EdgeCapacity
        {
            public int computedAtTick = -99999;

            public int[] standableEdgeCells;
        }

        private static readonly ConditionalWeakTable<Map, EdgeCapacity> capacity =
            new ConditionalWeakTable<Map, EdgeCapacity>();

        private const int CapacityTtlTicks = 2500;

        /// <summary>Standable, unfogged edge cells per band. The x edges run the full stack,
        /// so each band contributes its own rows; the z edges exist only for the bottom band
        /// (z = 0) and the top one (z = max). ~1,300 cells scanned per refresh at most once
        /// per <see cref="CapacityTtlTicks"/> - arrival decisions are rare, terrain at map
        /// edges changes rarely, and a slightly stale weight only skews flavor.</summary>
        internal static int[] EdgeCellsPerBand(Map map, ABBandMap bands)
        {
            EdgeCapacity cap = capacity.GetValue(map, _ => new EdgeCapacity());
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cap.standableEdgeCells != null
                && now - cap.computedAtTick < CapacityTtlTicks)
            {
                return cap.standableEdgeCells;
            }
            int[] counts = new int[bands.bandCount];
            int xMax = map.Size.x - 1;
            FogGrid fog = map.fogGrid;
            for (int b = 0; b < bands.bandCount; b++)
            {
                int z0 = b * bands.Slot;
                int z1 = z0 + bands.bandHeight;
                int n = 0;
                for (int z = z0; z < z1; z++)
                {
                    IntVec3 w = new IntVec3(0, 0, z);
                    IntVec3 e = new IntVec3(xMax, 0, z);
                    if (w.Standable(map) && !fog.IsFogged(w)) n++;
                    if (e.Standable(map) && !fog.IsFogged(e)) n++;
                }
                int zRow = b == 0 ? 0 : (b == bands.bandCount - 1 ? map.Size.z - 1 : -1);
                if (zRow >= 0)
                {
                    for (int x = 0; x <= xMax; x++)
                    {
                        IntVec3 c = new IntVec3(x, 0, zRow);
                        if (c.Standable(map) && !fog.IsFogged(c)) n++;
                    }
                }
                counts[b] = n;
            }
            cap.standableEdgeCells = counts;
            cap.computedAtTick = now;
            return counts;
        }

        /// <summary>Weighted pick over the surface plus every toggled band with capacity.
        /// Returns the surface when the dice say so or nothing else qualifies.</summary>
        internal static int PickArrivalBand(Map map, ABBandMap bands, Category cat)
        {
            int surface = bands.surfaceBand;
            int[] cells = EdgeCellsPerBand(map, bands);
            // Unopened bands need no special skip: they are fogged, so their edge cells
            // fail the capacity scan's fog test, and CanReachColony refuses the stragglers.
            int roll = Rand.RangeInclusive(1, TotalWeight(bands, cells, cat, surface));
            int acc = Mathf.Max(cells[surface], 1);
            if (roll <= acc)
            {
                return surface;
            }
            for (int b = 0; b < bands.bandCount; b++)
            {
                if (b == surface || !ArriveAllowed(cat, b > surface) || cells[b] <= 0)
                {
                    continue;
                }
                acc += cells[b];
                if (roll <= acc)
                {
                    return b;
                }
            }
            return surface;
        }

        private static int TotalWeight(ABBandMap bands, int[] cells, Category cat, int surface)
        {
            int total = Mathf.Max(cells[surface], 1);
            for (int b = 0; b < bands.bandCount; b++)
            {
                if (b != surface && ArriveAllowed(cat, b > surface) && cells[b] > 0)
                {
                    total += cells[b];
                }
            }
            return total;
        }

        /// <summary>Bands this pawn may exit from, nearest-to-its-own first. The surface is
        /// always allowed - forbidding every exit would trap pawns on the map, and the
        /// fallback in the postfix guards even that.</summary>
        internal static void AllowedExitBands(ABBandMap bands, Category cat, int pawnBand,
            List<int> outBands)
        {
            outBands.Clear();
            int surface = bands.surfaceBand;
            for (int b = 0; b < bands.bandCount; b++)
            {
                if (b == surface
                    || (b > surface && LeaveAllowed(cat, upper: true))
                    || (b < surface && LeaveAllowed(cat, upper: false)))
                {
                    outBands.Add(b);
                }
            }
            int Key(int b) => Mathf.Abs(b - pawnBand) * 16 + Mathf.Abs(b - surface);
            outBands.Sort((a, b2) => Key(a).CompareTo(Key(b2)));
        }
    }

    /// <summary>
    /// THE ENTRY CHOKEPOINT. Every walk-in in the game - raids, visitors, traders, herds,
    /// manhunters, ~25 call sites - funnels through this one method; the latch tells us who
    /// is walking, and no latch means vanilla untouched.
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomPawnEntryCell))]
    public static class Patch_RCellFinder_ABBandArrival
    {
        private static bool Prefix(ref IntVec3 result, Map map, float roadChance,
            bool allowFogged, Predicate<IntVec3> extraValidator, ref bool __result)
        {
            try
            {
                ABBandArrivals.Category? cat = ABBandArrivals.current;
                if (cat == null || map == null)
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return true;
                }
                int chosen = ABBandArrivals.PickArrivalBand(map, bands, cat.Value);
                if (chosen == bands.surfaceBand)
                {
                    return true; // the dice said surface: vanilla is the surface expert
                }
                // Vanilla's own validator, restricted to the chosen band. CanReachColony is
                // the load-bearing clause: an unconnected level can never host an arrival,
                // whatever the toggles say.
                bool ok = CellFinder.TryFindRandomEdgeCellWith(
                    (IntVec3 c) => bands.BandOf(c) == chosen && !bands.InGutter(c)
                        && c.Standable(map)
                        && (allowFogged || !c.Fogged(map))
                        && (map.TileInfo.AllowRoofedEdgeWalkIn || !map.roofGrid.Roofed(c))
                        && map.reachability.CanReachColony(c)
                        && (extraValidator == null || extraValidator(c)),
                    map, roadChance, out IntVec3 found);
                if (!ok)
                {
                    // No standable, colony-connected edge on that band right now. Fall back
                    // to vanilla's surface search: flavor never breaks an incident.
                    ABBandArrivals.arrivalFallbacks++;
                    return true;
                }
                result = found;
                __result = true;
                ABBandArrivals.offSurfaceArrivals++;
                ABLog.Dev("Cross-level arrival: " + cat.Value + " entering on band " + chosen
                    + " (level " + (chosen - bands.surfaceBand) + ") at " + found + ".");
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 cross-level arrival");
            }
            return true;
        }
    }

    /// <summary>
    /// EXIT CHOKEPOINT ONE: the "best" exit, used by fleeing raiders, departing guests and
    /// trade caravans. Vanilla's answer lands on whatever band the pawn stands on; when that
    /// band's exits are disallowed for this category, redirect to the nearest allowed band
    /// the pawn can actually reach - and when nothing allowed is reachable, keep vanilla's
    /// answer, because a disallowed exit beats a pawn trapped on the map forever.
    /// </summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindBestExitSpot))]
    public static class Patch_RCellFinder_ABBandExitBest
    {
        private static void Postfix(Pawn pawn, ref IntVec3 spot, TraverseMode mode,
            bool canBash, ref bool __result)
        {
            ABBandExits.Redirect(pawn, ref spot, mode, canBash, ref __result);
        }
    }

    /// <summary>EXIT CHOKEPOINT TWO: the "random" exit, used by roaming and panicking
    /// animals and a handful of mental states. Same rule, same fallback.</summary>
    [HarmonyPatch(typeof(RCellFinder), nameof(RCellFinder.TryFindRandomExitSpot))]
    public static class Patch_RCellFinder_ABBandExitRandom
    {
        private static void Postfix(Pawn pawn, ref IntVec3 spot, TraverseMode mode,
            ref bool __result)
        {
            ABBandExits.Redirect(pawn, ref spot, mode, canBash: false, ref __result);
        }
    }

    internal static class ABBandExits
    {
        [ThreadStatic]
        private static List<int> scratch;

        internal static void Redirect(Pawn pawn, ref IntVec3 spot, TraverseMode mode,
            bool canBash, ref bool __result)
        {
            try
            {
                if (!__result || pawn == null || !pawn.Spawned)
                {
                    return;
                }
                // ⚠ NEVER TOUCH PLAYER PAWNS. Colonists leaving with a caravan, slaves in a
                // rebellion, player animals being released - their exit semantics belong to
                // the player and to vanilla, not to the NPC travel toggles.
                if (pawn.Faction == Faction.OfPlayerSilentFail)
                {
                    return;
                }
                ABSettings s = ABMod.Settings;
                if (s == null || !s.crossLevelTravel)
                {
                    return;
                }
                Map map = pawn.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                ABBandArrivals.Category cat = ABBandArrivals.ClassifyPawn(pawn);
                int surface = bands.surfaceBand;
                int spotBand = bands.BandOf(spot);
                bool allowed = spotBand == surface
                    || (spotBand > surface && ABBandArrivals.LeaveAllowed(cat, upper: true))
                    || (spotBand < surface && ABBandArrivals.LeaveAllowed(cat, upper: false));
                if (allowed)
                {
                    return;
                }
                if (scratch == null)
                {
                    scratch = new List<int>();
                }
                ABBandArrivals.AllowedExitBands(bands, cat,
                    bands.BandOf(pawn.Position), scratch);
                for (int i = 0; i < scratch.Count; i++)
                {
                    int b = scratch[i];
                    if (CellFinder.TryFindRandomEdgeCellWith(
                        (IntVec3 c) => bands.BandOf(c) == b && !bands.InGutter(c)
                            && c.Standable(map) && !c.Fogged(map)
                            && pawn.CanReach(c, Verse.AI.PathEndMode.OnCell, Danger.Deadly,
                                canBash, canBashFences: false, mode),
                        map, 0f, out IntVec3 found))
                    {
                        spot = found;
                        ABBandArrivals.exitsRedirected++;
                        return;
                    }
                }
                // Nothing allowed is reachable. Keep vanilla's spot: a pawn that leaves via
                // a disallowed level is a flavor miss; a pawn that can never leave is a bug.
                ABBandArrivals.exitsKeptDisallowed++;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 cross-level exit");
            }
        }
    }

    // ---- the category latches ---------------------------------------------
    //
    // The entry finder cannot know who is arriving, but its CALLERS can. Each latch is a
    // prefix that sets the category and a Finalizer that clears it - a Finalizer, not a
    // postfix, because Harmony runs finalizers even when the original throws, and a latch
    // left set would silently re-band the NEXT unrelated arrival (§18a).

    [HarmonyPatch(typeof(PawnsArrivalModeWorker_EdgeWalkIn),
        nameof(PawnsArrivalModeWorker_EdgeWalkIn.TryResolveRaidSpawnCenter))]
    public static class Patch_EdgeWalkIn_ABArrivalLatch
    {
        private static void Prefix(IncidentParms parms)
        {
            ABBandArrivals.current = ABBandArrivals.ClassifyFaction(parms?.faction);
        }

        private static void Finalizer()
        {
            ABBandArrivals.current = null;
        }
    }

    /// <summary>The Distributed workers pick a fresh entry cell PER GROUP inside Arrive, so
    /// the latch has to cover the whole call. Groups may land on different levels, which is
    /// a feature: a two-pronged raid over the ridge and through the front door.</summary>
    [HarmonyPatch]
    public static class Patch_EdgeWalkInDistributed_ABArrivalLatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(PawnsArrivalModeWorker_EdgeWalkInDistributed),
                "Arrive");
        }

        private static bool Prepare()
        {
            return TargetMethod() != null;
        }

        private static void Prefix(IncidentParms parms)
        {
            ABBandArrivals.current = ABBandArrivals.ClassifyFaction(parms?.faction);
        }

        private static void Finalizer()
        {
            ABBandArrivals.current = null;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_NeutralGroup), "TryResolveParmsGeneral")]
    public static class Patch_NeutralGroup_ABArrivalLatch
    {
        private static void Prefix()
        {
            ABBandArrivals.current = ABBandArrivals.Category.Friendly;
        }

        private static void Finalizer()
        {
            ABBandArrivals.current = null;
        }
    }

    /// <summary>
    /// The animal incidents each call the entry finder from their own TryExecuteWorker.
    /// One latch class per def-worker, all four lines long, all Prepare-guarded so a future
    /// vanilla rename degrades to "that incident stays surface-only" instead of a startup
    /// error. Herd migration, wanderers, thrumbos, beavers, manhunters: through holes in the
    /// world, in and out.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_AnimalIncidents_ABArrivalLatch
    {
        private static readonly Type[] Workers =
        {
            typeof(IncidentWorker_AggressiveAnimals),
            typeof(IncidentWorker_FarmAnimalsWanderIn),
            typeof(IncidentWorker_HerdMigration),
            typeof(IncidentWorker_ThrumboPasses),
            typeof(IncidentWorker_Alphabeavers)
        };

        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            for (int i = 0; i < Workers.Length; i++)
            {
                System.Reflection.MethodBase m =
                    AccessTools.Method(Workers[i], "TryExecuteWorker");
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static void Prefix()
        {
            ABBandArrivals.current = ABBandArrivals.Category.Animal;
        }

        private static void Finalizer()
        {
            ABBandArrivals.current = null;
        }
    }
}
