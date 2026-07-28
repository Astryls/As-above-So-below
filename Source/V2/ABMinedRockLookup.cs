using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mining leave-terrain -> rock def / tint lookup.
    ///
    /// RESCUED FROM V1 (was LevelSync). Nothing here is V1-specific: it is a pure def-table
    /// derived from ThingDef.building.leaveTerrain, and SectionLayer_ABMountainCap needs it
    /// to know that a rough-hewn floor cell in the sky band was once a particular rock, so
    /// the cap can print that rock's own linked atlas tile instead of a flat fill.
    ///
    /// Both maps are built together on first use and modded rocks participate automatically.
    /// </summary>
    internal static class ABMinedRockLookup
    {
        private static Dictionary<TerrainDef, Color> minedRockColors;

        private static Dictionary<TerrainDef, ThingDef> minedRockDefs;

        /// <summary>Maps a mining leave-terrain to the ROCK DEF whose mining produces it, so
        /// renderers can use the rock's own linked atlas graphic.</summary>
        internal static bool TryGetMinedRockDef(TerrainDef leaveTerrain, out ThingDef rockDef)
        {
            rockDef = null;
            if (leaveTerrain == null)
            {
                return false;
            }
            if (minedRockDefs == null)
            {
                TryGetMinedRockColor(leaveTerrain, out _); // builds both maps
            }
            return minedRockDefs != null && minedRockDefs.TryGetValue(leaveTerrain, out rockDef);
        }

        /// <summary>Maps every mining leave-terrain (rough-hewn stone etc.) to the tint of
        /// the rock whose mining produces it. Miss means the terrain is not a mined floor.</summary>
        internal static bool TryGetMinedRockColor(TerrainDef leaveTerrain, out Color color)
        {
            color = default(Color);
            if (leaveTerrain == null)
            {
                return false;
            }
            if (minedRockColors == null)
            {
                minedRockColors = new Dictionary<TerrainDef, Color>();
                minedRockDefs = new Dictionary<TerrainDef, ThingDef>();
                List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    ThingDef d = defs[i];
                    TerrainDef leave = d.building?.leaveTerrain;
                    if (leave == null || !d.mineable || minedRockColors.ContainsKey(leave))
                    {
                        continue;
                    }
                    Color c = d.graphicData != null ? d.graphicData.color : Color.white;
                    if (c == Color.white && d.stuffProps != null)
                    {
                        c = d.stuffProps.color;
                    }
                    minedRockColors[leave] = c;
                    minedRockDefs[leave] = d;
                }
            }
            return minedRockColors.TryGetValue(leaveTerrain, out color);
        }
    }
}
