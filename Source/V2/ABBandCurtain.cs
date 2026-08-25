using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The band curtain: an opaque backdrop over everything outside the current band.
    ///
    /// WHY IT EXISTS. Patch_CameraDriver_ABClipViewToBand stops the other bands DRAWING,
    /// which is the correct half - but RimWorld's map camera does not clear to a background
    /// colour. It relies on the map mesh covering the screen, so any region where nothing is
    /// drawn is never written to and shows undefined GPU memory: flat blocks of arbitrary
    /// colour (observed as a full-screen red field with a blue rectangle in it). That is why
    /// the symptom looked like a shader or texture fault and was neither - `AB2: shader
    /// report` came back clean on every material this mod draws with, because nothing was
    /// drawing wrongly. Nothing was drawing at all.
    ///
    /// WHY IT USES THE ENGINE'S OWN MATERIAL. RimWorld already paints the area outside the
    /// map with MapEdgeClipDrawer, and the curtain sits directly against it, so any
    /// difference reads as a seam. Two attempts at hardcoding a colour both failed:
    ///
    ///   1. Color.black - visibly darker than the engine backdrop.
    ///   2. #1e1e1e in a plain solid-colour material - still wrong, because vanilla's clip
    ///      material is `new Color(0.1f, 0.1f, 0.1f)` as FLOATS. A float colour and a
    ///      Color32 are not interchangeable: the conversion depends on the project's colour
    ///      space, so 0.1f does not land on 25/255 on screen. Matching it by eye is chasing
    ///      a moving target.
    ///
    /// So the curtain now draws with `map.MapEdgeMaterial` - the exact material vanilla
    /// uses. It matches by construction in any colour space, and it inherits the variants
    /// for free: the Metal Hell material under Anomaly, a WorldObjectDef override, or a
    /// generator's custom clipper shader and texture. The MaterialPropertyBlock mirrors
    /// MapEdgeClipDrawer.DrawClippers, because a textured clipper tiles from
    /// MainTextureScale/MainTextureOffset and would sample wrongly without it.
    /// </summary>
    /// <remarks>
    /// [StaticConstructorOnStartup] is required, not decorative: RimWorld's startup
    /// reflection scan flags any type holding a static Unity asset field (here the
    /// MaterialPropertyBlock, same rule as Texture2D/Material) and warns "probably needs a
    /// StaticConstructorOnStartup attribute ... All assets must be loaded in the main
    /// thread". The attribute alone silences it and grants permission to run the static
    /// initialisers on the main thread during startup; no static constructor is needed.
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class ABBandCurtain
    {
        /// <summary>How far past the map the curtain extends. The camera can be zoomed out
        /// far enough to show space beyond the map bounds, and that space has exactly the
        /// same undefined-framebuffer problem. Matches vanilla's own 500-cell clip size
        /// doubled, so the curtain always outruns the view.</summary>
        private const float Overhang = 1000f;

        /// <summary>The same altitude vanilla's clippers use, so the curtain and the map-edge
        /// clip sort identically instead of one tinting the other. An earlier version sat at
        /// MetaOverlays with a forced render queue, which was solving the wrong problem:
        /// altitude is NOT draw order (queue decides, altitude only breaks ties within a
        /// queue), and once the material is vanilla's the whole question disappears.</summary>
        private static readonly float Altitude = AltitudeLayer.WorldClipper.AltitudeFor();

        private static readonly MaterialPropertyBlock PropertyBlock = new MaterialPropertyBlock();

        public static void Draw(Map map)
        {
            // Own switch, NOT the shared Rendering one (§46): any cosmetic layer tripping
            // Rendering used to kill the curtain with it, and a dead curtain means the
            // neighbouring band's straddling sections show through as clickable ghosts.
            if (map == null || !ABGuard.On(ABGuard.Curtain))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            Material mat = map.MapEdgeMaterial;
            if (mat == null)
            {
                return;
            }
            CellRect band = bands.RectOfBand(ABBandView.CurrentBand(map));

            float centreX = map.Size.x * 0.5f;
            float width = map.Size.x + Overhang * 2f;

            // Below the band: from far under the map up to the band's bottom edge.
            DrawQuad(mat, centreX, width, -Overhang, band.minZ);

            // Above the band. CellRect.maxZ is INCLUSIVE, so the band occupies world z up
            // to maxZ + 1 - starting the quad at maxZ would shave the band's top row.
            DrawQuad(mat, centreX, width, band.maxZ + 1, map.Size.z + Overhang);
        }

        private static void DrawQuad(Material mat, float centreX, float width,
            float bottom, float top)
        {
            float height = top - bottom;
            if (height <= 0f)
            {
                return;
            }
            Vector3 scale = new Vector3(width, 1f, height);
            Vector3 pos = new Vector3(centreX, 0f, (bottom + top) * 0.5f);

            // Mirrors MapEdgeClipDrawer: a textured clipper tiles from these, and without
            // them it would sample a single stretched texel across the whole quad.
            PropertyBlock.SetVector(ShaderPropertyIDs.MainTextureScale, scale);
            PropertyBlock.SetVector(ShaderPropertyIDs.MainTextureOffset, pos);

            Matrix4x4 matrix = default(Matrix4x4);
            matrix.SetTRS(pos.WithYOffset(Altitude), Quaternion.identity, scale);
            Graphics.DrawMesh(MeshPool.plane10, matrix, mat, 0, null, 0, PropertyBlock);
        }
    }

    /// <summary>Runs right after the map mesh, so the curtain lands over any region the
    /// section pass left untouched.
    ///
    /// The skyfaller relay rides along here because it is the mirror image of the curtain:
    /// the curtain paints the region outside the band, and the relay is the one thing that
    /// is deliberately allowed to draw OVER that paint. Issuing them from the same hook keeps
    /// the pair together. Draw order between the two does not matter - both go into the
    /// transparent queue and sort by altitude - but reading them side by side does.</summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh))]
    public static class Patch_MapDrawer_ABBandCurtain
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef =
            AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        private static void Postfix(MapDrawer __instance)
        {
            Map map = null;
            try
            {
                map = MapRef(__instance);
                ABBandCurtain.Draw(map);
            }
            catch (Exception e)
            {
                // Trip the curtain's OWN switch: the failure surfaces in the subsystem
                // health panel and as an in-game message instead of one buried log line -
                // a silently dead curtain is exactly the §46 ghosting report.
                ABGuard.Disable(ABGuard.Curtain, e, "V2 band curtain");
            }
            try
            {
                ABSkyfallerRelay.Draw(map);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: skyfaller relay threw: " + e, 331880418);
            }
        }
    }
}
