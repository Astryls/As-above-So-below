using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Column-wide crafter check for the "no pawns can do this bill" warning
    /// (parity scaffold P0 #1, user report: "bills warn no one can do it if
    /// there's no pawns on that level"). Both vanilla call sites - ITab_Bills
    /// (workbench bills) and HealthCardUtility (surgery bills) - enumerate
    /// FreeColonists of the BENCH'S OWN MAP only before popping
    /// Bill.CreateNoPawnsWithSkillDialog, so a qualified cook one level away
    /// triggers the warning even though work migration will bring them to the
    /// bench. The bill itself is still added by both callers (the dialog is
    /// informational), so suppressing the dialog at its single sink is a
    /// complete fix for every caller, mods included.
    ///
    /// The bench's column is resolved through the viewed map: bills are only
    /// added through UI on a selected bench/patient, and selection lives in
    /// the viewed column (including below-view selection). When any free
    /// colonist anywhere in that column satisfies the recipe's skill
    /// requirements, the warning is false and stays hidden; when truly nobody
    /// in the column qualifies, vanilla shows it exactly as before.
    /// Kill switch: ui.
    /// </summary>
    [HarmonyPatch(typeof(Bill), nameof(Bill.CreateNoPawnsWithSkillDialog))]
    internal static class Patch_NoPawnsWithSkillDialog_Column
    {
        private static bool Prefix(RecipeDef recipe)
        {
            if (!ABGuard.On(ABGuard.Ui) || recipe == null)
            {
                return true;
            }
            try
            {
                LevelComp controller = Find.CurrentMap?.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return true;
                }
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    // The viewed map is deliberately re-checked: with below-view
                    // selection the vanilla caller may have tested a DIFFERENT
                    // level's pawn list than the one on screen.
                    List<Pawn> colonists = m.mapPawns.FreeColonists;
                    for (int i = 0; i < colonists.Count; i++)
                    {
                        if (recipe.PawnSatisfiesSkillRequirements(colonists[i]))
                        {
                            // A qualified crafter lives in this column: the
                            // warning is false, work migration covers the trip.
                            return false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "column skill-warning scope");
                return true;
            }
            return true;
        }
    }
}
