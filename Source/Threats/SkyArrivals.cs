using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Benign arrivals on the sky level (parity pass 2026-07-24): visitor and
    /// traveler groups can walk in on a rooftop plateau when the column reads
    /// like a rooftop settlement. Diversion model, same doctrine as
    /// ThreatDivert: the storyteller fires the incident at the surface as
    /// always (pacing and points untouched); a share of eligible arrivals just
    /// retargets to the sky map INSIDE the worker - after the pocket-redirect
    /// chokepoint, which normalizes targets at TryExecute and must not bounce
    /// this one back.
    ///
    /// Eligibility is strict so the vanilla spot finders cannot whiff: the sky
    /// level must have a player building (a settlement to visit), a standable
    /// map-edge cell (the walk-in opening), and a usable stair pair to the
    /// surface (so the plateau is part of the colony, not scenery). Physical
    /// trade caravans stay on the surface by design (pack animals and plateau
    /// edges do not mix). Setting skyVisitorArrivals (default ON); kill
    /// switch: threats.
    /// </summary>
    internal static class SkyArrivals
    {
        private const float DivertChance = 0.5f;

        /// <summary>Retarget an arrival to the sky level when eligible. Called
        /// from worker prefixes (vanilla visitor/traveler + Hospitality's).</summary>
        internal static void TryDivert(IncidentParms parms)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Threats))
                {
                    return;
                }
                ABSettings settings = ABMod.Settings;
                if (settings == null || !settings.skyVisitorArrivals)
                {
                    return;
                }
                if (parms == null || !(parms.target is Map map) || map.Disposed || !map.IsPlayerHome)
                {
                    return;
                }
                LevelComp comp = map.Levels();
                if (comp == null || comp.level != 0)
                {
                    return;
                }
                Map sky = comp.upperMap;
                if (sky == null || sky.Disposed)
                {
                    return;
                }
                if (!Rand.Chance(DivertChance))
                {
                    return;
                }
                if (!SkyEligible(sky))
                {
                    return;
                }
                parms.target = sky;
                ABLog.Dev("Diverted a visitor/traveler arrival to the sky level.");
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "sky visitor divert");
            }
        }

        /// <summary>A rooftop settlement worth visiting: player buildings up
        /// there, a standable edge opening for the walk-in, and usable stairs
        /// linking the plateau to the surface.</summary>
        private static bool SkyEligible(Map sky)
        {
            if (sky.listerBuildings.allBuildingsColonist.Count == 0)
            {
                return false;
            }
            LevelComp skyComp = sky.Levels();
            List<Building_ABStairs> stairs = skyComp?.Stairs;
            bool anyStairs = false;
            if (stairs != null)
            {
                for (int i = 0; i < stairs.Count; i++)
                {
                    Building_ABStairs s = stairs[i];
                    if (s != null && s.Spawned && (s.Ext == null || !s.Ext.utilityOnly)
                        && s.Counterpart != null && !s.PassageForbiddenForColony(null))
                    {
                        anyStairs = true;
                        break;
                    }
                }
            }
            if (!anyStairs)
            {
                return false;
            }
            // A standable, unfogged, reachable-from-colony edge cell = the
            // opening a group can walk in through. Sampled, not exhaustive.
            CellRect edge = CellRect.WholeMap(sky);
            for (int i = 0; i < 40; i++)
            {
                IntVec3 c = edge.EdgeCells.RandomElement();
                if (c.InBounds(sky) && c.Standable(sky) && !c.Fogged(sky)
                    && sky.reachability.CanReachColony(c))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_VisitorGroup), "TryExecuteWorker")]
    internal static class Patch_VisitorGroup_SkyArrival
    {
        private static void Prefix(IncidentParms parms)
        {
            SkyArrivals.TryDivert(parms);
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_TravelerGroup), "TryExecuteWorker")]
    internal static class Patch_TravelerGroup_SkyArrival
    {
        private static void Prefix(IncidentParms parms)
        {
            SkyArrivals.TryDivert(parms);
        }
    }
}
