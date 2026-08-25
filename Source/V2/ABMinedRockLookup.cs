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
        private static Dictionary<TerrainDef, ThingDef> minedRockDefs;

        /// <summary>Maps a mining leave-terrain to the ROCK DEF whose mining produces it, so
        /// renderers can use the rock's own linked atlas graphic. The DEF map is safely
        /// static - nothing rewires leaveTerrain at runtime.</summary>
        internal static bool TryGetMinedRockDef(TerrainDef leaveTerrain, out ThingDef rockDef)
        {
            rockDef = null;
            if (leaveTerrain == null)
            {
                return false;
            }
            if (minedRockDefs == null)
            {
                minedRockDefs = new Dictionary<TerrainDef, ThingDef>();
                List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    ThingDef d = defs[i];
                    TerrainDef leave = d.building?.leaveTerrain;
                    if (leave == null || !d.mineable || minedRockDefs.ContainsKey(leave))
                    {
                        continue;
                    }
                    minedRockDefs[leave] = d;
                }
            }
            return minedRockDefs.TryGetValue(leaveTerrain, out rockDef);
        }

        /// <summary>The tint of the rock whose mining produces this leave-terrain. Read
        /// LIVE from the def's current graphicData, never baked: Better Mountains
        /// replaces rock graphicData wholesale (color included) at startup AND whenever
        /// its mod settings change, so a build-once color cache serves the OLD palette
        /// after a mid-game settings apply while the walls repaint to the new one.</summary>
        internal static bool TryGetMinedRockColor(TerrainDef leaveTerrain, out Color color)
        {
            color = default(Color);
            if (!TryGetMinedRockDef(leaveTerrain, out ThingDef rock))
            {
                return false;
            }
            Color c = rock.graphicData != null ? rock.graphicData.color : Color.white;
            if (c == Color.white && rock.stuffProps != null)
            {
                c = rock.stuffProps.color;
            }
            color = c;
            return true;
        }
    }
}
