using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Better Architect Menu (ferny.BetterArchitect) soft compat: column-wide
    /// material availability (user request 2026-07-24, "show buildable
    /// material count").
    ///
    /// BAM gates material visibility on the CURRENT map's ListerThings in two
    /// private methods of its DesignationTabOnGUI patch class:
    ///  - PopulateMaterials: the Floors-tab material list only shows a
    ///    material when Find.CurrentMap has at least one of it - so wood
    ///    stored on the surface vanished from the list while viewing the sky
    ///    level even though cross-level hauling delivers it.
    ///  - GetStuffFrom: the sort-value stuff resolver, same gate. (Its def
    ///    SOURCE is resourceCounter.AllCountedAmounts, which our
    ///    ResourceCounter postfix already column-merges; only the
    ///    per-map presence gate needed widening.)
    /// Both are retargeted with the SAME instruction swap the vanilla stuff
    /// picker already uses (Patch_DesignatorBuild_ColumnStuff transpiler):
    /// ListerThings.ThingsOfDef -> ColumnThingsOfDef, which returns the local
    /// list when non-empty and otherwise a linked level's non-empty list.
    /// Local materials therefore always win unchanged; the column only fills
    /// gaps. The info-box cost readout (DrawPanelReadout) is already
    /// column-correct through the merged resource counter and needs nothing.
    ///
    /// Verified non-interactions (decompiled 2026-07-24):
    ///  - BAM's own Designator_Build.ProcessInput transpiler anchors on the
    ///    MadeFromStuff getter, ours on ThingsOfDef - different instructions,
    ///    compose in either order.
    ///  - BAM's floors tab bypasses the stuff float menu entirely
    ///    (shouldSkipFloatMenu) and assigns the selected material directly;
    ///    our picker transpiler simply never runs in that flow.
    ///
    /// Detection-gated at startup; a missing type or method logs one warning
    /// and leaves BAM fully vanilla (fail open). Zero cost when BAM is absent.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABBetterArchitectCompat
    {
        private const string PackageId = "ferny.BetterArchitect";

        internal static bool Active { get; private set; }

        static ABBetterArchitectCompat()
        {
            try
            {
                if (!ABDetect.Active(PackageId))
                {
                    return;
                }
                Type tabType = AccessTools.TypeByName(
                    "BetterArchitect.ArchitectCategoryTab_DesignationTabOnGUI_Patch");
                if (tabType == null)
                {
                    Log.Warning(ABLog.Tag + " Better Architect Menu detected but its menu"
                        + " class was not found; column materials will not show in its lists.");
                    return;
                }
                HarmonyMethod transpiler = new HarmonyMethod(typeof(ABBetterArchitectCompat),
                    nameof(ColumnAvailabilityTranspiler));
                int patched = 0;
                foreach (string name in new[] { "PopulateMaterials", "GetStuffFrom" })
                {
                    MethodInfo method = AccessTools.Method(tabType, name);
                    if (method == null)
                    {
                        Log.Warning(ABLog.Tag + " Better Architect Menu compat: method "
                            + name + " not found (mod updated?); that surface stays map-local.");
                        continue;
                    }
                    HarmonyBoot.Harmony.Patch(method, transpiler: transpiler);
                    patched++;
                }
                Active = patched > 0;
                if (Active)
                {
                    ABLog.Dev("Better Architect Menu compat: column-wide material"
                        + " availability wired into " + patched + " method(s).");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Better Architect Menu compat failed to initialize;"
                    + " its menu stays vanilla. " + e.Message);
            }
        }

        private static readonly MethodInfo ThingsOfDefMethod =
            AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsOfDef));

        private static readonly MethodInfo ReplacementMethod =
            AccessTools.Method(typeof(Patch_DesignatorBuild_ColumnStuff),
                nameof(Patch_DesignatorBuild_ColumnStuff.ColumnThingsOfDef));

        /// <summary>Same in-place callvirt-to-static swap as the vanilla stuff
        /// picker transpiler; ColumnThingsOfDef guards itself (current-map
        /// lister only, Logistics kill switch, local list wins when non-empty).</summary>
        private static IEnumerable<CodeInstruction> ColumnAvailabilityTranspiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(ThingsOfDefMethod))
                {
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = ReplacementMethod;
                    replaced++;
                }
            }
            if (replaced == 0)
            {
                Log.Warning(ABLog.Tag + " Better Architect Menu compat: no ThingsOfDef call in "
                    + (original?.Name ?? "?") + "; column materials will not show there.");
            }
            return list;
        }
    }
}
