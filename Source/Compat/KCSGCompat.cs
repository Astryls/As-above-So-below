using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §68 - KCSG (Vanilla Expanded Framework) structure generation on a banded map.
    ///
    /// THE BUG THIS FIXES, verified against the decompiled 1.6 KCSG.dll (run #258 stack:
    /// IndexOutOfRangeException at KCSG.SettlementGenUtils+Sampling.AddFirstPoint, thrown
    /// under VFEMedieval.ScenPart_MedievalScenario.GenerateIntoMap, which erased the New
    /// Kingdom castle AND the 8 starting colonists in one genstep try/catch):
    ///
    ///   SettlementGenUtils.Generate allocates   grid = new CellType[size.z][]  with rows
    ///   of size.x - i.e. grid[z][x] - and EVERY consumer indexes it that way
    ///   (AddNextPoint, BuildingPlacement.CanPlaceAt/PlaceAt, PathFinder.DoPath, WidenPath).
    ///   AddFirstPoint alone writes
    ///
    ///       grid[val.x][val.z] = CellType.Sampling;     // TRANSPOSED
    ///
    ///   On a square vanilla map (size.x == size.z) the flip is bounds-safe and silently
    ///   marks a mirrored wrong cell - one cosmetically irrelevant seed point, so KCSG has
    ///   shipped this for years without a report. On a banded map (x = 250, z = seven
    ///   slots) the settlement rect sits at z far above size.x, so the transposed read
    ///   indexes column ~850 of a 250-wide row and throws. Any non-square map can trigger
    ///   it; ours merely guarantees it.
    ///
    /// THE FIX: replace AddFirstPoint wholesale with a bit-identical body whose grid write
    /// is oriented grid[z][x] like every other site. Deliberately UNCONDITIONAL (not gated
    /// on bands.Banded): the original is wrong on every map, our replacement is exactly
    /// the corrected original, and rectangular Geological Landforms maps (§56.1) need it
    /// too. Two paranoia upgrades over the original, both unreachable in the normal case:
    ///   - the rejection loop gets an iteration cap with a rect-centre fallback (the
    ///     original can spin forever if a caller hands a rect outside the sampling range;
    ///     rule 6 - a helper that cannot succeed should degrade, not hang);
    ///   - the grid write is bounds-checked (the original would NRE/IOORE; sampling a
    ///     point is still useful even if its grid mark cannot land).
    ///
    /// Reflection-only: no compile-time KCSG reference, silent no-op when VEF is absent,
    /// and if ANY member is missing (KCSG reshapes) the patch declines to install and says
    /// so once - broken-but-vanilla beats broken-by-us (rule 15: assert always).
    ///
    /// ⚠ REPORT UPSTREAM to Vanilla Expanded Framework: one-character fix on their side
    /// (grid[val.x][val.z] -> grid[val.z][val.x] in Sampling.AddFirstPoint).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class KCSGCompat
    {
        private static FieldInfo fRandom;
        private static FieldInfo fRejectionSq;
        private static FieldInfo fCenter;
        private static FieldInfo fActivePoints;
        private static FieldInfo fActivePointsCount;
        private static FieldInfo fPoints;
        private static FieldInfo fGrid;
        private static FieldInfo fSize;
        private static object samplingEnumValue;

        static KCSGCompat()
        {
            try
            {
                Install();
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: KCSG sampling fix failed to install - KCSG "
                    + "structure spawns keep KCSG's own behaviour (which throws on banded "
                    + "maps). " + e);
            }
        }

        private static void Install()
        {
            Type utils = AccessTools.TypeByName("KCSG.SettlementGenUtils");
            if (utils == null)
            {
                return; // no VEF installed - the normal case, not an error
            }
            Type sampling = utils.GetNestedType("Sampling", BindingFlags.Public | BindingFlags.NonPublic);
            Type cellType = utils.GetNestedType("CellType", BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo target = sampling == null ? null : AccessTools.Method(sampling, "AddFirstPoint");

            fRandom = sampling == null ? null : AccessTools.Field(sampling, "random");
            fRejectionSq = sampling == null ? null : AccessTools.Field(sampling, "rejectionSqDistance");
            fCenter = sampling == null ? null : AccessTools.Field(sampling, "center");
            fActivePoints = sampling == null ? null : AccessTools.Field(sampling, "activePoints");
            fActivePointsCount = sampling == null ? null : AccessTools.Field(sampling, "activePointsCount");
            fPoints = sampling == null ? null : AccessTools.Field(sampling, "points");
            fGrid = AccessTools.Field(utils, "grid");
            fSize = AccessTools.Field(utils, "size");

            if (target == null || cellType == null || fRandom == null || fRejectionSq == null
                || fCenter == null || fActivePoints == null || fActivePointsCount == null
                || fPoints == null || fGrid == null || fSize == null)
            {
                Log.Warning(ABLog.Tag + " V2: KCSG is present but Sampling's shape changed - "
                    + "sampling fix NOT installed. KCSG settlements on banded maps will "
                    + "throw in AddFirstPoint until this compat is updated.");
                return;
            }
            samplingEnumValue = Enum.Parse(cellType, "Sampling");

            HarmonyBoot.Harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(KCSGCompat), nameof(AddFirstPointFixed)));
            ABLog.Dev("KCSG sampling fix INSTALLED (AddFirstPoint transposed-grid write).");
        }

        /// <summary>The original body, transposition corrected. Returns false: the broken
        /// original must not run after us (its write is the crash).</summary>
        private static bool AddFirstPointFixed(CellRect rect, IntVec3 topLeft)
        {
            var random = (System.Random)fRandom.GetValue(null);
            float rejectionSq = (float)fRejectionSq.GetValue(null);
            IntVec3 center = (IntVec3)fCenter.GetValue(null);
            IntVec3 size = (IntVec3)fSize.GetValue(null);
            if (random == null)
            {
                return true; // Sample was not the caller; let the original explain itself
            }

            // Original sampling: x/z drawn from [topLeft, topLeft + mapSize), rejected
            // until inside the rect and the rejection radius. Expected ~60 draws on a
            // banded map, ~3 on a square one; the cap only exists for a caller whose rect
            // never intersects the sampled range.
            IntVec3 val = IntVec3.Invalid;
            for (int tries = 0; tries < 100000; tries++)
            {
                double r = random.NextDouble();
                int x = (int)(topLeft.x + size.x * r);
                r = random.NextDouble();
                int z = (int)(topLeft.z + size.z * r);
                IntVec3 candidate = new IntVec3(x, 0, z);
                float dx = center.x - candidate.x;
                float dz = center.z - candidate.z;
                if (rect.Contains(candidate) && dx * dx + dz * dz <= rejectionSq)
                {
                    val = candidate;
                    break;
                }
            }
            if (!val.IsValid)
            {
                val = rect.CenterCell; // inside the rect, distance 0: always acceptable
            }

            // THE FIX: grid[z][x], matching the allocation (new CellType[size.z][], rows
            // of size.x) and every other KCSG indexing site. Bounds-guarded because a
            // missing seed mark is recoverable and an exception here erases a scenario.
            if (fGrid.GetValue(null) is Array grid
                && val.z >= 0 && val.z < grid.Length
                && grid.GetValue(val.z) is Array row
                && val.x >= 0 && val.x < row.Length)
            {
                row.SetValue(samplingEnumValue, val.x);
            }

            if (fActivePoints.GetValue(null) is IList active)
            {
                active.Add(val);
            }
            fActivePointsCount.SetValue(null, (int)fActivePointsCount.GetValue(null) + 1);
            if (fPoints.GetValue(null) is IList points)
            {
                points.Add(val);
            }
            return false;
        }
    }
}
