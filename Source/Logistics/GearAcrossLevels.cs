using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Gear upkeep across levels (parity audit P1/P2): apparel optimization,
    /// opportunistic weapon pickup, and drug-policy inventory stocking all
    /// scan only the pawn's own map, so the wardrobe, armory, or drug shelf
    /// one level away never existed for them. Shared pattern: when the
    /// vanilla giver comes up empty, re-run the SAME giver with the pawn
    /// virtually placed at a linked level's stairwell exit; a hit proves the
    /// trip is worth it, the probe job is discarded, and the pawn takes the
    /// stairs - on arrival the vanilla giver re-resolves naturally. One
    /// shared per-pawn cooldown spans all three flavors so an idle colonist
    /// runs at most one gear probe per window; probes ride the same
    /// VirtualScanActive guard as work probes (which also stops the patched
    /// givers from recursing into themselves).
    /// </summary>
    internal static class ABGearAcrossLevels
    {
        private const int CooldownTicks = 2000;

        private static readonly ABPawnCooldown cooldown = new ABPawnCooldown();

        internal static bool Gate(Pawn pawn, Job result)
        {
            if (result != null || !LevelCensus.AnyLevelColumns || CrossLevelWork.VirtualScanActive
                || !ABGuard.On(ABGuard.Logistics))
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return false;
            }
            return NeedsCross.EligibleColonist(pawn);
        }

        /// <summary>Probe the linked levels with the given giver-flavored
        /// scan; returns the stairs job toward the first level where the scan
        /// found something, else null. Charges the shared cooldown on every
        /// attempt.</summary>
        internal static Job TryRoute(Pawn pawn, Func<Pawn, Job> probe)
        {
            try
            {
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return null;
                }
                int now = Find.TickManager.TicksGame;
                if (!cooldown.Ready(pawn, now))
                {
                    return null;
                }
                cooldown.ChargeUntil(pawn, now + CooldownTicks);
                return TryOn(pawn, probe, comp.upperMap) ?? TryOn(pawn, probe, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "gear probe routing");
                return null;
            }
        }

        private static Job TryOn(Pawn pawn, Func<Pawn, Job> probe, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs,
                out Building_ABStairs exit))
            {
                return null;
            }
            if (!ABVirtualPosition.TrySwap(pawn, target, exit.Position, out ABVirtualPosition.Token token))
            {
                return null;
            }
            Job found;
            CrossLevelWork.VirtualScanActive = true;
            try
            {
                found = probe(pawn);
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
                CrossLevelWork.VirtualScanActive = false;
            }
            if (found == null)
            {
                return null;
            }
            IntVec3 dest = found.targetA.IsValid && found.targetA.HasThing
                ? found.targetA.Thing.PositionHeld
                : IntVec3.Invalid;
            StairRouter.Reroute(pawn, target, dest, ref stairs, ref exit);
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }
    }

    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    internal static class Patch_OptimizeApparel_CrossLevel
    {
        private static readonly JobGiver_OptimizeApparel giver = new JobGiver_OptimizeApparel();

        private static readonly MethodInfo tryGiveJob =
            AccessTools.Method(typeof(JobGiver_OptimizeApparel), "TryGiveJob");

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGearAcrossLevels.Gate(pawn, __result) || tryGiveJob == null)
            {
                return;
            }
            Job route = ABGearAcrossLevels.TryRoute(pawn, Probe);
            if (route != null)
            {
                // Let the arrival scan run immediately instead of waiting out
                // the 6000-9000 tick optimize window the failed local scan set.
                pawn.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame;
                __result = route;
            }
        }

        private static Job Probe(Pawn pawn)
        {
            // The giver tick-gates itself; the failed local scan just charged
            // the window, so open it for the probe (it re-charges on a miss).
            pawn.mindState.nextApparelOptimizeTick = -99999;
            return tryGiveJob.Invoke(giver, new object[] { pawn }) as Job;
        }
    }

    [HarmonyPatch(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob")]
    internal static class Patch_PickUpWeapon_CrossLevel
    {
        private static readonly JobGiver_PickUpOpportunisticWeapon giver =
            new JobGiver_PickUpOpportunisticWeapon();

        private static readonly MethodInfo tryGiveJob =
            AccessTools.Method(typeof(JobGiver_PickUpOpportunisticWeapon), "TryGiveJob");

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGearAcrossLevels.Gate(pawn, __result) || tryGiveJob == null)
            {
                return;
            }
            // Only worth a trip for the genuinely unarmed.
            if (pawn.equipment?.Primary != null)
            {
                return;
            }
            Job route = ABGearAcrossLevels.TryRoute(pawn,
                p => tryGiveJob.Invoke(giver, new object[] { p }) as Job);
            if (route != null)
            {
                __result = route;
            }
        }
    }

    [HarmonyPatch(typeof(JobGiver_MoveDrugsToInventory), "TryGiveJob")]
    internal static class Patch_MoveDrugs_CrossLevel
    {
        private static readonly JobGiver_MoveDrugsToInventory giver = new JobGiver_MoveDrugsToInventory();

        private static readonly MethodInfo tryGiveJob =
            AccessTools.Method(typeof(JobGiver_MoveDrugsToInventory), "TryGiveJob");

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGearAcrossLevels.Gate(pawn, __result) || tryGiveJob == null)
            {
                return;
            }
            Job route = ABGearAcrossLevels.TryRoute(pawn,
                p => tryGiveJob.Invoke(giver, new object[] { p }) as Job);
            if (route != null)
            {
                __result = route;
            }
        }
    }
}
