using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Anomaly entity containment across levels (parity P3 #13, 2026-07-25).
    /// Two vanilla gates broke the column:
    ///
    ///  - The "Capture entity..." gizmo is disabled by
    ///    StudyUtility.HoldingPlatformAvailableOnCurrentMap, which scans ONE
    ///    map - a containment cellar one level down read as "no platform".
    ///    Postfixed column-wide below; the player then switches view and
    ///    clicks the platform (the targeter itself already accepts it, and
    ///    the cross-map targetHolder assignment survives - CompTick only
    ///    clears destroyed or occupied holders).
    ///
    ///  - WorkGiver_TakeEntityToHoldingPlatform hard-requires
    ///    targetHolder.MapHeld == entity.MapHeld, so the assignment above
    ///    never produced a job. WorkGiver_ABTakeEntityAcrossLevels covers
    ///    exactly the cross-level case (vanilla keeps every same-map case):
    ///    carry the entity through the stairs toward the holder's level, one
    ///    hop at a time; a pending order re-arms targetHolder after each hop
    ///    (CompHoldingPlatformTarget.PostSpawnSetup wipes cross-map holders
    ///    when the carrier lands the entity on an intermediate level), and
    ///    the vanilla giver finishes the final same-map leg.
    ///
    /// Platform-to-platform transfers of already-held entities stay same-map
    /// (vanilla flow untouched); release-to-wild uses walk-out routing that
    /// hostile/neutral exit systems already own. Kill switches: ui (gizmo),
    /// logistics (giver).
    /// </summary>
    [HarmonyPatch(typeof(StudyUtility), nameof(StudyUtility.HoldingPlatformAvailableOnCurrentMap))]
    internal static class Patch_HoldingPlatformAvailable_Column
    {
        private static void Postfix(ref bool __result)
        {
            if (__result || !ModsConfig.AnomalyActive || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                Map cur = Find.CurrentMap;
                LevelComp controller = cur?.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed || m == cur)
                    {
                        continue;
                    }
                    List<Building> buildings = m.listerBuildings.allBuildingsColonist;
                    for (int i = 0; i < buildings.Count; i++)
                    {
                        if (buildings[i].TryGetComp(out CompEntityHolder holder) && holder.Available)
                        {
                            // A free platform elsewhere in the column: enable
                            // the gizmo; the targeter still validates the
                            // actual click.
                            __result = true;
                            return;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "column platform availability");
            }
        }
    }

    /// <summary>The cross-level half of entity capture: entities whose target
    /// platform lives on another level of the column. Mirrors the vanilla
    /// giver's own eligibility checks (manipulation, threat disabled, empty
    /// target holder, reservations), then routes strictly toward the holder's
    /// level. Vanilla's giver keeps every same-map capture.</summary>
    public class WorkGiver_ABTakeEntityAcrossLevels : WorkGiver_Scanner
    {
        public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.HoldingPlatformTarget);

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !ModsConfig.AnomalyActive || !ABGuard.On(ABGuard.Logistics)
                || ABMod.Settings == null || !ABMod.Settings.crossLevelHauling
                || !pawn.Map.ConnectedToOtherLevel()
                || CrossLevelWork.LowPowerWorker(pawn);
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return TryBuild(pawn, t, forced, issue: false) != null;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            return TryBuild(pawn, t, forced, issue: true);
        }

        private static Job TryBuild(Pawn pawn, Thing t, bool forced, bool issue)
        {
            try
            {
                if (t == null || !t.Spawned || t.MapHeld != pawn.Map
                    || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
                {
                    return null;
                }
                CompHoldingPlatformTarget target = t.TryGetComp<CompHoldingPlatformTarget>();
                Thing holder = target?.targetHolder;
                if (holder == null || holder.Destroyed)
                {
                    return null;
                }
                Map holderMap = holder.MapHeld;
                if (holderMap == null || holderMap.Disposed || holderMap == t.MapHeld)
                {
                    return null; // same map: vanilla's giver owns it.
                }
                if (!pawn.Map.SameColumn(holderMap))
                {
                    return null;
                }
                if (target.EntityHolder?.HeldPawn != null)
                {
                    return null; // holder got occupied since assignment.
                }
                if (t is Pawn victim && !victim.ThreatDisabled(pawn))
                {
                    return null;
                }
                if (!pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Deadly, 1, -1, null, forced))
                {
                    return null;
                }
                LevelComp levels = pawn.Map.Levels();
                if (levels == null)
                {
                    return null;
                }
                int dir = Math.Sign(holderMap.Level() - pawn.Map.Level());
                Map next = dir > 0 ? levels.upperMap : dir < 0 ? levels.lowerMap : null;
                if (next == null || next.Disposed)
                {
                    return null;
                }
                IntVec3 hint = holderMap == next ? holder.Position : IntVec3.Invalid;
                if (!CrossLevelWork.TryResolveStairsStrict(pawn, next, hint,
                    out Building_ABStairs stairs, out Building_ABStairs exit))
                {
                    return null;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_TakeEntityAcrossLevels, t, stairs);
                job.targetC = exit;
                job.count = 1;
                if (issue)
                {
                    // Heal the PostSpawnSetup wipe: landing the entity on an
                    // intermediate level nulls a cross-map targetHolder the
                    // moment it respawns. Re-arm it on arrival so the chain
                    // (our giver again, or vanilla's for the final leg)
                    // continues seamlessly.
                    Thing entityRef = t;
                    Thing holderRef = holder;
                    ABPendingOrders.Set(pawn, next, delegate
                    {
                        CompHoldingPlatformTarget c = entityRef?.TryGetComp<CompHoldingPlatformTarget>();
                        if (c != null && c.targetHolder == null
                            && holderRef != null && !holderRef.Destroyed
                            && holderRef.TryGetComp(out CompEntityHolder h) && h.HeldPawn == null)
                        {
                            c.targetHolder = holderRef;
                        }
                    });
                }
                return job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level entity capture");
                return null;
            }
        }
    }
}
