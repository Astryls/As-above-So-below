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

        private const string RerollWindowTypeName = "MapPreview.MapSeedRerollWindow";

        private const string ApiTypeName = "MapPreview.MapPreviewAPI";

        // MapSeedRerollWindow's own layout constants, reproduced because the postfix has to
        // re-derive the row count from a corrected element height. Their names, their values:
        // WindowMargin 20, ElementSpacing 20, and a 70px header strip (a 50px toolbar plus
        // one spacing). If they ever change, the grid gets slightly too many or too few rows
        // - it does not break.
        private const float RerollWindowMargin = 20f;

        private const float RerollElementSpacing = 20f;

        private const float RerollHeaderStrip = 70f;

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

        private static MethodInfo rerollUpdateElementSize;

        private static MethodInfo rerollTryAddElement;

        private static FieldInfo rerollElementSize;

        private static FieldInfo rerollGridSize;

        private static FieldInfo rerollMapSize;

        private static PropertyInfo apiIsGeneratingPreview;

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

        internal static MethodBase RerollGridTarget
        {
            get
            {
                Resolve();
                return rerollUpdateElementSize;
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
                // The seed-reroll grid. Everything here is optional: the reroll feature is
                // OFF by default in their settings, and any one of these resolving to null
                // simply leaves the grid patch unarmed rather than breaking the rest.
                Type rerollType = AccessTools.TypeByName(RerollWindowTypeName);
                if (rerollType != null)
                {
                    rerollUpdateElementSize = AccessTools.Method(rerollType, "UpdateElementSize");
                    rerollTryAddElement = AccessTools.Method(rerollType, "TryAddElement");
                    rerollElementSize = AccessTools.Field(rerollType, "_elementSize");
                    rerollGridSize = AccessTools.Field(rerollType, "_gridSize");
                    rerollMapSize = AccessTools.Field(rerollType, "_mapSize");
                }
                Type apiType = AccessTools.TypeByName(ApiTypeName);
                if (apiType != null)
                {
                    apiIsGeneratingPreview = AccessTools.Property(apiType, "IsGeneratingPreview");
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
        /// The no-Harmony half, using their own public integration points.
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

            TryRegisterSizeTransform();
        }

        /// <summary>
        /// §56q  THE SUPPORTED SIZE HOOK, AND WHY IT REPLACED BOTH OF THE OLD ONES.
        ///
        /// Map Preview 1.6 added <c>MapSizeUtility.MapSizeTransforms</c> - a public list of
        /// <c>(World, PlanetTile, IntVec2) -&gt; IntVec2</c> delegates, aggregated inside
        /// <c>DetermineMapSize</c> before the clamp. It is the same hook their own VEF bridge
        /// uses, and it is strictly better than what we were doing:
        ///
        ///   - The field we USED to set, <c>MapSizeOverride</c>, DOES NOT EXIST. It was
        ///     renamed <c>GameInitMapSizeOverride</c>, so <c>AccessTools.Field</c> returned
        ///     null and the assignment silently did nothing - for however many versions.
        ///     Nothing broke, because the Harmony postfix below covered the same case, which
        ///     is exactly why nobody noticed. (Its dead double-inflation guard,
        ///     <c>AlreadyStacked</c>, is now live and used here.)
        ///   - The postfix REPLACED <c>__result</c> outright, discarding whatever the
        ///     transform chain had just agreed on. A player running Vanilla Expanded's
        ///     tile-mutator size overrides had them silently dropped on banded maps. A
        ///     transform COMPOSES: we receive their answer and inflate it.
        ///
        /// Registered by reflection because the delegate type is theirs; our target method is
        /// all vanilla types, so <c>Delegate.CreateDelegate</c> binds cleanly.
        ///
        /// Rule 33: if the list is absent (an older Map Preview), we say so and the Harmony
        /// postfix stays armed as the fallback. Exactly one of the two is ever live.
        /// </summary>
        private static void TryRegisterSizeTransform()
        {
            try
            {
                FieldInfo listField = AccessTools.Field(sizeUtilType, "MapSizeTransforms");
                Type delegateType = sizeUtilType.GetNestedType("MapSizeTransform");
                if (listField == null || delegateType == null
                    || !(listField.GetValue(null) is System.Collections.IList list))
                {
                    ABLog.Dev("Map Preview compat: MapSizeTransforms absent - falling back"
                        + " to the DetermineMapSize postfix.");
                    return;
                }
                MethodInfo mine = AccessTools.Method(typeof(MapPreviewCompat), nameof(TransformMapSize));
                Delegate d = Delegate.CreateDelegate(delegateType, mine, false);
                if (d == null)
                {
                    ABLog.Dev("Map Preview compat: MapSizeTransform signature changed -"
                        + " falling back to the DetermineMapSize postfix.");
                    return;
                }
                list.Add(d);
                sizeTransformRegistered = true;
                ABLog.Dev("Map Preview compat: registered a MapSizeTransform.");
            }
            catch (Exception e)
            {
                sizeTransformRegistered = false;
                ABLog.Dev("Map Preview compat: could not register a MapSizeTransform ("
                    + e.Message + ") - falling back to the DetermineMapSize postfix.");
            }
        }

        /// <summary>True once our transform is in their list; disarms the Harmony
        /// fallback so the inflation can never be applied twice.</summary>
        internal static bool SizeTransformRegistered
        {
            get { Resolve(); return sizeTransformRegistered; }
        }

        private static bool sizeTransformRegistered;

        /// <summary>
        /// Their contract: take the size agreed so far, return the size we want.
        ///
        /// ⚠ Signature is bound by <c>Delegate.CreateDelegate</c> against THEIR delegate
        /// type, so these parameter types are load-bearing and all three are vanilla
        /// (<c>PlanetTile</c> is 1.6's tile handle, not a Map Preview type).
        ///
        /// ⚠ The tile is deliberately ignored. Banding is a property of the COLONY, not the
        /// tile, and the parent-based test is the one that mirrors
        /// <c>ABBandedGeneration.ShouldBand</c>. Their aggregate does not hand us the
        /// MapParent, so the landing-site case (parent == null) is inferred from program
        /// state instead - see ShouldInflateForTile.
        /// </summary>
        private static IntVec2 TransformMapSize(RimWorld.Planet.World world,
            RimWorld.Planet.PlanetTile tile, IntVec2 size)
        {
            try
            {
                // ⚠ TEMPORARY DIAGNOSTIC (w18). Everything upstream of this method checks
                // out on paper, so the only unanswered question is whether the transform is
                // REACHED and what it decides. Behind verboseLogging like every other compat
                // line, i.e. off for players.
                bool inflate = ShouldInflateForTile(world, tile);
                bool already = AlreadyStacked(size);
                if (!inflate || already)
                {
                    ABLog.Dev("Map Preview transform CALLED: in=" + size.x + "x" + size.z
                        + " banding=" + Banding + " bands=" + ABV2.BandCount
                        + " inflate=" + inflate + " alreadyStacked=" + already
                        + " -> UNCHANGED");
                    return size;
                }
                IntVec2 stacked = Stacked(size.x, size.z);
                ABLog.Dev("Map Preview transform CALLED: in=" + size.x + "x" + size.z
                    + " bands=" + ABV2.BandCount + " -> " + stacked.x + "x" + stacked.z);
                return stacked;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview size transform threw: " + e, 762195893);
                return size;
            }
        }

        /// <summary>
        /// The transform runs before we know the MapParent, so resolve it from the world
        /// object at that tile - the same question <see cref="ShouldInflateFor"/> answers,
        /// asked one step earlier.
        /// </summary>
        private static bool ShouldInflateForTile(RimWorld.Planet.World world,
            RimWorld.Planet.PlanetTile tile)
        {
            if (!Banding)
            {
                return false;
            }
            try
            {
                MapParent parent = world?.worldObjects?.MapParentAt(tile);
                return ShouldInflateFor(parent);
            }
            catch
            {
                // No world object yet is the ordinary pre-game landing site.
                return true;
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
        /// <summary>
        /// The stacked size to preview at.
        ///
        /// ⚠ Both dimensions come from ONE planned size, deliberately. A band is SQUARE
        /// (§2), so x and z are the same number; clamping them independently was how the
        /// preview could end up previewing a shape that could never generate.
        ///
        /// See <see cref="ABMapSizeLimit.PlannedSize"/> for why this is not
        /// <c>Clamp</c> - that lossy snap is what limited the preview to 190x190.
        /// </summary>
        internal static IntVec2 Stacked(int x, int z)
        {
            int size = ABMapSizeLimit.PlannedSize(z > 0 ? z : x);
            lastBandHeight = size;
            return new IntVec2(size, ABV2.BandCount * ABBandMap.SlotFor(size));
        }

        /// <summary>Is a stacked size what this size already is? Guards against inflating a
        /// value we (or a re-entrant call) already inflated.
        ///
        /// ⚠ Dead for as long as the only caller was the override that never installed
        /// (§56q). It is live again now that the transform composes over a size other mods
        /// may also have touched, and it is the reason a doubled aggregate cannot compound.</summary>
        private static bool AlreadyStacked(IntVec2 size)
        {
            return size.x > 0 && ABV2.BandCount > 1
                && size.z == ABV2.BandCount * ABBandMap.SlotFor(size.x);
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

        /// <summary>
        /// SEED REROLL: make the candidate grid usable on a banded map.
        ///
        /// ⚠ WITHOUT THIS THE REROLL WINDOW OPENS COMPLETELY EMPTY, and the arithmetic is
        /// worth writing down because the symptom looks like the feature is missing rather
        /// than mis-sized.
        ///
        /// <c>MapSeedRerollWindow.PreOpen</c> takes its size from
        /// <c>MapSizeUtility.DetermineMapSize</c> - the method we already postfix - so it
        /// correctly gets the STACKED size, and that is exactly right: the thumbnails must
        /// generate at full stacked height or their noise would not match the real map (the
        /// whole §6d lesson). But <c>UpdateElementSize</c> then shapes each thumbnail with
        /// <c>y = width / mapSize.x * mapSize.z</c> and derives the row count from it:
        ///
        ///     126x896 stack, 2560px window, 6 per row
        ///       elementSize.y = 403 / 126 * 896 = 2,866 px      (a 7:1 sliver)
        ///       gridSize.y    = floor(1330 / 2886) = 0
        ///       DesiredCount  = 6 * 0 = 0
        ///
        /// The method's own trim loop then disposes every element, and <c>TryAddElement</c>
        /// opens with <c>if (_elements.Count >= DesiredCount) return;</c> - 0 >= 0 - so it
        /// returns immediately and nothing is ever queued.
        ///
        /// This is the SECOND consumer of the stacked size to be caught out by it; the first
        /// was their main preview window, fixed by FixWindowAspect above. The fix is the
        /// same shape: keep the stacked size for GENERATION, use the band's aspect for
        /// LAYOUT. Deliberately not solved by suppressing the inflation while the reroll
        /// window is open - that would make every thumbnail a picture of a map that will
        /// never generate.
        ///
        /// Their cropping already works here for free: <c>MapPreviewWidget.OnPromiseResolved</c>
        /// copies <c>result.TexCoords</c> and <c>DrawGenerated</c> draws through it, so the
        /// existing CropTexCoords patch trims each thumbnail to the surface band.
        /// </summary>
        internal static void FixRerollGrid(object window, int elementsPerRow)
        {
            if (rerollElementSize == null || rerollGridSize == null || rerollMapSize == null)
            {
                return;
            }
            Window w = window as Window;
            if (w == null || w.windowRect.height <= 0f || elementsPerRow <= 0)
            {
                return;
            }
            if (!(rerollMapSize.GetValue(window) is IntVec2 mapSize) || mapSize.x <= 0)
            {
                return;
            }
            // Only claim sizes that are OUR stack. An ordinary map (or a quest site) reaches
            // this with a size TryBandRows refuses, and their own arithmetic was already
            // correct for it - so the untouched path is the common one.
            if (!TryBandRows(mapSize.z, out int _, out int bandHeight) || bandHeight <= 0)
            {
                return;
            }
            if (!(rerollElementSize.GetValue(null) is Vector2 size) || size.x <= 0f)
            {
                return;
            }

            // Width is theirs and already right; only the height was derived from the stack.
            float y = size.x / mapSize.x * bandHeight;
            rerollElementSize.SetValue(null, new Vector2(size.x, y));

            int rows = Mathf.FloorToInt(
                (w.windowRect.height - 2f * RerollWindowMargin - RerollHeaderStrip)
                / (y + RerollElementSpacing));
            rerollGridSize.SetValue(null, new Vector2Int(elementsPerRow, Mathf.Max(1, rows)));

            // Their trim loop and TryAddElement call both already ran against the OLD (zero)
            // DesiredCount, so the list was emptied and nothing was queued. Kick it once now
            // that the grid is real; TryAddElement chains itself from each promise, so one
            // call fills the whole grid.
            if (rerollTryAddElement != null && !IsGeneratingPreview())
            {
                rerollTryAddElement.Invoke(window, null);
            }
        }

        /// <summary>Mirrors the guard their own UpdateElementSize puts on TryAddElement.
        /// Absent or unreadable resolves to "not busy", which fails toward populating the
        /// grid rather than leaving it blank.</summary>
        private static bool IsGeneratingPreview()
        {
            try
            {
                return apiIsGeneratingPreview != null
                    && apiIsGeneratingPreview.GetValue(null) is bool busy
                    && busy;
            }
            catch
            {
                return false;
            }
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
            // One band is square, and lastBandHeight is now the REAL planned size rather
            // than a re-clamped guess, so the window is square too. This used to re-Clamp
            // and could disagree with the size the preview had actually generated at.
            window.windowRect = GenUI.Rounded(new Rect(r.x, r.y, r.height, r.height));
        }
    }

    /// <summary>Generate the preview at the full stacked height so its noise matches the real
    /// map. See <see cref="MapPreviewCompat"/>.
    ///
    /// ⚠ FALLBACK ONLY (§56q). When <c>MapSizeTransforms</c> exists we register there
    /// instead and this patch stays unarmed - two live inflations would compound, and the
    /// postfix additionally discards other mods' transforms, which is why the hook is
    /// preferred whenever it is available.</summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABStackedSize
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active
                && !MapPreviewCompat.SizeTransformRegistered
                && MapPreviewCompat.DetermineMapSizeTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.DetermineMapSizeTarget;
        }

        private static void Postfix(MapParent mapParent, ref IntVec2 __result)
        {
            try
            {
                // ⚠ TEMPORARY DIAGNOSTIC (w18): says WHICH of the two size paths is live.
                ABLog.Dev("Map Preview FALLBACK postfix fired: in=" + __result.x + "x"
                    + __result.z + " inflate=" + MapPreviewCompat.ShouldInflateFor(mapParent));
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

    /// <summary>Make the seed-reroll candidate grid usable on a banded map - see
    /// <see cref="MapPreviewCompat.FixRerollGrid"/> for the arithmetic that otherwise leaves
    /// the window empty.
    ///
    /// A POSTFIX that repairs two of their statics, rather than a prefix that reimplements
    /// the method: their layout maths (element width, the trim loop) is correct and only the
    /// aspect is wrong, so replacing the body would duplicate work that can silently drift
    /// out of step on their next update.</summary>
    [HarmonyPatch]
    public static class Patch_MapPreview_ABRerollGrid
    {
        private static bool Prepare()
        {
            return MapPreviewCompat.Active && MapPreviewCompat.RerollGridTarget != null;
        }

        private static MethodBase TargetMethod()
        {
            return MapPreviewCompat.RerollGridTarget;
        }

        private static void Postfix(object __instance, int elementsPerRow)
        {
            try
            {
                if (ABV2.Enabled && ABV2.BandCount > 1)
                {
                    MapPreviewCompat.FixRerollGrid(__instance, elementsPerRow);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Map Preview reroll grid patch threw: " + e, 762195895);
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
