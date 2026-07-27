using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// When Allow Tool is present, moves our cross-level urgent-haul giver
    /// (AB_HaulUrgentlyAcrossLevels) off the vanilla Hauling work type and onto
    /// Allow Tool's own HaulingUrgent ("Haul+") work type at startup.
    ///
    /// Why it is authored under Hauling: HaulingUrgent only exists when Allow
    /// Tool is loaded, so referencing it in XML would be an unresolved def
    /// reference (load error) on every install without Allow Tool. The cost of
    /// that safe default is that the numeric Work-tab priority the player sets
    /// on Haul+ never drove our giver, and a pawn configured with Hauling OFF /
    /// Haul+ ON did no cross-level urgent hauling at all (user report: "haul
    /// urgently does not persist when triggered via ... work-tab priority").
    /// Re-parenting the giver makes it inherit HaulingUrgent's priority exactly
    /// like Allow Tool's own urgent giver, so BOTH the Orders-menu "Haul
    /// Urgently" designation and the Work-tab Haul+ priority engage cross-level
    /// hauling - and any numeric-priority mod (Work Tab) that reorders Haul+
    /// carries our giver with it, since everything downstream reads
    /// WorkGiversInOrderNormal.
    ///
    /// Safe in a static constructor: it fires after every def's
    /// ResolveReferences (both work types' workGiversByPriority lists are built)
    /// but before any pawn caches its giver order (no game is loaded at the main
    /// menu), so the per-pawn caches pick up the new membership with no
    /// invalidation needed. The lists are rebuilt exactly the way
    /// WorkTypeDef.ResolveReferences builds them (priorityInType descending).
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABAllowToolWorkType
    {
        static ABAllowToolWorkType()
        {
            try
            {
                if (!ABCompat.Detect("UnlimitedHugs.AllowTool", "Allow Tool"))
                {
                    return;
                }
                WorkGiverDef giver = DefDatabase<WorkGiverDef>.GetNamedSilentFail("AB_HaulUrgentlyAcrossLevels");
                WorkTypeDef urgent = DefDatabase<WorkTypeDef>.GetNamedSilentFail("HaulingUrgent");
                if (giver == null || urgent == null || giver.workType == urgent)
                {
                    return;
                }
                WorkTypeDef previous = giver.workType;
                giver.workType = urgent;
                // Rebuild both affected lists from scratch so membership and
                // priorityInType ordering match vanilla's own resolve exactly.
                RebuildGiverList(previous);
                RebuildGiverList(urgent);
                ABLog.Dev("Attached AB_HaulUrgentlyAcrossLevels to the HaulingUrgent (Haul+) work type "
                    + "so Allow Tool's numeric work priority drives cross-level urgent hauling.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " could not attach urgent cross-level hauling to Allow Tool's Haul+ "
                    + "work type; it stays on Hauling (still functional via the Orders-menu designation). " + e.Message);
            }
        }

        private static void RebuildGiverList(WorkTypeDef wt)
        {
            if (wt == null)
            {
                return;
            }
            List<WorkGiverDef> ordered = DefDatabase<WorkGiverDef>.AllDefs
                .Where(d => d.workType == wt)
                .OrderByDescending(d => d.priorityInType)
                .ToList();
            wt.workGiversByPriority.Clear();
            wt.workGiversByPriority.AddRange(ordered);
        }
    }
}
