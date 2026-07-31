using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE DEPTH CUE: one definition of "how much further away is the level below".
    ///
    /// Each below OBJECT draws smaller, about its OWN centre, compounding once per level of
    /// drop. Baked into the printed vertices, so it costs nothing per frame and can never
    /// slide relative to the ground it stands on. This is V1's `belowThingScale`, restored
    /// and generalised: V1 only ever had ONE level below, so a single 0.85 sufficed; here
    /// the drop is the accumulated descent from ABBands.TryResolveVisibleBelow and the scale
    /// is raised to that power.
    ///
    /// ⚠ A CAMERA-ANCHORED PERSPECTIVE MODE WAS BUILT HERE AND REMOVED. It contracted the
    /// whole below view towards the camera centre (`p' = eye + (p - eye) / (1 + k)`) as a
    /// per-frame Matrix4x4 handed to Graphics.DrawMesh, mirroring vanilla's own
    /// MapDrawLayer_OrbitalDebris. It worked and it was cheap. It was cut on the user's call
    /// after the shrink was seen in play: the shrink alone already carried the depth read,
    /// and perspective bought a second cue at the price of a real artifact (no occlusion
    /// exists between a band's floor and the level below, so content slid a fraction of a
    /// cell out from under the near lip of its opening towards the screen edges).
    ///
    /// Do not re-add it as a polish item. If it returns it needs a NEW reason - something
    /// the per-object shrink provably cannot express - and it must be applied to ALL SIX
    /// mirrored below passes from one definition, because any two of them disagreeing on the
    /// factor makes the lighting shear off the terrain it shades. That coupling is the
    /// expensive part, and it is the part that would have to be rebuilt.
    ///
    /// Kept deliberately tint-free. V1 dimmed below content as well as shrinking it; V2 has
    /// SectionLayer_ABBelowLighting, which shades it with the surface's own glow, so a tint
    /// on top is exactly the double-darkening that made V1's sky view murky. Size is a
    /// distance cue that costs no brightness, which is why only it came back.
    /// </summary>
    internal static class ABDepthView
    {
        /// <summary>Shrink per level of drop, at the tightest the slider allows. Below this
        /// a two- or three-level drop stops reading as distance and starts reading as
        /// broken sprites.</summary>
        internal const float MinFalloff = 0.60f;

        internal const float MaxFalloff = 1f;

        internal const float DefaultFalloff = 0.85f;

        /// <summary>Per-object shrink for content <paramref name="levels"/> bands below the
        /// one being viewed. 1 (no shrink) when the feature is off, when the drop is zero,
        /// or when the slider sits at 100%.</summary>
        internal static float ScaleForLevels(int levels)
        {
            if (levels <= 0)
            {
                return 1f;
            }
            ABSettings s = ABMod.Settings;
            if (s == null || !s.depthFalloff)
            {
                return 1f;
            }
            float per = Mathf.Clamp(s.depthFalloffPerLevel, MinFalloff, MaxFalloff);
            if (per > 0.999f)
            {
                return 1f;
            }
            // Levels are capped at 3 up / 3 down, so a loop beats Mathf.Pow and stays exact.
            float scale = 1f;
            for (int i = 0; i < levels && i < 8; i++)
            {
                scale *= per;
            }
            return scale;
        }

        /// <summary>
        /// V1'S FILTER, KEPT VERBATIM, because both of its exclusions were paid for.
        ///
        /// LINKED graphics (walls, fences, conduits) print one quad per cell, so shrinking
        /// each cell about its own centre opens a gap at every cell boundary and a wall
        /// becomes a dotted line.
        ///
        /// Natural rock is excluded BY DEF rather than by link type - `mineable` or
        /// `building.isNaturalRock` - because Better Mountains swaps rock graphics to
        /// non-linked Graphic_Random wholesale, which passes the link test and then tore
        /// the surface mountains into a gappy field seen from the sky (V1 run #50).
        /// </summary>
        internal static bool CanShrink(Thing t)
        {
            ThingDef d = t?.def;
            if (d == null)
            {
                return false;
            }
            if (d.mineable || (d.building != null && d.building.isNaturalRock))
            {
                return false;
            }
            GraphicData g = d.graphicData;
            return g == null || g.linkType == LinkDrawerType.None;
        }
    }
}
