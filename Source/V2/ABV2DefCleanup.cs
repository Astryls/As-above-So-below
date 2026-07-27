using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 supersedes V1's vertical links, so V1's stairwells must not stay in the
    /// architect menu next to the V2 ones.
    ///
    /// Run #4 caught this the hard way: both sets were buildable, V1's carry the finished
    /// MORTON art while V2's are still placeholder doors with near-identical labels, so
    /// the obvious thing to click was the V1 stairwell. It built, tried to generate a V1
    /// pocket level, and the V2 interlock correctly refused - producing
    /// "Stairs ... could not resolve a target level map" and no level.
    ///
    /// The interlock was right; leaving a dead button on screen was the bug.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABV2DefCleanup
    {
        /// <summary>Every V1 vertical-link building. Utility links (pipes/conduits) are
        /// included: they pair across V1 pocket maps and are equally dead under V2.</summary>
        private static readonly string[] V1LinkDefs =
        {
            "AB_StairsDown", "AB_StairsUp",
            "AB_GrandStairsDown", "AB_GrandStairsUp",
            "AB_WideStairsDown", "AB_WideStairsUp",
            "AB_LadderDown", "AB_LadderUp",
            "AB_Elevator"
        };

        static ABV2DefCleanup()
        {
            if (!ABV2.Enabled)
            {
                return;
            }
            try
            {
                HashSet<BuildableDef> removed = new HashSet<BuildableDef>();
                for (int i = 0; i < V1LinkDefs.Length; i++)
                {
                    ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(V1LinkDefs[i]);
                    if (d == null)
                    {
                        continue;
                    }
                    removed.Add(d);
                    d.designationCategory = null;
                }
                if (removed.Count == 0)
                {
                    return;
                }
                // DesignationCategoryDef.ResolveDesignators is private and already ran at
                // def-load, so prune the resolved list in place instead of re-resolving.
                int pruned = 0;
                List<DesignationCategoryDef> cats = DefDatabase<DesignationCategoryDef>.AllDefsListForReading;
                for (int i = 0; i < cats.Count; i++)
                {
                    pruned += cats[i].AllResolvedDesignators.RemoveAll(
                        des => des is Designator_Build b && b.PlacingDef != null && removed.Contains(b.PlacingDef));
                }
                ABLog.Dev("V2: removed " + pruned + " V1 vertical-link designators from the architect menu.");
            }
            catch (System.Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: could not prune V1 link designators: " + e.Message);
            }
        }
    }
}
