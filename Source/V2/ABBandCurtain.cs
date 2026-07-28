using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The black curtain: an opaque backdrop over everything outside the current band.
    ///
    /// WHY THIS IS NEEDED, and why clipping the view rect was only half a solution.
    /// Patch_CameraDriver_ABClipViewToBand stops the other bands DRAWING, which is the
    /// correct half - but RimWorld's map camera does not clear to a background colour. It
    /// relies on the map mesh covering the whole screen, so any region where nothing is
    /// drawn is simply never written to. Those pixels then show whatever was last left in
    /// the framebuffer: undefined GPU memory, which appears as flat blocks of arbitrary
    /// colour (observed as a full-screen red field with a blue rectangle in it).
    ///
    /// That is why the symptom looked like a shader or texture fault and was neither -
    /// `AB2: shader report` came back clean on every shader and material this mod draws
    /// with, because nothing was drawing wrongly. Nothing was drawing at all.
    ///
    /// The fix is to draw SOMETHING opaque there. Two quads - one below the band, one above
    /// it - guarantee every pixel outside the current level is defined and black, which is
    /// also exactly the intended look: pan off the edge of a level and you see empty space.
    ///
    /// Cost is two Graphics.DrawMesh calls per frame, unconditional on a banded map. That
    /// is cheaper than testing whether the camera currently overhangs (which would need the
    /// unclipped view rect back), and when the band clamp is active the quads are simply
    /// off-screen.
    /// </summary>
    public static class ABBandCurtain
    {
        /// <summary>How far past the map the curtain extends. The camera can be zoomed out
        /// far enough to show space beyond the map bounds, and that space has exactly the
        /// same undefined-framebuffer problem.</summary>
        private const float Overhang = 2000f;

        /// <summary>Drawn above map content so it reliably covers stale pixels, but below
        /// the UI layer. Nothing legitimate is ever inside these quads - they are strictly
        /// outside the current band - so a high altitude cannot hide real content.</summary>
        private static readonly float Altitude = AltitudeLayer.MetaOverlays.AltitudeFor();

        /// <summary>Render queue, and the reason the curtain needed one.
        ///
        /// ALTITUDE IS NOT DRAW ORDER. MetaOverlays is already the highest AltitudeLayer
        /// there is, yet the curtain still came out a different shade from RimWorld's own
        /// out-of-map backdrop, and a different shade again at night. Sorting between
        /// materials is decided by render QUEUE first; the y coordinate only breaks ties
        /// within a queue. The lighting overlay sits at 3100 and the darkness/fog overlays
        /// above that, so a curtain in the default transparent queue (~3000) was being
        /// painted over by every one of them - it was picking up the map's night tint while
        /// the engine backdrop, which is not part of the map, kept its flat colour.
        ///
        /// 3800 puts it after the lighting, darkness and fog overlays but still below UI,
        /// so #1e1e1e stays exactly #1e1e1e at any hour.</summary>
        private const int CurtainQueue = 3800;

        /// <summary>#1e1e1e — deliberately NOT pure black.
        ///
        /// RimWorld already paints the area outside the map bounds with its own dark grey
        /// backdrop. A pure-black curtain therefore reads as a distinct panel butted up
        /// against that backdrop, and the seam between them is clearly visible whenever
        /// both are on screen at once (which is most of the time when panning off a level).
        /// Matching the engine's colour makes "off the edge of this level" and "off the
        /// edge of the map" look like the same nothing, which is the intended read.</summary>
        private static readonly Color CurtainColor = new Color32(30, 30, 30, 255);

        private static Material curtainMat;

        private static Material Mat
        {
            get
            {
                if (curtainMat == null)
                {
                    // Our own instance, not a shared cached one - the render queue is
                    // overridden below and that must not leak into anything else using the
                    // same colour. Plain solid colour, NOT the vertex-colour variant: the
                    // quad's vertex colours are whatever MeshPool.plane10 ships with, and
                    // multiplying by them is exactly how a "fixed" colour stops being fixed.
                    curtainMat = SolidColorMaterials.NewSolidColorMaterial(
                        CurtainColor, ShaderDatabase.MetaOverlay);
                    curtainMat.renderQueue = CurtainQueue;
                }
                return curtainMat;
            }
        }

        public static void Draw(Map map)
        {
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            CellRect band = bands.RectOfBand(ABBandView.CurrentBand(map));

            float left = -Overhang;
            float width = map.Size.x + Overhang * 2f;
            float centreX = (left + (left + width)) * 0.5f;

            // Below the band: from far under the map up to the band's bottom edge.
            float lowTop = band.minZ;
            float lowBottom = -Overhang;
            DrawQuad(centreX, width, lowBottom, lowTop);

            // Above the band: from the band's top edge to far above the map.
            // CellRect.maxZ is INCLUSIVE, so the band occupies world z up to maxZ + 1.
            float highBottom = band.maxZ + 1;
            float highTop = map.Size.z + Overhang;
            DrawQuad(centreX, width, highBottom, highTop);
        }

        private static void DrawQuad(float centreX, float width, float bottom, float top)
        {
            float height = top - bottom;
            if (height <= 0f)
            {
                return;
            }
            Vector3 pos = new Vector3(centreX, Altitude, (bottom + top) * 0.5f);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.identity,
                new Vector3(width, 1f, height));
            Graphics.DrawMesh(MeshPool.plane10, matrix, Mat, 0);
        }
    }

    /// <summary>Runs right after the map mesh, so the curtain lands over any region the
    /// section pass left untouched.</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    public static class Patch_MapDrawer_ABBandCurtain
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef =
            AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        private static void Postfix(MapDrawer __instance)
        {
            try
            {
                ABBandCurtain.Draw(MapRef(__instance));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: band curtain draw threw: " + e, 762195881);
            }
        }
    }
}
