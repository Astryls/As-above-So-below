using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Right-click option to haul an item to storage (or construction demand) on a
    /// linked level. Auto-discovered by FloatMenuMakerMap. Gives the player direct
    /// control where autonomous hauling would wait for an idle pawn.
    /// </summary>
    public class FloatMenuOptionProvider_HaulAcrossLevels : FloatMenuOptionProvider
    {
        protected override bool Drafted => false;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelHauling)
            {
                return null;
            }
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || clickedThing == null || !clickedThing.def.EverHaulable)
            {
                return null;
            }
            Map map = pawn.Map;
            if (map == null || !map.ConnectedToOtherLevel() || clickedThing.Map != map)
            {
                return null;
            }
            if (clickedThing.IsForbidden(pawn)
                || !pawn.CanReach(clickedThing, PathEndMode.ClosestTouch, Danger.Deadly))
            {
                return null;
            }
            try
            {
                Map target = CrossLevelHaul.TargetLevelFor(pawn, clickedThing, out Building_ABStairs stairs,
                    ignorePins: false, out int allowedCount, out bool _);
                if (target == null || stairs == null)
                {
                    return null;
                }
                bool up = target.Level() > map.Level();
                string label = (up ? "AB_HaulUpTo" : "AB_HaulDownTo").Translate(clickedThing.LabelShort);
                FloatMenuOption option = new FloatMenuOption(label, delegate
                {
                    // Build handles adjacent (single/bulk) AND far (2-gap relay
                    // hop) targets; the old inline single job broke for a 2-floor
                    // destination (CounterpartTowards returned null).
                    Job job = CrossLevelHaulJob.Build(pawn, clickedThing, target, stairs, allowedCount: allowedCount);
                    if (job == null)
                    {
                        return;
                    }
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.Default);
                return FloatMenuUtility.DecoratePrioritizedTask(option, pawn, clickedThing);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level haul option");
                return null;
            }
        }
    }

    /// <summary>Remove vanilla's disabled "No empty, accessible spot to store it"
    /// haul option whenever the clicked item actually HAS a better home elsewhere
    /// in the column - our own "Haul {0} to the level above/below" option covers
    /// it, so the redundant "no spot" line is just noise (user report 2026-07-26).
    /// The check is item-centric (ColumnStorage), so it holds regardless of which
    /// level the pawn is standing on. When there is genuinely no storage anywhere
    /// in the column the vanilla line is accurate and stays.</summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    internal static class Patch_SuppressNoSpotForCrossLevelHaul
    {
        private static void Postfix(ref List<FloatMenuOption> __result, FloatMenuContext context)
        {
            if (CrossLevelOrders.Redirecting || __result == null || __result.Count == 0
                || context == null || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelHauling)
            {
                return;
            }
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || pawn.Map == null || !pawn.Map.ConnectedToOtherLevel())
            {
                return;
            }
            try
            {
                List<Thing> things = context.ClickedThings;
                if (things == null)
                {
                    return;
                }
                bool crossHome = false;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t != null && t.def.EverHaulable && t.SpawnedOrAnyParentSpawned
                        && ColumnStorage.TryFindBetter(pawn, t, out Map _, out IntVec3 _,
                            out IHaulDestination _, out StoragePriority _))
                    {
                        crossHome = true;
                        break;
                    }
                }
                if (!crossHome)
                {
                    return;
                }
                string noSpot = HaulAIUtility.NoEmptyPlaceLowerTrans;
                if (!string.IsNullOrEmpty(noSpot))
                {
                    __result.RemoveAll(o => o != null && o.Disabled
                        && !string.IsNullOrEmpty(o.Label) && o.Label.Contains(noSpot));
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "suppress no-spot option");
            }
        }
    }
}
