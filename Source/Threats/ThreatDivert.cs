using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// T8 threat opt-ins. Pocket levels are raid-free by default because the
    /// storyteller never targets pocket maps. These patches divert a share of
    /// already-fired surface incidents into the column instead of making pocket
    /// maps first-class storyteller targets: pacing, points and cooldowns stay
    /// exactly vanilla, only the battlefield moves. Infestations burrow up into
    /// the basement; drop-pod raiders land on the sky level and then descend
    /// the stairs (HostileDescend). Both default OFF. Kill switch: threats.
    /// </summary>
    internal static class ThreatDivert
    {
        /// <summary>The basement to divert a surface infestation into, or null
        /// when the roll, the settings, or the map disqualify it.</summary>
        public static Map InfestationTarget(IncidentParms parms)
        {
            if (!ABGuard.On(ABGuard.Threats))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.threatBasementInfest)
            {
                return null;
            }
            if (!(parms.target is Map map) || map.Disposed || !map.IsPlayerHome)
            {
                return null;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level != 0)
            {
                return null;
            }
            Map basement = comp.lowerMap;
            if (basement == null || basement.Disposed)
            {
                return null;
            }
            if (!Rand.Chance(settings.threatDivertChance))
            {
                return null;
            }
            // The worker spawns tunnels with spawnAnywhereIfNoGoodCell false;
            // only divert when the basement genuinely supports an infestation.
            if (!InfestationCellFinder.TryFindCell(out IntVec3 _, basement))
            {
                return null;
            }
            return basement;
        }

        /// <summary>The sky level to divert a hostile drop-pod raid onto, or
        /// null. Called from inside the drop arrival workers, so the arrival
        /// mode is drop by construction.</summary>
        public static Map SkyDropTarget(IncidentParms parms)
        {
            if (!ABGuard.On(ABGuard.Threats))
            {
                return null;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.threatSkyDrops)
            {
                return null;
            }
            if (parms.faction == null || !parms.faction.HostileTo(Faction.OfPlayer))
            {
                return null;
            }
            if (!parms.questTag.NullOrEmpty() || parms.quest != null)
            {
                // Never hijack quest raids: their scripts hold map references.
                return null;
            }
            if (parms.spawnCenter.IsValid)
            {
                // Something upstream picked a spot deliberately; leave it be.
                return null;
            }
            if (parms.raidStrategy?.Worker is RaidStrategyWorker_Siege)
            {
                return null;
            }
            if (!(parms.target is Map map) || map.Disposed || !map.IsPlayerHome)
            {
                return null;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level != 0)
            {
                return null;
            }
            Map sky = comp.upperMap;
            if (sky == null || sky.Disposed)
            {
                return null;
            }
            if (!Rand.Chance(settings.threatDivertChance))
            {
                return null;
            }
            // The rooftop needs real standable landing room for the pods.
            if (!DropCellFinder.TryFindRaidDropCenterClose(out IntVec3 _, sky))
            {
                return null;
            }
            return sky;
        }
    }

    /// <summary>Divert surface infestations into the basement level. Prefix on
    /// the worker, so storyteller selection and points ran against the surface
    /// as usual; only the map the tunnels spawn on changes.</summary>
    [HarmonyPatch(typeof(IncidentWorker_Infestation), "TryExecuteWorker")]
    internal static class Patch_Infestation_Divert
    {
        private static void Prefix(IncidentParms parms)
        {
            try
            {
                Map basement = ThreatDivert.InfestationTarget(parms);
                if (basement != null)
                {
                    parms.target = basement;
                    ABLog.Dev("Diverted infestation to basement map " + basement.uniqueID + ".");
                }
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "infestation divert");
            }
        }
    }

    /// <summary>Divert hostile drop-pod raids onto the sky level. Hooked at
    /// TryResolveRaidSpawnCenter of the drop arrival workers: the raid's
    /// faction, strategy and arrival mode are fully resolved (against the
    /// surface) at this point, but no spawn cell is chosen and no pawn exists
    /// yet, so retargeting here moves the pods, the letter and the lord in one
    /// stroke.</summary>
    [HarmonyPatch]
    internal static class Patch_DropRaid_Divert
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (System.Type type in new[]
            {
                typeof(PawnsArrivalModeWorker_EdgeDrop),
                typeof(PawnsArrivalModeWorker_CenterDrop),
                typeof(PawnsArrivalModeWorker_RandomDrop),
                typeof(PawnsArrivalModeWorker_EdgeDropGroups)
            })
            {
                MethodBase m = AccessTools.DeclaredMethod(type, "TryResolveRaidSpawnCenter");
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static void Prefix(IncidentParms parms)
        {
            try
            {
                Map sky = ThreatDivert.SkyDropTarget(parms);
                if (sky != null)
                {
                    parms.target = sky;
                    ABLog.Dev("Diverted drop raid to sky map " + sky.uniqueID + ".");
                }
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "drop raid divert");
            }
        }
    }
}
