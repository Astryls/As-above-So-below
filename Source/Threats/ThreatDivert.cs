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
        /// <summary>Whether the column's basement can host an infestation for
        /// this surface target: settings on, basement alive, insects exist,
        /// hive cap not reached, and a valid infestation cell down there (the
        /// worker spawns with spawnAnywhereIfNoGoodCell false). No chance roll
        /// here - CanFireNow must be deterministic.</summary>
        public static bool BasementInfestationEligible(IncidentParms parms, out Map basement, out Map surface)
        {
            basement = null;
            surface = null;
            if (!ABGuard.On(ABGuard.Threats))
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.threatBasementInfest)
            {
                return false;
            }
            if (!(parms.target is Map map) || map.Disposed || !map.IsPlayerHome)
            {
                return false;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level != 0)
            {
                return false;
            }
            Map lower = comp.lowerMap;
            if (lower == null || lower.Disposed)
            {
                return false;
            }
            if (Faction.OfInsects == null || HiveUtility.TotalSpawnedHivesCount(lower) >= 30)
            {
                return false;
            }
            // Vanilla's cell finder scores against colony-distance curves tuned
            // for big mountain bases and rejects modest mined-out basements
            // wholesale (playtest round 13: the event never fired). Accept the
            // basement when EITHER finder is satisfied; execution prefers the
            // vanilla pick and falls back to ours via infestationLocOverride.
            if (!InfestationCellFinder.TryFindCell(out IntVec3 _, lower)
                && !TryFindBasementInfestationCell(lower, out IntVec3 _))
            {
                return false;
            }
            surface = map;
            basement = lower;
            return true;
        }

        /// <summary>Relaxed basement spawn cell: standable, unfogged, thick
        /// roofed, at least 6 cells from any colonist building, preferring the
        /// farthest sampled cell so hives rise deep in the tunnels. Sampled,
        /// not exhaustive: 220 tries bound the cost on huge maps.</summary>
        public static bool TryFindBasementInfestationCell(Map basement, out IntVec3 best)
        {
            best = IntVec3.Invalid;
            List<Building> colony = basement.listerBuildings.allBuildingsColonist;
            float bestScore = -1f;
            for (int i = 0; i < 220; i++)
            {
                IntVec3 c = CellFinder.RandomCell(basement);
                if (!c.Standable(basement) || c.Fogged(basement))
                {
                    continue;
                }
                RoofDef roof = basement.roofGrid.RoofAt(c);
                if (roof == null || !roof.isThickRoof)
                {
                    continue;
                }
                float minDist = float.MaxValue;
                for (int j = 0; j < colony.Count; j++)
                {
                    float d = (colony[j].Position - c).LengthHorizontalSquared;
                    if (d < minDist)
                    {
                        minDist = d;
                    }
                }
                if (colony.Count > 0 && minDist < 36f)
                {
                    // Not right on the stairwell or inside built-up rooms.
                    continue;
                }
                float score = colony.Count == 0 ? 1f : (minDist < 2500f ? minDist : 2500f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best.IsValid;
        }

        /// <summary>True when this incident is a pod-drop raid whose landing should be
        /// honored on the sky rather than bounced to the surface: either an explicit
        /// drop location the user/quest already pinned on a plateau, or a resolved
        /// pod-drop arrival mode. Walk-in raids, non-raid incidents, and anything with
        /// no drop signal return false and are left to redirect.</summary>
        internal static bool IsSkyPodDrop(IncidentParms parms, Map sky)
        {
            if (parms == null || sky == null)
            {
                return false;
            }
            if (parms.spawnCenter.IsValid && parms.spawnCenter.InBounds(sky)
                && sky.terrainGrid.TerrainAt(parms.spawnCenter) != ABDefOf.AB_OpenAir)
            {
                return true;
            }
            PawnsArrivalModeWorker w = parms.raidArrivalMode?.Worker;
            return w is PawnsArrivalModeWorker_EdgeDrop
                || w is PawnsArrivalModeWorker_CenterDrop
                || w is PawnsArrivalModeWorker_RandomDrop
                || w is PawnsArrivalModeWorker_EdgeDropGroups
                || w is PawnsArrivalModeWorker_SpecificLocationDrop;
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

    /// <summary>No storyteller event belongs on a pocket level directly: no
    /// trader ships passing over the basement, no cargo pods bursting on open
    /// air. Any incident that arrives targeting one of our levels (dev palette
    /// on the current map is the classic vector) is redirected to the column's
    /// surface at the single TryExecute chokepoint. Our own diverts retarget
    /// AFTER this point (inside the workers), so opt-in basement infestations
    /// and sky drops still land where they should. Deep drill infestations are
    /// exempt - they are local to the drill's map by nature.</summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    internal static class Patch_Incident_PocketRedirect
    {
        private static void Prefix(IncidentWorker __instance, IncidentParms parms)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Threats))
                {
                    return;
                }
                if (__instance is IncidentWorker_DeepDrillInfestation)
                {
                    return;
                }
                // T12 API: incidents can opt into pocket levels via XML.
                ABIncidentLevelPolicy policy = __instance.def?.GetModExtension<ABIncidentLevelPolicy>();
                if (policy != null && policy.allowOnPocketLevels)
                {
                    return;
                }
                if (parms == null || !(parms.target is Map map) || map.Disposed)
                {
                    return;
                }
                LevelComp comp = map.Levels();
                if (comp == null || comp.level == 0)
                {
                    return;
                }
                Map ground = comp.groundMap;
                if (ground == null || ground.Disposed || ground == map)
                {
                    return;
                }
                // A pod-drop raid aimed at the SKY is a supported landing now - it groups
                // on the plateaus via ABSkyDropCells. Don't bounce it to the surface: the
                // bounce keeps the sky spawn center, so the drop runs at nonsensical
                // surface coords (under the mountain) and scatters via vanilla's
                // random-walkable fallback (the "pods spread across the lower level"
                // report). Basement drops and every non-drop incident still redirect.
                if (comp.level >= 1 && ThreatDivert.IsSkyPodDrop(parms, map))
                {
                    ABLog.Dev("Allowed pod-drop raid " + (__instance.def?.defName ?? "unknown")
                        + " to land on the sky level instead of redirecting to the surface.");
                    return;
                }
                parms.target = ground;
                ABLog.Dev("Redirected incident " + (__instance.def?.defName ?? "unknown")
                    + " from pocket level " + comp.level + " to the column surface.");
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "pocket incident redirect");
            }
        }
    }

    /// <summary>Let infestations FIRE when only the basement qualifies. On a
    /// surface without overhead mountain (most colonies), vanilla CanFireNow
    /// rejects the incident outright and the basement opt-in would never see
    /// action; with the toggle on, a valid basement counts as infestation
    /// ground for the surface target.</summary>
    [HarmonyPatch(typeof(IncidentWorker_Infestation), "CanFireNowSub")]
    internal static class Patch_Infestation_CanFire
    {
        private static void Postfix(IncidentParms parms, ref bool __result)
        {
            try
            {
                if (!__result && ThreatDivert.BasementInfestationEligible(parms, out Map _, out Map _))
                {
                    __result = true;
                }
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "infestation can-fire");
            }
        }
    }

    /// <summary>Divert surface infestations into the basement level. Prefix on
    /// the worker, so storyteller selection and points ran against the surface
    /// as usual; only the map the tunnels spawn on changes. Forced (no roll)
    /// when the surface itself has no valid infestation cell - in that case
    /// the incident only fired because the basement qualified.</summary>
    [HarmonyPatch(typeof(IncidentWorker_Infestation), "TryExecuteWorker")]
    internal static class Patch_Infestation_Divert
    {
        private static void Prefix(IncidentParms parms)
        {
            try
            {
                if (!ThreatDivert.BasementInfestationEligible(parms, out Map basement, out Map surface))
                {
                    return;
                }
                bool surfaceValid = InfestationCellFinder.TryFindCell(out IntVec3 _, surface);
                if (!surfaceValid || Rand.Chance(ABMod.Settings.threatDivertChance))
                {
                    parms.target = basement;
                    // Prefer vanilla's own pick when it accepts the basement;
                    // otherwise seed the spawn from the relaxed finder. The
                    // override also flips SpawnTunnels into its lenient
                    // spawn-anywhere-near mode, so the spawn cannot fizzle.
                    if (!InfestationCellFinder.TryFindCell(out IntVec3 _, basement)
                        && ThreatDivert.TryFindBasementInfestationCell(basement, out IntVec3 seed))
                    {
                        parms.infestationLocOverride = seed;
                    }
                    ABLog.Dev("Diverted infestation to basement map " + basement.uniqueID
                        + (surfaceValid ? " (roll)" : " (surface has no valid cell)")
                        + (parms.infestationLocOverride.HasValue ? " with relaxed seed cell." : "."));
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
