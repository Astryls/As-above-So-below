using Verse;

namespace AsAboveSoBelow
{
    public class ABStairsExtension : DefModExtension
    {
        /// <summary>+1 for stairs leading up, -1 for stairs leading down.</summary>
        public int deltaLevel;

        /// <summary>Def spawned as the linked stairwell on the destination level.</summary>
        public ThingDef counterpartDef;

        /// <summary>Climb time multiplier for this stairs type: ladders are slow
        /// (above 1), grand staircases fast (below 1). Scaled further by quality
        /// when the building has a quality comp, and by the settings slider.</summary>
        public float climbFactor = 1f;
    }
}
