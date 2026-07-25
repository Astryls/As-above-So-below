using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Biotech mech recharge across levels (parity audit P1, same bug class as
    /// the Misc. Robots dock fix): JobGiver_GetEnergy_Charger only searches
    /// the mech's own map, so a mech working on a level without a charger
    /// drains dry and self-shutdowns next to the stairs instead of walking
    /// home. When the vanilla giver finds nothing and a charger on a linked
    /// level could take this mech, route it there; arrival re-runs the giver
    /// against a local charger. Charger usability is evaluated under a
    /// virtual position swap so vanilla's own checks (power, occupancy,
    /// compatibility) apply unmodified.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_GetEnergy_Charger), "TryGiveJob")]
    internal static class Patch_MechCharge_CrossLevel
    {
        private const int CooldownTicks = 600;

        private static readonly ABPawnCooldown cooldown = new ABPawnCooldown();

        private static readonly MethodInfo shouldAutoRecharge =
            AccessTools.Method(typeof(JobGiver_GetEnergy), "ShouldAutoRecharge");

        private static void Postfix(JobGiver_GetEnergy_Charger __instance, Pawn pawn, ref Job __result)
        {
            if (__result != null || !ModsConfig.BiotechActive || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed
                    || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.IsMechanoid
                    || pawn.needs?.energy == null || pawn.GetLord() != null)
                {
                    return;
                }
                if (!WantsCharge(__instance, pawn))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!cooldown.Ready(pawn, now))
                {
                    return;
                }
                cooldown.ChargeUntil(pawn, now + CooldownTicks);
                __result = TryToward(pawn, comp.upperMap) ?? TryToward(pawn, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "mech charge routing", pawn);
            }
        }

        /// <summary>ShouldAutoRecharge is a protected virtual INSTANCE method
        /// (it reads the giver's forced field). The first cut invoked it with a
        /// null target, which throws TargetException ("Non-static method
        /// requires a target") on the FIRST idle colony mech and tripped the
        /// whole Logistics kill switch - the 2026-07-24 user bug wave (silent
        /// loss of cross-level bill supply and product hauling, blamed on WVC
        /// work modes). Invoking on the patched giver instance also dispatches
        /// virtually, so modded overrides of the recharge policy (WVC etc.)
        /// are respected rather than bypassed.</summary>
        private static bool WantsCharge(JobGiver_GetEnergy_Charger giver, Pawn pawn)
        {
            if (shouldAutoRecharge != null && giver != null)
            {
                try
                {
                    return shouldAutoRecharge.Invoke(giver, new object[] { pawn }) is bool b && b;
                }
                catch (Exception e)
                {
                    // A modded recharge-policy override (WVC etc.) threw. Do not
                    // let it trip the whole Logistics kill switch - name it once
                    // and fall through to the simple low-battery threshold.
                    Log.WarningOnce(ABLog.Tag + " a mech recharge policy threw for "
                        + ABBlame.Describe(pawn) + "; using the low-battery fallback. "
                        + (e.InnerException ?? e).Message,
                        Gen.HashCombineInt(pawn.def?.shortHash ?? 0, 0x0C0FFEE));
                }
            }
            // Fallback if the vanilla helper moves or a modded override faulted:
            // route only when clearly low.
            return pawn.needs.energy.CurLevelPercentage < 0.35f;
        }

        /// <summary>Internal so the WVC Work Modes compat module can reuse the
        /// verified charger probe for their own charger giver (which declares
        /// its own TryGiveJob, bypassing this patch).</summary>
        internal static Job TryToward(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            List<Building> buildings = target.listerBuildings.allBuildingsColonist;
            Building_MechCharger charger = null;
            for (int i = 0; i < buildings.Count; i++)
            {
                if (!(buildings[i] is Building_MechCharger candidate))
                {
                    continue;
                }
                // Vanilla's usability check, with the mech virtually on the
                // charger's level so map-scoped conditions evaluate there.
                if (!ABVirtualPosition.TrySwap(pawn, target, candidate.Position,
                    out ABVirtualPosition.Token token))
                {
                    continue;
                }
                bool usable;
                try
                {
                    usable = candidate.CanPawnChargeCurrently(pawn);
                }
                catch (Exception ce)
                {
                    // Harden against modded chargers (or modded mechs whose
                    // usability check chokes): skip this one charger and keep
                    // scanning instead of letting the throw bubble up and trip
                    // the entire Logistics subsystem. Named once, not spammed.
                    Log.WarningOnce(ABLog.Tag + " charger " + ABBlame.Describe(candidate)
                        + " threw while checking " + ABBlame.Describe(pawn)
                        + " for cross-level recharge; skipping it. "
                        + (ce.InnerException ?? ce).Message,
                        Gen.HashCombineInt(candidate.thingIDNumber, 0x5EED));
                    continue;
                }
                finally
                {
                    ABVirtualPosition.Restore(pawn, token);
                }
                if (usable)
                {
                    charger = candidate;
                    break;
                }
            }
            if (charger == null)
            {
                return null;
            }
            if (!CrossLevelWork.TryStairsJobToward(pawn, target, charger.Position, out Job job))
            {
                return null;
            }
            ABLog.Dev("Routing mech " + pawn.LabelShort + " toward a charger on level " + target.Level() + ".");
            return job;
        }
    }
}
