using System;
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
                Map target = CrossLevelHaul.TargetLevelFor(pawn, clickedThing, out Building_ABStairs stairs);
                if (target == null || stairs == null)
                {
                    return null;
                }
                bool up = target.Level() > map.Level();
                string label = (up ? "AB_HaulUpTo" : "AB_HaulDownTo").Translate(clickedThing.LabelShort);
                FloatMenuOption option = new FloatMenuOption(label, delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, clickedThing, stairs);
                    job.count = Mathf.Min(clickedThing.stackCount, pawn.carryTracker.MaxStackSpaceEver(clickedThing.def));
                    job.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }, MenuOptionPriority.High);
                return FloatMenuUtility.DecoratePrioritizedTask(option, pawn, clickedThing);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level haul option");
                return null;
            }
        }
    }
}
