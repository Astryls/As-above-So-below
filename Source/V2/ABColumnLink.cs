using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE ONE PLACE THAT ANSWERS "what is this carrier joined to".
    ///
    /// Every family's merge patch funnels through <see cref="AppendPartners"/>, because the
    /// three network systems ask the same underlying question in three different shapes:
    /// VEF walks a neighbour list, vanilla power floods over cardinal cells, and Dubwise
    /// flood-fills a cell dictionary. Only the ANSWER is shared, so only the answer lives
    /// here - each patch adapts it to its own caller.
    ///
    /// The riser era's junction/breaker split is GONE. A column spawns one identical
    /// invisible carrier per connected network in its own cell and in the cell one Slot up
    /// (§62, rule 27): toggling a network spawns or despawns the pair, which every host
    /// already handles natively through SpawnSetup/DeSpawn. So "is this end live" has
    /// exactly one meaning - the carrier is spawned - and there is no flick state to read.
    ///
    /// ⚠ THE PARTNER CELL IS EXACTLY ONE SLOT AWAY IN Z, NOT ONE BAND "UP". Bands are
    /// aligned 1:1 in x/z and stack by Slot (band height PLUS gutter), so the counterpart of
    /// (x, z) is (x, z ± Slot). Stepping by bandHeight instead skews the offset by a growing
    /// multiple of the gutter - the same arithmetic slip that has produced the single-step
    /// descent bug nine times elsewhere in this mod.
    /// </summary>
    public static class ABColumnLink
    {
        /// <summary>The two cells a carrier at <paramref name="cell"/> could pair with.
        /// Invalid where the band does not exist, the cell is off-map, or it lands in a
        /// gutter.</summary>
        public static bool TryPartnerCells(Map map, IntVec3 cell, out IntVec3 up, out IntVec3 down)
        {
            up = IntVec3.Invalid;
            down = IntVec3.Invalid;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int slot = bands.Slot;
            int band = bands.BandOf(cell);
            IntVec3 u = new IntVec3(cell.x, 0, cell.z + slot);
            IntVec3 d = new IntVec3(cell.x, 0, cell.z - slot);
            if (u.InBounds(map) && !bands.InGutter(u) && bands.BandOf(u) == band + 1)
            {
                up = u;
            }
            if (d.InBounds(map) && !bands.InGutter(d) && bands.BandOf(d) == band - 1)
            {
                down = d;
            }
            return up.IsValid || down.IsValid;
        }

        /// <summary>Append every carrier this one is cross-level joined to.
        ///
        /// Symmetric by construction: the same carrier def sits at both ends of a link, so
        /// whichever end a network rebuild starts from finds the other. That symmetry is
        /// what makes the merge model work without any patch having to know which side it
        /// is standing on.</summary>
        public static void AppendPartners(Thing t, List<Thing> into)
        {
            if (into == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                ABCarrierExt ext = t?.def?.GetModExtension<ABCarrierExt>();
                if (ext == null || t.Map == null || ext.network.NullOrEmpty())
                {
                    return;
                }
                if (!TryPartnerCells(t.Map, t.Position, out IntVec3 up, out IntVec3 down))
                {
                    return;
                }
                AddAt(t.Map, up, ext.network, into);
                AddAt(t.Map, down, ext.network, into);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "column link", t);
            }
        }

        private static void AddAt(Map map, IntVec3 cell, string network, List<Thing> into)
        {
            if (!cell.IsValid)
            {
                return;
            }
            List<Thing> here = cell.GetThingList(map);
            for (int i = 0; i < here.Count; i++)
            {
                Thing candidate = here[i];
                ABCarrierExt ext = candidate.def?.GetModExtension<ABCarrierExt>();
                if (ext == null || ext.network != network)
                {
                    continue;
                }
                if (!candidate.Spawned || into.Contains(candidate))
                {
                    continue;
                }
                into.Add(candidate);
            }
        }
    }
}
