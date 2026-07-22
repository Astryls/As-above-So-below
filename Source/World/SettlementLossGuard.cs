using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// T11 settlement-loss safety. Abandoning the surface with colonists still
    /// on other levels keeps vanilla loss semantics, but the player gets an
    /// explicit extra confirmation naming the column headcount first (vanilla's
    /// warning only counts surface pawns). The pocket maps themselves are
    /// destroyed with the ground map (see LevelComp.MapRemoved) so no orphaned
    /// level can keep ticking against a dead colony. Kill switch: world.
    /// </summary>
    [HarmonyPatch(typeof(SettlementAbandonUtility), nameof(SettlementAbandonUtility.TryAbandonViaInterface))]
    internal static class Patch_Abandon_ColumnWarning
    {
        private static bool bypass;

        private static bool Prefix(MapParent settlement)
        {
            if (bypass || !ABGuard.On(ABGuard.World))
            {
                return true;
            }
            Map map = settlement?.Map;
            LevelComp comp = map?.Levels();
            if (comp == null || comp.level != 0)
            {
                return true;
            }
            int stranded = CountColonists(comp.upperMap) + CountColonists(comp.lowerMap);
            if (stranded == 0)
            {
                return true;
            }
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "AB_AbandonLevelsWarning".Translate(stranded),
                delegate
                {
                    bypass = true;
                    try
                    {
                        SettlementAbandonUtility.TryAbandonViaInterface(settlement);
                    }
                    finally
                    {
                        bypass = false;
                    }
                }));
            return false;
        }

        private static int CountColonists(Map level)
        {
            if (level == null || level.Disposed)
            {
                return 0;
            }
            return level.mapPawns.FreeColonistsSpawnedCount;
        }
    }
}
