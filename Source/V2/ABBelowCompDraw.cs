using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §95 TIER E - ANIMATED COMP OVERLAYS SEEN FROM ABOVE (the campfire's flame).
    ///
    /// THE DEFECT. The below realtime pass hands every thing a TRANSLATED draw location,
    /// and 1.6's new comp hook (`Comps_DrawAt(drawLoc, flip)`) forwards it - comps written
    /// against that hook work from above for free. But the LEGACY hook, `PostDraw()`, takes
    /// no location: CompFireOverlay reads `parent.DrawPos` and draws its Graphic_Flicker
    /// flame there - the source band, outside the camera's clamped view. Net effect: a
    /// campfire watched from the sky band showed its printed logs (mesh layer) and its
    /// smoke (fleck mirror, §95 Tier B) but no fire. Torches, braziers and darktorches -
    /// everything on CompFireOverlayBase - failed identically.
    ///
    /// THE SEAM (rule 38: own the draw - but at the FUNNEL, not per comp). Every fire-family
    /// overlay, and most legacy modded overlays, draw through
    ///     Graphic.Draw(Vector3 loc, Rot4 rot, Thing thing, float extraRotation)
    /// which is NON-VIRTUAL and delegates to DrawWorker - one patch covers Graphic_Flicker
    /// and every other subclass with no override bypass. Rejected alternatives: patching
    /// `Thing.DrawPos`'s getter (the hottest getter in the game, and V1's original sin -
    /// "no patching of any getter" is this file's neighbourhood pride); per-comp prefix
    /// reimplementation (rule 62: a table of finished answers rots, and windmill-class
    /// comps hold private spin state we should not re-derive).
    ///
    /// THE DISCRIMINATION. Inside the armed window BOTH kinds of call arrive: the thing's
    /// own graphic receives our ALREADY-TRANSLATED loc; a legacy comp's overlay arrives
    /// still at the RAW position. Bands sit >= a Slot (192+) apart in z, comp draw offsets
    /// are a few cells at most, so "within 8 cells of the raw z" separates the two with a
    /// 24x margin. A legacy comp drawing further than 8 cells from its parent stays
    /// untranslated (invisible from above) - accepted, documented, unobserved in vanilla.
    ///
    /// KNOWN GAP, deliberate: comps that bypass Graphic entirely and issue
    /// Graphics.DrawMesh with their own matrices - windmill blades, watermill wheels -
    /// are not in this seam. Their towers print; their moving parts stay invisible from
    /// above. Fixing them means per-comp matrix work; evidence-gated (§95.f).
    ///
    /// COST DISCIPLINE (§36e): outside the window the prefix is one static float read and
    /// a branch; the window exists only inside ABBelowDynamicDraw's realtime loop, never
    /// during vanilla's own pass, never during the pawn loop. No try/catch: float
    /// arithmetic on locals cannot throw, and this runs per realtime draw call.
    /// </summary>
    [HarmonyPatch(typeof(Graphic), nameof(Graphic.Draw))]
    public static class Patch_Graphic_ABBelowLegacyCompDraw
    {
        private const float Tolerance = 8f;

        private static void Prefix(ref Vector3 loc)
        {
            float drop = ABBelowDynamicDraw.RealtimeDropZ;
            if (drop == 0f)
            {
                return; // the permanent common case
            }
            if (Mathf.Abs(loc.z - ABBelowDynamicDraw.RealtimeRawZ) > Tolerance)
            {
                return; // already translated: the thing's own graphic with our loc
            }
            loc.z += drop;
        }
    }
}
