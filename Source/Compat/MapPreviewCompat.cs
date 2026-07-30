using System;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Map Preview (m00nl1ght.MapPreview): make the world-map preview show
    /// what the colony's SURFACE LEVEL will actually look like.
    ///
    /// WHY IT WAS WRONG, AND WHY IT IS SUBTLE. Map Preview does not go through
    /// <c>MapGenerator.GenerateMap</c> at all - it builds its own bare Map and runs a short
    /// list of gensteps (ElevationFertility, Terrain, the Mutator passes) on a background
    /// thread. So our banding prefix never fires, and it previewed an ordinary square map.
    /// That would be harmless if the surface band were an ordinary square map, but it is
    /// NOT: it is a horizontal SLICE of a much taller generated map, at rows
    /// [surfaceBand*Slot, +bandHeight). Every genstep here samples POSITION-BASED noise, so
    /// rows 384-510 of a 190x768 map bear no relation to rows 0-189 of a 190x190 one. The
    /// preview was not slightly off; it was a different map.
    ///
    /// Hence the shape of the fix: generate at the FULL STACKED HEIGHT, exactly as the real
    /// map does, then show only the surface band's rows. Same size, same seed, same noise
    /// coordinates - so the preview is the real thing by construction rather than by
    /// approximation. Our carve never touches the surface band, which is what makes the
    /// crop honest.
    ///
    /// MOST OF THIS IS NOT HARMONY. Map Preview ships public integration points for exactly
    /// this scenario - their own <c>ModCompat_BetterMapSizes</c> uses them - so the size half
    /// is two field assignments:
    ///   <c>MapSizeUtility.MaxMapSize</c>    - the clamp, and also the preview widget's
    ///                                         texture and pixel-buffer size.
    ///   <c>MapSizeUtility.MapSizeOverride</c> - a Func consulted while choosing a landing
    ///                                         site.
    /// Only the parts with no public hook are patched, and each patch is independent: any
    /// one failing to resolve leaves the others working.
    ///
    /// As with the Dubs bridge, no foreign type appears in any signature here - HarmonyBoot's
    /// class processor resolves parameter types when it walks a patch class, so a MapPreview
    /// type in a signature would be logged as a broken patch class on every load where the
    /// mod is absent.
    /// </summary>
    public static class MapPreviewCompat
    {
        private const string SizeUtilTypeName = "MapPreview.MapSizeUtility";

        private const string ResultTypeName = "MapPreview.MapPreviewResult";

        private const string WindowTypeName = "MapPreview.MapPreviewWindow";

        private const string WidgetTypeName = "MapPreview.MapPreviewWidget";

        /// <summary>Headroom for the preview widget's texture and colour buffer.
        ///
        /// The widget is built as <c>new MapPreviewWidgetWithPreloader(MapSizeUtility.MaxMapSize)</c>
        /// and the request's TextureSize is taken from that texture, while
        /// <c>QueuePreviewRequest</c> THROWS when MapSize exceeds TextureSize. Their default
        /// cap is 500, and a stacked map is up to 7 x 128 = 896 rows, so the cap has to rise
        /// or every banded preview dies with "Map size exceeds max preview size". Only z is
        /// raised: x is still the clamp for ordinary previews (quest sites and the like) and
        /// shrinking it would break them.</summary>
        private const int MaxStackedZ = 1024;

        private static bool resolved;

        private static Type sizeUtilType;

        private static MethodInfo determineMapSize;

        private static MethodInfo texCoordsGetter;

        private static MethodInfo onWorldTileSelected;

        private static MethodInfo mapPosFromScreenPos;

        private static PropertyInfo resultMapSize;

        private static PropertyInfo resultTextureSize;

        public static bool Active
        {
            get
            {
                Resolve();
                return sizeUtilType != null;
            }
        }

        internal static MethodBase DetermineMapSizeTarget
        {
            get
            {
                Resolve();
                return determineMapSize;
            }
        }

        internal static MethodBase TexCoordsTarget
        {
            get
            {
                Resolve();
                return texCoordsGetter;
            }
        }

        internal static MethodBase OnWorldTileSelectedTarget
        {
            get
            {
                Resolve();
                return onWorldTileSelected;
            }
        }

        internal static MethodBase MapPosFromScreenPosTarget
        {
            get
            {
                Resolve();
                return mapPosFromScreenPos;
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
                sizeUtilType = AccessTools.TypeByName(SizeUtilTypeName);
                if (sizeUtilType == null)
                {
                    // Not an error: their components live under Lunar/Components and are
                    // loaded by LunarLoader, so absence just means the mod is not installed.
                    ABLog.Dev("Map Preview compat: not present.");
                    return;
                }
                determineMapSize = AccessTools.Method(sizeUtilType, "DetermineMapSize");

                Type resultType = AccessTools.TypeByName(ResultTypeName);
                if (resultType != null)
                {
                    texCoordsGetter = AccessTools.PropertyGetter(resultType, "TexCoords");
                    resultMapSize = AccessTools.Property(resultType, "MapSize");
                    resultTextureSize = AccessTools.Property(resultType, "TextureSize");
                }
                Type windowType = AccessTools.TypeByName(WindowTypeName);
                if (windowType != null)
                {
                    onWorldTileSelected = AccessTools.Method(windowType, "OnWorldTileSelected");
                }
                Type widgetType = AccessTools.TypeByName(WidgetTypeName);
                if (widgetType != null)
                {
                    mapPosFromScreenPos = AccessTools.Method(widgetType, "MapPosFromScreenPos");
                }

                RaiseSizeCaps();
                ABLog.Dev("Map Preview compat: ACTIVE.");
            }
            catch (Exception e)
            {
                ABLog.Dev("Map Preview compat: resolve failed (" + e.Message + ") - leaving vanilla.");
            }
        }

        /// <summary>
        /// The no-Harmony half, using their own public integration fields.
        ///
        /// MUST run before the preview window is first opened: the widget is a field
        /// initialiser on <c>MapPreviewWindow</c>, so it snapshots MaxMapSize at construction
        /// and its texture size is fixed from then on. Startup (via HarmonyBoot's
        /// StaticConstructorOnStartup) is comfortably early enough.
        /// </summary>
        private static void RaiseSizeCaps()
        {
            FieldInfo maxField = AccessTools.Field(sizeUtilType, "MaxMapSize");
            if (maxField != null && maxField.GetValue(null) is IntVec2 max && max.z < MaxStackedZ)
            {
                maxField.SetValue(null, new IntVec2(max.x, MaxStackedZ));
            }

            // MapSizeOverride is only consulted while CHOOSING a landing site (their
            // DetermineMapSizeUnclamped takes the world.info.initialMapSize branch once a
            // game is running). That is the case that matters most - it is the preview you
            // look at before committing - and using their hook rather than a patch means the
            // Better Map Sizes path stays intact if the user runs both.
            FieldInfo overrideField = AccessTools.Field(sizeUtilType, "MapSizeOverride");
            if (overrideField != null && overrideField.GetValue(null) == null)
            {
                overrideField.SetValue(null, new Func<IntVec2>(LandingSiteSizeOverride));
            }
        }

        /// <summary>Their convention: a non-positive component means "no opinion".</summary>
        private static IntVec2 LandingSiteSizeOverride()
        {
            try
            {
                GameInitData init = Find.GameInitData;
                if (init == null || init.mapSize <= 0 || !Banding)
                {
                    return new IntVec2(-1, -1);
                }
                return Stacked(init.mapSize, init.mapSize);
            }
            catch
            {
                return new IntVec2(-1, -1);
            }
        }

        // ---- the layout ----------------------------------------------------

        private static bool Banding => ABV2.Enabled && ABV2.BandCount > 1;

        /// <summary>The surface dimension the last inflation was computed from - i.e. the
        /// height of ONE band. Cached because the preview window has to undo their aspect
        /// calculation afterwards (see the window patch) and would otherwise have to guess
        /// which band height produced the stacked size it is looking at.</summary>
        private static int lastBandHeight;

        /// <summary>Inflate a would-be surface size into the full stacked size, exactly as
        /// ABBandedGeneration's own prefix does for a real map.</summary>
        internal static IntVec2 Stacked(int x, int z)
        {
            int clampedX = ABMapSizeLimit.Clamp(x);
            int clampedZ = ABMapSizeLimit.Clamp(z);
            lastBandHeight = clampedZ;
            return new IntVec2(clampedX, ABV2.BandCount * ABBandMap.SlotFor(clampedZ));
        }

        /// <summary>Is a stacked size what this size already is? Guards against inflating a
        /// value we (or a re-entrant call) already inflated.</summary>
        private static bool AlreadyStacked(IntVec2 size)
        {
            return size.z == ABV2.BandCount * ABBandMap.SlotFor(size.x);
        }

        /// <summary>Only PLAYER COLONY maps are banded, mirroring
        /// <c>ABBandedGeneration.ShouldBand</c>. A null parent is the pre-game landing site
        /// (which will become a player settlement); a quest Site is not banded and must keep
        /// its own PreferredMapSize or its preview would be garbage.</summary>
        internal static bool ShouldInflateFor(MapParent parent)
        {
            if (!Banding)
            {
                return false;
            }
            if (parent == null)
            {
                return true;
            }
            Settlement s = parent as Settlement;
            return s != null && s.Faction != null && s.Faction.IsPlayer;
        }

        /// <summary>The surface band's rows, from the cached band height alone.</summary>
        internal static bool TryBandRows(out int minZ, out int height)
        {
            minZ = 0;
            height = 0;
            if (!Banding || lastBandHeight <= 0)
            {
                return false;
            }
            int slot = ABBandMap.SlotFor(lastBandHeight);
            if (slot <= 0)
            {
                return false;
            }
            minZ = ABV2.SurfaceBand * slot;
            height = lastBandHeight;
            return true;
        }

        /// <summary>As above, but first CONFIRMS the size in hand is a stack we produced.
        /// Used on the display path, where mis-cropping someone else's preview (a quest site,
        /// or another mod's oversized map) would be worse than not cropping at all.</summary>
        internal static bool TryBandRows(int stackedZ, out int minZ, out int height)
        {
            if (!TryBandRows(out minZ, out height))
            {
                return false;
            }
            if (ABV2.BandCount * ABBandMap.SlotFor(height) != stackedZ)
            {
                minZ = 0;
                height = 0;
                return false; // not one of ours - leave it alone
            }
            return true;
        }

        // ---- display crop ---------------------------------------------------

        /// <summary>
        /// Crop the drawn region to the surface band.
        ///
        /// Their <c>TexCoords</c> already exists to draw a SUB-RECT of an oversized buffer -
        /// the buffer is MaxMapSize while the map is usually smaller - so cropping to a band
        /// needs no new drawing code at all, just a different rect. The v axis maps straight
        /// to cell z: pixels are written as <c>Pixels[z * TextureSize.x + x]</c> and
        /// <c>SetPixels</c> treats index 0 as bottom-left, the same origin GUI texture
        /// coordinates use.
        /// </summary>
        internal static bool CropTexCoords(object result, ref Rect texCoords)
        {
            if (resultMapSize == null || resultTextureSize == null)
            {
                return false;
            }
            if (!(resultMapSize.GetValue(result) is IntVec2 mapSize)
                || !(resultTextureSize.GetValue(result) is IntVec2 texSize)
                || texSize.x <= 0 || texSize.z <= 0)
            {
                return false;
            }
            if (!TryBandRows(mapSize.z, out int minZ, out int height))
            {
                return false;
            }
            texCoords = new Rect(
                0f,
                minZ / (float)texSize.z,
                mapSize.x / (float)texSize.x,
                height / (float)texSize.z);
            return true;
        }

        /// <summary>
        /// Undo their aspect calculation for the preview window.
        ///
        /// They size the window from the map's proportions:
        /// <c>scale = PreviewWindowSize / max(x, z)</c>, then <c>(x*scale, z*scale)</c>. Fed a
        /// stacked size that produces a tall narrow sliver of a window - correct for the map
        /// they think they are previewing, wrong for the single square band we are actually
        /// showing. The height already equals PreviewWindowSize (z is the larger side), so
        /// only the width needs restating, in the BAND's proportions.
        /// </summary>
        internal static void FixWindowAspect(object windowObj)
        {
            Window window = windowObj as Window;
            if (window == null || lastBandHeight <= 0)
            {
                return;
            }
            Rect r = window.windowRect;
            if (r.height <= 0f)
            {
                return;
            }
            int x = ABMapSizeLimit.Clamp(lastBandHeight);
            float width = r.height * (x / (float)lastBandHeight);
            window.windowRect = GenUI.Rounded(new Rect(r.x, r.y, width, r.height));
        }
    }

    /// <summary>Generate the preview at the full stacked height so its noise matches the real
    /// map. See <see cref="MapPreviewCompat"/>.</summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABStackedSize
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active && MapPreviewCompat.DetermineMapSizeTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.DetermineMapSizeTarget;
        }

        private static void Postfix(MapParent mapParent, ref IntVec2 __result)
        {
            try
            {
                if (!MapPreviewCompat.ShouldInflateFor(mapParent))
                {
                    return;
                }
                // Postfix on the CLAMPED entry point deliberately: their clamp would cut a
                // stacked height back to MaxMapSize, and answering last means our value is
                // the one that survives.
                __result = MapPreviewCompat.Stacked(__result.x, __result.z);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview size patch threw: " + e, 762195891);
            }
        }
    }

    /// <summary>Show only the surface band's rows - see
    /// <see cref="MapPreviewCompat.CropTexCoords"/>.</summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABCropToSurfaceBand
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active && MapPreviewCompat.TexCoordsTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.TexCoordsTarget;
        }

        private static void Postfix(object __instance, ref Rect __result)
        {
            try
            {
                MapPreviewCompat.CropTexCoords(__instance, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview crop patch threw: " + e, 762195892);
            }
        }
    }

    /// <summary>Keep the preview window square instead of a tall sliver - see
    /// <see cref="MapPreviewCompat.FixWindowAspect"/>.</summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABWindowAspect
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active && MapPreviewCompat.OnWorldTileSelectedTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.OnWorldTileSelectedTarget;
        }

        private static void Postfix(object __instance)
        {
            try
            {
                if (ABV2.Enabled && ABV2.BandCount > 1)
                {
                    MapPreviewCompat.FixWindowAspect(__instance);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview window aspect patch threw: " + e, 762195893);
            }
        }
    }

    /// <summary>
    /// The hover tooltip names the terrain under the cursor, mapping screen position through
    /// the FULL map height. With the view cropped to one band that lands several levels away
    /// - it would cheerfully report open air over the sky band. Shifting the answer into the
    /// surface band costs one add and keeps the tooltip honest.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABTooltipCell
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active && MapPreviewCompat.MapPosFromScreenPosTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.MapPosFromScreenPosTarget;
        }

        private static void Postfix(Rect mapRect, Vector2 screenPos, ref IntVec3 __result)
        {
            try
            {
                if (!ABV2.Enabled || ABV2.BandCount <= 1 || mapRect.height <= 0f)
                {
                    return;
                }
                // The unvalidated overload: this runs after their own clamping, so the
                // stacked height is no longer visible here - the cached band layout is the
                // only thing to go on.
                if (!MapPreviewCompat.TryBandRows(out int minZ, out int height) || height <= 1)
                {
                    return;
                }
                float f = Mathf.Clamp01(1f - screenPos.y / mapRect.height);
                int z = minZ + Mathf.Clamp(Mathf.RoundToInt(f * (height - 1)), 0, height - 1);
                __result = new IntVec3(__result.x, 0, z);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview tooltip patch threw: " + e, 762195894);
            }
        }
    }
}
