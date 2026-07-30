using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// BAKED per-level camera bounds, and the in-game tool used to calibrate them.
    ///
    /// This replaces the old `freeCameraPan` setting. That setting was a binary between two
    /// unsatisfying extremes - either the view was pinned strictly inside the band, or it
    /// roamed with nothing but the view-rect clip stopping the neighbouring level showing -
    /// and it asked the player to make a judgement call about rendering internals. What the
    /// view should do at the edge of a level is an authored, per-level decision, so it is
    /// baked here and calibrated by eye in game.
    ///
    /// Keyed by LEVEL (band - surfaceBand: 0 surface, +n above, -n below), NOT by band
    /// index, deliberately: level is stable if the band count ever changes, so a table
    /// tuned today still means the same thing on a map with more slices.
    /// </summary>
    internal static class ABCameraBounds
    {
        internal struct Limits
        {
            /// <summary>Hard cap on `rootSize` (orthographic half-height in cells). Zero or
            /// less means "derive": half the band height, which is the tightest value that
            /// can still fill the screen with band.</summary>
            public float maxZoom;

            /// <summary>How many cells the VIEW may overhang the band edge. 0 keeps the
            /// whole viewport inside the level (the old clamped behaviour). Larger values
            /// let the level sit away from the screen edge; whatever shows past the band is
            /// clipped out by Patch_CameraDriver_ABClipViewToBand, so this is purely a
            /// framing choice and cannot leak the neighbouring level.</summary>
            public float panMargin;

            public Limits(float maxZoom, float panMargin)
            {
                this.maxZoom = maxZoom;
                this.panMargin = panMargin;
            }
        }

        // ================= THE BAKED TABLE =================
        // Values below reproduce the previous clamped behaviour exactly (derive zoom from
        // band height, no overhang). Run the calibration window in game - Debug Actions ->
        // "As above" -> "AB2: camera calibration" - pan and zoom to the framing that feels
        // right on each level, then report the numbers so they can be filled in here.
        //
        //   maxZoom   : paste "max seen" zoom from the readout
        //   panMargin : paste the overhang you actually want to allow
        // ==================================================
        internal static Limits For(int level)
        {
            switch (level)
            {
                case 1:  // sky
                    return new Limits(0f, 0f);
                case 0:  // surface
                    return new Limits(0f, 0f);
                case -1: // basement
                    return new Limits(0f, 0f);
                default: // any further level, once slice counts are configurable
                    return new Limits(0f, 0f);
            }
        }

        /// <summary>True while the calibration window is open: the band clamp stands down
        /// so the camera can be pushed past the current limits to find better ones. Without
        /// this the tool could only ever measure the limits already baked in.</summary>
        internal static bool CalibrationUnlocked;

        /// <summary>Shared with the clamp patch so there is one definition of how the
        /// camera's private state is reached.</summary>
        internal static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPos =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        internal static readonly AccessTools.FieldRef<CameraDriver, float> RootSize =
            AccessTools.FieldRefAccess<CameraDriver, float>("rootSize");

        internal static string DescribeLimits(Limits lim, float bandHeight)
        {
            string zoom = lim.maxZoom > 0f
                ? lim.maxZoom.ToString("0.0")
                : "derive(" + (bandHeight * 0.5f).ToString("0.0") + ")";
            return "maxZoom=" + zoom + "  panMargin=" + lim.panMargin.ToString("0.0");
        }
    }

    /// <summary>
    /// Camera calibration readout (dev tool - deliberately un-localised, like the rest of
    /// ABDevTools). Opens unlocked so the camera can roam past the baked limits, tracks the
    /// extremes reached, and hands back a paste-ready summary.
    /// </summary>
    internal class Dialog_ABCameraCalibration : Window
    {
        private float minZoomSeen = float.MaxValue;

        private float maxZoomSeen;

        private float maxOverhangSouth;

        private float maxOverhangNorth;

        public Dialog_ABCameraCalibration()
        {
            draggable = true;
            doCloseX = true;
            onlyOneOfTypeAllowed = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            preventCameraMotion = false; // the whole point is to pan and zoom while it is open
            absorbInputAroundWindow = false;
            resizeable = false;
            layer = WindowLayer.GameUI;
        }

        public override Vector2 InitialSize => new Vector2(430f, 350f);

        public override void PreOpen()
        {
            base.PreOpen();
            ABCameraBounds.CalibrationUnlocked = true;
        }

        public override void PostClose()
        {
            base.PostClose();
            ABCameraBounds.CalibrationUnlocked = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Map map = Find.CurrentMap;
            CameraDriver cam = Find.CameraDriver;
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            Text.Font = GameFont.Medium;
            list.Label("Camera calibration");
            Text.Font = GameFont.Small;

            if (map == null || cam == null
                || !ABBandView.TryBandBounds(map, out float minZ, out float maxZ))
            {
                list.Label("No banded map in view.");
                list.End();
                return;
            }

            ABBandMap bands = ABBands.CompOf(map);
            int band = ABBandView.CurrentBand(map);
            int level = ABBandView.CurrentLevel(map);
            float bandHeight = maxZ - minZ;
            float zoom = ABCameraBounds.RootSize(cam);
            float centreZ = ABCameraBounds.RootPos(cam).z;
            float viewLo = centreZ - zoom;
            float viewHi = centreZ + zoom;
            float overSouth = Mathf.Max(0f, minZ - viewLo);
            float overNorth = Mathf.Max(0f, viewHi - maxZ);

            minZoomSeen = Mathf.Min(minZoomSeen, zoom);
            maxZoomSeen = Mathf.Max(maxZoomSeen, zoom);
            maxOverhangSouth = Mathf.Max(maxOverhangSouth, overSouth);
            maxOverhangNorth = Mathf.Max(maxOverhangNorth, overNorth);

            Color old = GUI.color;
            GUI.color = new Color(0.55f, 0.9f, 0.6f);
            list.Label("Camera UNLOCKED while this window is open - pan and zoom freely.");
            GUI.color = old;
            list.Gap(4f);

            list.Label("level " + (level > 0 ? "+" + level : level.ToString())
                + "   band " + band + " of " + (bands != null ? bands.bandCount : 1));
            list.Label("band z: " + minZ.ToString("0") + ".." + maxZ.ToString("0")
                + "   height " + bandHeight.ToString("0"));
            list.GapLine(6f);

            list.Label("zoom now " + zoom.ToString("0.0")
                + "   min seen " + (minZoomSeen < float.MaxValue ? minZoomSeen.ToString("0.0") : "-")
                + "   max seen " + maxZoomSeen.ToString("0.0"));
            list.Label("view z " + viewLo.ToString("0.0") + ".." + viewHi.ToString("0.0")
                + "   (half-height = zoom)");
            list.Label("overhang now  S " + overSouth.ToString("0.0")
                + "   N " + overNorth.ToString("0.0"));
            list.Label("overhang max  S " + maxOverhangSouth.ToString("0.0")
                + "   N " + maxOverhangNorth.ToString("0.0"));
            list.GapLine(6f);

            list.Label("baked for this level: "
                + ABCameraBounds.DescribeLimits(ABCameraBounds.For(level), bandHeight));

            list.Gap(8f);
            if (list.ButtonText("Copy summary + log"))
            {
                string s = Summary(map, bands, band, level, minZ, maxZ, bandHeight, zoom);
                GUIUtility.systemCopyBuffer = s;
                Log.Message(s);
                Messages.Message("AB2: calibration summary copied to clipboard.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            if (list.ButtonText("Reset extremes"))
            {
                minZoomSeen = float.MaxValue;
                maxZoomSeen = 0f;
                maxOverhangSouth = 0f;
                maxOverhangNorth = 0f;
            }
            list.End();
        }

        private string Summary(Map map, ABBandMap bands, int band, int level, float minZ,
            float maxZ, float bandHeight, float zoom)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("AB2 camera calibration");
            sb.AppendLine("  level      : " + (level > 0 ? "+" + level : level.ToString())
                + "  (band " + band + " of " + (bands != null ? bands.bandCount : 1) + ")");
            sb.AppendLine("  map        : " + map.Size);
            sb.AppendLine("  band z     : " + minZ.ToString("0") + ".." + maxZ.ToString("0")
                + "  height " + bandHeight.ToString("0"));
            sb.AppendLine("  zoom now   : " + zoom.ToString("0.0"));
            sb.AppendLine("  zoom seen  : min "
                + (minZoomSeen < float.MaxValue ? minZoomSeen.ToString("0.0") : "-")
                + "  max " + maxZoomSeen.ToString("0.0"));
            sb.AppendLine("  overhang   : max S " + maxOverhangSouth.ToString("0.0")
                + "  max N " + maxOverhangNorth.ToString("0.0"));
            sb.AppendLine("  baked now  : "
                + ABCameraBounds.DescribeLimits(ABCameraBounds.For(level), bandHeight));
            sb.AppendLine("  -> bake as : maxZoom=" + maxZoomSeen.ToString("0.0")
                + "  panMargin=" + Mathf.Max(maxOverhangSouth, maxOverhangNorth).ToString("0.0"));
            return sb.ToString();
        }
    }
}
