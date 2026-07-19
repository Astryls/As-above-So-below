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

        /// <summary>Utility-only links (vertical conduit, vertical water pipe)
        /// carry networks but never pawns: excluded from every pawn transport
        /// path and from stairwell heat exchange (a sealed shaft).</summary>
        public bool utilityOnly;

        /// <summary>Whether this link equalizes Dubs Bad Hygiene water networks
        /// touching its cells. On for stairs and the vertical water pipe.</summary>
        public bool bridgeWater = true;

        /// <summary>Whether this link equalizes Vanilla Expanded Framework pipe
        /// networks touching its cells. On for stairs; utility shafts are
        /// single-purpose.</summary>
        public bool bridgeVef = true;
    }
}
