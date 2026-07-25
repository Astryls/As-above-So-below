using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-level "is there plausibly any work of this type here" cache, the
    /// cheap gate in front of the priority-aware cross-level probe. One entry
    /// per map, one bool per WorkTypeDef (indexed by def.index), rebuilt at
    /// most every 600 ticks from listers and designation counts - never from
    /// real work scans. Fail open: work types without an explicit detector
    /// (BasicWorker, Childcare, modded types like VE Fishing) stay plausible,
    /// so parity is preserved and only the probe cooldown bounds their cost.
    ///
    /// Event-driven invalidation: a postfix on DesignationManager.AddDesignation
    /// drops the touched map's entry and bumps a global work version. Probe
    /// cooldowns store the version they were charged at, so "I just designated
    /// mining upstairs" bypasses every pawn's cooldown on their next think
    /// cycle instead of waiting out the TTL (the global per-tick probe budget
    /// smooths the stampede).
    /// </summary>
    public static class LevelWorkSummary
    {
        private static int TtlTicks => ABMod.Settings?.jobCacheTtl ?? 600;

        private sealed class Entry
        {
            public int tick = -99999;
            public bool[] plausible;
        }

        private static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();

        private static int workVersion = 1;

        /// <summary>Bumped whenever player actions create new work (designations
        /// added). Cooldowns keyed on this react instantly to fresh orders.</summary>
        public static int WorkVersion => workVersion;

        public static void Notify_WorkChanged(Map map)
        {
            workVersion++;
            if (map != null)
            {
                cache.Remove(map.uniqueID);
            }
        }

        /// <summary>True when any giver strictly earlier than stopIndex in the
        /// pawn's ordered giver list has a plausibly non-empty work type on the
        /// target map. Pure cache reads after the entry is built.</summary>
        public static bool AnyPlausibleBefore(Map target, List<WorkGiver> order, int stopIndex)
        {
            if (target == null || target.Disposed)
            {
                return false;
            }
            bool[] plausible = GetEntry(target).plausible;
            for (int i = 0; i < stopIndex; i++)
            {
                WorkGiverDef def = order[i].def;
                if (def?.workType == null || IsOwnCrossLevelGiver(def))
                {
                    continue;
                }
                int idx = def.workType.index;
                if (idx >= plausible.Length || plausible[idx])
                {
                    return true;
                }
            }
            return false;
        }

        public static bool Plausible(Map target, WorkTypeDef workType)
        {
            if (workType == null)
            {
                return true;
            }
            bool[] plausible = GetEntry(target).plausible;
            int idx = workType.index;
            return idx >= plausible.Length || plausible[idx];
        }

        /// <summary>Our own cross-level givers must never run inside a probe on
        /// another level - they would recurse the level graph from there.</summary>
        public static bool IsOwnCrossLevelGiver(WorkGiverDef def)
        {
            return def.giverClass != null && def.giverClass.Namespace == "AsAboveSoBelow";
        }

        // ------------------------------------------------------------------
        // Entry build
        // ------------------------------------------------------------------

        private static WorkTypeDef wtFirefighter, wtDoctor, wtWarden, wtHandling, wtCooking,
            wtHunting, wtConstruction, wtGrowing, wtMining, wtPlantCutting, wtHauling,
            wtCleaning, wtResearch, wtSmithing, wtTailoring, wtCrafting, wtArt,
            wtChildcare, wtMechRepair;

        private static DesignationDef[] miningDesigs, plantDesigs, constructionDesigs, huntDesigs, tameDesigs;

        private static bool defsResolved;

        private static void ResolveDefs()
        {
            defsResolved = true;
            WorkTypeDef W(string name) => DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
            wtFirefighter = W("Firefighter");
            wtDoctor = W("Doctor");
            wtWarden = W("Warden");
            wtHandling = W("Handling");
            wtCooking = W("Cooking");
            wtHunting = W("Hunting");
            wtConstruction = W("Construction");
            wtGrowing = W("Growing");
            wtMining = W("Mining");
            wtPlantCutting = W("PlantCutting");
            wtHauling = W("Hauling");
            wtCleaning = W("Cleaning");
            wtResearch = W("Research");
            wtSmithing = W("Smithing");
            wtTailoring = W("Tailoring");
            wtCrafting = W("Crafting");
            wtArt = W("Art");
            // Biotech (null without the DLC; Set() ignores null work types).
            wtChildcare = W("Childcare");
            wtMechRepair = DefDatabase<WorkGiverDef>.GetNamedSilentFail("RepairMech")?.workType;
            DesignationDef D(string name) => DefDatabase<DesignationDef>.GetNamedSilentFail(name);
            miningDesigs = Compact(D("Mine"), D("MineVein"));
            plantDesigs = Compact(D("CutPlant"), D("HarvestPlant"), D("ExtractTree"));
            constructionDesigs = Compact(D("Deconstruct"), D("Uninstall"), D("SmoothWall"), D("SmoothFloor"), D("RemoveFloor"));
            huntDesigs = Compact(D("Hunt"));
            tameDesigs = Compact(D("Tame"), D("Slaughter"), D("ReleaseAnimalToWild"));
        }

        private static DesignationDef[] Compact(params DesignationDef[] defs)
        {
            int n = 0;
            for (int i = 0; i < defs.Length; i++)
            {
                if (defs[i] != null)
                {
                    defs[n++] = defs[i];
                }
            }
            DesignationDef[] result = new DesignationDef[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = defs[i];
            }
            return result;
        }

        private static Entry GetEntry(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(map.uniqueID, out Entry entry) && now - entry.tick < TtlTicks)
            {
                return entry;
            }
            if (!defsResolved)
            {
                ResolveDefs();
            }
            if (entry == null)
            {
                if (cache.Count > 64)
                {
                    cache.Clear();
                }
                entry = new Entry();
                cache[map.uniqueID] = entry;
            }
            Build(map, entry);
            entry.tick = now;
            return entry;
        }

        private static void Set(bool[] plausible, WorkTypeDef wt, bool value)
        {
            if (wt != null && wt.index < plausible.Length)
            {
                plausible[wt.index] = value;
            }
        }

        private static bool AnyDesig(Map map, DesignationDef[] defs)
        {
            for (int i = 0; i < defs.Length; i++)
            {
                if (map.designationManager.AnySpawnedDesignationOfDef(defs[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Build(Map map, Entry entry)
        {
            int count = DefDatabase<WorkTypeDef>.DefCount;
            bool[] p = entry.plausible;
            if (p == null || p.Length != count)
            {
                p = entry.plausible = new bool[count];
            }
            // Fail open: everything starts plausible; covered types get a
            // real verdict below.
            for (int i = 0; i < count; i++)
            {
                p[i] = true;
            }

            Set(p, wtFirefighter, map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0);
            Set(p, wtMining, AnyDesig(map, miningDesigs));
            Set(p, wtPlantCutting, AnyDesig(map, plantDesigs));
            Set(p, wtHunting, AnyDesig(map, huntDesigs));
            Set(p, wtHauling, map.listerHaulables.ThingsPotentiallyNeedingHauling().Count > 0);
            Set(p, wtCleaning, map.listerFilthInHomeArea.FilthInHomeArea.Count > 0);

            // Doctor / Warden: patients or prisoners present anywhere on the level.
            bool patients = false;
            bool prisoners = map.mapPawns.PrisonersOfColonySpawned.Count > 0;
            if (prisoners)
            {
                patients = AnyPatient(map.mapPawns.PrisonersOfColonySpawned);
            }
            bool animals = false;
            List<Pawn> colony = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < colony.Count; i++)
            {
                Pawn pawn = colony[i];
                if (!patients && IsPatient(pawn))
                {
                    patients = true;
                }
                if (!animals && pawn.RaceProps != null && pawn.RaceProps.Animal)
                {
                    animals = true;
                }
                if (patients && animals)
                {
                    break;
                }
            }
            Set(p, wtDoctor, patients);
            Set(p, wtWarden, prisoners);
            Set(p, wtHandling, animals || AnyDesig(map, tameDesigs));
            // Biotech: babies pull carers; damaged player mechs pull the
            // mechanitor's repair work type (both null-safe without the DLC).
            if (wtChildcare != null || wtMechRepair != null)
            {
                bool babies = false;
                bool damagedMech = false;
                List<Pawn> colonyPawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                for (int i = 0; i < colonyPawns.Count; i++)
                {
                    Pawn q = colonyPawns[i];
                    if (!babies && q.DevelopmentalStage.Baby())
                    {
                        babies = true;
                    }
                    if (!damagedMech && q.RaceProps.IsMechanoid
                        && q.health?.summaryHealth != null
                        && q.health.summaryHealth.SummaryHealthPercent < 1f)
                    {
                        damagedMech = true;
                    }
                    if (babies && damagedMech)
                    {
                        break;
                    }
                }
                Set(p, wtChildcare, babies);
                Set(p, wtMechRepair, damagedMech);
            }

            // Construction: blueprints, frames, repairables, rework designations.
            bool construction =
                map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count > 0
                || map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count > 0
                || map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer).Count > 0
                || AnyDesig(map, constructionDesigs)
                // Roof areas never show up as designations (parity audit P2).
                || map.areaManager.BuildRoof.TrueCount > 0
                || map.areaManager.NoRoof.TrueCount > 0;
            Set(p, wtConstruction, construction);

            // Growing: any grow zone or plant grower building.
            bool growing = false;
            List<Zone> zones = map.zoneManager.AllZones;
            for (int i = 0; i < zones.Count; i++)
            {
                if (zones[i] is Zone_Growing)
                {
                    growing = true;
                    break;
                }
            }

            // One walk over colonist buildings covers growers, bill benches and
            // research benches.
            bool bills = false;
            bool researchBench = false;
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Building b = buildings[i];
                if (!growing && b is Building_PlantGrower)
                {
                    growing = true;
                }
                else if (!researchBench && b is Building_ResearchBench)
                {
                    researchBench = true;
                }
                else if (!bills && b is Building_WorkTable table
                    && table.CurrentlyUsableForBills() && AnyActiveBill(table))
                {
                    bills = true;
                }
                if (growing && bills && researchBench)
                {
                    break;
                }
            }
            Set(p, wtGrowing, growing);
            Set(p, wtResearch, researchBench && Find.ResearchManager.GetProject() != null);
            // Bill-driven types share one coarse verdict; per-recipe work type
            // mapping is not worth the walk.
            Set(p, wtCooking, bills);
            Set(p, wtSmithing, bills);
            Set(p, wtTailoring, bills);
            Set(p, wtCrafting, bills);
            Set(p, wtArt, bills);
        }

        private static bool AnyActiveBill(Building_WorkTable table)
        {
            BillStack stack = table.BillStack;
            if (stack == null)
            {
                return false;
            }
            for (int i = 0; i < stack.Count; i++)
            {
                if (stack[i].ShouldDoNow())
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPatient(Pawn p)
        {
            if (p.health == null)
            {
                return false;
            }
            if (p.Downed && !p.InBed())
            {
                return true;
            }
            if (p.health.HasHediffsNeedingTendByPlayer())
            {
                return true;
            }
            // Scheduled surgery counts as doctor work even on a healthy,
            // walking pawn (parity P1 #6, 2026-07-25): without this a BUSY
            // surgeon's better-work probe skipped the Doctor bit for a level
            // whose only "patient" is a bionics candidate, so the operation
            // waited for the surgeon to go fully idle.
            if (p.health.surgeryBills != null && p.health.surgeryBills.AnyShouldDoNow)
            {
                return true;
            }
            return p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p);
        }

        private static bool AnyPatient(List<Pawn> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (IsPatient(list[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>New designations are the snappiest "go work over there" player
    /// action; react instantly instead of waiting out the summary TTL.</summary>
    [HarmonyPatch(typeof(DesignationManager), nameof(DesignationManager.AddDesignation))]
    internal static class Patch_Designation_WorkChanged
    {
        private static void Postfix(DesignationManager __instance)
        {
            LevelWorkSummary.Notify_WorkChanged(__instance.map);
        }
    }

    /// <summary>Fresh blueprints should pull builders and materials from other
    /// levels immediately, not after the work summary and demand caches wind
    /// down. Blueprint declares its own SpawnSetup, so this never touches the
    /// base Thing method.</summary>
    [HarmonyPatch(typeof(Blueprint), nameof(Blueprint.SpawnSetup))]
    internal static class Patch_BlueprintSpawn_WorkChanged
    {
        private static void Postfix(Blueprint __instance)
        {
            try
            {
                Map map = __instance?.Map;
                if (map != null)
                {
                    LevelWorkSummary.Notify_WorkChanged(map);
                    CrossLevelDemand.Invalidate(map);
                }
            }
            catch
            {
                // Cache freshness only; never let it break spawning.
            }
        }
    }

    /// <summary>Fire is the highest-stakes freshness case (parity P1 #7,
    /// 2026-07-25): the emergency migration path reads the live fire lister
    /// and responds instantly, but the BETTER-WORK path (a pawn busy on
    /// low-priority work whose Firefighter priority is top) consults the
    /// summary's Firefighter bit, which could be a TTL (600 ticks) stale.
    /// A spreading fire earns an instant version bump: probes re-arm and the
    /// touched map's summary rebuilds on next read. Fire declares its own
    /// SpawnSetup, so this never touches the base Thing method.</summary>
    [HarmonyPatch(typeof(Fire), nameof(Fire.SpawnSetup))]
    internal static class Patch_FireSpawn_WorkChanged
    {
        private static void Postfix(Fire __instance)
        {
            try
            {
                Map map = __instance?.Map;
                if (map != null && map.ConnectedToOtherLevel())
                {
                    LevelWorkSummary.Notify_WorkChanged(map);
                }
            }
            catch
            {
                // Cache freshness only; never let it break spawning.
            }
        }
    }

    /// <summary>New bills re-arm the column immediately (parity P1 #6/#8 +
    /// P4 #18 cadence, 2026-07-25): adding a bill - workbench production OR
    /// scheduled surgery (the surgery bill stack's giver is the PAWN) - used
    /// to wait out both the work-summary TTL (busy crafters' probes skipped
    /// the stale bills/Doctor bits) and the demand-cache TTL (ingredient
    /// ferries idled). One postfix at the shared AddBill sink bumps the work
    /// version and wakes the demand cache for the giver's map, mirroring the
    /// blueprint-spawn hook. Load-time scribe restores bypass AddBill, so
    /// this fires only on real player/runtime additions.</summary>
    [HarmonyPatch(typeof(BillStack), nameof(BillStack.AddBill))]
    internal static class Patch_BillAdded_WorkChanged
    {
        private static void Postfix(BillStack __instance)
        {
            try
            {
                Map map = __instance?.billGiver?.Map;
                if (map != null && map.ConnectedToOtherLevel())
                {
                    LevelWorkSummary.Notify_WorkChanged(map);
                    CrossLevelDemand.Invalidate(map);
                }
            }
            catch
            {
                // Cache freshness only; never let it break bill creation.
            }
        }
    }
}
