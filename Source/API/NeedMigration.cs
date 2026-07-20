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
    /// Generic cross-level migration for MODDED NEEDS (T11/T12). Mods like RJW
    /// (Sex need) and Intimacy satisfy their needs through their own
    /// ThinkNode_JobGiver subclasses, which scan only the pawn's map - a
    /// partner or facility on another level is invisible and the need tanks.
    /// Registered giver types get the joy treatment: when they return no job,
    /// the same giver re-runs virtually at each linked stairwell exit, and on
    /// a hit the pawn takes the stairs and re-rolls on arrival. Because the
    /// hook only fires when the giver was INVOKED, the mod's own think tree
    /// gating (need thresholds, chance-per-hour nodes) is inherited untouched.
    ///
    /// Mechanism: one LOW-priority postfix on the BASE
    /// ThinkNode_JobGiver.TryIssueJobPackage. Types that override it
    /// (JobGiver_Work does; it has its own migration) are unaffected by
    /// C# override dispatch. Cost with nothing registered: one static bool.
    ///
    /// Built-in registrations (type names verified against the shipped
    /// assemblies): RJW JoinInBed + DoQuickie (partner-seeking only - solo and
    /// hostile-context givers are deliberately excluded), Intimacy
    /// GetIntimacy. Other mods self-register via
    /// ABApi.RegisterNeedJobGiver("Full.Type.Name").
    /// </summary>
    internal static class NeedMigration
    {
        internal static bool Any;

        private static readonly HashSet<string> registeredNames = new HashSet<string>();

        private static readonly Dictionary<Type, bool> typeCache = new Dictionary<Type, bool>();

        internal static void Register(string fullTypeName)
        {
            if (fullTypeName.NullOrEmpty())
            {
                return;
            }
            registeredNames.Add(fullTypeName);
            typeCache.Clear();
            Any = true;
        }

        internal static bool IsRegistered(Type type)
        {
            if (typeCache.TryGetValue(type, out bool known))
            {
                return known;
            }
            bool match = registeredNames.Contains(type.FullName);
            if (typeCache.Count > 256)
            {
                typeCache.Clear();
            }
            typeCache[type] = match;
            return match;
        }
    }

    [StaticConstructorOnStartup]
    internal static class NeedMigrationBuiltins
    {
        static NeedMigrationBuiltins()
        {
            if (ModsConfig.IsActive("rim.job.world"))
            {
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_JoinInBed");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_DoQuickie");
                // Rape/breeding family (user opt-in): colonist-initiated only -
                // the engine's IsColonistPlayerControlled filter means enemy AI
                // variants (AIRapePrisoner, NymphSapper) would be dead entries
                // and stay unregistered. Targets on other levels are found by
                // the giver's own scan from the stairwell exit.
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_Breed");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_Bestiality");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_ComfortPrisonerRape");
                ABApi.RegisterNeedJobGiver("rjw.JobGiver_RandomRape");
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

        private static readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

        private static void Postfix(ThinkNode_JobGiver __instance, Pawn pawn, ref ThinkResult __result)
        {
            // Ordered cheapest-first: this postfix sees every job-giver
            // evaluation of every pawn.
            if (!NeedMigration.Any || __result.Job != null || inVirtualScan
                || CrossLevelWork.VirtualScanActive)
            {
                return;
            }
            if (!NeedMigration.IsRegistered(__instance.GetType()))
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
            if (pawn == null || !pawn.Spawned || pawn.Downed || pawn.Drafted
                || !pawn.IsColonistPlayerControlled || pawn.GetLord() != null)
            {
                return;
            }
            if (pawn.InMentalState)
            {
                // Mental-state think trees (RJW's random-rape break among
                // them) re-issue jobs on their own cadence; injecting a
                // stairs commute would oscillate against the state.
                return;
            }
            LevelComp comp = pawn.Map.Levels();
            if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (nextAllowedTick.TryGetValue(pawn.thingIDNumber, out int next) && now < next)
            {
                return;
            }
            if (nextAllowedTick.Count > 512)
            {
                nextAllowedTick.Clear();
            }
            nextAllowedTick[pawn.thingIDNumber] = now + RetryCooldownTicks;
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
                + " for " + giver.GetType().Name + ".");
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }
    }
}
