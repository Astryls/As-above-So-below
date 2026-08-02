using System.Collections.Generic;
using System.Text;
using LudeonTK;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The riser diagnostic. Run it BEFORE theorising about a link that will not form.
    ///
    /// A failed bridge has at least five distinct causes that all look identical in game -
    /// "the two levels are not connected" - and only one of them is the merge itself:
    ///   1. no partner in the matching cell at all (wrong cell, wrong level, wrong Slot)
    ///   2. a partner is there but it is the wrong ROLE (two junctions, or two breakers)
    ///   3. a partner is there but its switch is off
    ///   4. the pair resolves but the two ends sit on the SAME net already (nothing to do)
    ///   5. the pair resolves, the nets differ, and the merge did not take
    ///
    /// This prints which one it is per riser, so a report becomes one line of fact instead
    /// of a round of guessing. Cases 1-3 are content or placement; only case 5 is a bug in
    /// the merge patches.
    /// </summary>
    public static class ABDevToolsRisers
    {
        [DebugAction("As above", "AB2: riser report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RiserReport()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            ABBandMap bands = ABBands.CompOf(map);
            sb.AppendLine(ABLog.Tag + " RISER REPORT");
            sb.AppendLine("  banded=" + (bands != null && bands.Banded)
                + "  slot=" + (bands != null ? bands.Slot : 0)
                + "  bands=" + ABBands.BandCount(map));

            List<ThingDef> defs = ABRiserDefs.All;
            sb.AppendLine("  riser defs loaded: " + defs.Count + " (30 with every host mod active)");

            int total = 0;
            List<Thing> partners = new List<Thing>();
            for (int i = 0; i < defs.Count; i++)
            {
                ABRiserExt ext = defs[i].GetModExtension<ABRiserExt>();
                List<Thing> built = map.listerThings.ThingsOfDef(defs[i]);
                if (built.Count == 0)
                {
                    continue;
                }
                for (int j = 0; j < built.Count; j++)
                {
                    total++;
                    Thing t = built[j];
                    sb.AppendLine("  " + defs[i].defName + " @ " + t.Position
                        + "  band=" + (bands != null ? bands.BandOf(t.Position) : 0)
                        + "  net=" + ext.network + "  role=" + ext.role
                        + "  live=" + ABRiserLink.EndIsLive(t));

                    if (!ABRiserLink.TryPartnerCells(map, t.Position, out IntVec3 up, out IntVec3 down))
                    {
                        sb.AppendLine("      no partner cell exists (edge of the stack)");
                        continue;
                    }
                    sb.AppendLine("      looking at " + (up.IsValid ? up.ToString() : "-")
                        + " and " + (down.IsValid ? down.ToString() : "-"));
                    Describe(map, up, ext, sb);
                    Describe(map, down, ext, sb);

                    partners.Clear();
                    ABRiserLink.AppendPartners(t, partners);
                    sb.AppendLine("      RESOLVED PARTNERS: " + partners.Count);
                }
            }
            if (total == 0)
            {
                sb.AppendLine("  nothing built. Place a junction and a breaker in the SAME cell "
                    + "one level apart, both inside a wall.");
            }
            Log.Warning(sb.ToString());
        }

        /// <summary>Say what is actually in the candidate cell, so "nothing there" and
        /// "something there but rejected" are never confused.</summary>
        private static void Describe(Map map, IntVec3 cell, ABRiserExt want, StringBuilder sb)
        {
            if (!cell.IsValid)
            {
                return;
            }
            List<Thing> here = cell.GetThingList(map);
            bool any = false;
            for (int i = 0; i < here.Count; i++)
            {
                ABRiserExt ext = here[i].def?.GetModExtension<ABRiserExt>();
                if (ext == null)
                {
                    continue;
                }
                any = true;
                string why = ext.network != want.network
                    ? "REJECTED - different network (" + ext.network + ")"
                    : ext.role == want.role
                        ? "REJECTED - same role, a junction needs a BREAKER opposite it"
                        : !ABRiserLink.EndIsLive(here[i])
                            ? "REJECTED - switched off"
                            : "ACCEPTED";
                sb.AppendLine("        " + cell + ": " + here[i].def.defName + " -> " + why);
            }
            if (!any)
            {
                sb.AppendLine("        " + cell + ": no riser here");
            }
        }
    }
}
