using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Vanilla Landmarks Expanded (VanillaExpanded.VExplorationE).
    ///
    /// VEE ships about 150 TileMutatorDefs behind roughly 55 worker classes, and the headline
    /// number is misleading in BOTH directions. Most of them - the wildlife, weather and
    /// incident modifiers - carry no map geometry at all and cannot be affected by banding.
    /// Most of the ones that DO carry geometry extend a VANILLA base class, so §6b's river
    /// centre remap and §6e's coast remap already cover them at zero cost. What is left is a
    /// short list of workers that build their own field around <c>map.Center</c>, and those
    /// are corrected here.
    ///
    /// Two are fixed:
    ///   - the LoneIsland family, which otherwise DROWNS THE ENTIRE COLONY (see below);
    ///   - the Crater family, which otherwise puts its crater in the wrong band.
    ///
    /// Detection is by type name only - no compile-time reference, no foreign type in any
    /// patch signature (§14), and every patch fails open to vanilla VEE behaviour when the
    /// mod is absent or has been refactored.
    /// </summary>
    public static class VanillaLandmarksCompat
    {
        public const string PackageId = "VanillaExpanded.VExplorationE";

        private const string Ns = "VanillaExplorationExpanded.";

        /// <summary>The three workers that share the LoneIsland coast field. WithLake and
        /// WithMountain extend LoneIsland; each name is probed for its OWN declaration so a
        /// subclass that overrides the sampler is still corrected, and one that merely
        /// inherits is not patched twice.</summary>
        private static readonly string[] IslandTypes =
        {
            "TileMutatorWorker_LoneIsland",
            "TileMutatorWorker_LoneIslandWithLake",
            "TileMutatorWorker_LoneIslandWithMountain"
        };

        /// <summary>Crater and the four workers built on it.</summary>
        private static readonly string[] CraterTypes =
        {
            "TileMutatorWorker_Crater",
            "TileMutatorWorker_CraterLake",
            "TileMutatorWorker_ResurgentCaldera",
            "TileMutatorWorker_ToxicCrater",
            "TileMutatorWorker_Volcano"
        };

        /// <summary>The three noise fields Crater.Init builds. All private, all declared on
        /// Crater itself, so the subclasses inherit the same storage.</summary>
        private static readonly string[] CraterModuleFields = { "outerRim", "opening", "innerAsh" };

        private static bool resolved;

        private static List<MethodBase> islandNoise;

        private static List<MethodBase> craterInit;

        private static List<MethodBase> craterOffsetRange;

        private static List<FieldInfo> craterModules;

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            islandNoise = new List<MethodBase>();
            craterInit = new List<MethodBase>();
            craterOffsetRange = new List<MethodBase>();
            craterModules = new List<FieldInfo>();
            try
            {
                foreach (string n in IslandTypes)
                {
                    Type t = AccessTools.TypeByName(Ns + n);
                    MethodInfo m = t == null ? null : AccessTools.DeclaredMethod(t, "GetNoiseValue");
                    if (m != null)
                    {
                        islandNoise.Add(m);
                    }
                }
                foreach (string n in CraterTypes)
                {
                    Type t = AccessTools.TypeByName(Ns + n);
                    if (t == null)
                    {
                        continue;
                    }
                    MethodInfo init = AccessTools.DeclaredMethod(t, "Init", new[] { typeof(Map) });
                    if (init != null)
                    {
                        craterInit.Add(init);
                    }
                    MethodInfo getter = AccessTools.DeclaredPropertyGetter(t, "CenterOffsetRange");
                    if (getter != null)
                    {
                        craterOffsetRange.Add(getter);
                    }
                }
                Type crater = AccessTools.TypeByName(Ns + "TileMutatorWorker_Crater");
                if (crater != null)
                {
                    foreach (string f in CraterModuleFields)
                    {
                        FieldInfo fi = AccessTools.DeclaredField(crater, f);
                        if (fi != null)
                        {
                            craterModules.Add(fi);
                        }
                    }
                }
                ABLog.Dev("Vanilla Landmarks Expanded compat: island=" + islandNoise.Count
                    + " craterInit=" + craterInit.Count + " craterRange=" + craterOffsetRange.Count
                    + " craterFields=" + craterModules.Count);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " VEE compat resolve threw: " + e, 762195890);
            }
        }

        internal static List<MethodBase> IslandNoiseTargets
        {
            get
            {
                Resolve();
                return islandNoise;
            }
        }

        internal static List<MethodBase> CraterInitTargets
        {
            get
            {
                Resolve();
                return craterInit;
            }
        }

        internal static List<MethodBase> CraterOffsetRangeTargets
        {
            get
            {
                Resolve();
                return craterOffsetRange;
            }
        }

        /// <summary>Swap Crater's three noise fields for band-local wrappers. Called from the
        /// Init postfix, once per generated map.</summary>
        internal static void RebindCraterModules(object worker, Map map)
        {
            Resolve();
            if (worker == null || craterModules.Count == 0)
            {
                return;
            }
            if (!ABBandLocal.TryBandGeometry(map, out _, out int slot, out int offset))
            {
                return;
            }
            for (int i = 0; i < craterModules.Count; i++)
            {
                FieldInfo f = craterModules[i];
                ModuleBase current = f.GetValue(worker) as ModuleBase;
                if (current == null || current is ABBandLocal.BandLocalModule)
                {
                    // Already wrapped. Double-wrapping is NOT harmless here: the rewrite is
                    // modulo-then-offset, so applying it twice folds an already-centred
                    // coordinate back down to near zero and the crater vanishes.
                    continue;
                }
                f.SetValue(worker, ABBandLocal.Wrap(current, slot, offset));
            }
            ABLog.Dev("V2: VEE crater fields rebound band-local (slot " + slot + ", offset " + offset + ").");
        }
    }

    /// <summary>
    /// VEE LONE ISLAND: THE WORST BANDING BUG FOUND IN ANY THIRD-PARTY MOD SO FAR.
    ///
    /// The worker builds its coast field as a falloff radius of <c>map.Size.x * 0.6</c>
    /// centred on <c>map.Center</c>, plus two DistFromPoint modules parked by
    /// <c>Translate(-Size.x/2, 0, -Size.z/2)</c> - the exact same mis-centring §6e documents
    /// for vanilla coasts, and for the same reason: <c>Size.z</c> appears only in the
    /// translate that puts the field on the map centre.
    ///
    /// What makes it far worse than the vanilla case is what the worker then DOES with it.
    /// <c>GeneratePostElevationFertility</c> and <c>GeneratePostTerrain</c> both walk
    /// <c>map.AllCells</c> and, wherever the field reads below 0.2, write elevation 0 and
    /// deep ocean terrain. On a 126x896 stack the island is a disc of radius about 76 centred
    /// at z=448, which is inside the MIDDLE band - so every other band, INCLUDING THE SURFACE
    /// THE COLONY LANDS ON, sits outside the disc and is turned into open ocean.
    ///
    /// The symptom would be identical to the one that originally exposed §6e: a coastal
    /// colony generating with its entire surface band underwater.
    ///
    /// ONE INTERCEPTION FIXES THE FAMILY. Unlike the vanilla coast workers, VEE declares its
    /// own <c>protected virtual float GetNoiseValue(IntVec3 cell)</c>, and it is the single
    /// point where the field is read by both passes. That is the "find the ONE virtual
    /// everything funnels through" rule paying out again - and it is the reason §6e's note
    /// that "none of them override GetNoiseValue" had to be re-scoped: that finding was only
    /// ever about vanilla.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_VEE_LoneIsland_ABBandLocal
    {
        private static bool Prepare()
        {
            return VanillaLandmarksCompat.IslandNoiseTargets.Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return VanillaLandmarksCompat.IslandNoiseTargets;
        }

        private static void Prefix(ref IntVec3 cell)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                // Not handed a map, exactly like the vanilla coast sampler - and taking it
                // from mapBeingGenerated is also what makes this work during a Map Preview.
                ABBandLocal.TryRemap(MapGenerator.mapBeingGenerated, ref cell);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: VEE island band-local patch threw: " + e, 762195891);
            }
        }
    }

    /// <summary>
    /// VEE CRATER: right shape, wrong band.
    ///
    /// <c>Init</c> centres the crater on <c>map.Center</c> and then jitters it by
    /// <c>map.Size.x * r</c> in x and <c>map.Size.z * r</c> in z, with r in ±0.2 drawn
    /// separately for each axis. On a stack the x term is already correct (Size.x IS the band
    /// width) while the z term is a fraction of the WHOLE COLUMN: ±0.2 of 896 is ±179 cells,
    /// more than an entire 126-cell band. The radius is derived from Size.x only, so the
    /// crater stays round - this is purely a placement fault, the FIELD and ANCHOR rows of
    /// the slicing rule appearing together.
    ///
    /// Two corrections, because there are two separate errors:
    ///
    /// 1. THE FIELD is re-centred by wrapping the three noise modules in the band-local
    ///    rewrite. Crater has no single sampling hook - it calls <c>outerRim.GetValue</c> and
    ///    <c>innerAsh.GetValue</c> straight out of two different generate passes - so the fix
    ///    goes on the data rather than the reader, which corrects both passes and anything
    ///    VEE adds later.
    ///
    /// 2. THE JITTER is scaled by <c>bandHeight / Size.z</c> so that <c>Size.z * r</c> once
    ///    again lands in band-scale units. Without this the crater centre is off-window about
    ///    two thirds of the time and simply does not appear.
    ///
    /// ⚠ STATED COST, not an oversight. The same property supplies both axes, so scaling it
    /// also compresses the X jitter by the same factor - craters sit closer to the band's
    /// horizontal centre than VEE intends. That is a cosmetic loss of variety, and it is
    /// strictly better than a crater that is absent two maps in three. Removing the cost
    /// needs a transpiler on Init to rewrite only the z multiplier; because our bands are
    /// SQUARE (Size.x == bandHeight, §2) that rewrite is exactly "load Size.x instead of
    /// Size.z", which is worth doing if craters ever become load-bearing.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_VEE_Crater_ABBandLocal
    {
        private static bool Prepare()
        {
            return VanillaLandmarksCompat.CraterInitTargets.Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return VanillaLandmarksCompat.CraterInitTargets;
        }

        // __instance is deliberately typed as object: a VEE type in the signature would make
        // HarmonyBoot's class processor log a broken patch class on every load without VEE.
        private static void Postfix(object __instance, Map map)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                VanillaLandmarksCompat.RebindCraterModules(__instance, map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: VEE crater rebind threw: " + e, 762195892);
            }
        }
    }

    /// <summary>Band-scale the crater centre jitter. See the cost note on
    /// <see cref="Patch_VEE_Crater_ABBandLocal"/>.</summary>
    [HarmonyPatch]
    public static class Patch_VEE_Crater_ABCenterJitter
    {
        private static bool Prepare()
        {
            return VanillaLandmarksCompat.CraterOffsetRangeTargets.Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return VanillaLandmarksCompat.CraterOffsetRangeTargets;
        }

        private static void Postfix(ref FloatRange __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.LevelGen))
                {
                    return;
                }
                Map map = MapGenerator.mapBeingGenerated;
                if (map == null || map.Size.z <= 0)
                {
                    return;
                }
                if (!ABBandLocal.TryBandGeometry(map, out int bandHeight, out _, out _))
                {
                    return;
                }
                float scale = bandHeight / (float)map.Size.z;
                __result = new FloatRange(__result.min * scale, __result.max * scale);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: VEE crater jitter patch threw: " + e, 762195893);
            }
        }
    }
}
