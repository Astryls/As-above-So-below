using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat for Allow Tool's "Haul Urgently" priority hauling
    /// (UnlimitedHugs.AllowTool). Allow Tool marks stacks with a
    /// HaulUrgentlyDesignation and hauls them on its own HaulingUrgent work type
    /// (ranked above normal Hauling) - but its WorkGiver_HaulUrgently only
    /// stores on the SAME map (HaulAIUtility.HaulToStorageJob), so an urgent
    /// item whose only better storage is on a linked level would drop out of the
    /// urgent lane and drift across at ordinary priority (and Allow Tool would
    /// nag "no urgent storage"). This detector lets our high-priority urgent
    /// cross-level giver spot those stacks and carry them across first. Its
    /// placement clears the designation through Allow Tool's own PlaceHauledThing
    /// patch, so nothing here has to touch designations.
    ///
    /// The designation is resolved by name (no assembly reference); absent Allow
    /// Tool the def is null and every query returns false - the giver stays
    /// inert.
    /// </summary>
    public static class ABAllowToolCompat
    {
        private const string PackageId = "UnlimitedHugs.AllowTool";

        private static bool resolved;
        private static bool active;
        private static DesignationDef urgentDef;

        private static void EnsureInit()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            try
            {
                if (!ABDetect.Active(PackageId))
                {
                    return;
                }
                urgentDef = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
                active = urgentDef != null;
            }
            catch (Exception e)
            {
                active = false;
                Log.Warning("[As above, So below] Allow Tool compat init failed, urgent cross-level hauling disabled: " + e.Message);
            }
        }

        public static bool Active
        {
            get
            {
                EnsureInit();
                return active;
            }
        }

        /// <summary>Cheap map-level gate for the urgent giver's ShouldSkip: are
        /// there any urgently-designated stacks on this map at all.</summary>
        public static bool AnyUrgent(Map map)
        {
            EnsureInit();
            if (!active || map?.designationManager == null)
            {
                return false;
            }
            return map.designationManager.AnySpawnedDesignationOfDef(urgentDef);
        }

        /// <summary>This exact thing is flagged Haul Urgently.</summary>
        public static bool IsUrgent(Thing t)
        {
            EnsureInit();
            if (!active || t?.MapHeld?.designationManager == null)
            {
                return false;
            }
            return t.MapHeld.designationManager.DesignationOn(t, urgentDef) != null;
        }

        /// <summary>Every urgently-designated spawned thing on the map -
        /// authoritative enumeration straight from the designations (urgent
        /// stacks already sitting in some valid-but-worse storage never appear
        /// in the haulables lister, so the lister is the wrong source here).</summary>
        public static IEnumerable<Thing> UrgentThings(Map map)
        {
            EnsureInit();
            if (!active || map?.designationManager == null)
            {
                yield break;
            }
            foreach (Designation d in map.designationManager.SpawnedDesignationsOfDef(urgentDef))
            {
                Thing t = d.target.Thing;
                if (t != null && t.Spawned)
                {
                    yield return t;
                }
            }
        }
    }
}
