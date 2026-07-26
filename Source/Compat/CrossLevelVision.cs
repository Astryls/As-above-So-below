using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Cross-level fog-of-war VISION for CAI 5000 (option B support). A player
    /// pawn watching through the open-air gap contributes sight to the OTHER
    /// level's fog, so holes stay lit where pawns can actually see across them:
    /// a rooftop sentry reveals the surface it overlooks, and a colonist under
    /// an open gap reveals the sky above.
    ///
    /// Mechanism: CAI's own public, line-of-sight-correct reveal
    /// (MapComponent_FogGrid.RevealSpot, called via ABCombatAICompat) at the
    /// looker's plumb cell on the OTHER level. VISION only - we never feed CAI's
    /// threat-AI enemy flags across the gap (our own cross-level combat handles
    /// that). Inert unless CAI is loaded AND its Fog Of War is on; gated by
    /// ABGuard.CombatAI.
    ///
    /// ARCHITECTURAL LIMITATION (CAI, not us): CAI only advances a map's fog
    /// while that map is the one on screen; a non-current map's fog is frozen and
    /// our queued reveals for it are not processed until it is viewed again.
    /// So a reveal takes visible effect on whichever level is CURRENT: sky->
    /// surface reveals apply while viewing the surface, surface->sky while viewing
    /// the sky. Reveals for the off-screen level are queued (bounded + deduped by
    /// cell, so nothing leaks) and apply the moment that level is next viewed.
    /// </summary>
    internal static class CrossLevelVision
    {
        /// <summary>Re-issue cadence (ticks). Each reveal lasts RevealDuration,
        /// comfortably over 2x this, so a stationary looker's cone never flickers.</summary>
        internal const int ScanInterval = 30;

        private const int RevealDuration = 75;

        // Day/night sight radius for a plunging / looking-up viewpoint (cells),
        // lerped by the target level's daylight so night watch sees less.
        private const float DayRadius = 24f;
        private const float NightRadius = 13f;

        /// <summary>Drive one cross-level vision pass for a sky/surface pair.
        /// Called from the sky comp on the ScanInterval throttle. Cheap no-op
        /// unless CAI Fog Of War is on and one of the two levels is on screen.</summary>
        internal static void ScanPair(Map sky, Map surface)
        {
            if (sky == null || surface == null || sky.Disposed || surface.Disposed
                || !ABCombatAICompat.FogEnabled)
            {
                return;
            }
            // A reveal only takes effect on the CURRENT map; if neither level is
            // viewed, nothing would process, so skip the work entirely.
            Map current = Find.CurrentMap;
            if (current != sky && current != surface)
            {
                return;
            }

            TerrainGrid skyTerrain = sky.terrainGrid;
            RoofGrid surfaceRoofs = surface.roofGrid;
            TerrainDef air = ABDefOf.AB_OpenAir;

            float radiusDown = Mathf.Lerp(NightRadius, DayRadius, surface.skyManager.CurSkyGlow);
            float radiusUp = Mathf.Lerp(NightRadius, DayRadius, sky.skyManager.CurSkyGlow);

            // Sky -> surface: a player pawn at a hole rim lights the surface below.
            List<Pawn> skyPawns = sky.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < skyPawns.Count; i++)
            {
                Pawn p = skyPawns[i];
                if (!IsSeer(p))
                {
                    continue;
                }
                IntVec3 c = p.Position;
                if (c.InBounds(surface) && NearOpenAir(c, sky, skyTerrain, air, surface, surfaceRoofs))
                {
                    ABCombatAICompat.RevealOnMap(surface, c, radiusDown, RevealDuration);
                }
            }

            // Surface -> sky: a player pawn under an open gap lights the sky above.
            List<Pawn> surfacePawns = surface.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
            for (int i = 0; i < surfacePawns.Count; i++)
            {
                Pawn p = surfacePawns[i];
                if (!IsSeer(p))
                {
                    continue;
                }
                IntVec3 c = p.Position;
                if (c.InBounds(sky) && skyTerrain.TerrainAt(c) == air && !surfaceRoofs.Roofed(c))
                {
                    ABCombatAICompat.RevealOnMap(sky, c, radiusUp, RevealDuration);
                }
            }
        }

        /// <summary>Any spawned, conscious player pawn contributes sight
        /// (colonists, slaves, animals, mechs) - matching "your pawns light what
        /// they can see". Downed/dead pawns see nothing.</summary>
        private static bool IsSeer(Pawn p)
        {
            return p != null && p.Spawned && !p.Dead && !p.Downed;
        }

        /// <summary>True when the pawn's own cell or a neighbor is open air on the
        /// sky with the surface below it unroofed - i.e. the pawn is at the rim of
        /// a hole it can watch down. This keeps reveals to pawns who genuinely
        /// overlook the surface, not ones deep on a solid rooftop.</summary>
        private static bool NearOpenAir(IntVec3 c, Map sky, TerrainGrid skyTerrain,
            TerrainDef air, Map surface, RoofGrid surfaceRoofs)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    IntVec3 q = new IntVec3(c.x + dx, 0, c.z + dz);
                    if (q.InBounds(sky) && q.InBounds(surface)
                        && skyTerrain.TerrainAt(q) == air && !surfaceRoofs.Roofed(q))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
