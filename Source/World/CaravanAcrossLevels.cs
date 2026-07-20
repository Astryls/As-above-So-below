using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// T11 cross-level caravan forming. The form-caravan dialog lists free
    /// colonists from the whole column; on accept, off-surface picks are
    /// trimmed out of the map-scoped forming lord, sent down the stairs, and
    /// joined into the gathering on arrival (the same pending-order replay the
    /// Reverse Commands bridge uses). Animals and prisoners on other levels
    /// stay out of the list in v1: walk them down first. Kill switch: world.
    /// </summary>
    internal static class CaravanAcrossLevels
    {
        internal static bool Active(Map map, out LevelComp comp)
        {
            comp = null;
            if (!ABGuard.On(ABGuard.World))
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.worldIntegration)
            {
                return false;
            }
            comp = map?.Levels();
            return comp != null && comp.level == 0
                && (comp.upperMap != null || comp.lowerMap != null);
        }

        internal static bool ListableForCaravan(Pawn p)
        {
            return p != null && p.Spawned && !p.Dead && !p.Downed && !p.InMentalState
                && p.IsFreeColonist && p.RaceProps.Humanlike && p.RaceProps.allowedOnCaravan
                && !p.IsQuestLodger() && !p.IsQuestHelper()
                && p.GetLord() == null;
        }
    }

    /// <summary>Append the column's free colonists to the sendable-pawn list.
    /// Only for plain caravan calls: transporter-loading lists (groupID >= 0)
    /// keep vanilla scope because that flow is not stair-aware.</summary>
    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllSendablePawns))]
    internal static class Patch_AllSendablePawns_Column
    {
        private static void Postfix(Map map, int allowLoadAndEnterTransportersLordForGroupID, List<Pawn> __result)
        {
            if (allowLoadAndEnterTransportersLordForGroupID >= 0
                || !CaravanAcrossLevels.Active(map, out LevelComp comp))
            {
                return;
            }
            AppendFrom(comp.upperMap, __result);
            AppendFrom(comp.lowerMap, __result);
        }

        private static void AppendFrom(Map level, List<Pawn> list)
        {
            if (level == null || level.Disposed)
            {
                return;
            }
            List<Pawn> colonists = level.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (CaravanAcrossLevels.ListableForCaravan(p) && !list.Contains(p))
                {
                    list.Add(p);
                }
            }
        }
    }

    /// <summary>Off-surface picks cannot enter the surface forming lord
    /// directly (lords are map-scoped). Trim them in the prefix, let vanilla
    /// build the lord from the surface picks, then send each trimmed pawn down
    /// the stairs with a pending join replayed on arrival.</summary>
    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.StartFormingCaravan))]
    internal static class Patch_StartFormingCaravan_Column
    {
        private static readonly List<Pawn> trimmed = new List<Pawn>();

        private static bool Prefix(List<Pawn> pawns, List<Pawn> downedPawns)
        {
            trimmed.Clear();
            if (pawns == null || pawns.Count == 0)
            {
                return true;
            }
            Map ground = null;
            for (int i = 0; i < pawns.Count; i++)
            {
                Map m = pawns[i].MapHeld;
                if (m != null && m.Levels()?.level == 0)
                {
                    ground = m;
                    break;
                }
            }
            if (ground == null)
            {
                // Everyone picked lives off-surface: a caravan cannot form on a
                // pocket level (no map edges to exit). Vanilla-style reject.
                Messages.Message("AB_CaravanNeedsSurfacePawn".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            if (!CaravanAcrossLevels.Active(ground, out LevelComp _))
            {
                return true;
            }
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                if (pawns[i].MapHeld != ground)
                {
                    trimmed.Add(pawns[i]);
                    pawns.RemoveAt(i);
                }
            }
            // Defensive: off-surface downed pawns can never be gathered.
            downedPawns?.RemoveAll(p => p.MapHeld != ground);
            return true;
        }

        private static void Postfix(List<Pawn> pawns)
        {
            if (trimmed.Count == 0)
            {
                return;
            }
            try
            {
                Lord lord = pawns.Count > 0 ? pawns[0].GetLord() : null;
                if (lord == null || !(lord.LordJob is LordJob_FormAndSendCaravan))
                {
                    return;
                }
                Map ground = lord.Map;
                int sent = 0;
                for (int i = 0; i < trimmed.Count; i++)
                {
                    Pawn p = trimmed[i];
                    if (!CrossLevelWork.TryStairsJobToward(p, ground, out Job ride))
                    {
                        Messages.Message("AB_CaravanPawnNoStairs".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, historical: false);
                        continue;
                    }
                    Lord target = lord;
                    ABPendingOrders.Set(p, ground, delegate
                    {
                        // The gathering may have finished or died while riding.
                        if (target.ownedPawns != null && ground.lordManager.lords.Contains(target)
                            && p.Spawned && !p.Dead && !p.Downed && p.GetLord() == null)
                        {
                            target.AddPawn(p);
                        }
                    });
                    p.jobs?.StartJob(ride, JobCondition.InterruptForced);
                    sent++;
                }
                if (sent > 0)
                {
                    Messages.Message("AB_CaravanPawnsDescending".Translate(sent), MessageTypeDefOf.SilentInput, historical: false);
                }
            }
            finally
            {
                trimmed.Clear();
            }
        }
    }
}
