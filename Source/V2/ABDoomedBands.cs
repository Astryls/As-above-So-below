using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// DOOMED-BAND GENSTEP SCOPING.
    ///
    /// A "doomed" band is any band that is not the surface band: its contents are known to
    /// be dead before the first genstep runs, because the layout is already fixed in
    /// ABBandedGeneration.pending. Vanilla has no idea it is generating a stack, so it
    /// fills all four bands with a full biome's worth of content and the carve then
    /// deletes three quarters of it.
    ///
    /// WHY THIS IS THE TOP PERFORMANCE ITEM, measured on tile 846 (run #188, 190x768,
    /// 4 bands, TemperateForest / Mountainous / Mountain+Caves):
    ///
    ///     all 42 gensteps combined (wall)     3,972 ms
    ///     AB band carve                      20,447 ms
    ///       of which Sky clear bands 1-3     20,126 ms   (98.4% of the carve)
    ///     Map.FinalizeInit                      169 ms
    ///     74,584 things destroyed
    ///
    /// The generation half is only 16% of the cost. THE PRIZE IS THE DESTROY HALF: 74,584
    /// removals at ~0.27 ms each, and that per-op price is the ENGINE's - ListerThings
    /// removal is a linear List.Remove over lists holding ~100k things, so every extra
    /// thing spawned into a doomed band makes every OTHER destroy slower too. The saving is
    /// therefore superlinear, which is why the plant skip bought 16.9 s -> 4.2 s.
    ///
    /// So the goal is NOT "make gensteps faster". It is to leave the doomed bands EMPTY so
    /// ClearCellHard finds nothing to remove.
    ///
    /// THE TARGET. 74,584 things over 108,300 doomed cells is 0.689 per cell.
    /// GenStep_RocksFromGrid walks map.AllCells and spawns a rock in every cell whose
    /// elevation clears 0.7 outside a cave - which on a Mountainous tile carrying the
    /// Mountain mutator is very close to 0.689. Plants are already skipped, so rock is the
    /// only mass per-cell spawner still running in the doomed bands.
    ///
    /// WHY IT IS SAFE TO SUPPRESS, and this is the part that matters:
    ///
    ///  - SHAPE. RocksFromGrid's spawn is a PER-CELL test against a noise grid. There is no
    ///    fixed count being redistributed, so suppressing it in one band cannot thin or
    ///    concentrate another. This is the same safety argument that licensed the plant
    ///    skip, and it is the line that separates safe scoping from unsafe scoping.
    ///
    ///  - STATE-NEUTRALITY. We suppress the SPAWN only, never the genstep. RocksFromGrid
    ///    also sets rock roofs, runs the small-group roof cleanup, and invokes
    ///    ScatterLumpsMineable; all of those keep running. And ClearCellHard never touched
    ///    roofs anyway - it only removes things - so the state ABSkyBandGen is handed after
    ///    the carve is BIT-IDENTICAL to before this patch. The only difference is that a
    ///    rock is not created and then destroyed in between.
    ///
    ///  - SKY BANDS ONLY. The basement is a different trade: FillRock deliberately KEEPS
    ///    vanilla's rock wherever the def matches its own vein noise (that optimisation is
    ///    why the profile reads "0 rocks spawned"), so suppressing there would swap a free
    ///    keep for a fresh spawn and could easily cost more than it saves. Note this is a
    ///    SEMANTIC split - sky bands erase, basement bands consume - and NOT the per-band
    ///    cost heuristic that runs #174/#175 falsified. Cost may not be tuned per band;
    ///    what the band DOES with the rock is a different question.
    ///
    /// The scope flag is set only for the duration of RocksFromGrid.Generate, so the
    /// GenSpawn prefix costs one static bool check for every other spawn in the game.
    /// </summary>
    public static class ABDoomedBands
    {
        /// <summary>Master switch for the sky-band rock suppression, exposed as a dev
        /// toggle so the fix can be A/B'd against a baseline on the same tile.
        ///
        /// Its state is STAMPED INTO THE PROFILE REPORT deliberately. Bisect toggles
        /// persist in the pinned debug palette across runs, and run #172 opened with a
        /// stale `belowWater=False` that mimicked a fixed bug exactly. A toggle that can
        /// silently invalidate a measurement must announce itself inside that
        /// measurement.</summary>
        public static bool SkipRockInSkyBands = true;

        /// <summary>True only while GenStep_RocksFromGrid.Generate is on the stack.</summary>
        internal static bool InRocksFromGrid;

        /// <summary>What we PREVENTED, by def. The counterpart to the destroyed census in
        /// ABGenProfile: with the skip on, suppressed should be large and destroyed should
        /// collapse, and the two together are a conservation check rather than a single
        /// number that has to be taken on trust.</summary>
        internal static readonly Dictionary<string, int> suppressed = new Dictionary<string, int>();

        internal static void NoteSuppressed(ThingDef def)
        {
            string key = def != null ? def.defName : "(null)";
            suppressed.TryGetValue(key, out int n);
            suppressed[key] = n + 1;
        }

        internal static void Reset()
        {
            suppressed.Clear();
            InRocksFromGrid = false;
        }

        /// <summary>
        /// Is this cell in a doomed SKY band of the map currently being generated?
        ///
        /// Reads the PENDING layout, not the component: ABBandMap.Setup only runs in the
        /// GenerateMap postfix, so for the whole of generation bands.Banded is still false
        /// and every band helper answers as if the map were ordinary.
        /// </summary>
        internal static bool InDoomedSkyBand(Map map, IntVec3 c)
        {
            if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int slot)
                || slot <= 0)
            {
                return false;
            }
            if (surface.Contains(c))
            {
                return false;
            }
            int surfaceBand = surface.minZ / slot;
            int band = c.z / slot;
            return band > surfaceBand;
        }
    }

    /// <summary>Marks the window in which a cancelled spawn is known to be carve fodder.
    /// A prefix/postfix pair rather than a transpiler because we are not changing what the
    /// genstep does, only what one of its callees is allowed to do while it runs.</summary>
    [HarmonyPatch(typeof(GenStep_RocksFromGrid), nameof(GenStep_RocksFromGrid.Generate))]
    public static class Patch_GenStep_RocksFromGrid_ABScope
    {
        private static void Prefix()
        {
            ABDoomedBands.InRocksFromGrid = true;
        }

        private static void Postfix()
        {
            ABDoomedBands.InRocksFromGrid = false;
        }
    }

    /// <summary>
    /// The interception itself.
    ///
    /// Patched on the ThingDef overload rather than the Thing funnel that
    /// Patch_GenSpawn_ABNoVoidSpawn uses, because this overload is the one RocksFromGrid
    /// calls and cancelling here happens BEFORE ThingMaker.MakeThing - so we skip the
    /// allocation too, not just the spawn.
    ///
    /// Returning null is safe for this caller specifically: RocksFromGrid discards the
    /// return value, and the scope flag guarantees no other caller can reach this branch.
    /// </summary>
    [HarmonyPatch(typeof(GenSpawn), nameof(GenSpawn.Spawn), new Type[]
    {
        typeof(ThingDef), typeof(IntVec3), typeof(Map), typeof(WipeMode)
    })]
    public static class Patch_GenSpawn_ABSkipDoomedRock
    {
        private static bool Prefix(ThingDef def, IntVec3 loc, Map map, ref Thing __result)
        {
            if (!ABDoomedBands.InRocksFromGrid || !ABDoomedBands.SkipRockInSkyBands)
            {
                return true;
            }
            try
            {
                if (map == null || !ABDoomedBands.InDoomedSkyBand(map, loc))
                {
                    return true;
                }
                ABDoomedBands.NoteSuppressed(def);
                __result = null;
                return false;
            }
            catch
            {
                return true; // never let the optimisation be the thing that breaks generation
            }
        }
    }
}
