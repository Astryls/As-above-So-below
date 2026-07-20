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
    /// Cross-level recreation. When the joy scan finds NOTHING on the pawn's
    /// level (no horseshoes on the rooftop, no telescope in the basement), the
    /// pawn is virtually placed at a linked stairwell's exit and the same joy
    /// giver re-runs on the other map; if anything is available there, the
    /// pawn takes the stairs and re-rolls joy on arrival. Runs at LOW Harmony
    /// priority so Common Sense's joy tweaks (drug-on-the-way, ingest
    /// preferences) and every other joy patch get first refusal - we only act
    /// on a final null. Kill switch: logistics.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_GetJoy), "TryGiveJob")]
    [HarmonyPriority(Priority.Low)]
    internal static class Patch_GetJoy_CrossLevel
    {
        private const int RetryCooldownTicks = 600;

        /// <summary>True while the virtual re-scan runs so the postfix does
        /// not recurse through TryIssueJobPackage.</summary>
        private static bool inVirtualScan;

        private static readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

        private static void Postfix(Pawn pawn, ref Job __result, JobGiver_GetJoy __instance)
        {
            if (__result != null || inVirtualScan || !ABGuard.On(ABGuard.Logistics))
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
            Map map = pawn.Map;
            LevelComp comp = map.Levels();
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
                __result = TryTowards(__instance, pawn, comp.upperMap)
                    ?? TryTowards(__instance, pawn, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level recreation");
            }
        }

        private static Job TryTowards(JobGiver_GetJoy giver, Pawn pawn, Map target)
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
            ABLog.Dev("Joy migration: " + pawn.LabelShort + " heading to level " + target.Level() + " for recreation.");
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }
    }
}
