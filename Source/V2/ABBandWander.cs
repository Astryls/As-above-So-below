using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Keep idle wandering inside the pawn's own band.
    ///
    /// This is the graph/geometry dividing line biting in an unusual direction. Cross-band
    /// CanReach is the feature that makes hauling, work scanning and storage free - but
    /// vanilla's wander-root picker treats "reachable" as "a sensible place to idle", and on
    /// a banded map that silently includes the whole column.
    ///
    /// WanderUtility.GetColonyWanderRoot chooses from three map-wide sources, every one of
    /// them gated on nothing but pawn.CanReach:
    ///   1. gatherSpotLister.activeSpots
    ///   2. listerBuildings.allBuildingsColonist  (the 35-cell LengthHorizontalSquared &lt;= 1225
    ///      check only guards the EARLY RETURN; the candidateCells fallback has no limit)
    ///   3. mapPawns.FreeColonistsSpawned positions
    ///
    /// So an idle colonist on the sky band can pick a root on the surface, path toward it,
    /// get segmented to the stairwell and transit - just to stand somewhere. Several idle
    /// pawns doing that at once is the reported "pawns bunch up at the stairs when
    /// wandering": they were not stuck, they were all commuting to idle.
    ///
    /// Falling back to pawn.Position is exactly what vanilla itself returns when it finds no
    /// candidate, so the result stays inside the method's own contract and the pawn simply
    /// wanders locally. Deliberately NOT done by re-picking a same-band candidate: that would
    /// duplicate vanilla's selection logic and drift from it on every update.
    ///
    /// Only the COLONY root is corrected. Other wander givers root at the pawn's own position
    /// or a duty location, both of which are already in-band.
    /// </summary>
    [HarmonyPatch(typeof(WanderUtility), nameof(WanderUtility.GetColonyWanderRoot))]
    public static class Patch_WanderUtility_ABKeepRootInBand
    {
        private static void Postfix(Pawn pawn, ref IntVec3 __result)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || !__result.IsValid)
                {
                    return;
                }
                Map map = pawn.Map;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                if (ABBands.SameBand(map, pawn.Position, __result))
                {
                    return;
                }
                __result = pawn.Position;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 wander root band clamp");
            }
        }
    }
}
