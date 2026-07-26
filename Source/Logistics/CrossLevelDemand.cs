using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-map cache of the materials this level still needs, WHERE it needs
    /// them, and what it can spare. Reworked 2026-07-24 (two-house ferry loop):
    ///
    /// 1. Every demand registration now carries a SITE cell (the blueprint, the
    ///    workbench, the patient, the refuelable). Sites are grouped into
    ///    walkable ISLANDS via pawn-less PassDoors reachability, because a
    ///    level is often several disconnected areas (two roofs, a bridge) and
    ///    routing a delivery through stairs that cannot reach the demanding
    ///    island created infinite up-and-back ferry loops.
    /// 2. Construction demand is shortfall-based PER ISLAND: material already
    ///    lying on the demanding island counts against the need, so builders
    ///    at work stop attracting surplus ferries.
    /// 3. Export pinning shrank to exactly two rules (storage priority wins
    ///    everywhere else, per vanilla parity): a stack on an island whose own
    ///    construction still needs it stays; a stack that a demand-pull just
    ///    imported stays for a short window (the import tag) so it cannot
    ///    bounce straight back to better storage on the level it came from.
    ///    Meals, bill ingredients, and fuel now always flow to best storage;
    ///    consumers fetch them back per need - exactly the one-big-map rule.
    ///
    /// Consumable demand (bills, meals, fuel, transporters) stays
    /// map-wide-shortfall at registration and island-scoped only for routing;
    /// consumable availability inside one island is intentionally not modeled
    /// (the local hauler distributes once goods land on the right island).
    /// </summary>
    public static class CrossLevelDemand
    {
        private static int CacheTtlTicks => ABMod.Settings?.jobCacheTtl ?? 600;

        private const int MaxBillsPerMap = 30;

        private const int MaxDefsPerBill = 40;

        /// <summary>Cap on distinct demand sites tracked per def; overflow
        /// merges into the nearest kept site so island totals stay right.</summary>
        private const int MaxSitesPerDef = 16;

        /// <summary>Cap on stacks examined for island availability.</summary>
        private const int MaxAvailScan = 400;

        /// <summary>How long an imported stack refuses re-export (~1 in-game
        /// hour): long enough for the local consumer or hauler to claim it,
        /// short enough that a genuinely misdelivered stack self-heals.</summary>
        private const int ImportPinTicks = 2500;

        private static readonly Dictionary<int, CacheEntry> cache = new Dictionary<int, CacheEntry>();

        /// <summary>thingIDNumber -> tick until which the stack is pinned to
        /// the map a demand-pull delivered it to.</summary>
        private static readonly Dictionary<int, int> importPins = new Dictionary<int, int>();

        /// <summary>How long a delivered-level entry lingers before it is
        /// pruned. Purely a dictionary bound - the RETENTION itself is the
        /// condition "still on the level it was delivered to", not this timer
        /// (that is exactly the mistake the old ImportPinTicks pin made). One
        /// in-game day is long enough that a genuinely looping item never gets
        /// a re-try window, short enough that the map stays tiny.</summary>
        private const int DeliveredExpireTicks = 60000;

        /// <summary>thingIDNumber -> the map a cross-level ferry last delivered
        /// the stack to. Drives the monotone storage pin below.</summary>
        private static readonly Dictionary<int, DeliveredEntry> deliveredTo =
            new Dictionary<int, DeliveredEntry>();

        private struct DeliveredEntry
        {
            public int mapId;
            public int tick;
        }

        private struct DemandSite
        {
            public IntVec3 cell;
            public int count;
            public bool construction;
            /// <summary>True for retention-only sites: the RAW consumer
            /// requirement (bill counts, mouths x meals, live refuel gap),
            /// registered regardless of shortfall. Drives the export pin so
            /// already-delivered goods stay put while the want persists;
            /// never feeds pulls or relay (2026-07-25 log-carousel fix).</summary>
            public bool requirementOnly;
            /// <summary>True for relay sites: a NEIGHBOR level's native need
            /// projected onto this map at the interchange stairs toward it.
            /// Relay sites pull goods (so two-hop chains form hop by hop) but
            /// never pin them (the pin would block the very onward hop the
            /// relay exists to feed).</summary>
            public bool relay;
        }

        private sealed class IslandDemand
        {
            /// <summary>First site cell seen for this island; used as the
            /// routing goal and the reachability anchor.</summary>
            public IntVec3 rep;
            public int consumableNeed;
            /// <summary>RAW remaining construction count on this island,
            /// INCLUDING relay (used for pull gating: total minus avail).
            /// Pull gating subtracts avail (shortfall); the export pin compares
            /// avail - stack against the NATIVE figure below. Never pre-reduce
            /// these - doing both against a shortfall double-counts
            /// availability and lets stacks export that the island needs.</summary>
            public int constructionNeed;
            /// <summary>Construction need from THIS map's own blueprints only
            /// (relay excluded). The export pin uses this: goods parked at an
            /// interchange must stay liftable toward the demanding level.</summary>
            public int nativeConstructionNeed;
            /// <summary>RAW consumable requirement on this island (native
            /// only). The export pin holds stacks while island availability
            /// stays below this - the condition-based generalization of the
            /// construction pin, so meals near patients and fuel near burners
            /// cannot be dragged back to storage while still wanted.</summary>
            public int consumableRequired;
            /// <summary>Stacks of the def reachable from rep, lazily counted.</summary>
            public int avail = -1;
        }

        private sealed class CacheEntry
        {
            public int tick;
            public readonly Dictionary<ThingDef, List<DemandSite>> sites = new Dictionary<ThingDef, List<DemandSite>>();
            /// <summary>Total registered per def (construction raw remaining +
            /// consumable shortfalls + relay). Fast existence gate only; island
            /// math is authoritative for actual pulls and pins.</summary>
            public readonly Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, int> constructionNeed = new Dictionary<ThingDef, int>();
            /// <summary>This map's OWN need (relay excluded). Neighbors read
            /// these when building their relay - one hop only, no cascades:
            /// relay never feeds relay, so a 3-level column cannot loop.</summary>
            public readonly Dictionary<ThingDef, int> nativeNeed = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, int> nativeConstructionNeed = new Dictionary<ThingDef, int>();
            /// <summary>RAW consumable requirement per def (native only,
            /// registered regardless of shortfall). Fast existence gate for
            /// the generalized export pin; island math is authoritative.</summary>
            public readonly Dictionary<ThingDef, int> nativeConsumableRequired = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, int> available = new Dictionary<ThingDef, int>();
            /// <summary>Lazy per-def island grouping (reach checks are paid at
            /// most once per def per cache lifetime).</summary>
            public readonly Dictionary<ThingDef, List<IslandDemand>> islands = new Dictionary<ThingDef, List<IslandDemand>>();
            /// <summary>Memo for pawn-less reach checks: source region id +
            /// dest cell index -> verdict. Things in the same stockpile share a
            /// region, so hundreds of checks collapse to a handful.</summary>
            public readonly Dictionary<long, bool> reachMemo = new Dictionary<long, bool>();
        }

        /// <summary>Drop a map's cached entry so the next query rebuilds it
        /// (event-driven wakeup, e.g. a freshly placed blueprint). Direct
        /// neighbors drop too: their entries embed this map's native need as
        /// relay sites, and a new sky blueprint must wake the surface relay
        /// immediately, not a TTL later.</summary>
        public static void Invalidate(Map map)
        {
            if (map == null)
            {
                return;
            }
            cache.Remove(map.uniqueID);
            LevelComp comp = map.Levels();
            if (comp?.upperMap != null)
            {
                cache.Remove(comp.upperMap.uniqueID);
            }
            if (comp?.lowerMap != null)
            {
                cache.Remove(comp.lowerMap.uniqueID);
            }
        }

        /// <summary>Full reset - storage settings changed somewhere, every
        /// verdict downstream of storage priorities is suspect. Cheap: entries
        /// rebuild lazily.</summary>
        public static void InvalidateAll()
        {
            cache.Clear();
        }

        /// <summary>A stack crossed levels as cargo: pin it briefly against
        /// re-export and refresh both sides' demand pictures. Called from the
        /// stair transfer for every non-pawn cargo, which also covers plain
        /// storage-bound hauls (a stored item is at its best storage; the pin
        /// never fights that).</summary>
        public static void NoteTransferred(Thing t, Map from, Map to)
        {
            if (t == null || t is Pawn || t.def == null || !t.def.EverHaulable)
            {
                return;
            }
            if (importPins.Count > 512)
            {
                PrunePins();
            }
            importPins[t.thingIDNumber] = Find.TickManager.TicksGame + ImportPinTicks;
            NoteDelivered(t, to);
            Invalidate(from);
            Invalidate(to);
        }

        private static void PrunePins()
        {
            int now = Find.TickManager.TicksGame;
            List<int> dead = null;
            foreach (KeyValuePair<int, int> kvp in importPins)
            {
                if (kvp.Value <= now)
                {
                    (dead ?? (dead = new List<int>())).Add(kvp.Key);
                }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                {
                    importPins.Remove(dead[i]);
                }
            }
            if (importPins.Count > 512)
            {
                importPins.Clear();
            }
        }

        private static bool ImportPinned(Thing t)
        {
            return importPins.TryGetValue(t.thingIDNumber, out int until)
                && Find.TickManager.TicksGame < until;
        }

        /// <summary>Record which map a ferry just delivered a stack to, for the
        /// monotone storage pin (DeliveredHere).</summary>
        private static void NoteDelivered(Thing t, Map to)
        {
            if (t == null || to == null)
            {
                return;
            }
            if (deliveredTo.Count > 1024)
            {
                PruneDelivered();
            }
            deliveredTo[t.thingIDNumber] = new DeliveredEntry
            {
                mapId = to.uniqueID,
                tick = Find.TickManager.TicksGame
            };
        }

        /// <summary>Monotone storage retention (2026-07-25, the "hauls an item up
        /// and down the stairs forever" report). A cross-level ferry delivered
        /// this stack to THIS map, so the STORAGE flows must not lift it back
        /// off again. Vanilla picks strictly-better storage, but a destination's
        /// free space is a race - the scan sees an open slot, a local hauler
        /// claims it before the carrier finishes climbing - so an item chasing a
        /// nearly-full better-storage one level away lands in worse storage here
        /// and instantly chases it again. Storage type is irrelevant (zone,
        /// shelf, or a modded container all present the same race). Pinning the
        /// item to the level it was delivered to makes every storage move
        /// monotone: it can be pulled onward by real DEMAND (a consumer, a
        /// blueprint - those gates never call this), but never storage-bounced
        /// back the way it came. Condition-based on purpose: the pin holds only
        /// while the item stays on this map and self-clears the instant it
        /// leaves (consumed, demand-pulled, hand-moved) or its entry ages out.</summary>
        private static bool DeliveredHere(Map map, Thing t)
        {
            if (map == null || t == null
                || !deliveredTo.TryGetValue(t.thingIDNumber, out DeliveredEntry e))
            {
                return false;
            }
            if (Find.TickManager.TicksGame - e.tick > DeliveredExpireTicks)
            {
                deliveredTo.Remove(t.thingIDNumber);
                return false;
            }
            return e.mapId == map.uniqueID;
        }

        private static void PruneDelivered()
        {
            int now = Find.TickManager.TicksGame;
            List<int> dead = null;
            foreach (KeyValuePair<int, DeliveredEntry> kvp in deliveredTo)
            {
                if (now - kvp.Value.tick > DeliveredExpireTicks)
                {
                    (dead ?? (dead = new List<int>())).Add(kvp.Key);
                }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                {
                    deliveredTo.Remove(dead[i]);
                }
            }
            if (deliveredTo.Count > 1024)
            {
                deliveredTo.Clear();
            }
        }

        // --- in-flight ledger (2026-07-25) --------------------------------
        //
        // "They need to be aware other pawns are doing a task that requires
        // traversing levels" (user report): demand shortfalls were computed
        // from map contents only, so every idle hauler saw the SAME shortfall
        // until the cache TTL and each ferried a full stack - goods piled up
        // at the stair mouths far beyond what the level wanted. Every
        // demand-routed haul now claims what it carries toward which map;
        // want queries net the ledger out, and job counts clamp to the
        // residual, so the second hauler only covers what the first left.

        private const int InFlightExpireTicks = 5000;

        private struct InFlightEntry
        {
            public Pawn pawn;
            public int demandMapId;
            public ThingDef def;
            public int count;
            public int expire;
        }

        /// <summary>pawn id -> its single outstanding demand errand (a pawn
        /// carries one demand load at a time, so re-claims overwrite).</summary>
        private static readonly Dictionary<int, InFlightEntry> inFlight = new Dictionary<int, InFlightEntry>();

        /// <summary>Claim a demand errand: pawn is about to carry count of def
        /// toward demandMap. Called at job-build time by every demand-routed
        /// flow (haul giver, fetch giver's outbound leg and haul-back, the
        /// construction supply giver, and the onward interchange hop).</summary>
        public static void NoteInFlight(Pawn pawn, Map demandMap, ThingDef def, int count)
        {
            if (pawn == null || demandMap == null || def == null || count <= 0)
            {
                return;
            }
            if (inFlight.Count > 128)
            {
                inFlight.Clear();
            }
            inFlight[pawn.thingIDNumber] = new InFlightEntry
            {
                pawn = pawn,
                demandMapId = demandMap.uniqueID,
                def = def,
                count = count,
                expire = Find.TickManager.TicksGame + InFlightExpireTicks
            };
        }

        /// <summary>Sum of cargo currently en route toward demandMap for def,
        /// excluding the acting pawn's own claim (its stale entry must never
        /// block the errand it is executing). Entries self-validate: the pawn
        /// must be alive and either mid cross-level errand (stairs/haul job)
        /// or already on the demand map with the cargo in its arms (the store
        /// leg); anything else is pruned on sight - no driver hooks needed.</summary>
        private static int InFlightToward(Map demandMap, ThingDef def, Pawn ignore)
        {
            if (inFlight.Count == 0)
            {
                return 0;
            }
            int now = Find.TickManager.TicksGame;
            int sum = 0;
            List<int> dead = null;
            foreach (KeyValuePair<int, InFlightEntry> kvp in inFlight)
            {
                InFlightEntry e = kvp.Value;
                if (!InFlightLive(e, now))
                {
                    (dead ?? (dead = new List<int>())).Add(kvp.Key);
                    continue;
                }
                if (ignore != null && kvp.Key == ignore.thingIDNumber)
                {
                    continue;
                }
                if (e.demandMapId == demandMap.uniqueID && e.def == def)
                {
                    sum += e.count;
                }
            }
            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                {
                    inFlight.Remove(dead[i]);
                }
            }
            return sum;
        }

        private static bool InFlightLive(InFlightEntry e, int now)
        {
            if (now >= e.expire || e.pawn == null || e.pawn.Dead || !e.pawn.Spawned)
            {
                return false;
            }
            JobDef cur = e.pawn.CurJobDef;
            if (cur == ABDefOf.AB_UseStairs || cur == ABDefOf.AB_HaulAcrossLevels
                || cur == ABDefOf.AB_BulkHaulAcrossLevels)
            {
                return true;
            }
            // Store leg: arrived on the demand map, cargo still in hand.
            return e.pawn.Map != null && e.pawn.Map.uniqueID == e.demandMapId
                && e.pawn.carryTracker?.CarriedThing?.def == e.def;
        }

        /// <summary>Push side: the pawn stands next to stack t on its own map;
        /// does a linked level want it, and which stairs actually reach the
        /// wanting island? Strict: no routable island, no verdict.</summary>
        public static bool TryRouteDemand(Pawn pawn, Map demandMap, Thing t,
            out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            return TryRouteDemand(pawn, demandMap, t, out stairs, out exit, out int _);
        }

        /// <summary>Count-aware variant: wanted is the residual item count the
        /// matched island still pulls AFTER netting out other pawns' en-route
        /// cargo - callers clamp their job counts to it so demand hauls carry
        /// exactly what is missing, never the whole stack.</summary>
        public static bool TryRouteDemand(Pawn pawn, Map demandMap, Thing t,
            out Building_ABStairs stairs, out Building_ABStairs exit, out int wanted)
        {
            stairs = null;
            exit = null;
            wanted = 0;
            if (pawn == null || demandMap == null || demandMap.Disposed || t?.def == null)
            {
                return false;
            }
            CacheEntry entry = GetEntry(demandMap);
            if (!entry.need.TryGetValue(t.def, out int n) || n <= 0)
            {
                return false;
            }
            int inflight = InFlightToward(demandMap, t.def, pawn);
            List<IslandDemand> islands = IslandsFor(demandMap, entry, t.def);
            for (int i = 0; i < islands.Count; i++)
            {
                int want = IslandWantCount(demandMap, entry, t.def, islands[i], constructionOnly: false);
                if (want <= 0)
                {
                    continue;
                }
                // Consume the ledger against islands in scan order (stable per
                // cache lifetime): cargo already en route covers the earliest
                // islands first, and only uncovered want routes a new trip.
                int covered = Math.Min(want, inflight);
                want -= covered;
                inflight -= covered;
                if (want <= 0)
                {
                    continue;
                }
                if (StairRouter.TryBestToward(pawn, demandMap, islands[i].rep, requireReach: true,
                    out stairs, out exit))
                {
                    wanted = want;
                    return true;
                }
            }
            stairs = null;
            exit = null;
            return false;
        }

        /// <summary>Pull side, executed FROM the source map (physically for the
        /// supply giver and the return leg, virtually for the fetch probe): a
        /// stack sourceMap can spare that some routable island of demandMap
        /// still wants. Returns the stack plus the strict stair route toward
        /// the wanting island; null when either half fails.</summary>
        public static Thing FindFetchableDemand(Map demandMap, Map sourceMap, Pawn pawn,
            bool requireReachable, bool constructionOnly,
            out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            return FindFetchableDemand(demandMap, sourceMap, pawn, requireReachable, constructionOnly,
                out stairs, out exit, out int _);
        }

        /// <summary>Count-aware variant: wanted is the matched island's residual
        /// want net of other pawns' en-route cargo (see TryRouteDemand).</summary>
        public static Thing FindFetchableDemand(Map demandMap, Map sourceMap, Pawn pawn,
            bool requireReachable, bool constructionOnly,
            out Building_ABStairs stairs, out Building_ABStairs exit, out int wanted)
        {
            stairs = null;
            exit = null;
            wanted = 0;
            if (demandMap == null || demandMap.Disposed || sourceMap == null || sourceMap.Disposed
                || pawn == null)
            {
                return null;
            }
            CacheEntry entry = GetEntry(demandMap);
            Dictionary<ThingDef, int> gate = constructionOnly ? entry.constructionNeed : entry.need;
            foreach (KeyValuePair<ThingDef, int> kvp in gate)
            {
                if (kvp.Value <= 0)
                {
                    continue;
                }
                Thing stack = FindExportableStack(sourceMap, kvp.Key, pawn, requireReachable);
                if (stack == null)
                {
                    continue;
                }
                int inflight = InFlightToward(demandMap, kvp.Key, pawn);
                List<IslandDemand> islands = IslandsFor(demandMap, entry, kvp.Key);
                for (int i = 0; i < islands.Count; i++)
                {
                    int want = IslandWantCount(demandMap, entry, kvp.Key, islands[i], constructionOnly);
                    if (want <= 0)
                    {
                        continue;
                    }
                    int covered = Math.Min(want, inflight);
                    want -= covered;
                    inflight -= covered;
                    if (want <= 0)
                    {
                        continue;
                    }
                    if (StairRouter.TryBestToward(pawn, demandMap, islands[i].rep, requireReach: true,
                        out stairs, out exit))
                    {
                        wanted = want;
                        return stack;
                    }
                }
                stairs = null;
                exit = null;
            }
            return null;
        }

        /// <summary>Cheap pre-check for the fetch giver, run while the pawn
        /// still stands ON the demand map: an island the pawn itself can reach
        /// wants something the source map can spare. Pawn-reachability of the
        /// island predicts return-routability (the stairs the pawn will take
        /// outbound land its counterpart on this very island).</summary>
        public static bool HasFetchableDemand(Map demandMap, Map sourceMap, Pawn pawn)
        {
            if (demandMap == null || demandMap.Disposed || sourceMap == null || sourceMap.Disposed
                || pawn == null || pawn.Map != demandMap)
            {
                return false;
            }
            CacheEntry entry = GetEntry(demandMap);
            foreach (KeyValuePair<ThingDef, int> kvp in entry.need)
            {
                if (kvp.Value <= 0)
                {
                    continue;
                }
                int inflight = InFlightToward(demandMap, kvp.Key, pawn);
                List<IslandDemand> islands = IslandsFor(demandMap, entry, kvp.Key);
                bool anyIsland = false;
                for (int i = 0; i < islands.Count; i++)
                {
                    int want = IslandWantCount(demandMap, entry, kvp.Key, islands[i], constructionOnly: false);
                    if (want <= 0)
                    {
                        continue;
                    }
                    int covered = Math.Min(want, inflight);
                    want -= covered;
                    inflight -= covered;
                    if (want > 0 && pawn.CanReach(islands[i].rep, PathEndMode.Touch, Danger.Deadly))
                    {
                        anyIsland = true;
                        break;
                    }
                }
                if (!anyIsland)
                {
                    continue;
                }
                if (FindExportableStack(sourceMap, kvp.Key, pawn, requireReachable: false) != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Export policy for STORAGE-priority hauling (vanilla
        /// parity: storage wins everything else). Two holds: an island whose
        /// OWN need (construction or live consumer requirement) still covers
        /// the stack, and a fresh import pin.</summary>
        /// <summary>Dev diagnostic: which sub-condition of ExportAllowed (if any)
        /// is blocking a storage export of t on map.</summary>
        public static string ExportDiag(Map map, Thing t)
        {
            if (map == null || t?.def == null)
            {
                return "n/a";
            }
            return "importPinned=" + ImportPinned(t)
                + " deliveredHere=" + DeliveredHere(map, t)
                + " constrPin=" + PinnedByNativeNeed(map, t, constructionOnly: true);
        }

        public static bool ExportAllowed(Map map, Thing t)
        {
            if (map == null || map.Disposed || t?.def == null)
            {
                return true;
            }
            if (ImportPinned(t))
            {
                return false;
            }
            // Monotone: never storage-bounce a just-delivered stack back off
            // the level it landed on (demand flows use ExportAllowedForDemand
            // and are unaffected, so consumers still pull it onward).
            if (DeliveredHere(map, t))
            {
                return false;
            }
            // Retain CONSTRUCTION materials only (constructionOnly). A blueprint
            // actively pulls material toward itself and holds it until built, so
            // storage must not drag it away and trigger the re-demand "log
            // carousel". CONSUMABLES (food, medicine) are consumed on delivery -
            // they do not carousel (the just-delivered DeliveredHere retention
            // above covers the brief window) - so they follow one-big-map storage
            // priority instead: they consolidate into the player's best stockpile
            // (e.g. a Critical basement) and consumers fetch them cross-level,
            // exactly like vanilla stores medicine in a far stockpile and pawns
            // walk to it. Retaining consumables here made medicine and food ignore
            // a strictly-better stockpile the player explicitly built on another
            // level (diagnostic: ColumnStorage=cross, but exportAllowed=False).
            return !PinnedByNativeNeed(map, t, constructionOnly: true);
        }

        /// <summary>Export policy for DEMAND-pull flows (supply givers, demand
        /// hauls, relay hops). The import pin does NOT apply: a stack that just
        /// landed on an interchange level must be liftable onward toward the
        /// level that actually wants it, immediately - otherwise every two-hop
        /// chain stalls for the pin duration. Ping-pong stays impossible
        /// because demand pulls only move toward native shortfall, and the
        /// native-need pin below still protects a needing island.</summary>
        public static bool ExportAllowedForDemand(Map map, Thing t)
        {
            if (map == null || map.Disposed || t?.def == null)
            {
                return true;
            }
            return !PinnedByNativeNeed(map, t);
        }

        /// <summary>Condition-based retention (2026-07-25 log-carousel fix,
        /// generalizing the old construction-only pin): a stack standing on
        /// an island whose OWN want - remaining construction cost OR raw live
        /// consumer requirement (bills, meals, refuel, transporter loads) -
        /// exceeds what the island holds without this stack is not exported
        /// by ANY flow. The old timer pin let storage drag demand-delivered
        /// goods back after ~1h, the next cache rebuild re-registered the
        /// shortfall, and the same logs rode the stairs forever. When the
        /// want ends (bill done, torch refueled, patient healed) the pin
        /// lifts by itself and surplus flows to best storage once.</summary>
        private static bool PinnedByNativeNeed(Map map, Thing t, bool constructionOnly = false)
        {
            CacheEntry entry = GetEntry(map);
            entry.nativeConstructionNeed.TryGetValue(t.def, out int constr);
            int required = 0;
            if (!constructionOnly)
            {
                entry.nativeConsumableRequired.TryGetValue(t.def, out required);
            }
            if (constr <= 0 && required <= 0)
            {
                return false;
            }
            List<IslandDemand> islands = IslandsFor(map, entry, t.def);
            for (int i = 0; i < islands.Count; i++)
            {
                IslandDemand isl = islands[i];
                int hold = isl.nativeConstructionNeed + (constructionOnly ? 0 : isl.consumableRequired);
                if (hold <= 0)
                {
                    continue;
                }
                // The pin only concerns stacks standing ON the needing island.
                if (!ReachMemo(map, entry, t.Position, isl.rep))
                {
                    continue;
                }
                EnsureAvail(map, entry, t.def, isl);
                if (isl.avail - t.stackCount < hold)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- internals

        /// <summary>Does this island still PULL the def? Construction pulls on
        /// its shortfall (raw need minus what already lies on the island);
        /// consumables registered their shortfall at build time.</summary>
        private static bool IslandWants(Map map, CacheEntry entry, ThingDef def, IslandDemand isl, bool constructionOnly)
        {
            return IslandWantCount(map, entry, def, isl, constructionOnly) > 0;
        }

        /// <summary>How many items of def the island still pulls: construction
        /// shortfall plus (unless constructionOnly) the registered consumable
        /// shortfall. The count feeds the in-flight netting and job clamps.</summary>
        private static int IslandWantCount(Map map, CacheEntry entry, ThingDef def, IslandDemand isl, bool constructionOnly)
        {
            int want = 0;
            if (isl.constructionNeed > 0)
            {
                EnsureAvail(map, entry, def, isl);
                int shortfall = isl.constructionNeed - isl.avail;
                if (shortfall > 0)
                {
                    want += shortfall;
                }
            }
            if (!constructionOnly && isl.consumableNeed > 0)
            {
                want += isl.consumableNeed;
            }
            return want;
        }

        private static Thing FindExportableStack(Map sourceMap, ThingDef def, Pawn pawn, bool requireReachable)
        {
            List<Thing> things = sourceMap.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || !t.Spawned || t.Map != sourceMap || t.IsForbidden(pawn))
                {
                    continue;
                }
                // Never strip a level of a material its own construction needs;
                // import pins do not apply to demand pulls (relay hops must
                // lift freshly-landed cargo onward without waiting them out).
                if (!ExportAllowedForDemand(sourceMap, t))
                {
                    continue;
                }
                if (requireReachable
                    && !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced: false))
                {
                    continue;
                }
                return t;
            }
            return null;
        }

        /// <summary>Lazy island grouping for one def. Sites that pawn-lessly
        /// reach each other pool their counts; the first site cell anchors the
        /// island. Construction islands also learn their local availability
        /// when the pin or shortfall math first asks.</summary>
        private static List<IslandDemand> IslandsFor(Map map, CacheEntry entry, ThingDef def)
        {
            if (entry.islands.TryGetValue(def, out List<IslandDemand> list))
            {
                return list;
            }
            list = new List<IslandDemand>(2);
            if (entry.sites.TryGetValue(def, out List<DemandSite> sites))
            {
                for (int i = 0; i < sites.Count; i++)
                {
                    DemandSite site = sites[i];
                    IslandDemand island = null;
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (ReachMemo(map, entry, site.cell, list[j].rep))
                        {
                            island = list[j];
                            break;
                        }
                    }
                    if (island == null)
                    {
                        island = new IslandDemand { rep = site.cell };
                        list.Add(island);
                    }
                    if (site.requirementOnly)
                    {
                        // Retention only: never feeds IslandWantCount pulls.
                        island.consumableRequired += site.count;
                    }
                    else if (site.construction)
                    {
                        island.constructionNeed += site.count;
                        if (!site.relay)
                        {
                            island.nativeConstructionNeed += site.count;
                        }
                    }
                    else
                    {
                        island.consumableNeed += site.count;
                    }
                }
            }
            entry.islands[def] = list;
            return list;
        }

        private static void EnsureAvail(Map map, CacheEntry entry, ThingDef def, IslandDemand isl)
        {
            if (isl.avail >= 0)
            {
                return;
            }
            int sum = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            int scanned = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || !t.Spawned)
                {
                    continue;
                }
                if (++scanned > MaxAvailScan)
                {
                    break;
                }
                if (ReachMemo(map, entry, t.Position, isl.rep))
                {
                    sum += t.stackCount;
                }
            }
            isl.avail = sum;
        }

        /// <summary>Region-memoized pawn-less reachability. Falls back to a
        /// direct check when the source cell has no valid region (item inside
        /// a wall, mid-rebuild).</summary>
        private static bool ReachMemo(Map map, CacheEntry entry, IntVec3 from, IntVec3 to)
        {
            Region r = from.InBounds(map) ? map.regionGrid.GetValidRegionAt_NoRebuild(from) : null;
            if (r == null)
            {
                return StairIslands.PawnlessReaches(map, from, to);
            }
            long key = ((long)r.id << 32) | (uint)map.cellIndices.CellToIndex(to);
            if (entry.reachMemo.TryGetValue(key, out bool verdict))
            {
                return verdict;
            }
            verdict = StairIslands.PawnlessReaches(map, from, to);
            if (entry.reachMemo.Count < 2048)
            {
                entry.reachMemo[key] = verdict;
            }
            return verdict;
        }

        private static CacheEntry GetEntry(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(map.uniqueID, out CacheEntry entry) && now - entry.tick < CacheTtlTicks)
            {
                return entry;
            }
            if (cache.Count > 64)
            {
                cache.Clear();
            }
            entry = new CacheEntry { tick = now };
            // Cache BEFORE the relay phase: building a neighbor's entry can
            // re-enter GetEntry for this map (its relay reads OUR native need);
            // the cache hit returns this entry with its native half complete,
            // which is all a neighbor ever reads. One-hop by construction.
            cache[map.uniqueID] = entry;
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint), entry);
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame), entry);
            AddBillNeeds(map, entry);
            AddSurgeryNeeds(map, entry);
            AddMealNeeds(map, entry);
            AddRefuelNeeds(map, entry);
            AddTransporterNeeds(map, entry);
            AddRelayNeeds(map, entry);
            return entry;
        }

        /// <summary>Relay demand (2026-07-24, two-hop fix): each NEIGHBOR
        /// level's NATIVE need is projected onto this map as sites at the
        /// interchange stairs leading toward that neighbor. A sky build with
        /// its wood in the basement then resolves hop by hop: the basement's
        /// flows see "the surface wants wood (at the sky stairs)", carry it
        /// up, and the surface's own flows lift it the rest of the way. Only
        /// native need propagates - relay never feeds relay - so demand
        /// travels exactly one level outward per entry and cannot loop.
        /// Consumable relay subtracts this map's own availability first (the
        /// middle level's stock should satisfy the neighbor directly).</summary>
        private static void AddRelayNeeds(Map map, CacheEntry entry)
        {
            if (!map.TryLinkedLevels(out LevelComp comp))
            {
                return;
            }
            AddRelayFrom(map, entry, comp.upperMap);
            AddRelayFrom(map, entry, comp.lowerMap);
        }

        private static void AddRelayFrom(Map map, CacheEntry entry, Map neighbor)
        {
            if (neighbor == null || neighbor.Disposed)
            {
                return;
            }
            // The interchange: the first usable stairwell on THIS map leading
            // toward the neighbor anchors the relay island.
            IntVec3 interchange = IntVec3.Invalid;
            List<Building_ABStairs> stairs = map.Levels()?.Stairs;
            if (stairs != null)
            {
                for (int i = 0; i < stairs.Count; i++)
                {
                    Building_ABStairs s = stairs[i];
                    if (s != null && s.Spawned && (s.Ext == null || !s.Ext.utilityOnly)
                        && s.CounterpartTowards(neighbor) != null
                        && !s.PassageForbiddenForColony(neighbor))
                    {
                        interchange = s.Position;
                        break;
                    }
                }
            }
            if (!interchange.IsValid)
            {
                return;
            }
            CacheEntry neighborEntry = GetEntry(neighbor);
            foreach (KeyValuePair<ThingDef, int> kvp in neighborEntry.nativeNeed)
            {
                if (kvp.Value <= 0)
                {
                    continue;
                }
                neighborEntry.nativeConstructionNeed.TryGetValue(kvp.Key, out int constr);
                int consumable = kvp.Value - constr;
                if (constr > 0)
                {
                    // Construction relay is gated by island shortfall at query
                    // time (goods piling at the interchange count against it).
                    Register(entry, kvp.Key, interchange, constr, construction: true, relay: true);
                }
                if (consumable > 0)
                {
                    // Consumable relay: this map's own stock serves the
                    // neighbor directly through the normal one-hop pull, so
                    // only the uncovered remainder relays further out.
                    int uncovered = consumable - Available(map, entry, kvp.Key);
                    if (uncovered > 0)
                    {
                        Register(entry, kvp.Key, interchange, uncovered, construction: false, relay: true);
                    }
                }
            }
        }

        /// <summary>Register one demand at one site. Keeps the fast-gate sums
        /// in step and merges overflow into the nearest kept site so island
        /// totals stay correct when a floor holds hundreds of blueprints.</summary>
        private static void Register(CacheEntry entry, ThingDef def, IntVec3 cell, int count,
            bool construction, bool relay = false, bool requirementOnly = false)
        {
            if (def == null || count <= 0)
            {
                return;
            }
            if (requirementOnly)
            {
                // Retention bookkeeping only: raw requirement, never a pull.
                entry.nativeConsumableRequired.TryGetValue(def, out int curR);
                entry.nativeConsumableRequired[def] = curR + count;
            }
            else
            {
                entry.need.TryGetValue(def, out int cur);
                entry.need[def] = cur + count;
                if (!relay)
                {
                    entry.nativeNeed.TryGetValue(def, out int curN);
                    entry.nativeNeed[def] = curN + count;
                }
                if (construction)
                {
                    entry.constructionNeed.TryGetValue(def, out int curC);
                    entry.constructionNeed[def] = curC + count;
                    if (!relay)
                    {
                        entry.nativeConstructionNeed.TryGetValue(def, out int curNC);
                        entry.nativeConstructionNeed[def] = curNC + count;
                    }
                }
            }
            if (!entry.sites.TryGetValue(def, out List<DemandSite> list))
            {
                list = new List<DemandSite>(4);
                entry.sites[def] = list;
            }
            // Per-kind cap: overflow merges into the NEAREST site of the SAME
            // kind AND relay class (cross-merging would corrupt the native/
            // relay and construction/consumable splits the island math depends
            // on). When the cap is hit but no same-class site exists yet,
            // append - the list stays bounded and tiny.
            int kindCount = 0;
            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].construction != construction || list[i].relay != relay
                    || list[i].requirementOnly != requirementOnly)
                {
                    continue;
                }
                kindCount++;
                int d = (list[i].cell - cell).LengthHorizontalSquared;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            if (kindCount >= MaxSitesPerDef && best >= 0)
            {
                DemandSite merged = list[best];
                merged.count += count;
                list[best] = merged;
                return;
            }
            list.Add(new DemandSite
            {
                cell = cell,
                count = count,
                construction = construction,
                relay = relay,
                requirementOnly = requirementOnly
            });
        }

        private static int CountOnMap(Map map, ThingDef def)
        {
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            int sum = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                // Forbidden stacks are unusable by every consumer this cache
                // feeds (bills, meals, fuel, transporters). Counting them as
                // available suppressed real shortfalls: a forbidden corpse
                // rotting on the bench's level silently blocked the ferry of
                // the usable one from the floor below (2026-07-24 report).
                if (t == null || t.IsForbidden(Faction.OfPlayer))
                {
                    continue;
                }
                sum += t.stackCount;
            }
            return sum;
        }

        /// <summary>The def exists as at least one spawned stack somewhere in
        /// this map's column. Registration filter for wide alternative slots:
        /// only defs a cross-level pull could actually fetch get demand sites,
        /// so the per-bill def cap can never crowd out the def the player
        /// actually stockpiled.</summary>
        private static bool PresentInColumn(Map map, ThingDef def)
        {
            LevelComp controller = map.Controller();
            if (controller == null)
            {
                return map.listerThings.ThingsOfDef(def).Count > 0;
            }
            foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
            {
                Map m = kvp.Value;
                if (m != null && !m.Disposed && m.listerThings.ThingsOfDef(def).Count > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddBillNeeds(Map map, CacheEntry entry)
        {
            if (!(ABMod.Settings?.supplyBills ?? true))
            {
                return;
            }
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            int billsSeen = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!(buildings[i] is Building_WorkTable table) || !table.CurrentlyUsableForBills())
                {
                    continue;
                }
                BillStack stack = table.BillStack;
                if (stack == null)
                {
                    continue;
                }
                for (int b = 0; b < stack.Count; b++)
                {
                    if (!(stack[b] is Bill_Production bill) || !bill.ShouldDoNow())
                    {
                        continue;
                    }
                    if (++billsSeen > MaxBillsPerMap)
                    {
                        return;
                    }
                    AddRecipeIngredientNeeds(map, entry, bill, table.Position);
                }
            }
        }

        /// <summary>Shared ingredient-shortfall registration for one bill at
        /// one site: fixed ingredients directly, alternatives aggregated across
        /// the allowed defs first so any of them can satisfy the pull. Used by
        /// workbench production bills AND pawn surgery bills (medicine,
        /// hemogen packs, body parts flow to the patient's level).</summary>
        private static void AddRecipeIngredientNeeds(Map map, CacheEntry entry, Bill bill, IntVec3 site)
        {
            List<IngredientCount> ings = bill.recipe.ingredients;
            for (int k = 0; k < ings.Count; k++)
            {
                IngredientCount ing = ings[k];
                if (ing.IsFixedIngredient)
                {
                    ThingDef def = ing.FixedIngredient;
                    if (def == null)
                    {
                        continue;
                    }
                    int required = ing.CountRequiredOfFor(def, bill.recipe, bill);
                    // Retention: the raw requirement holds already-delivered
                    // ingredients near the bill while it is live.
                    Register(entry, def, site, required, construction: false, relay: false,
                        requirementOnly: true);
                    int shortfall = required - Available(map, entry, def);
                    if (shortfall > 0)
                    {
                        Register(entry, def, site, shortfall, construction: false);
                    }
                    continue;
                }
                // Alternative slots, fan-cap fix (2026-07-24): the old code
                // walked AllowedThingDefs with a blind 40-def cap on BOTH the
                // availability sum and the registration - on modlists with
                // many meats, and for butchery (the corpse filter fans out to
                // one def per race), whether the player's actual stored def
                // made the first 40 was enumeration-order luck, so cooks
                // "refused to acknowledge" meat or corpses one level away.
                // Availability now sums UNCAPPED (cached dictionary lookups);
                // registration is limited to defs actually PRESENT somewhere
                // in the column - the only defs a pull could ever fetch - with
                // the cap kept as a site-bookkeeping bound.
                int totalAvailable = 0;
                int anyRequired = 0;
                foreach (ThingDef def in ing.filter.AllowedThingDefs)
                {
                    if (!bill.ingredientFilter.Allows(def))
                    {
                        continue;
                    }
                    totalAvailable += Available(map, entry, def);
                    if (anyRequired == 0)
                    {
                        anyRequired = ing.CountRequiredOfFor(def, bill.recipe, bill);
                    }
                }
                int aggShortfall = anyRequired - totalAvailable;
                int fan = 0;
                foreach (ThingDef def in ing.filter.AllowedThingDefs)
                {
                    if (!bill.ingredientFilter.Allows(def) || !PresentInColumn(map, def))
                    {
                        continue;
                    }
                    if (++fan > MaxDefsPerBill)
                    {
                        break;
                    }
                    // Retention runs even with zero shortfall: goods that
                    // already landed must stay while the bill wants them.
                    Register(entry, def, site, anyRequired, construction: false, relay: false,
                        requirementOnly: true);
                    if (aggShortfall > 0)
                    {
                        Register(entry, def, site, aggShortfall, construction: false);
                    }
                }
            }
        }

        /// <summary>Surgery bills live on PAWNS, not workbenches (parity audit
        /// P2): a patient scheduled for surgery on a level with no medicine
        /// registers the ingredient shortfall so medicine, hemogen packs, and
        /// body parts flow to them and the doctor operates locally.</summary>
        private static void AddSurgeryNeeds(Map map, CacheEntry entry)
        {
            if (!(ABMod.Settings?.supplyBills ?? true))
            {
                return;
            }
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            int billsSeen = 0;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.health?.surgeryBills == null
                    || (p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony))
                {
                    continue;
                }
                BillStack stack = p.health.surgeryBills;
                for (int b = 0; b < stack.Count; b++)
                {
                    Bill bill = stack[b];
                    // ShouldDoNow already accounts for suspension.
                    if (bill == null || !bill.ShouldDoNow())
                    {
                        continue;
                    }
                    if (++billsSeen > MaxBillsPerMap)
                    {
                        return;
                    }
                    AddRecipeIngredientNeeds(map, entry, bill, p.PositionHeld);
                }
            }
        }

        private const int MaxRefuelablesPerMap = 40;

        /// <summary>Refuelables (parity audit P1): generators, turrets, growth
        /// vats and anything else with an auto-refuel comp register the
        /// shortfall to their target fuel level, so wood, chemfuel, and vat
        /// nutrition flow toward the level that burns them.</summary>
        private static void AddRefuelNeeds(Map map, CacheEntry entry)
        {
            if (!(ABMod.Settings?.supplyFuel ?? true))
            {
                return;
            }
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            int seen = 0;
            for (int i = 0; i < buildings.Count; i++)
            {
                CompRefuelable comp = buildings[i].TryGetComp<CompRefuelable>();
                if (comp == null || !comp.ShouldAutoRefuelNow)
                {
                    continue;
                }
                if (++seen > MaxRefuelablesPerMap)
                {
                    return;
                }
                // ITEM count, not fuel units (2026-07-25): the old math
                // ignored the fuel-per-item multiplier and over-pulled.
                int required = comp.GetFuelCountToFullyRefuel();
                if (required <= 0)
                {
                    continue;
                }
                int fan = 0;
                int totalAvailable = 0;
                foreach (ThingDef def in comp.Props.fuelFilter.AllowedThingDefs)
                {
                    if (++fan > MaxDefsPerBill)
                    {
                        break;
                    }
                    totalAvailable += Available(map, entry, def);
                }
                int shortfall = required - totalAvailable;
                fan = 0;
                foreach (ThingDef def in comp.Props.fuelFilter.AllowedThingDefs)
                {
                    if (++fan > MaxDefsPerBill)
                    {
                        break;
                    }
                    // Retention holds delivered fuel beside the burner until
                    // the refueler consumes it (or the comp stops asking).
                    Register(entry, def, buildings[i].Position, required, construction: false,
                        relay: false, requirementOnly: true);
                    if (shortfall > 0)
                    {
                        Register(entry, def, buildings[i].Position, shortfall, construction: false);
                    }
                }
            }
        }

        /// <summary>Transport pods and shuttles being loaded (parity audit P1):
        /// whatever the load manifest still wants that this level lacks pulls
        /// from linked levels; the local load-transporters giver takes over
        /// once the goods land.</summary>
        private static void AddTransporterNeeds(Map map, CacheEntry entry)
        {
            List<Thing> transporters = map.listerThings.ThingsInGroup(ThingRequestGroup.Transporter);
            for (int i = 0; i < transporters.Count; i++)
            {
                CompTransporter comp = transporters[i].TryGetComp<CompTransporter>();
                List<TransferableOneWay> load = comp?.leftToLoad;
                if (load == null)
                {
                    continue;
                }
                for (int j = 0; j < load.Count; j++)
                {
                    TransferableOneWay tr = load[j];
                    ThingDef def = tr?.ThingDef;
                    if (def == null || tr.CountToTransfer <= 0)
                    {
                        continue;
                    }
                    // Retention keeps staged cargo beside the pod until loaded.
                    Register(entry, def, transporters[i].Position, tr.CountToTransfer,
                        construction: false, relay: false, requirementOnly: true);
                    int shortfall = tr.CountToTransfer - Available(map, entry, def);
                    if (shortfall > 0)
                    {
                        Register(entry, def, transporters[i].Position, shortfall, construction: false);
                    }
                }
            }
        }

        private const int MealsPerMouth = 2;

        private static ThingDef[] mealDefs;

        /// <summary>Patient and prisoner feeding (T7 #2/#7): levels with bedridden
        /// patients or prisoners register shortfall demand for a small buffer of
        /// meals, so food flows to them and doctors and wardens feed locally.
        /// Aggregated across meal types like bill alternatives: any meal type on
        /// hand counts. Site is the first mouth found - good enough to land the
        /// buffer on the right island in every sane prison or hospital layout.</summary>
        private static void AddMealNeeds(Map map, CacheEntry entry)
        {
            if (!(ABMod.Settings?.supplyMeals ?? true))
            {
                return;
            }
            int mouths = 0;
            int babies = 0;
            IntVec3 mouthSite = IntVec3.Invalid;
            IntVec3 babySite = IntVec3.Invalid;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.IsPrisonerOfColony
                    || (p.Faction == Faction.OfPlayer && p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)))
                {
                    mouths++;
                    if (!mouthSite.IsValid)
                    {
                        mouthSite = p.PositionHeld;
                    }
                }
                if (ModsConfig.BiotechActive && p.Faction == Faction.OfPlayer
                    && p.DevelopmentalStage.Baby())
                {
                    babies++;
                    if (!babySite.IsValid)
                    {
                        babySite = p.PositionHeld;
                    }
                }
            }
            // Babies (Biotech, parity audit P2): keep a buffer of baby food
            // where the babies are, so carers feed locally.
            if (babies > 0)
            {
                ThingDef babyFood = DefDatabase<ThingDef>.GetNamedSilentFail("BabyFood");
                if (babyFood != null)
                {
                    int requiredB = babies * MealsPerMouth;
                    Register(entry, babyFood, babySite, requiredB, construction: false,
                        relay: false, requirementOnly: true);
                    int shortfallB = requiredB - Available(map, entry, babyFood);
                    if (shortfallB > 0)
                    {
                        Register(entry, babyFood, babySite, shortfallB, construction: false);
                    }
                }
            }
            if (mouths == 0)
            {
                return;
            }
            int required = mouths * MealsPerMouth;
            ThingDef[] meals = mealDefs ?? (mealDefs = new[]
            {
                ThingDefOf.MealSimple, ThingDefOf.MealFine, ThingDefOf.MealNutrientPaste,
                ThingDefOf.MealSurvivalPack, ThingDefOf.Pemmican
            });
            int totalAvailable = 0;
            for (int i = 0; i < meals.Length; i++)
            {
                if (meals[i] != null)
                {
                    totalAvailable += Available(map, entry, meals[i]);
                    // Retention keeps the delivered buffer beside the mouths
                    // while patients or prisoners remain - the old timer pin
                    // let storage reclaim it and the pull loop restarted.
                    Register(entry, meals[i], mouthSite, required, construction: false,
                        relay: false, requirementOnly: true);
                }
            }
            int shortfall = required - totalAvailable;
            if (shortfall <= 0)
            {
                return;
            }
            for (int i = 0; i < meals.Length; i++)
            {
                if (meals[i] != null)
                {
                    Register(entry, meals[i], mouthSite, shortfall, construction: false);
                }
            }
        }

        private static int Available(Map map, CacheEntry entry, ThingDef def)
        {
            if (entry.available.TryGetValue(def, out int a))
            {
                return a;
            }
            a = CountOnMap(map, def);
            entry.available[def] = a;
            return a;
        }

        private static void AddFrom(List<Thing> things, CacheEntry entry)
        {
            for (int i = 0; i < things.Count; i++)
            {
                // Install/reinstall blueprints carry their own thing and have no
                // material cost; asking them logs a vanilla error.
                if (things[i] is Blueprint_Install
                    || !(things[i] is IConstructible constructible) || things[i].Faction != Faction.OfPlayer)
                {
                    continue;
                }
                List<ThingDefCountClass> cost = constructible.TotalMaterialCost();
                for (int j = 0; j < cost.Count; j++)
                {
                    ThingDef def = cost[j].thingDef;
                    if (def == null)
                    {
                        continue;
                    }
                    int remaining = constructible.ThingCountNeeded(def);
                    if (remaining > 0)
                    {
                        Register(entry, def, things[i].Position, remaining, construction: true);
                    }
                }
            }
        }
    }
}
