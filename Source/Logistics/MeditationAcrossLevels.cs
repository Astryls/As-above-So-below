using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Meditation across levels (user report 2026-07-23): when the meditation
    /// schedule hits, vanilla's MeditationUtility.AllMeditationSpotCandidates
    /// only enumerates the pawn's CURRENT map, so an assigned meditation spot
    /// or throne on another level is invisible and the pawn folds its legs
    /// wherever it happens to stand. Postfix JobGiver_Meditate.TryGiveJob:
    /// when the pawn is about to meditate WITHOUT an assigned spot on its own
    /// map, and its assigned spot (meditation spot building or throne) lives
    /// elsewhere in the column, route one hop toward it - on arrival the giver
    /// re-runs and vanilla's own scoring picks the assigned spot (+200 bias).
    /// An assigned spot on the pawn's own map stays vanilla's business
    /// (occupied or unreachable is not ours to second-guess), mirroring the
    /// rest redirect. Pawns with no assigned spot anywhere keep the vanilla
    /// meditate-in-place behavior.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_Meditate), "TryGiveJob")]
    internal static class Patch_Meditate_CrossLevel
    {
        private static readonly Dictionary<int, int> cooldown = new Dictionary<int, int>();

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (!ABGuard.On(ABGuard.Logistics))
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
                // Null result means CanMeditateNow said no - not our business.
                if (__result == null || !NeedsCross.EligibleColonist(pawn))
                {
                    return;
                }
                if (NeedsCross.OnCooldown(cooldown, pawn))
                {
                    return;
                }
                LevelComp comp = pawn.Map.Levels();
                if (comp == null || (comp.upperMap == null && comp.lowerMap == null))
                {
                    return;
                }
                if (AssignedSpotOn(pawn, pawn.Map) != null)
                {
                    // Assigned spot right here: whatever vanilla decided stands.
                    return;
                }
                Thing spot = FindAssignedSpotInColumn(pawn);
                if (spot == null)
                {
                    NeedsCross.Charge(cooldown, pawn);
                    return;
                }
                // One hop toward the spot's level; a direct link aims for the
                // stairwell nearest the spot itself. Two hops chain on arrival.
                Map nextMap = spot.Map.Level() > comp.level ? comp.upperMap : comp.lowerMap;
                if (nextMap == null)
                {
                    return;
                }
                IntVec3 dest = spot.Map == nextMap ? spot.Position : IntVec3.Invalid;
                if (CrossLevelWork.TryStairsJobToward(pawn, nextMap, dest, out Job job))
                {
                    __result = job;
                }
                else
                {
                    NeedsCross.Charge(cooldown, pawn);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level meditation");
            }
        }

        /// <summary>The pawn's assigned meditation spot or throne on the given
        /// map, or null. Assignment lives on the building, so meditation spots
        /// are scanned by def; thrones resolve through ownership directly.</summary>
        private static Thing AssignedSpotOn(Pawn pawn, Map map)
        {
            if (map == null || map.Disposed)
            {
                return null;
            }
            List<Building> spots = map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.MeditationSpot);
            for (int i = 0; i < spots.Count; i++)
            {
                if (spots[i].GetAssignedPawn() == pawn)
                {
                    return spots[i];
                }
            }
            Building_Throne throne = pawn.ownership?.AssignedThrone;
            if (throne != null && throne.Spawned && throne.Map == map)
            {
                return throne;
            }
            return null;
        }

        /// <summary>The pawn's assigned spot anywhere else in this column
        /// (ground plus its sky and basement), or null.</summary>
        private static Thing FindAssignedSpotInColumn(Pawn pawn)
        {
            Map ground = pawn.Map.GroundMap();
            if (ground == null)
            {
                return null;
            }
            LevelComp groundComp = ground.Levels();
            Map[] column = { ground, groundComp?.upperMap, groundComp?.lowerMap };
            for (int i = 0; i < column.Length; i++)
            {
                Map m = column[i];
                if (m == null || m.Disposed || m == pawn.Map)
                {
                    continue;
                }
                Thing spot = AssignedSpotOn(pawn, m);
                if (spot != null)
                {
                    return spot;
                }
            }
            return null;
        }
    }
}
