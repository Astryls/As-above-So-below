using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Hover-info parity for the see-below view. The vanilla mouseover readout (the
    /// bottom-left terrain + things panel, the only hover-triggered thing info RimWorld
    /// draws) reads Find.CurrentMap only, so hovering a surface building/item through open
    /// air reported the SKY cell's contents. When the cursor sits over a see-through cell
    /// (open air, unroofed, unfogged) we point Find.CurrentMap at the lower map for the
    /// duration of the readout draw - the plumb transform means the same cursor cell
    /// resolves, so the whole vanilla panel reports the below thing/terrain with zero
    /// reimplementation. Read-only GUI draw, restored immediately. Gated on the live-below
    /// render toggle + the UI kill switch; fails open to the sky readout.
    /// </summary>
    [HarmonyPatch(typeof(MouseoverReadout), nameof(MouseoverReadout.MouseoverReadoutOnGUI))]
    internal static class Patch_MouseoverReadout_Below
    {
        // MouseoverReadoutOnGUI is a non-reentrant main-thread GUI call, so a single
        // static token safely carries the swap from prefix to postfix.
        private static ABCurrentMapSwap.Token savedToken;

        private static void Prefix(out bool __state)
        {
            __state = false;
            if (!ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                if (!BelowSelection.TryGetLiveBelowView(out Map sky, out Map lower))
                {
                    return;
                }
                IntVec3 c = UI.MouseCell();
                if (!BelowSelection.CellVisibleFromAbove(c, sky, lower))
                {
                    return;
                }
                if (ABCurrentMapSwap.Swap(lower, out ABCurrentMapSwap.Token token))
                {
                    savedToken = token;
                    __state = true;
                }
            }
            catch
            {
                // Best-effort hover info; never let it break the readout.
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                ABCurrentMapSwap.Restore(savedToken);
            }
        }
    }
}
