using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A vertical utility shaft (conduit or pipe): full stairwell counterpart
    /// lifecycle - spawn the far end, collapse together, sever on map loss -
    /// but never a pawn passage. The def extension's utilityOnly flag excludes
    /// it from NearestUsableStairs (so no work migration, gizmo, or job ever
    /// targets it) and from climate exchange; this class only strips the
    /// pawn-facing float menu.
    /// </summary>
    public class Building_ABUtilityLink : Building_ABStairs
    {
        protected override List<FloatMenuOption> BuildUseOptions(Pawn selPawn)
        {
            // No go up / go down orders: nothing can climb a cable duct.
            return new List<FloatMenuOption>();
        }

        protected override string LinkLine()
        {
            if (Counterpart != null)
            {
                return (DeltaLevel > 0 ? "AB_LinkedAbove" : "AB_LinkedBelow").Translate();
            }
            return "AB_NotLinkedLine".Translate();
        }
    }
}
