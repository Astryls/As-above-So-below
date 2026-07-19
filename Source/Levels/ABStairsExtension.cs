using Verse;

namespace AsAboveSoBelow
{
    public class ABStairsExtension : DefModExtension
    {
        /// <summary>+1 for stairs leading up, -1 for stairs leading down.</summary>
        public int deltaLevel;

        /// <summary>Def spawned as the linked stairwell on the destination level.</summary>
        public ThingDef counterpartDef;
    }
}
