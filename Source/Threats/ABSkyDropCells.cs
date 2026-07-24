using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Keep drop pods that land on a SKY level grouped on the plateaus instead of
    /// scattered across the whole map.
    ///
    /// The sky level is mostly impassable open air with standable rooftop / mountain-top
    /// plateaus punched through it. Vanilla's drop machinery assumes a mostly-walkable
    /// map, so on the sparse sky it breaks two ways:
    ///   - DropCellFinder.FindRaidDropCenterDistant can resolve the raid CENTER onto an
    ///     open-air / edge cell (its last resort is AllCells.RandomElement()), anchoring
    ///     the whole drop over nothing.
    ///   - DropCellFinder.TryFindDropSpotNear only searches radius 16 and never snaps off
    ///     an open-air center, so it fails, and DropPodUtility.DropThingGroupsNear then
    ///     falls back to CellFinderLoose.RandomCellWith(walkable) - a RANDOM walkable cell
    ///     anywhere on the level. Each pod that hits that path is flung to a different
    ///     far plateau: the "pods spread out across the map" bug.
    ///
    /// Two scoped patches (sky levels only, everything else is pure vanilla):
    ///   - FindRaidDropCenterDistant postfix snaps a bad center onto a real plateau.
    ///   - TryFindDropSpotNear prefix runs a plateau-aware clustered search that succeeds
    ///     whenever any reachable plateau exists, so the random-walkable scatter fallback
    ///     never fires and the group lands together on solid rooftop.
    /// Fails open (defer to vanilla) on anything unexpected.
    /// </summary>
    internal static class ABSkyDropCells
    {
        /// <summary>Cap on GenRadial ring scans (its precomputed pattern is bounded).</summary>
        private const int MaxRingRadius = 40;

        internal static bool IsSkyLevel(Map map)
        {
            LevelComp c = map?.Levels();
            return c != null && c.level >= 1;
        }

        /// <summary>A drop spot is valid on the sky only on a plateau (standable rooftop
        /// or mountain-top). Open air is impassable, so vanilla IsGoodDropSpot already
        /// rejects it - the explicit terrain check is belt-and-suspenders.</summary>
        internal static bool IsPlateauCell(Map map, IntVec3 c, bool canRoofPunch, bool allowIndoors, IntVec2? size)
        {
            if (size.HasValue)
            {
                CellRect rect = c.RectAbout(size.Value);
                if (!rect.InBounds(map))
                {
                    return false;
                }
                foreach (IntVec3 cell in rect)
                {
                    if (!PlateauSingle(map, cell, canRoofPunch, allowIndoors))
                    {
                        return false;
                    }
                }
                return true;
            }
            return PlateauSingle(map, c, canRoofPunch, allowIndoors);
        }

        private static bool PlateauSingle(Map map, IntVec3 c, bool canRoofPunch, bool allowIndoors)
        {
            if (!c.InBounds(map) || map.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
            {
                return false;
            }
            return DropCellFinder.IsGoodDropSpot(c, map, allowFogged: true, canRoofPunch, allowIndoors);
        }

        /// <summary>Nearest plateau cell to <paramref name="from"/> within a bounded ring
        /// scan (nearest-first), or false if the sky has no plateau in reach.</summary>
        internal static bool TrySnapToPlateau(Map map, IntVec3 from, bool canRoofPunch, bool allowIndoors,
            IntVec2? size, out IntVec3 anchor)
        {
            foreach (IntVec3 c in GenRadial.RadialCellsAround(from, MaxRingRadius, true))
            {
                if (IsPlateauCell(map, c, canRoofPunch, allowIndoors, size))
                {
                    anchor = c;
                    return true;
                }
            }
            anchor = IntVec3.Invalid;
            return false;
        }

        /// <summary>A clustered plateau drop spot near <paramref name="center"/>: anchor on
        /// the nearest plateau, then pick a random plateau cell in a growing radius (kept
        /// reachable from the anchor so the group stays connected). Succeeds whenever any
        /// reachable plateau exists, so the vanilla random-walkable scatter never fires.</summary>
        internal static bool TryFindPlateauSpotNear(IntVec3 center, Map map, bool canRoofPunch, int maxRadius,
            bool allowIndoors, IntVec2? size, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            IntVec3 anchor = center;
            if (!IsPlateauCell(map, center, canRoofPunch, allowIndoors, size)
                && !TrySnapToPlateau(map, center, canRoofPunch, allowIndoors, size, out anchor))
            {
                return false;
            }
            IntVec3 root = anchor;
            bool Validator(IntVec3 c) =>
                IsPlateauCell(map, c, canRoofPunch, allowIndoors, size)
                && map.reachability.CanReach(root, c, PathEndMode.OnCell, TraverseMode.PassDoors, Danger.Deadly);
            int cap = Mathf.Clamp(maxRadius, 8, MaxRingRadius);
            for (int r = 4; r <= cap; r += 3)
            {
                if (CellFinder.TryFindRandomCellNear(root, map, r, Validator, out result))
                {
                    return true;
                }
            }
            // The anchor itself is a valid plateau cell - never leak an invalid spot.
            result = anchor;
            return true;
        }
    }

    /// <summary>Sky drops cluster on the plateaus. Prefix on the maxRadius overload of
    /// TryFindDropSpotNear (the short overload delegates to it), so every DropThingGroupsNear
    /// caller is covered before it can hit the random-walkable scatter fallback.</summary>
    [HarmonyPatch]
    internal static class Patch_DropCellFinder_TryFindDropSpotNear_Sky
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(DropCellFinder), nameof(DropCellFinder.TryFindDropSpotNear),
                new[]
                {
                    typeof(IntVec3), typeof(Map), typeof(IntVec3).MakeByRefType(), typeof(bool), typeof(bool),
                    typeof(int), typeof(bool), typeof(IntVec2?), typeof(bool)
                });
        }

        private static bool Prefix(IntVec3 center, Map map, ref IntVec3 result, bool canRoofPunch,
            int maxRadius, bool allowIndoors, IntVec2? size, ref bool __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Transit) || !ABSkyDropCells.IsSkyLevel(map))
                {
                    return true;
                }
                if (ABSkyDropCells.TryFindPlateauSpotNear(center, map, canRoofPunch,
                        Math.Max(maxRadius, 40), allowIndoors, size, out IntVec3 spot))
                {
                    result = spot;
                    __result = true;
                    return false;
                }
                // No plateau anywhere in reach: let vanilla run.
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Transit, e, "sky drop spot near");
                return true;
            }
        }
    }

    /// <summary>Snap a raid drop center that landed on open air / an edge onto a real
    /// plateau, so the per-pod search anchors over solid rooftop and the group lands
    /// together. Sky levels only.</summary>
    [HarmonyPatch(typeof(DropCellFinder), nameof(DropCellFinder.FindRaidDropCenterDistant))]
    internal static class Patch_DropCellFinder_RaidCenter_Sky
    {
        private static void Postfix(Map map, ref IntVec3 __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Transit) || !ABSkyDropCells.IsSkyLevel(map))
                {
                    return;
                }
                if (ABSkyDropCells.IsPlateauCell(map, __result, canRoofPunch: true, allowIndoors: false, size: null))
                {
                    return;
                }
                if (ABSkyDropCells.TrySnapToPlateau(map, __result, canRoofPunch: true, allowIndoors: false,
                        size: null, out IntVec3 snapped))
                {
                    __result = snapped;
                }
                else if (DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 close, map))
                {
                    __result = close;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Transit, e, "sky raid drop center");
            }
        }
    }
}
