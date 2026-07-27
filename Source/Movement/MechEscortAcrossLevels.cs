using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Escort-mode mechs follow their mechanitor across levels. Vanilla's
    /// escort branch (ThinkNode_ConditionalWorkMode Escort) nulls out top to
    /// bottom when the overseer holds a different map: AIFollowOverseer dies
    /// on the cross-map CanReach, WanderOverseer bails on Map != pawn.Map,
    /// ExitMapFollowOverseer only fires for caravan exits - so the tree falls
    /// through to the idle "Patrolling" wander and the group strands.
    ///
    /// Fix: postfix JobGiver_AIFollowOverseer.TryGiveJob. When it produced no
    /// job and the overseer is on a linked same-column level, issue our
    /// destination-aware stairs job toward the overseer's level. Arrival
    /// re-runs the escort branch: overseer now local means normal follow;
    /// two-hop cases (sky mech, basement overseer) chain naturally because
    /// the giver fires again on the intermediate level. The think node
    /// already gates this to Escort work mode, so drafted mechs and other
    /// work modes never enter the postfix.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIFollowOverseer), "TryGiveJob")]
    internal static class Patch_MechEscortFollow_CrossLevel
    {
        /// <summary>Gate on re-scanning for stairs each think tick while the
        /// overseer stays off-level. One rethink interval, so a mech left on
        /// the wrong level notices new stairs quickly; a failed stair search
        /// stays cheap because the charge lands before the region scan.</summary>
        private const int FollowCooldownTicks = 250;

        private static readonly ABPawnCooldown followCooldown = new ABPawnCooldown();

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.Map == null
                    || pawn.GetLord() != null)
                {
                    return;
                }
                Pawn overseer = pawn.GetOverseer();
                if (overseer == null)
                {
                    return;
                }
                Map overseerMap = overseer.MapHeld;
                if (overseerMap == null || overseerMap == pawn.Map)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                // Next hop toward the overseer's level; two hops max (cap 3).
                Map next;
                if (comp.upperMap == overseerMap || comp.lowerMap == overseerMap)
                {
                    next = overseerMap;
                }
                else if (comp.upperMap != null && comp.upperMap.Levels()?.upperMap == overseerMap)
                {
                    next = comp.upperMap;
                }
                else if (comp.lowerMap != null && comp.lowerMap.Levels()?.lowerMap == overseerMap)
                {
                    next = comp.lowerMap;
                }
                else
                {
                    // Different map stack entirely (overseer caravanning or at
                    // another colony): vanilla's stranded behavior stands.
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!followCooldown.Ready(pawn, now))
                {
                    return;
                }
                followCooldown.ChargeUntil(pawn, now + FollowCooldownTicks);
                IntVec3 dest = next == overseerMap ? overseer.PositionHeld : IntVec3.Invalid;
                if (!CrossLevelWork.TryStairsJobToward(pawn, next, dest, out Job job))
                {
                    return;
                }
                ABLog.Dev("Escort mech " + pawn.LabelShort + " following overseer " + overseer.LabelShort
                    + " toward level " + overseerMap.Level() + ".");
                __result = job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross-level mech escort follow");
            }
        }
    }
}
