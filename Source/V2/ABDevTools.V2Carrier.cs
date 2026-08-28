using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §75.g PROBE. Why a carrier is visible when every static reading says it cannot be.
    ///
    /// The field report (run #346, DBH + Rimefeller + VNutrientE) is carrier art drawn at
    /// the cell below a column and the cell above it, in NORMAL view, on the level the
    /// player is standing on. Four facts contradict that and they cannot all hold at once:
    ///   1. `ShouldPrint` refuses every carrier unconditionally since §75.e.
    ///   2. The prefix carrying that verdict sits on `SectionLayer_ThingsGeneral`
    ///      .TakePrintFrom, which the 1.6 assembly really does declare, plus the
    ///      Dubs-family layers, whose names really are in the shipped assemblies.
    ///   3. Every host's pipe def is `MapMeshOnly` in its 1.6 defs, so a section layer is
    ///      the ONLY route its art can take to the screen.
    ///   4. Nothing in the column or riser code draws a carrier graphic by hand.
    /// One of the four is false in the RUNNING GAME, and reading cannot say which - so ask
    /// the game. Rule 37: name the enforcement point before naming the symptom.
    ///
    /// ⚠ THE PATCH CENSUS IS THE POINT. Everything else here is context. A patch that
    /// failed to bind at runtime looks exactly like a patch that bound and declined to fire,
    /// and only `Harmony.GetPatchInfo` can tell those apart. If a layer is missing from the
    /// census, fact 2 is the false one; if every layer is present and the thing still draws,
    /// fact 3 is (the def's drawer is not what its XML implied, for instance because the
    /// clone inherited from a template that is not the pipe).
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: carrier probe", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2CarrierProbe()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                Messages.Message("AB2: probe cell is out of bounds.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(ABLog.Tag).AppendLine(" V2 CARRIER PROBE");
            ABBandMap bands = ABBands.CompOf(map);
            sb.Append("CLICKED ").Append(c);
            if (bands != null && bands.Banded)
            {
                sb.Append("  band=").Append(bands.BandOf(c))
                    .Append("  viewBand=").Append(ABBandView.CurrentBand(map));
            }
            sb.AppendLine();

            sb.Append("  carrier def registry: ")
                .Append(ABColumnNetworks.CarrierDefCount).AppendLine(" def(s)");

            List<Thing> things = map.thingGrid.ThingsListAt(c);
            if (things == null || things.Count == 0)
            {
                sb.AppendLine("  NO THINGS AT THIS CELL (the art is not a thing here: look at"
                    + " a neighbouring cell's linked graphic, or a non-thing draw).");
            }
            else
            {
                sb.Append("  ").Append(things.Count).AppendLine(" thing(s):");
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t?.def == null)
                    {
                        continue;
                    }
                    bool isCarrier = ABColumnNetworks.IsCarrier(t.def);
                    bool hasExt = t.def.GetModExtension<ABCarrierExt>() != null;
                    bool wouldPrint =
                        Patch_SectionLayer_ABHideCarrierUnderColumn.ShouldPrint(t);
                    sb.Append("    ").Append(t.def.defName)
                        .Append("  class=").Append(t.def.thingClass?.Name ?? "null")
                        .Append("  drawer=").Append(t.def.drawerType)
                        .Append("  altitude=").Append(t.def.altitudeLayer)
                        .AppendLine();
                    sb.Append("      IsCarrier=").Append(isCarrier)
                        .Append("  hasCarrierExt=").Append(hasExt)
                        .Append("  ShouldPrint=").Append(wouldPrint)
                        .Append(wouldPrint && (isCarrier || hasExt)
                            ? "   <-- ⚠ CONTRADICTION" : "")
                        .AppendLine();
                    // A carrier that is NOT MapMeshOnly is drawn by the dynamic drawer every
                    // frame and never consults a section layer at all - which would make the
                    // print suppression irrelevant no matter how correctly it is installed.
                    if ((isCarrier || hasExt) && t.def.drawerType != DrawerType.MapMeshOnly)
                    {
                        sb.AppendLine("      ⚠ DYNAMIC DRAW: drawerType is not MapMeshOnly,"
                            + " so this thing bypasses TakePrintFrom entirely.");
                    }
                }
            }

            sb.AppendLine("  PATCH CENSUS - every patched TakePrintFrom and whether the"
                + " prefix is OURS:");
            int seen = 0;
            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
            {
                if (m == null || m.Name != "TakePrintFrom")
                {
                    continue;
                }
                seen++;
                bool ours = false;
                Patches info = Harmony.GetPatchInfo(m);
                if (info?.Prefixes != null)
                {
                    foreach (Patch p in info.Prefixes)
                    {
                        if (p.owner == HarmonyBoot.Harmony.Id)
                        {
                            ours = true;
                        }
                    }
                }
                sb.Append("    ").Append(m.DeclaringType?.FullName ?? "?")
                    .Append("  ourPrefix=").Append(ours).AppendLine();
            }
            if (seen == 0)
            {
                sb.AppendLine("    NONE. No TakePrintFrom anywhere is patched - the hide is"
                    + " not installed at all in this session.");
            }

            Log.Warning(sb.ToString());
            Messages.Message("AB2: carrier probe written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
