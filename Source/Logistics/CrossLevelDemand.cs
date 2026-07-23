using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-map cache of the materials this level still needs. Two sources:
    /// construction (player blueprints and frames, exact remaining counts via
    /// IConstructible.ThingCountNeeded) and production bills (T7 #1: active
    /// bills on usable benches register the SHORTFALL of one batch of their
    /// ingredients, required minus what the level already has). Bill demand is
    /// shortfall-based because bills are chronic, not one-shot like frames: a
    /// stocked kitchen registers nothing, and as the cook consumes meat the
    /// shortfall reappears and haulers top the level back up. For ingredients
    /// with alternatives (any raw food), availability is aggregated across the
    /// allowed defs before the shortfall is registered on each, so a level with
    /// enough rice does not pull meat. Used to pull materials toward other
    /// levels and to stop storage hauling from carrying materials away from a
    /// level that needs them. Delivery is loose at the stairs; local hauling
    /// and the bench's ingredient radius take it from there.
    /// </summary>
    public static class CrossLevelDemand
    {
        private static int CacheTtlTicks => ABMod.Settings?.jobCacheTtl ?? 600;

        private const int MaxBillsPerMap = 30;

        private const int MaxDefsPerBill = 40;

        private static readonly Dictionary<int, CacheEntry> cache = new Dictionary<int, CacheEntry>();

        private class CacheEntry
        {
            public int tick;
            public readonly Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();
            /// <summary>Blueprint/frame need only (a subset of need): lets the
            /// construction-work-type supply giver ship building materials
            /// without also adopting bill and meal logistics.</summary>
            public readonly Dictionary<ThingDef, int> constructionNeed = new Dictionary<ThingDef, int>();
            public readonly Dictionary<ThingDef, int> available = new Dictionary<ThingDef, int>();
        }

        /// <summary>Drop a map's cached entry so the next query rebuilds it
        /// (event-driven wakeup, e.g. a freshly placed blueprint).</summary>
        public static void Invalidate(Map map)
        {
            if (map != null)
            {
                cache.Remove(map.uniqueID);
            }
        }

        public static bool Demands(Map map, ThingDef def)
        {
            if (map == null || map.Disposed || def == null)
            {
                return false;
            }
            CacheEntry entry = GetEntry(map);
            return entry.need.TryGetValue(def, out int n) && n > 0;
        }

        /// <summary>A stack on sourceMap of a material that demandMap still needs
        /// and sourceMap can spare, or null. Lets a colonist on a level that needs
        /// something (a basement full of blueprints, a level with a hungry prisoner)
        /// go and fetch it from a linked level even when the material sits in a
        /// valid stockpile there and so never appears in the haulables lister - the
        /// gap the pure push side could not cover. Reachability is only meaningful
        /// with the pawn virtually placed on sourceMap, so pass requireReachable
        /// true only from inside such a swap.</summary>
        public static Thing FindFetchableDemand(Map demandMap, Map sourceMap, Pawn pawn, bool requireReachable,
            bool constructionOnly = false)
        {
            if (demandMap == null || demandMap.Disposed || sourceMap == null || sourceMap.Disposed
                || pawn == null)
            {
                return null;
            }
            CacheEntry entry = GetEntry(demandMap);
            foreach (KeyValuePair<ThingDef, int> kvp in constructionOnly ? entry.constructionNeed : entry.need)
            {
                if (kvp.Value <= 0)
                {
                    continue;
                }
                List<Thing> things = sourceMap.listerThings.ThingsOfDef(kvp.Key);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !t.Spawned || t.Map != sourceMap || t.IsForbidden(pawn))
                    {
                        continue;
                    }
                    // Never strip a level of a material it needs for its own work.
                    if (!ExportAllowed(sourceMap, t))
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
            }
            return null;
        }

        /// <summary>Cheap pre-check (no reachability) for the fetch work giver: does
        /// sourceMap hold anything demandMap needs and can spare?</summary>
        public static bool HasFetchableDemand(Map demandMap, Map sourceMap, Pawn pawn)
        {
            return FindFetchableDemand(demandMap, sourceMap, pawn, requireReachable: false) != null;
        }

        /// <summary>Quantity-aware pin: exporting this stack is fine as long as the
        /// level keeps enough of the material to cover its remaining construction
        /// need. A level needing 20 steel out of 500 exports freely.</summary>
        public static bool ExportAllowed(Map map, Thing t)
        {
            if (map == null || map.Disposed || t?.def == null)
            {
                return true;
            }
            CacheEntry entry = GetEntry(map);
            if (!entry.need.TryGetValue(t.def, out int need) || need <= 0)
            {
                return true;
            }
            entry.available.TryGetValue(t.def, out int available);
            return available - t.stackCount >= need;
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
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint), entry);
            AddFrom(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame), entry);
            AddBillNeeds(map, entry);
            AddMealNeeds(map, entry);
            foreach (KeyValuePair<ThingDef, int> kvp in entry.need)
            {
                if (!entry.available.ContainsKey(kvp.Key))
                {
                    entry.available[kvp.Key] = CountOnMap(map, kvp.Key);
                }
            }
            cache[map.uniqueID] = entry;
            return entry;
        }

        private static int CountOnMap(Map map, ThingDef def)
        {
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            int sum = 0;
            for (int i = 0; i < things.Count; i++)
            {
                sum += things[i].stackCount;
            }
            return sum;
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
                            int shortfall = required - Available(map, entry, def);
                            if (shortfall > 0)
                            {
                                entry.need.TryGetValue(def, out int cur);
                                entry.need[def] = cur + shortfall;
                            }
                            continue;
                        }
                        // Alternatives: aggregate availability across the allowed
                        // defs first, then register the shortfall on each so any
                        // of them can satisfy the pull.
                        int fan = 0;
                        int totalAvailable = 0;
                        int anyRequired = 0;
                        foreach (ThingDef def in ing.filter.AllowedThingDefs)
                        {
                            if (!bill.ingredientFilter.Allows(def))
                            {
                                continue;
                            }
                            if (++fan > MaxDefsPerBill)
                            {
                                break;
                            }
                            totalAvailable += Available(map, entry, def);
                            if (anyRequired == 0)
                            {
                                anyRequired = ing.CountRequiredOfFor(def, bill.recipe, bill);
                            }
                        }
                        int aggShortfall = anyRequired - totalAvailable;
                        if (aggShortfall <= 0)
                        {
                            continue;
                        }
                        fan = 0;
                        foreach (ThingDef def in ing.filter.AllowedThingDefs)
                        {
                            if (!bill.ingredientFilter.Allows(def))
                            {
                                continue;
                            }
                            if (++fan > MaxDefsPerBill)
                            {
                                break;
                            }
                            entry.need.TryGetValue(def, out int cur);
                            entry.need[def] = cur + aggShortfall;
                        }
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
        /// hand counts.</summary>
        private static void AddMealNeeds(Map map, CacheEntry entry)
        {
            if (!(ABMod.Settings?.supplyMeals ?? true))
            {
                return;
            }
            int mouths = 0;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p.IsPrisonerOfColony
                    || (p.Faction == Faction.OfPlayer && p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)))
                {
                    mouths++;
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
                    entry.need.TryGetValue(meals[i], out int cur);
                    entry.need[meals[i]] = cur + shortfall;
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
                        entry.need.TryGetValue(def, out int cur);
                        entry.need[def] = cur + remaining;
                        entry.constructionNeed.TryGetValue(def, out int curC);
                        entry.constructionNeed[def] = curC + remaining;
                    }
                }
            }
        }
    }
}
