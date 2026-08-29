using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Toggleable Overlays (Owlchemist.ToggleableOverlays, and the
    /// "Continued" reupload, which keeps that same packageId): make its hide-unless-hovered
    /// rule work for the band below, and stop the two mods fighting over blueprint alpha.
    ///
    /// Detection and every read are reflection-only, same rules as every bridge in this
    /// folder (see the banner in DubsMintMinimapCompat for why no foreign type may appear in
    /// any signature and why this class carries no patch attribute): resolved once, inert
    /// forever if any member is missing, and the per-call cost when TO is absent is one
    /// boolean test. The field reads go through Harmony's <c>FieldRef</c> delegates rather
    /// than <c>FieldInfo.GetValue</c> because two of these sit on a per-frame overlay path
    /// that has already been profiled once at 1.41 ms - reflection that boxes an int every
    /// frame per thing is not acceptable there.
    ///
    /// FOUR SEPARATE COLLISIONS, ALL FOUND BY READING THEIR DLL:
    ///
    /// 1. THE MOUSE IS IN THE WRONG FRAME OF REFERENCE, AND THIS IS THE BIG ONE. TO's whole
    ///    premise is "the overlay only appears for the thing under the cursor", and every
    ///    one of its ten-odd gates is literally
    ///        <c>thing.positionInt.x == mousePositionX &amp;&amp; thing.positionInt.z == mousePositionZ</c>
    ///    against a cell it derives in <c>Patch_Game_UpdatePlay</c> from a raw
    ///    <c>ScreenPointToRay</c> - i.e. the VIEW band's cell. A thing on a band below lives
    ///    at <c>z - drop</c>, so its position can never equal that mouse cell and NO below
    ///    overlay can ever be revealed by hovering it. With TO's shipped defaults
    ///    (hideItems, hideStorageBuilding, hideBedAssignment, hideFuelWarnings all ON) that
    ///    silently deletes stack counts, storage labels, bed/throne names and fuel warnings
    ///    from every level below the one you are looking at, permanently.
    ///
    ///    ⚠ THE FIX IS ONE SUBSTITUTION, NOT TEN PATCHES. Their gates all read the same
    ///    three static fields, so for the duration of our below pass those fields are
    ///    pointed at the cell the hovered COLUMN actually shows (the same
    ///    <c>ABBands.TryResolveVisibleBelow</c> the renderer and the label placer use), and
    ///    restored afterwards. Every TO gate - present and future - then answers correctly
    ///    without knowing bands exist. Note the two frames of reference can never collide by
    ///    accident: bands PARTITION z, so a below thing's z is never equal to a view-band z,
    ///    which is why the bug is "never reveals" and never "reveals the wrong thing".
    ///
    /// 2. OUR FORBIDDEN REDRAW BYPASSES THEM ENTIRELY. Patch_OverlayDrawer_ABBelowForbidden
    ///    paints the X with a bare <c>Graphics.DrawMesh</c> and never enters
    ///    <c>OverlayDrawer.RenderForbiddenOverlay</c>, which is where TO's transpiler lives -
    ///    so with "hide forbidden" on, the same-band X's vanish and the below-band ones stay
    ///    lit. <see cref="AllowForbiddenBelow"/> re-implements their
    ///    <c>CheckRenderForbiddenOverlay</c> + <c>RenderForbiddenBigOverlay</c> rules against
    ///    the substituted mouse cell.
    ///
    /// 3. ZOOM CULLING. Their <c>ThingOverlays</c> transpiler swaps the lister group from
    ///    <c>HasGUIOverlay</c> to <c>Pawn</c> at any zoom above Closest (when optimizedLister
    ///    and zoomFilter are on). Our below pass walked <c>HasGUIOverlay</c> unconditionally,
    ///    so zoomed out the below level showed labels the current level had culled.
    ///    <see cref="BelowOverlayGroup"/> mirrors their choice. Their slider wins.
    ///
    /// 4. BLUEPRINT ALPHA (§74 vs their slider). <c>Mod_ToggleableOverlays.WriteSettings</c>
    ///    walks every blueprint ThingDef and writes <c>graphic.color.a = blueprintTransparency</c>,
    ///    and it runs BOTH from their <c>[StaticConstructorOnStartup]</c> and again every
    ///    single time their settings window closes. Rule 39: winning the last write cannot
    ///    settle a disagreement about the target, and mod load order decides who writes last
    ///    at startup anyway. So we do not race them - we ADOPT their number
    ///    (<see cref="ABBlueprintLook.ApplyAlpha"/>) from a postfix on that same method, which
    ///    makes their slider authoritative for AB2 plans too and makes the result independent
    ///    of load order.
    ///
    ///    ⚠ CONSEQUENCE, KNOWN AND ACCEPTED: their slider DEFAULTS TO 1.0, which is a no-op
    ///    in their world (vanilla blueprints carry their wash in graphicData, not here) but
    ///    for us means fully opaque plans - the §74.b "reads as already built" look. A TO user
    ///    who never opens TO's settings gets opaque AB2 blueprints. That is what "their slider
    ///    wins" means; change the one line in <see cref="SyncBlueprintAlpha"/> to
    ///    <c>Mathf.Min(theirs, ABBlueprintLook.DefaultBlueprintAlpha)</c> to reverse it.
    ///
    /// Member shape as of TO 1.6 (verified against the shipped ToggleableOverlays.dll):
    ///   ToggleableOverlays.ToggleableOverlaysUtility   - internal static class
    ///     .mousePositionX / .mousePositionZ            - public static int FIELDS
    ///     .mousePosition                               - public static IntVec3 FIELD
    ///     .quickView                                   - public static bool FIELD
    ///   ToggleableOverlays.ModSettings_ToggleableOverlays - public class, all settings are
    ///     public STATIC fields: hideForbidden, hideForbiddenBuildings, optimizedLister,
    ///     zoomFilter, blueprintTransparency
    ///   ToggleableOverlays.Mod_ToggleableOverlays.WriteSettings - public override, no args
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ToggleableOverlaysCompat
    {
        private static bool resolved;

        private static bool present;

        // --- ToggleableOverlaysUtility ---------------------------------------------------
        private static AccessTools.FieldRef<int> mouseX;

        private static AccessTools.FieldRef<int> mouseZ;

        private static AccessTools.FieldRef<IntVec3> mousePos;

        /// <summary>Their "hold the key to see everything" escape hatch. It short-circuits
        /// every gate they have to true, so it must short-circuit ours identically or the
        /// quick-view key would reveal the current level and not the one below it.</summary>
        private static AccessTools.FieldRef<bool> quickView;

        // --- ModSettings_ToggleableOverlays ----------------------------------------------
        private static AccessTools.FieldRef<bool> hideForbidden;

        private static AccessTools.FieldRef<bool> hideForbiddenBuildings;

        private static AccessTools.FieldRef<bool> optimizedLister;

        private static AccessTools.FieldRef<bool> zoomFilter;

        private static AccessTools.FieldRef<float> blueprintTransparency;

        // --- mouse substitution state ----------------------------------------------------
        /// <summary>Guards Pop against an unbalanced call and against nesting: the two draw
        /// passes that push are siblings, never nested, so a second push while one is live
        /// means something changed and the safe move is to leave the first one owning the
        /// restore.</summary>
        private static bool swapped;

        private static int savedX;

        private static int savedZ;

        private static IntVec3 savedPos;

        static ToggleableOverlaysCompat()
        {
            Resolve();
        }

        /// <summary>True when TO is loaded and its shape bound.</summary>
        internal static bool Present
        {
            get
            {
                if (!resolved)
                {
                    Resolve();
                }
                return present;
            }
        }

        private static void Resolve()
        {
            resolved = true;
            try
            {
                Type util = AccessTools.TypeByName("ToggleableOverlays.ToggleableOverlaysUtility");
                if (util == null)
                {
                    return; // mod absent: stay inert, log nothing (ghost-warning rule)
                }
                Type settings = AccessTools.TypeByName("ToggleableOverlays.ModSettings_ToggleableOverlays");
                mouseX = IntRef(util, "mousePositionX");
                mouseZ = IntRef(util, "mousePositionZ");
                mousePos = CellRef(util, "mousePosition");
                quickView = BoolRef(util, "quickView");
                hideForbidden = BoolRef(settings, "hideForbidden");
                hideForbiddenBuildings = BoolRef(settings, "hideForbiddenBuildings");
                optimizedLister = BoolRef(settings, "optimizedLister");
                zoomFilter = BoolRef(settings, "zoomFilter");
                blueprintTransparency = FloatRef(settings, "blueprintTransparency");

                // The mouse trio is the load-bearing part; everything else degrades to
                // "behave as if that toggle were off", which is the vanilla-AB2 behaviour.
                present = mouseX != null && mouseZ != null && mousePos != null;
                if (!present)
                {
                    // The mod IS loaded but its shape moved - say so once, because the
                    // symptom (no labels at all on any level below) is otherwise completely
                    // unattributable in the field.
                    Log.WarningOnce(ABLog.Tag + " Toggleable Overlays is loaded but its"
                        + " mouse-position fields were not found; overlays on levels below"
                        + " the one you are viewing will not appear on hover.", 0x2B10B0);
                    return;
                }
                InstallBlueprintAlphaSync();
                SyncBlueprintAlpha();
                ABLog.Dev("Toggleable Overlays bridge resolved.");
            }
            catch (Exception e)
            {
                present = false;
                Log.WarningOnce(ABLog.Tag + " Toggleable Overlays bridge failed to resolve: "
                    + e.Message, 0x2B10B1);
            }
        }

        private static AccessTools.FieldRef<int> IntRef(Type t, string name)
        {
            FieldInfo f = t == null ? null : AccessTools.Field(t, name);
            return f == null || f.FieldType != typeof(int)
                ? null
                : AccessTools.StaticFieldRefAccess<int>(f);
        }

        private static AccessTools.FieldRef<bool> BoolRef(Type t, string name)
        {
            FieldInfo f = t == null ? null : AccessTools.Field(t, name);
            return f == null || f.FieldType != typeof(bool)
                ? null
                : AccessTools.StaticFieldRefAccess<bool>(f);
        }

        private static AccessTools.FieldRef<float> FloatRef(Type t, string name)
        {
            FieldInfo f = t == null ? null : AccessTools.Field(t, name);
            return f == null || f.FieldType != typeof(float)
                ? null
                : AccessTools.StaticFieldRefAccess<float>(f);
        }

        private static AccessTools.FieldRef<IntVec3> CellRef(Type t, string name)
        {
            FieldInfo f = t == null ? null : AccessTools.Field(t, name);
            return f == null || f.FieldType != typeof(IntVec3)
                ? null
                : AccessTools.StaticFieldRefAccess<IntVec3>(f);
        }

        // ---------------------------------------------------------------------------------
        // 3. ZOOM CULLING
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Which lister group the below overlay pass should walk, mirroring TO's own
        /// <c>AlteredListByGroup</c>: <c>HasGUIOverlay</c> normally, <c>Pawn</c> only once
        /// their optimized lister AND zoom filter are on and the camera is above Closest.
        /// Falls back to the vanilla group whenever TO is absent or a setting failed to bind.
        /// </summary>
        internal static ThingRequestGroup BelowOverlayGroup
        {
            get
            {
                if (!resolved)
                {
                    Resolve();
                }
                if (!present || optimizedLister == null || zoomFilter == null
                    || !optimizedLister() || !zoomFilter())
                {
                    return ThingRequestGroup.HasGUIOverlay;
                }
                CameraDriver cam = Find.CameraDriver;
                return cam == null || cam.CurrentZoom == CameraZoomRange.Closest
                    ? ThingRequestGroup.HasGUIOverlay
                    : ThingRequestGroup.Pawn;
            }
        }

        // ---------------------------------------------------------------------------------
        // 1. THE MOUSE SUBSTITUTION
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Point TO's mouse cell at the cell the hovered column actually SHOWS, for the
        /// duration of a below draw pass. Always pair with <see cref="PopMouse"/> in a
        /// finally - an escaped exception that left their mouse parked on a below cell would
        /// hide every overlay on the CURRENT level until the cursor moved.
        ///
        /// The cursor cell is read from their own fields rather than recomputed from
        /// UI.MouseCell, so the substitution is guaranteed to be the same cell their gates
        /// would otherwise have compared against this frame (they truncate a ray origin; we
        /// would floor a projected point, and the two disagree off-map).
        /// </summary>
        internal static void PushBelowMouse(Map map, ABBandMap bands, int viewBand)
        {
            if (!resolved)
            {
                Resolve();
            }
            if (!present || swapped || map == null || bands == null)
            {
                return;
            }
            try
            {
                savedX = mouseX();
                savedZ = mouseZ();
                savedPos = mousePos();
                swapped = true;

                IntVec3 hovered = new IntVec3(savedX, 0, savedZ);
                IntVec3 target = IntVec3.Invalid;
                if (hovered.InBounds(map) && bands.BandOf(hovered) == viewBand
                    && ABBands.TryResolveVisibleBelow(map, bands, hovered,
                        out IntVec3 seen, out int drop)
                    && drop > 0)
                {
                    target = new IntVec3(seen.x, 0, seen.z);
                }
                // Off-map, an opaque column, or nothing below: park the mouse somewhere no
                // thing can be. Leaving the view-band value would be harmless (bands
                // partition z) but says something false, and rule 28 is about exactly that.
                mouseX() = target.x;
                mouseZ() = target.z;
                mousePos() = target;
            }
            catch
            {
                PopMouse(); // never leave their state half-written
            }
        }

        /// <summary>Restore. Idempotent and safe to call when no push happened.</summary>
        internal static void PopMouse()
        {
            if (!swapped)
            {
                return;
            }
            swapped = false;
            try
            {
                mouseX() = savedX;
                mouseZ() = savedZ;
                mousePos() = savedPos;
            }
            catch
            {
                // Nothing useful to do; their next Game.UpdatePlay postfix rewrites all three
                // unconditionally, so the worst case is one stale frame.
            }
        }

        // ---------------------------------------------------------------------------------
        // 2. FORBIDDEN MARKERS
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Should the below-band forbidden X be drawn? Mirrors TO's
        /// <c>Patch_OverlayDrawer_RenderForbiddenOverlay.CheckRenderForbiddenOverlay</c> and
        /// <c>Patch_OverlayDrawer_RenderForbiddenBigOverlay.Prefix</c>, evaluated against the
        /// substituted mouse cell - so this MUST be called between a
        /// <see cref="PushBelowMouse"/> and its <see cref="PopMouse"/>.
        ///
        /// Their settings are read LIVE rather than snapshotted, which is correct here even
        /// though their patches are gated by <c>Prepare()</c>: their WriteSettings does
        /// UnpatchAll + PatchAll on every settings write, so their own behaviour tracks the
        /// live values too.
        /// </summary>
        internal static bool AllowForbiddenBelow(Thing t)
        {
            if (!present || t == null)
            {
                return true;
            }
            try
            {
                if (quickView != null && quickView())
                {
                    return true; // their see-everything key
                }
                bool hideItems = hideForbidden != null && hideForbidden();
                bool hideBuildings = hideForbiddenBuildings != null && hideForbiddenBuildings();
                if (!hideItems && !hideBuildings)
                {
                    return true; // neither transpiler was installed: vanilla behaviour
                }
                // Their zoom gate is injected at the TOP of RenderForbiddenOverlay, so once
                // either setting is on it applies to every category, not just the hidden one.
                CameraDriver cam = Find.CameraDriver;
                if (cam == null || cam.CurrentZoom > CameraZoomRange.Middle)
                {
                    return false;
                }
                ThingCategory cat = t.def.category;
                if (cat == ThingCategory.Item)
                {
                    // sizeOne: true in their call - an item is compared cell-to-cell even if
                    // its def claims a bigger footprint.
                    return !hideItems || MouseOverCell(t.Position);
                }
                if (hideBuildings
                    && (cat == ThingCategory.Building || cat == ThingCategory.Ethereal))
                {
                    return MouseOverThing(t);
                }
                return true;
            }
            catch
            {
                return true; // a cosmetic gate must never suppress a marker on a throw
            }
        }

        private static bool MouseOverCell(IntVec3 c)
        {
            return c.x == mouseX() && c.z == mouseZ();
        }

        private static bool MouseOverThing(Thing t)
        {
            IntVec2 size = t.def.size;
            if (size.x == 1 && size.z == 1)
            {
                return MouseOverCell(t.Position);
            }
            return GenAdj.IsInside(mousePos(), t.Position, t.Rotation, size);
        }

        // ---------------------------------------------------------------------------------
        // 4. BLUEPRINT ALPHA
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Postfix their WriteSettings so the adoption survives both orders of
        /// <c>[StaticConstructorOnStartup]</c> AND every later trip through their settings
        /// window (which is when they re-run the def sweep and re-patch everything).
        /// </summary>
        private static void InstallBlueprintAlphaSync()
        {
            try
            {
                Type mod = AccessTools.TypeByName("ToggleableOverlays.Mod_ToggleableOverlays");
                MethodInfo target = mod == null ? null : AccessTools.Method(mod, "WriteSettings");
                if (target == null || blueprintTransparency == null)
                {
                    return; // nothing to adopt; §74's own alpha stands
                }
                HarmonyBoot.Harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(ToggleableOverlaysCompat),
                        nameof(SyncBlueprintAlpha)));
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Toggleable Overlays blueprint-alpha sync failed"
                    + " to install: " + e.Message, 0x2B10B2);
            }
        }

        /// <summary>Parameterless and foreign-type-free on purpose: this is a postfix on a
        /// method of a type this assembly must never name in a signature.</summary>
        private static void SyncBlueprintAlpha()
        {
            try
            {
                if (blueprintTransparency == null)
                {
                    return;
                }
                // Their slider wins outright (see the ⚠ in the class banner for what the
                // 1.0 default means for us).
                ABBlueprintLook.ApplyAlpha(Mathf.Clamp01(blueprintTransparency()));
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Toggleable Overlays blueprint-alpha sync threw: "
                    + e.Message, 0x2B10B3);
            }
        }
    }
}
