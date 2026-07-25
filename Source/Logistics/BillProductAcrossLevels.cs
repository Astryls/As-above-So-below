using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bill products follow their storage across levels (2026-07-25 report:
    /// "pawns drop items on the floor instead of hauling to best stockpile
    /// when the stockpile is at a different z level"). Vanilla's
    /// FinishRecipeAndStartStoringProduct searches actor.Map ONLY: with the
    /// bill on "take to best stockpile" and the only accepting stockpile on a
    /// linked level, the local search fails, the product is dropped at the
    /// bench, and the job ends - the crafter walks away, leaving the ferry to
    /// whichever hauler rediscovers the stack minutes later.
    ///
    /// The toil's initAction is wrapped: when the vanilla branch ends in the
    /// drop (no carry started, no local store job), the crafter immediately
    /// takes the cross-level haul toward the level whose storage (or demand)
    /// wants the product - same continuation vanilla gives for a local
    /// stockpile, one level further. "Drop on floor" bills and specific-
    /// stockpile bills keep exact vanilla behavior (the specific group is
    /// map-scoped by definition). Kill switch: logistics.
    /// </summary>
    [HarmonyPatch(typeof(Toils_Recipe), nameof(Toils_Recipe.FinishRecipeAndStartStoringProduct))]
    internal static class Patch_BillProduct_CrossLevelStore
    {
        private static void Postfix(Toil __result)
        {
            Action original = __result.initAction;
            if (original == null)
            {
                return;
            }
            __result.initAction = delegate
            {
                // Snapshot BEFORE the original runs: it ends the job, after
                // which the bill and recipe are no longer reachable.
                ThingDef productDef = null;
                try
                {
                    Job curJob = __result.actor?.jobs?.curJob;
                    if (curJob?.bill != null
                        && curJob.bill.GetStoreMode() == BillStoreModeDefOf.BestStockpile)
                    {
                        List<ThingDefCountClass> products = curJob.RecipeDef?.products;
                        if (products != null && products.Count == 1)
                        {
                            productDef = products[0].thingDef;
                        }
                    }
                }
                catch
                {
                    productDef = null;
                }
                original();
                if (productDef == null)
                {
                    return;
                }
                try
                {
                    TryCrossLevelStore(__result.actor, productDef);
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.Logistics, e, "bill product cross-level store");
                }
            };
        }

        /// <summary>The vanilla toil ended in the drop-at-bench branch when the
        /// pawn carries nothing afterwards. Find the freshly dropped product
        /// beside the pawn and, when a linked level's storage or demand wants
        /// it, start the crafter's own cross-level haul at once.</summary>
        private static void TryCrossLevelStore(Pawn actor, ThingDef productDef)
        {
            if (!ABGuard.On(ABGuard.Logistics) || actor == null || !actor.Spawned)
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelHauling)
            {
                return;
            }
            if (actor.carryTracker?.CarriedThing != null || actor.CurJobDef == JobDefOf.HaulToCell)
            {
                return; // vanilla found local storage and is handling it.
            }
            if (actor.Drafted || actor.GetLord() != null || !actor.Map.ConnectedToOtherLevel())
            {
                return;
            }
            // The drop was ThingPlaceMode.Near from the pawn's cell: scan the
            // immediate neighborhood for an unstored, unforbidden stack of the
            // product def (a merge target counts - storing it is just as right).
            Thing product = null;
            float bestDist = float.MaxValue;
            CellRect rect = CellRect.CenteredOn(actor.Position, 2).ClipInsideMap(actor.Map);
            foreach (IntVec3 c in rect)
            {
                List<Thing> things = c.GetThingList(actor.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t.def != productDef || !t.Spawned || t.IsForbidden(actor))
                    {
                        continue;
                    }
                    if (StoreUtility.CurrentStoragePriorityOf(t) != StoragePriority.Unstored)
                    {
                        continue;
                    }
                    float d = (t.Position - actor.Position).LengthHorizontalSquared;
                    if (d < bestDist)
                    {
                        bestDist = d;
                        product = t;
                    }
                }
            }
            if (product == null)
            {
                return;
            }
            Map target = CrossLevelHaul.TargetLevelFor(actor, product, out Building_ABStairs stairs,
                ignorePins: false, out int demandCount);
            if (target == null || stairs == null)
            {
                return;
            }
            Job job = JobMaker.MakeJob(ABDefOf.AB_HaulAcrossLevels, product, stairs);
            job.targetC = stairs.CounterpartTowards(target);
            int count = Mathf.Min(product.stackCount, actor.carryTracker.MaxStackSpaceEver(product.def));
            if (demandCount > 0)
            {
                count = Mathf.Min(count, demandCount);
                CrossLevelDemand.NoteInFlight(actor, target, product.def, count);
            }
            job.count = count;
            actor.jobs.StartJob(job, JobCondition.InterruptForced);
            ABLog.Dev("Bill product continuation: " + actor.LabelShort + " carries "
                + product.LabelShort + " toward level " + target.Level() + ".");
        }
    }
}
