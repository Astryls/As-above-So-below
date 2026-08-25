using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// VANILLA POWER, MADE CROSS-LEVEL.
    ///
    /// `PowerNetMaker.ContiguousPowerBuildings` is the private flood fill that decides what
    /// belongs in one grid: a breadth-first walk over `GenAdj.CellsAdjacentCardinal`
    /// collecting `TransmitsPowerNow` buildings. Bands are a whole Slot apart, so it can
    /// never reach across one unaided.
    ///
    /// ⚠ THIS EXTENDS VANILLA'S ANSWER RATHER THAN REPLACING THE ALGORITHM, AND THE TRICK
    /// IS TO RE-USE THE ORIGINAL ON THE FAR SIDE. Rather than reimplementing the flood (and
    /// owning every future change to it), the postfix takes vanilla's component, finds any
    /// riser in it whose partner is not yet included, and asks THE SAME METHOD for the
    /// partner's component. Unioning those gives the merged grid, and repeating until no new
    /// partners appear gives the transitive closure - so a junction on level 0 feeding
    /// breakers on +1 and -1, with another junction on +1 feeding +2, all end up as one net.
    ///
    /// ⚠ AND THAT RE-ENTRY IS WHY THE GUARD IS `[ThreadStatic]` AND NOT OPTIONAL. Calling
    /// the method from its own postfix re-enters this postfix; without the latch that is
    /// unbounded recursion, and a StackOverflowException in .NET is UNCATCHABLE - no
    /// try/catch, no finalizer, no log line, the process simply vanishes and the player
    /// reports "the game closed when I built a conduit". The latch is the entire defence.
    ///
    /// Re-entry is otherwise safe: vanilla clears its three static HashSets on the way out,
    /// so by the time a postfix runs there is no in-progress state to clobber.
    /// </summary>
    [HarmonyPatch(typeof(PowerNetMaker), "ContiguousPowerBuildings")]
    public static class Patch_PowerNetMaker_ABRiserLink
    {
        [ThreadStatic]
        private static bool inMerge;

        private static int floods;
        private static int carriersSeen;
        private static int links;
        private static string skip = "(none)";

        /// <summary>Rule 15. Power had NO telemetry at all through the first column field
        /// test, which made "the lamp upstairs is dark" unfalsifiable from the log.</summary>
        public static string CounterReport()
        {
            return "    Power: floods=" + floods + " carriers=" + carriersSeen
                + " links=" + links + " | skip: " + skip;
        }

        private static readonly MethodInfo Contiguous =
            AccessTools.Method(typeof(PowerNetMaker), "ContiguousPowerBuildings");

        /// <summary>⚠ The parameter MUST be called `root` - Harmony binds postfix arguments
        /// to the original's by NAME, and a mismatch throws at PatchAll time and takes every
        /// other patch in this assembly down with it.</summary>
        private static void Postfix(Building root, ref IEnumerable<CompPower> __result)
        {
            if (inMerge || __result == null || Contiguous == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            Map map = root?.Map;
            if (map == null || !ABBands.Banded(map))
            {
                return;
            }
            try
            {
                floods++;
                List<CompPower> merged = __result.ToList();
                HashSet<Building> have = new HashSet<Building>();
                for (int i = 0; i < merged.Count; i++)
                {
                    if (merged[i]?.parent is Building b)
                    {
                        have.Add(b);
                    }
                }

                List<Thing> partners = new List<Thing>();
                Queue<Building> pending = new Queue<Building>();
                foreach (Building b in have)
                {
                    Enqueue(b, have, partners, pending);
                }
                if (pending.Count == 0)
                {
                    return; // no riser reaches out of this grid - the overwhelming case
                }

                inMerge = true;
                try
                {
                    while (pending.Count > 0)
                    {
                        Building far = pending.Dequeue();
                        if (!far.Spawned || far.Map != map)
                        {
                            continue;
                        }
                        // The SAME vanilla flood, run from the far side. Guarded, so this
                        // returns the plain spatial component with no further merging.
                        IEnumerable<CompPower> comp =
                            Contiguous.Invoke(null, new object[] { far }) as IEnumerable<CompPower>;
                        if (comp == null)
                        {
                            continue;
                        }
                        foreach (CompPower cp in comp)
                        {
                            if (!(cp?.parent is Building fb) || !have.Add(fb))
                            {
                                continue;
                            }
                            merged.Add(cp);
                            Enqueue(fb, have, partners, pending);
                        }
                    }
                }
                finally
                {
                    inMerge = false;
                }
                __result = merged;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "power riser merge", root);
            }
        }

        /// <summary>Queue any cross-level partner of <paramref name="b"/> we have not already
        /// pulled in. `have` is checked here as well as at insertion so a pair that is
        /// already wholly inside one grid costs nothing. Only generated carriers (§62) can
        /// have partners; everything else early-outs on a null mod extension.</summary>
        private static void Enqueue(Building b, HashSet<Building> have, List<Thing> partners,
            Queue<Building> pending)
        {
            if (b.def?.GetModExtension<ABCarrierExt>() == null)
            {
                return;
            }
            carriersSeen++;
            partners.Clear();
            ABColumnLink.AppendPartners(b, partners);
            if (partners.Count == 0)
            {
                // The carrier is in the grid but nothing answers one Slot away: either the
                // column never spawned its upper carrier, or the cell above is a gutter or
                // off the top band. The column report says which.
                skip = "carrier at " + b.Position + " has no cross-band partner";
                return;
            }
            for (int i = 0; i < partners.Count; i++)
            {
                if (partners[i] is Building pb && pb.TransmitsPowerNow && !have.Contains(pb))
                {
                    links++;
                    pending.Enqueue(pb);
                }
            }
        }
    }
}
