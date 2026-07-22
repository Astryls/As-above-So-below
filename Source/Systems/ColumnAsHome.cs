using System;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// One colony, one home (BUG pass 2026-07-21). Two vanilla verdicts made
    /// pawns on other levels read as "away from the colony":
    ///
    /// PSYCHIC BOND: ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson
    /// ends on a MapHeld equality check, so a Highmate on the sky level and
    /// their spouse on the surface both took the -10 bond distance debuff (the
    /// hediff's distance stage keys off the same method, so the consciousness
    /// penalty followed too). Same column now counts as near; real separation
    /// (caravans, other tiles) keeps the vanilla verdict.
    ///
    /// PLAYER HOME: our levels are pocket maps, and pocket parents are never
    /// canBePlayerHome, so Map.IsPlayerHome was false below and above ground.
    /// Everything that asks "is this pawn at the colony" - vanilla royalty
    /// expectations, guest/prisoner forbidden rules, quest requirements, and
    /// every modded separation/"gone from colony" thought that checks the
    /// map's home flag - treated level pawns as travellers. A level now
    /// reports its column ground map's verdict, EXCEPT inside two scans where
    /// per-map home semantics are load-bearing:
    ///  - the alert readout (Alert_LowFood, NeedMealSource, NeedDefenses...
    ///    evaluate every home map in isolation; levels counting as home would
    ///    nag per level for services the column provides once), and
    ///  - quest generation (QuestGen.Working, vanilla's own flag), so quest
    ///    map pickers keep anchoring shuttles, raids and rewards on the
    ///    surface rather than a basement.
    /// Reentrancy is bounded: a level's lookup asks the GROUND map, and the
    /// ground map's own postfix exits on the level check.
    /// Kill switch: social.
    /// </summary>
    [HarmonyPatch(typeof(ThoughtWorker_PsychicBondProximity), nameof(ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson))]
    internal static class Patch_PsychicBondProximity_CrossLevel
    {
        private static void Postfix(Pawn pawn, Hediff_PsychicBond bondHediff, ref bool __result)
        {
            if (__result || !ABGuard.On(ABGuard.Social))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelSocial)
            {
                return;
            }
            try
            {
                if (!(bondHediff?.target is Pawn bonded) || pawn == null)
                {
                    return;
                }
                Map a = pawn.MapHeld;
                Map b = bonded.MapHeld;
                if (a == null || b == null || a == b)
                {
                    return;
                }
                if (a.SameColumn(b))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Social, e, "cross-level psychic bond proximity");
            }
        }
    }

    /// <summary>Marks the frames spent inside the alert readout's recalcs so
    /// the IsPlayerHome column extension can stand down there (per-map alerts
    /// must keep seeing levels as non-home or they nag once per level).</summary>
    [HarmonyPatch(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate))]
    internal static class Patch_AlertsScope_VanillaHome
    {
        internal static int Depth;

        private static void Prefix()
        {
            Depth++;
        }

        private static void Finalizer()
        {
            Depth = Math.Max(0, Depth - 1);
        }
    }

    [HarmonyPatch(typeof(Map), nameof(Map.IsPlayerHome), MethodType.Getter)]
    internal static class Patch_Map_IsPlayerHome_Column
    {
        private static void Postfix(Map __instance, ref bool __result)
        {
            if (__result || !ABGuard.On(ABGuard.Social))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelSocial)
            {
                return;
            }
            if (Patch_AlertsScope_VanillaHome.Depth > 0 || QuestGen.Working)
            {
                return;
            }
            try
            {
                if (ColumnWorld.TryGetColumnGround(__instance, out Map ground))
                {
                    __result = ground.IsPlayerHome;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Social, e, "column player-home identity");
            }
        }
    }
}
