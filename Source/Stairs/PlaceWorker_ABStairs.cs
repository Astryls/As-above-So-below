using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Enforces the level cap and validates that stairwells linking back to an
    /// existing level will find a clear, supportive spot at the matching
    /// coordinates there. Newly generated levels (basement, sky) clear their own
    /// landing, so only links to existing maps need the check.
    /// </summary>
    public class PlaceWorker_ABStairs : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            ABStairsExtension ext = (checkingDef as ThingDef)?.GetModExtension<ABStairsExtension>();
            if (ext == null)
            {
                return true;
            }
            int target = map.Level() + ext.deltaLevel;
            if (target < -1 || target > 1)
            {
                return new AcceptanceReport("AB_LevelCap".Translate());
            }
            // Foreign special maps opted out of z-levels (e.g. Ancient urban
            // ruins exploration submaps). Building the stairs would generate a
            // level on a map that is not part of a column; refuse up front.
            if (AncientUrbanRuinsCompat.BlocksLevels(map))
            {
                return new AcceptanceReport("AB_NoLevelsUrbanRuins".Translate());
            }
            if (target == 0 && map.Level() != 0)
            {
                Map ground = map.GroundMap();
                if (ground == null || ground.Disposed)
                {
                    return new AcceptanceReport("AB_LevelCap".Translate());
                }
                // Whole footprint: the grand staircase is 2x2.
                foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, ((ThingDef)checkingDef).Size))
                {
                    if (!c.InBounds(ground))
                    {
                        return new AcceptanceReport("AB_BlockedOnTarget".Translate());
                    }
                    Building edifice = c.GetEdifice(ground);
                    if (edifice != null && edifice.def.passability == Traversability.Impassable)
                    {
                        return new AcceptanceReport("AB_BlockedOnTarget".Translate());
                    }
                    TerrainDef terrain = ground.terrainGrid.TerrainAt(c);
                    if (terrain?.affordances == null || !terrain.affordances.Contains(TerrainAffordanceDefOf.Medium))
                    {
                        return new AcceptanceReport("AB_NoSupportOnTarget".Translate());
                    }
                }
            }
            return true;
        }
    }
}
