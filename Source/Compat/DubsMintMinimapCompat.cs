using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Dubs Mint Minimap: show only the ACTIVE LEVEL instead of the whole
    /// stack squeezed into one panel.
    ///
    /// Detection and every call are reflection-only - no compile-time reference to their
    /// assembly, and no Dubs type appears in any signature in this file. That second part is
    /// load-bearing: HarmonyBoot's class processor walks a patch class's method list, which
    /// makes Mono resolve parameter types, so a foreign type in a signature throws
    /// "Could not resolve type" and gets logged as a broken patch class whenever the mod is
    /// absent. That was the Rimefeller ghost-warning bug. Hence <c>object</c> for the
    /// instance and Unity/Verse types for everything else, and hence this bridge class
    /// carrying no patch attribute at all.
    ///
    /// WHAT WAS WRONG. Everything in their window is driven by two things: one static
    /// <c>MapTexture</c> sized to the whole map, and a coordinate mapping that is always
    ///     <c>GenMath.LerpDoubleClamped(0, map.Size.z, rect.height, 0, pos.z)</c>
    /// repeated across six draw passes (pawns, fires, projectiles, pings, thing locators,
    /// camera box). On a banded map <c>map.Size.z</c> is the whole stack, so the minimap
    /// showed all seven levels at once, at one seventh scale, with every level's pawns
    /// overlaid on every other's.
    ///
    /// THE FIX IS ONE RECT, NOT SEVEN PATCHES. Their draw passes all take the same
    /// <c>radarRect</c> and are all LINEAR in position, so choosing that rect is enough to
    /// re-aim the entire window. Solving
    ///     <c>rect.y + rect.height * (1 - z/Size.z) == view.height * (1 - (z-bandMin)/H)</c>
    /// for the two unknowns gives
    ///     <c>height = view.height * Size.z / H</c>,
    ///     <c>y      = view.height * (H + bandMin - Size.z) / H</c>
    /// - an over-tall rect, slid so that exactly the active band lands in the visible window.
    /// Their own unmodified passes then draw every marker in the right place.
    ///
    /// AND THE CULLING COMES FREE. Anything on another band maps outside the window, and
    /// their draw already happens inside <c>GUI.BeginClip</c>, so other levels' pawns are
    /// clipped away by the existing clip rather than by a filter we would have to write into
    /// six separate passes.
    ///
    /// The one thing that cannot be reused is their texture blit:
    /// <c>Widgets.DrawTextureFitted(rect, tex, 1f, 1f)</c> binds to the
    /// <c>(Rect, Texture, float scale, float alpha)</c> overload, which fits the texture
    /// PRESERVING ITS OWN ASPECT RATIO. That is fine for a square vanilla map, where the
    /// fitted rect coincides with the marker rect; on a 190x768 stack it letterboxes the map
    /// into a narrow vertical sliver and the two stop agreeing. So the texture is drawn here
    /// with <c>ScaleMode.StretchToFill</c> at exactly the marker rect, which is what makes
    /// pixels and markers line up.
    /// </summary>
    public static class DubsMintMinimapCompat
    {
        private const string MinimapTypeName = "DubsMintMinimap.MainTabWindow_MiniMap";

        private static bool resolved;

        private static Type minimapType;

        private static MethodInfo minimapMethod;

        private static MethodInfo updateMapTexMethod;

        private static FieldInfo mapField;

        private static FieldInfo mapTextureField;

        private static FieldInfo pixelsField;

        private static FieldInfo dirtyField;

        private static FieldInfo thingLocatorField;

        private static MethodInfo drawAllPawns;

        private static MethodInfo drawFires;

        private static MethodInfo drawProjectiles;

        private static MethodInfo drawPings;

        private static MethodInfo drawThingLocators;

        private static MethodInfo drawCam;

        /// <summary>Their <c>procCell</c> as a real delegate, not a MethodInfo.
        ///
        /// This one is genuinely hot - it runs per cell during a texture refresh, tens of
        /// thousands of times - so reflection Invoke here would be a per-cell boxing
        /// allocation. Everything else in this file is invoked at most six times a frame and
        /// stays on MethodInfo, where readability is worth more than the nanoseconds.</summary>
        private static Func<IntVec3, Map, Color> procCell;

        /// <summary>Detection is by TYPE PRESENCE, not packageId. The packageId varies
        /// between the Steam and local copies of the same mod (<c>..._steam</c>), and what we
        /// actually depend on is the assembly being loaded - which is exactly what this
        /// asks.</summary>
        public static bool Active
        {
            get
            {
                Resolve();
                return minimapType != null && minimapMethod != null && procCell != null;
            }
        }

        internal static MethodBase MinimapTarget
        {
            get
            {
                Resolve();
                return minimapMethod;
            }
        }

        internal static MethodBase UpdateMapTexTarget
        {
            get
            {
                Resolve();
                return updateMapTexMethod;
            }
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            try
            {
                minimapType = AccessTools.TypeByName(MinimapTypeName);
                if (minimapType == null)
                {
                    ABLog.Dev("Dubs Mint Minimap compat: not present.");
                    return;
                }
                minimapMethod = AccessTools.Method(minimapType, "Minimap", new[] { typeof(Rect) });
                updateMapTexMethod = AccessTools.Method(minimapType, "UpdateMapTex",
                    new[] { typeof(Map), typeof(bool) });
                mapField = AccessTools.Field(minimapType, "map");
                mapTextureField = AccessTools.Field(minimapType, "MapTexture");
                pixelsField = AccessTools.Field(minimapType, "pixels");
                dirtyField = AccessTools.Field(minimapType, "dirtyGirls");
                thingLocatorField = AccessTools.Field(minimapType, "ThingLocator");
                drawAllPawns = AccessTools.Method(minimapType, "DrawAllPawns", new[] { typeof(Rect) });
                drawFires = AccessTools.Method(minimapType, "DrawFires", new[] { typeof(Rect) });
                drawProjectiles = AccessTools.Method(minimapType, "DrawProjectiles", new[] { typeof(Rect) });
                drawPings = AccessTools.Method(minimapType, "DrawPings", new[] { typeof(Rect) });
                drawThingLocators = AccessTools.Method(minimapType, "DrawThingLocators", new[] { typeof(Rect) });
                drawCam = AccessTools.Method(minimapType, "DrawCam", new[] { typeof(Rect), typeof(Vector3) });

                MethodInfo proc = AccessTools.Method(minimapType, "procCell",
                    new[] { typeof(IntVec3), typeof(Map) });
                if (proc != null)
                {
                    procCell = (Func<IntVec3, Map, Color>)Delegate.CreateDelegate(
                        typeof(Func<IntVec3, Map, Color>), proc);
                }
                ABLog.Dev("Dubs Mint Minimap compat: " + (Active ? "ACTIVE" : "present but unusable"));
            }
            catch (Exception e)
            {
                ABLog.Dev("Dubs Mint Minimap compat: resolve failed (" + e.Message + ") - leaving vanilla.");
            }
        }

        private static Map TheirMap()
        {
            try
            {
                return mapField?.GetValue(null) as Map ?? Find.CurrentMap;
            }
            catch
            {
                return Find.CurrentMap;
            }
        }

        // ---- the band view -------------------------------------------------

        private static readonly object[] oneRect = new object[1];

        private static readonly object[] rectAndVec = new object[2];

        private static void Call(MethodInfo m, object instance, Rect r)
        {
            if (m == null)
            {
                return;
            }
            oneRect[0] = r;
            m.Invoke(instance, oneRect);
        }

        /// <summary>Draw the active band only. Returns false to let vanilla run.</summary>
        internal static bool DrawBandMinimap(object window, Rect inRect)
        {
            Map map = TheirMap();
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded || bands.bandHeight <= 0)
            {
                return false; // ordinary map: their minimap is already correct
            }
            Texture2D tex = mapTextureField?.GetValue(null) as Texture2D;
            if (tex == null)
            {
                return false;
            }

            // Their own header inset, reproduced: the top 25px of the window is the button
            // strip, and the map is drawn below it.
            inRect.y += 25f;
            inRect.height -= 25f;
            Rect view = inRect.ContractedBy(1f);

            int band = ABBandView.CurrentBand(map);
            CellRect bandRect = bands.RectOfBand(band);
            float h = bands.bandHeight;
            float sizeZ = map.Size.z;

            // See the class comment for the derivation. The rect is deliberately TALLER than
            // the window - it represents the whole map at the band's scale - and is slid up
            // or down so the active band is the part that shows.
            Rect radar = new Rect(
                0f,
                view.height * (h + bandRect.minZ - sizeZ) / h,
                view.width,
                view.height * sizeZ / h);

            HandleClickToJump(view, map, bands, bandRect);

            if (Event.current.type != EventType.Repaint)
            {
                return true; // input handled; nothing to paint this pass
            }

            // CameraDriver.CurrentRealPosition is PRIVATE - Dubs only reads it because their
            // build publicises Assembly-CSharp. It is defined as MyCamera.transform.position,
            // and MyCamera is Current.Camera, so Find.Camera reaches the same transform
            // through public API. Using the transform rather than CameraDriver.MapPosition
            // keeps the view box smooth instead of snapping to whole cells.
            Vector3 camPos = Find.Camera != null
                ? Find.Camera.transform.position
                : Find.CameraDriver.MapPosition.ToVector3Shifted();
            GUI.BeginClip(view);
            GUI.BeginGroup(radar);
            try
            {
                // StretchToFill, NOT DrawTextureFitted - see the class comment. The texture
                // is the whole stack; the clip is what reduces it to one level.
                GUI.DrawTexture(new Rect(0f, 0f, radar.width, radar.height), tex,
                    ScaleMode.StretchToFill);

                // Their own passes, unmodified, aimed by the rect. Markers on other bands
                // land outside the clip and are discarded for free.
                Call(drawAllPawns, window, radar);
                if (thingLocatorField != null && thingLocatorField.GetValue(window) is bool on && on)
                {
                    Call(drawThingLocators, window, radar);
                }
                Call(drawFires, window, radar);
                Call(drawProjectiles, window, radar);
                if (drawCam != null)
                {
                    rectAndVec[0] = radar;
                    rectAndVec[1] = camPos;
                    drawCam.Invoke(window, rectAndVec);
                }
                Call(drawPings, window, radar);
            }
            finally
            {
                // Unity's GUI clip stack is global: leaving it unbalanced corrupts every
                // window drawn after this one, so the pops happen even if one of their draw
                // passes throws.
                GUI.EndGroup();
                GUI.EndClip();
            }
            return true;
        }

        /// <summary>Click or drag on the minimap moves the camera, mapped through the ACTIVE
        /// band rather than the whole stack. Their version divides by <c>map.Size.z</c>, which
        /// on a banded map would fling the camera into a different level (or the gutter).
        /// </summary>
        private static void HandleClickToJump(Rect view, Map map, ABBandMap bands, CellRect bandRect)
        {
            if (!Mouse.IsOver(view) || !Input.GetMouseButton(0))
            {
                return;
            }
            Vector2 m = Event.current.mousePosition;
            float fx = Mathf.InverseLerp(view.x, view.xMax, m.x);
            float fz = Mathf.InverseLerp(view.yMax, view.y, m.y);
            Vector3 target = new Vector3(
                map.Size.x * fx,
                0f,
                bandRect.minZ + bands.bandHeight * fz);
            Find.CameraDriver.JumpToCurrentMapLoc(target);
        }

        // ---- texture refresh ------------------------------------------------

        private static int lastBand = -1;

        /// <summary>The map's uniqueID, used ONLY as a change detector - never as a cache
        /// key. Ids restart at 0 between games, so a stale match costs at most one extra
        /// full repaint, and storing an int rather than the Map keeps a dead map from being
        /// pinned alive by a static.</summary>
        private static int lastMapId = -1;

        /// <summary>
        /// Refresh only the ACTIVE band's cells.
        ///
        /// Their version walks <c>map.AllCells</c> and calls <c>procCell</c> on every one -
        /// which on a seven-level map is seven times the work for six levels nobody can
        /// currently see, and <c>procCell</c> is not cheap (edifice, terrain, plant and fog
        /// lookups plus a colour cache probe per cell).
        ///
        /// The texture, pixel cache and dirty array all stay FULL MAP SIZED and are written
        /// at unchanged (x, z) coordinates, so nothing about their layout changes and the
        /// other bands simply hold whatever they last had - invisible behind the clip. A
        /// band switch forces one full pass over the newly visible level.
        /// </summary>
        internal static bool BandUpdateMapTex(Map map, bool forced, ref Texture2D result)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded || bands.bandHeight <= 0)
            {
                return false;
            }
            Texture2D tex = mapTextureField?.GetValue(null) as Texture2D;
            // Deliberately NOT reinitialising the texture ourselves. Their code does that on
            // the size-mismatch branch, and the Unity API for it was renamed across versions
            // (Resize -> Reinitialize); letting their build call its own is one less thing to
            // get wrong. Until the sizes agree we simply stand aside, which costs one vanilla
            // full pass on the first refresh of a map.
            if (tex == null || tex.width != map.Size.x || tex.height != map.Size.z)
            {
                return false;
            }
            Color[,] pixels = pixelsField?.GetValue(null) as Color[,];
            bool[] dirty = dirtyField?.GetValue(null) as bool[];
            if (pixels == null || dirty == null
                || pixels.GetLength(0) != map.Size.x || pixels.GetLength(1) != map.Size.z
                || dirty.Length != map.Size.x * map.Size.z)
            {
                return false;
            }

            int band = ABBandView.CurrentBand(map);
            bool switched = band != lastBand || map.uniqueID != lastMapId;
            lastBand = band;
            lastMapId = map.uniqueID;
            bool all = forced || switched;

            CellIndices indices = map.cellIndices;
            bool setAny = false;
            foreach (IntVec3 c in bands.RectOfBand(band))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                int idx = indices.CellToIndex(c);
                if (!all && !dirty[idx])
                {
                    continue;
                }
                Color col = procCell(c, map);
                if (col != pixels[c.x, c.z])
                {
                    setAny = true;
                    pixels[c.x, c.z] = col;
                    tex.SetPixel(c.x, c.z, col);
                }
                dirty[idx] = false;
            }
            if (setAny)
            {
                tex.Apply(false);
            }
            result = tex;
            return true;
        }
    }

    /// <summary>Show only the active level in Dubs Mint Minimap. See
    /// <see cref="DubsMintMinimapCompat"/> for the whole design; the patch itself is a
    /// two-line hand-off so no foreign type ever reaches a signature here.</summary>
    [HarmonyPatch]
    public static class Patch_DubsMinimap_ABActiveLevelOnly
    {
        private static bool Prepare()
        {
            return DubsMintMinimapCompat.Active;
        }

        private static MethodBase TargetMethod()
        {
            return DubsMintMinimapCompat.MinimapTarget;
        }

        private static bool Prefix(object __instance, Rect inRect)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Ui))
                {
                    return true;
                }
                return !DubsMintMinimapCompat.DrawBandMinimap(__instance, inRect);
            }
            catch (Exception e)
            {
                // Their minimap working the old way beats a dead window.
                ABGuard.Disable(ABGuard.Ui, e, "Dubs minimap band view");
                return true;
            }
        }
    }

    /// <summary>Refresh only the active band's cells - see
    /// <see cref="DubsMintMinimapCompat.BandUpdateMapTex"/>.</summary>
    [HarmonyPatch]
    public static class Patch_DubsMinimap_ABBandTextureRefresh
    {
        private static bool Prepare()
        {
            return DubsMintMinimapCompat.Active && DubsMintMinimapCompat.UpdateMapTexTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return DubsMintMinimapCompat.UpdateMapTexTarget;
        }

        private static bool Prefix(Map map, bool forced, ref Texture2D __result)
        {
            try
            {
                if (!ABGuard.On(ABGuard.Ui) || map == null)
                {
                    return true;
                }
                return !DubsMintMinimapCompat.BandUpdateMapTex(map, forced, ref __result);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "Dubs minimap band texture refresh");
                return true;
            }
        }
    }
}
