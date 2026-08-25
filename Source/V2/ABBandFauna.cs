using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §60 THE WILD ANIMAL ECOSYSTEM IS SIZED FROM ONE LEVEL, NOT FROM THE WHOLE STACK.
    ///
    /// ⚠⚠ THE BUG: `WildAnimalSpawner.DesiredTotalAnimalWeight` is literally
    /// `map.Area / (10000f / DesiredAnimalDensity)` - LINEAR IN RAW MAP AREA, with no
    /// weighting for whether a cell is habitable. A banded map IS one Map whose `Size.z` is
    /// `bandCount * Slot`, so a 4-level column asks vanilla for FOUR TIMES the wild animal
    /// population of the same colony unbanded. Each of those is a full `Pawn` with jobs,
    /// needs and pathfinding.
    ///
    /// ⚠ AND IT IS A GAMEPLAY BUG BEFORE IT IS A PERFORMANCE BUG. Ambient wildlife enters
    /// through `RCellFinder.TryFindRandomPawnEntryCell`, and off-surface arrival is a
    /// per-category SETTING (`animalArriveUpper` / `animalArriveLower`) whose lower default
    /// is OFF. So on default settings the 4x herd is competing for 1x of usable land.
    ///
    /// ⚠ WHY THE PLANT SPAWNER NEEDS NO EQUIVALENT, which is the instructive contrast.
    /// `WildPlantSpawner` accumulates `GetDesiredPlantsCountAt`, which is FERTILITY WEIGHTED:
    /// solid rock and open air contribute ~0, so its target barely moves however many dead
    /// bands you stack. The animal target uses raw `map.Area` and has no such weighting.
    /// When auditing a vanilla system for band inflation, the question is not "does it read
    /// map.Area" but "does what it reads discount uninhabitable cells".
    ///
    /// ⚠ ONE INTERCEPTION COVERS BOTH POPULATION PATHS, and that is why this is a property
    /// postfix rather than a tick patch. `DesiredTotalAnimalWeight` has exactly two consumers
    /// in 1.6: `AnimalEcosystemFull` and a debug string. `AnimalEcosystemFull` is the `while`
    /// condition of `GenStep_Animals.Generate` (the one-shot map-gen population) AND the gate
    /// in `WildAnimalSpawnerTick` (ongoing repopulation). Capping the target fixes the
    /// initial herd and the long-run ceiling together.
    ///
    /// ⚠ THE SPAWN RATE IS DELIBERATELY LEFT ALONE. `WildAnimalSpawnerTick` fires on
    /// `TicksGame % 1213 == 0` and spawns ONE animal against `Rand.Chance(0.0269f *
    /// DesiredAnimalDensity)` - a DENSITY term, not a weight term. So animals still trickle
    /// in at exactly the vanilla cadence; they simply stop sooner. Scaling the rate as well
    /// would double-apply the fix.
    /// </summary>
    [HarmonyPatch(typeof(WildAnimalSpawner), "DesiredTotalAnimalWeight", MethodType.Getter)]
    public static class Patch_WildAnimalSpawner_ABSliceEcosystem
    {
        private static readonly AccessTools.FieldRef<WildAnimalSpawner, Map> MapRef =
            AccessTools.FieldRefAccess<WildAnimalSpawner, Map>("map");

        /// <summary>Observe-only, surfaced in `AB2: pathing report`. A guard that silently
        /// early-returns is indistinguishable from an unimplemented feature (§14).</summary>
        public static int capsApplied;

        public static float lastScale = 1f;

        private static void Postfix(WildAnimalSpawner __instance, ref float __result)
        {
            try
            {
                if (__result <= 0f)
                {
                    return; // density is zero: nothing to scale, and no divide to risk
                }
                Map map = MapRef(__instance);
                if (map == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                // A slice is one band's rect: `map.Size.x * bandHeight`. Every band is the
                // same height by construction (RectOfBand is `(0, band * Slot, Size.x,
                // bandHeight)`), so "the surface band" here means "any band" - it is named
                // for readability, not because the surface is special.
                int slice = bands.RectOfBand(bands.surfaceBand).Area;
                int area = map.Area;
                // `slice >= area` means a single-band map (or a gutterless degenerate one):
                // the scale would be >= 1 and this would INFLATE the population. Never let
                // that happen - the cap may only ever reduce.
                if (slice <= 0 || area <= 0 || slice >= area)
                {
                    return;
                }
                lastScale = (float)slice / area;
                __result *= lastScale;
                capsApplied++;
            }
            catch (Exception e)
            {
                // ⚠ ErrorOnce IS LOAD-BEARING HERE. This getter is the `while` condition of
                // GenStep_Animals, which iterates up to 10,000 times - a bare Log.Error
                // would emit ten thousand lines during map generation.
                Log.ErrorOnce(ABLog.Tag + " V2: animal ecosystem cap threw: " + e,
                    762195938);
            }
        }
    }

    /// <summary>
    /// §60.2 AMBIENT WILDLIFE NOW HONOURS THE SAME PER-LEVEL SETTING THE ANIMAL INCIDENTS DO.
    ///
    /// `ABBandArrivals` latches `Category.Animal` around five INCIDENT workers (herd
    /// migration, thrumbo passes, alphabeavers, farm animals, manhunter packs), so those
    /// already respect `animalArriveUpper` / `animalArriveLower`. But the ambient ecosystem
    /// tick - the one that actually maintains the resident wildlife population - calls the
    /// same entry finder UNLATCHED, so it stayed surface-only no matter what the settings
    /// said. That is an inconsistency the player cannot see or explain: the toggle visibly
    /// works for a thrumbo and silently does nothing for the deer that live there.
    ///
    /// ⚠ THE LATCH IS AN ALLOWLIST AND THIS IS AN ADDITION TO IT, not a change of rule. All
    /// the existing safety still applies at the chokepoint: the chosen band must have
    /// standable, unfogged, colony-reachable edge cells or the search falls back to vanilla's
    /// surface behaviour. Vanilla's own `extraValidator` (`CanReachMapEdge`) is ANDed in, not
    /// replaced.
    ///
    /// ⚠ COST: this method is called once per map per tick, so the patch is two Harmony
    /// dispatches per tick (~sub-microsecond) for a body that is one ThreadStatic write. The
    /// latch cannot be moved anywhere cheaper: vanilla calls the entry finder from inside the
    /// `if` CONDITION of the tick method, so there is no inner call site to wrap.
    /// </summary>
    [HarmonyPatch(typeof(WildAnimalSpawner),
        nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    public static class Patch_WildAnimalSpawner_ABArrivalLatch
    {
        private static void Prefix()
        {
            ABBandArrivals.current = ABBandArrivals.Category.Animal;
        }

        /// <summary>§18a: prefix state releases in a Finalizer, so a throw anywhere inside
        /// the spawn cannot leave the latch armed for the next unrelated entry search.</summary>
        private static void Finalizer()
        {
            ABBandArrivals.current = null;
        }
    }
}
