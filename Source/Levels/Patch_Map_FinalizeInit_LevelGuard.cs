using System;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Bug report 2026-07-25: a third-party postfix on Map.FinalizeInit that
    /// throws WHILE we are generating a level map used to abort our entire
    /// MapGenerator.GenerateMap call. LevelMapGen caught that and tripped the
    /// levelGen kill switch, and the stair that requested the level then logged
    /// "could not resolve a target level map". The reported culprit was
    /// Owlchemist's Perspective: Ores, whose ore-lump flood fill walks off the
    /// map edge (IndexOutOfRangeException in EdificeGrid) on our solid-rock
    /// basements.
    ///
    /// FinalizeInit is the LAST step of map generation, so by the time a postfix
    /// on it throws, the map itself is fully built and usable - only a cosmetic
    /// add-on failed. This finalizer runs ONLY inside our own level generation
    /// (LevelMapGen.CurrentContext != null) and ONLY for faults attributed to a
    /// third-party mod (ABBlame finds a non-vanilla, non-us frame on the stack).
    /// In that case it swallows the exception and logs one warning naming the
    /// culprit, so a foreign map-postprocessor can no longer abort our level or
    /// shut down levelGen. Our own and vanilla faults propagate unchanged - they
    /// still trip the switch and discard the half-built map, which is correct.
    ///
    /// Loading an existing save calls FinalizeInit with CurrentContext == null,
    /// so this never touches normal map loads.
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.FinalizeInit))]
    internal static class Patch_Map_FinalizeInit_LevelGuard
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception == null || LevelMapGen.CurrentContext == null)
            {
                return __exception;
            }
            string culprit = ABBlame.BlameMod(__exception);
            if (culprit == null)
            {
                // No third-party frame: our own or a vanilla fault. Let it
                // propagate so a genuinely broken level is discarded.
                return __exception;
            }
            Log.Warning(ABLog.Tag + " A level map finished generating but '" + culprit
                + "' threw while post-processing it (" + __exception.GetType().Name
                + "). Kept the level and ignored the add-on's failure instead of aborting level"
                + " generation. Details: " + __exception);
            return null;
        }
    }
}
