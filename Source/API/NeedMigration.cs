using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Generic cross-level migration for need-driven and mental-state-driven
    /// job givers (T11/T12). Registered ThinkNode_JobGiver types get the joy
    /// treatment: when they return no job on the pawn's map, the same giver
    /// re-runs virtually at each linked stairwell exit, and on a hit the pawn
    /// takes the stairs and re-rolls on arrival. The hook fires only when the
    /// giver was INVOKED, so each mod's own think-tree gating is inherited.
    ///
    /// TWO TIERS: normal registrations act on player-controlled colonists
    /// outside mental states. MENTAL-SAFE registrations also act during
    /// mental breaks (binges hunt beer downstairs, berserkers come down the
    /// stairs after victims) - the state keeps issuing its own jobs and the
    /// 600t cooldown absorbs any interruption churn. Note that
    /// IsColonistPlayerControlled is FALSE during states, so the mental tier
    /// uses a faction+humanlike filter instead.
    ///
    /// Mechanism: one LOW-priority postfix on the BASE
    /// ThinkNode_JobGiver.TryIssueJobPackage; types overriding it
    /// (JobGiver_Work) are unaffected by override dispatch. The registry
    /// matches CONCRETE runtime type names (JobGiver_Binge is abstract: the
    /// real nodes are BingeDrug and BingeFood).
    /// </summary>
    internal static class NeedMigration
    {
        internal const int TierNone = 0;
        internal const int TierNormal = 1;
        internal const int TierMentalSafe = 2;

        internal static bool Any;

        private static readonly Dictionary<string, bool> registeredNames = new Dictionary<string, bool>();

        private static readonly Dictionary<Type, int> typeCache = new Dictionary<Type, int>();

        internal static void Register(string fullTypeName, bool mentalSafe)
        {
            if (fullTypeName.NullOrEmpty())
            {
                return;
            }
            registeredNames[fullTypeName] = mentalSafe;
            typeCache.Clear();
            Any = true;
        }

        internal static int TierOf(Type type)
        {
            if (typeCache.TryGetValue(type, out int tier))
            {
                return tier;
            }
            tier = registeredNames.TryGetValue(type.FullName, out bool mentalSafe)
                ? (mentalSafe ? TierMentalSafe : TierNormal)
                : TierNone;
            if (typeCache.Count > 256)
            {
                typeCache.Clear();
            }
            typeCache[type] = tier;
            return tier;
        }
    }

    [StaticConstructorOnStartup]
    internal static class NeedMigrationBuiltins
    {
        static NeedMigrationBuiltins()
        {
            // Vanilla mental breaks that hunt for something findable on other
            // levels. Concrete types only; wander-style givers are pointless
            // here (they never return null locally).
            ABApi.RegisterNeedJobGiver("RimWorld.JobGiver_BingeDrug", allowInMentalState: true);
            ABApi.RegisterNeedJobGiver("RimWorld.JobGiver_BingeFood", allowInMentalState: true);
            ABApi.RegisterNeedJobGiver("RimWorld.JobGiver_Berserk", allowInMentalState: true);
            ABApi.RegisterNeedJobGiver("RimWorld.JobGiver_MurderousRage", allowInMentalState: true);
            if (ModsConfig.IsActive("rim.job.world"))
            {
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_JoinInBed");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_DoQuickie");
                // Rape/breeding family (user opt-in): colonist-initiated only -
                // enemy AI variants (AIRapePrisoner, NymphSapper) would be dead
                // entries under the pawn filters and stay unregistered.
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_Breed");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_Bestiality");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_ComfortPrisonerRape");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_RandomRape", allowInMentalState: true);
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_RapeEnemy");
                ABLog.Dev("RJW detected: sex, breeding and rape givers registered for cross-level migration.");
            }
            if (ModsConfig.IsActive("LovelyDovey.Sex.WithEuterpe"))
            {
                ABApi.RegisterNeedJobGiver("LoveyDoveySexWithEuterpe.JobGiver_GetIntimacy");
                ABLog.Dev("Intimacy detected: intimacy giver registered for cross-level migration.");
            }
        }
    }

    [HarmonyPatch(typeof(ThinkNode_JobGiver), nameof(ThinkNode_JobGiver.TryIssueJobPackage))]
    [HarmonyPriority(Priority.Low)]
    internal static class Patch_NeedGiver_CrossLevel
    {
        private const int RetryCooldownTicks = 600;

        private static bool inVirtualScan;

        private static readonly ABPawnCooldown retryCooldown = new ABPawnCooldown();

        private static void Postfix(ThinkNode_JobGiver __instance, Pawn pawn, ref ThinkResult __result)
        {
            // Ordered cheapest-first: this postfix sees every job-giver
            // evaluation of every pawn.
            if (!NeedMigration.Any || __result.Job != null || inVirtualScan
                || CrossLevelWork.VirtualScanActive)
            {
                return;
            }
            int tier = NeedMigration.TierOf(__instance.GetType());
            if (tier == NeedMigration.TierNone)
            {
                return;
            }
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.Drafted || pawn.GetLord() != null)
            {
                return;
            }
            if (pawn.InMentalState)
            {
                // IsColonistPlayerControlled is false in a state; mental-safe
                // givers act on the colony's own humanlikes (incl. slaves).
                if (tier != NeedMigration.TierMentalSafe
                    || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike)
                {
                    return;
                }
            }
            else if (!pawn.IsColonistPlayerControlled)
            {
                return;
            }
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (!retryCooldown.Ready(pawn, now))
            {
                return;
            }
            retryCooldown.ChargeUntil(pawn, now + RetryCooldownTicks);
            try
            {
                Job stairsJob = TryTowards(__instance, pawn, comp.upperMap)
                    ?? TryTowards(__instance, pawn, comp.lowerMap);
                if (stairsJob != null)
                {
                    __result = new ThinkResult(stairsJob, __instance, JobTag.SatisfyingNeeds);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "modded need migration");
            }
        }

        private static Job TryTowards(ThinkNode_JobGiver giver, Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return null;
            }
            bool found;
            inVirtualScan = true;
            try
            {
                found = ABVirtualPosition.WithPawnAt(pawn, target, exit.Position,
                    () => giver.TryIssueJobPackage(pawn, default(JobIssueParams)).Job != null);
            }
            finally
            {
                inVirtualScan = false;
            }
            if (!found)
            {
                return null;
            }
            ABLog.Dev("Need migration: " + pawn.LabelShort + " heading to level " + target.Level()
                + " for " + giver.GetType().Name + (pawn.InMentalState ? " (mental state)." : "."));
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }
    }
}
