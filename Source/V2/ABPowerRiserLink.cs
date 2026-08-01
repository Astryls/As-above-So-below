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
    /// A power breaker that honours its switch.
    ///
    /// ⚠ `Building.TransmitsPowerNow` IS VIRTUAL AND THE BASE IGNORES FLICKING ENTIRELY -
    /// it is just `PowerComp?.Props.transmitsPower`. Adding CompFlickable to a transmitter
    /// therefore gives you a switch that visibly toggles and changes nothing about the grid.
    /// Vanilla solves this on `Building_PowerSwitch` by overriding the property, and the
    /// rebuild is driven by `PowerNetManager.Notfiy_TransmitterTransmitsPowerNowChanged`
    /// (vanilla's spelling, not ours) which deregisters and re-registers the transmitter.
    ///
    /// We subclass `Building_PowerSwitch` rather than reimplementing it so both halves come
    /// for free - but it also overrides `Graphic` to `flickableComp.CurrentGraphic`, and
    /// `CompFlickable.CurrentGraphic` returns `parent.DefaultGraphic` when the switch is on
    /// and an off-variant otherwise. With no `offGraphicData` on the def that resolves back
    /// to the normal graphic, so our Graphic_Multi rotations survive untouched.
    /// </summary>
    public class Building_ABPowerBreaker : Building_PowerSwitch
    {
    }

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
        /// already wholly inside one grid costs nothing.</summary>
        private static void Enqueue(Building b, HashSet<Building> have, List<Thing> partners,
            Queue<Building> pending)
        {
            if (!ABRiserDefs.IsRiser(b.def))
            {
                return;
            }
            partners.Clear();
            ABRiserLink.AppendPartners(b, partners);
            for (int i = 0; i < partners.Count; i++)
            {
                if (partners[i] is Building pb && pb.TransmitsPowerNow && !have.Contains(pb))
                {
                    pending.Enqueue(pb);
                }
            }
        }
    }
}
