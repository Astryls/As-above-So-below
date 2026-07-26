using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Gravship (Odyssey) placement gate for vertical links. Substructure is the
    /// gravship foundation terrain; a stacked column of z-levels cannot ride along
    /// when the ship launches (the level maps are separate pocket maps, not part
    /// of the substructure that flies), so stairs, ladders, elevators and utility
    /// risers are refused on any substructure cell. Inert without Odyssey:
    /// TerrainDef.IsSubstructure is false when the DLC is not active, so no
    /// explicit DLC check is needed here.
    /// </summary>
    public static class ABGravship
    {
        /// <summary>True when any cell of the given footprint sits on gravship
        /// substructure. Uses the foundation layer (FoundationAt), the same check
        /// vanilla's SubstructureGrid draws from.</summary>
        public static bool OnSubstructure(Map map, IntVec3 loc, Rot4 rot, IntVec2 size)
        {
            if (map == null)
            {
                return false;
            }
            TerrainGrid grid = map.terrainGrid;
            if (grid == null)
            {
                return false;
            }
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, size))
            {
                if (c.InBounds(map) && (grid.FoundationAt(c)?.IsSubstructure ?? false))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Footprint overload for an already-spawned building.</summary>
        public static bool OnSubstructure(Thing thing)
        {
            return thing != null && thing.Spawned && thing.def != null
                && OnSubstructure(thing.Map, thing.Position, thing.Rotation, thing.def.size);
        }
    }
}
