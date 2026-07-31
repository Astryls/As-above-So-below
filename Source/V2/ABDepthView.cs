using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE DEPTH MODEL: one definition of "how much further away is the level below".
    ///
    /// Two independent cues, deliberately kept as two, because they fail in opposite ways
    /// and only one of them is safe to bake:
    ///
    ///   1. DEPTH FALLOFF (default ON). Each below OBJECT draws smaller, about its OWN
    ///      centre, compounding once per level of drop. Baked into the printed vertices,
    ///      so it costs nothing per frame and never slides relative to the ground it
    ///      stands on. This is V1's `belowThingScale`, restored and generalised: V1 only
    ///      ever had ONE level below, so a single 0.85 sufficed; here the drop is the
    ///      accumulated descent from ABBands.TryResolveVisibleBelow and the scale is
    ///      raised to that power.
    ///
    ///   2. PERSPECTIVE MODE (default OFF). The whole below view contracts TOWARDS THE
    ///      CAMERA CENTRE, which is what an actual pinhole camera does to anything below
    ///      the focal plane: dead centre of the screen you look straight down a shaft and
    ///      see its floor unmoved, and the further off-centre a shaft is the more of its
    ///      far wall you see. Applied per frame as a Matrix4x4 handed to Graphics.DrawMesh,
    ///      NOT baked - vanilla does exactly this in MapDrawLayer_OrbitalDebris.DrawLayer,
    ///      which is the proof the technique costs nothing (no mesh is touched, only the
    ///      transform the GPU is given).
    ///
    /// ⚠ WHY PERSPECTIVE IS DEPTH-UNIFORM AND FALLOFF IS NOT.
    /// A section's below mesh is a MIX of drops - neighbouring columns can descend one
    /// level and three - and one Graphics.DrawMesh call gets exactly one matrix. Worse,
    /// SIX separate layers mirror the below view (terrain+things, lighting, shadows,
    /// watergen, snow, and the dynamic pawn pass), and if any two of them used a different
    /// factor the lighting would slide off the terrain it shades. So perspective uses ONE
    /// factor for all below content at any depth, and the per-level part of the cue is
    /// carried entirely by the falloff, which is per-object and has no such coupling.
    /// That division is why the two features compose instead of fighting.
    ///
    /// ⚠ KNOWN ARTIFACT of perspective mode, and why it ships off by default and capped.
    /// We have no occlusion between the viewing band's floor and the level below it, so
    /// content sliding towards screen centre slides a fraction of a cell OUT from under
    /// the near lip of its opening and over the solid floor beside it. The opposite edge
    /// exposes the air mask, which happens to read correctly as the shaft's far wall. The
    /// displacement is zero at screen centre and grows linearly outwards; because it is a
    /// fraction of the camera's half-height it is roughly CONSTANT IN PIXELS at every
    /// zoom, so the cap is expressed that way: at maximum strength the screen edge moves
    /// about 4% of the viewport, and the default is well under that.
    /// </summary>
    internal static class ABDepthView
    {
        /// <summary>Shrink per level of drop, at the tightest the slider allows. Below this
        /// a two- or three-level drop stops reading as distance and starts reading as
        /// broken sprites.</summary>
        internal const float MinFalloff = 0.60f;

        internal const float MaxFalloff = 1f;

        internal const float DefaultFalloff = 0.85f;

        /// <summary>Contraction per unit of camera half-height at strength 1.0. The visible
        /// displacement at the top/bottom screen edge is k/(1+k) of the viewport half-height,
        /// so 0.04 is about 4% of the half-screen - already more than "very slight", which
        /// is why it is the CAP and not the default.</summary>
        internal const float MaxPerspectiveK = 0.040f;

        internal const float DefaultPerspectiveStrength = 0.35f;

        // ---- per-frame state -------------------------------------------------

        private static int frame = -1;

        private static bool perspectiveOn;

        private static Matrix4x4 matrix = Matrix4x4.identity;

        private static float factor = 1f;

        private static Vector3 eye;

        /// <summary>Refreshes the per-frame transform. Cheap enough to call from every
        /// consumer: one int compare after the first call each frame.
        ///
        /// Reads the camera through ABCameraBounds.RootPos, a plain FIELD ref, never the
        /// Unity transform - section regeneration can run inside a long event and a Unity
        /// transform read there throws (the same hazard ABBandMap.FinalizeInit documents).
        /// </summary>
        private static void Refresh()
        {
            int f = Time.frameCount;
            if (f == frame)
            {
                return;
            }
            frame = f;
            perspectiveOn = false;
            matrix = Matrix4x4.identity;
            factor = 1f;
            try
            {
                ABSettings s = ABMod.Settings;
                if (s == null || !s.perspectiveMode || s.perspectiveStrength <= 0.001f)
                {
                    return;
                }
                CameraDriver cam = Find.CameraDriver;
                if (cam == null || Current.ProgramState != ProgramState.Playing)
                {
                    return;
                }
                float k = Mathf.Clamp01(s.perspectiveStrength) * MaxPerspectiveK;
                if (k <= 0.0001f)
                {
                    return;
                }
                perspectiveOn = true;
                factor = 1f / (1f + k);
                Vector3 root = ABCameraBounds.RootPos(cam);
                eye = new Vector3(root.x, 0f, root.z);
                // Contract about the eye in x/z only. Altitude is untouched: it is this
                // mod's draw ORDER, not a height, and scaling it would reshuffle every
                // render-queue relationship the below view depends on.
                matrix = Matrix4x4.Translate(eye)
                    * Matrix4x4.Scale(new Vector3(factor, 1f, factor))
                    * Matrix4x4.Translate(-eye);
            }
            catch
            {
                perspectiveOn = false;
                matrix = Matrix4x4.identity;
                factor = 1f;
            }
        }

        /// <summary>True when the perspective transform is doing anything this frame.</summary>
        internal static bool PerspectiveActive
        {
            get
            {
                Refresh();
                return perspectiveOn;
            }
        }

        /// <summary>The transform to hand Graphics.DrawMesh for below geometry. Identity
        /// when perspective mode is off, so callers never need to branch.</summary>
        internal static Matrix4x4 Matrix
        {
            get
            {
                Refresh();
                return matrix;
            }
        }

        /// <summary>Point form of <see cref="Matrix"/>, for the per-frame passes that place
        /// pawns and realtime things by position rather than by mesh.</summary>
        internal static Vector3 Apply(Vector3 p)
        {
            Refresh();
            if (!perspectiveOn)
            {
                return p;
            }
            return new Vector3(eye.x + (p.x - eye.x) * factor, p.y,
                eye.z + (p.z - eye.z) * factor);
        }

        /// <summary>Inverse of <see cref="Apply"/>: maps a point on screen back to the below
        /// world position that renders there. Every hit-test against below content funnels
        /// through here so click accuracy tracks the visuals instead of drifting from
        /// them.</summary>
        internal static Vector3 Unapply(Vector3 p)
        {
            Refresh();
            if (!perspectiveOn || factor <= 0.0001f)
            {
                return p;
            }
            float inv = 1f / factor;
            return new Vector3(eye.x + (p.x - eye.x) * inv, p.y,
                eye.z + (p.z - eye.z) * inv);
        }

        // ---- depth falloff ---------------------------------------------------

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

        // ---- draw helper -----------------------------------------------------

        /// <summary>
        /// Draws a below layer's submeshes through the perspective transform.
        ///
        /// <paramref name="pinnedA"/> and <paramref name="pinnedB"/> name materials that
        /// must NOT move: the air mask and the fog fan define where the openings ARE, and
        /// an opening that slides is not an opening, it is a hole in the floor next to the
        /// hole in the floor. Everything the opening shows moves; the opening itself does
        /// not.
        /// </summary>
        internal static void DrawSubMeshes(List<LayerSubMesh> subs,
            Material pinnedA = null, Material pinnedB = null)
        {
            if (subs == null)
            {
                return;
            }
            Matrix4x4 m = Matrix;
            bool moving = perspectiveOn;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sub = subs[i];
                if (sub == null || !sub.finalized || sub.disabled || sub.mesh == null)
                {
                    continue;
                }
                Material mat = sub.material;
                Matrix4x4 use = moving && mat != pinnedA && mat != pinnedB
                    ? m
                    : Matrix4x4.identity;
                Graphics.DrawMesh(sub.mesh, use, mat, sub.renderLayer);
            }
        }
    }
}
