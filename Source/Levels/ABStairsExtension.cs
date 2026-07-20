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

        /// <summary>Whether this link bridges Dubs Bad Hygiene water networks
        /// touching its cells. Only the vertical water pipe sets this; stairs
        /// never carry resources.</summary>
        public bool bridgeWater;

        /// <summary>Whether this link bridges Vanilla Expanded Framework pipe
        /// networks (Vanilla Pipes Expanded, Helixien gas, Vanilla Temperature
        /// Expanded) touching its cells. Only the vertical duct sets this.</summary>
        public bool bridgeVef;

        /// <summary>Whether this link bridges Rimefeller oil and fuel pipeline
        /// networks. Only the vertical chem pipe sets this.</summary>
        public bool bridgeChem;
    }
}
