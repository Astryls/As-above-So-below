using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 world-space UI for the band below: selection brackets, path lines and forbidden
    /// markers.
    ///
    /// Same root cause as every other geometry item - these draw at the thing's REAL world
    /// position, which for a thing one band down is a Slot away, so they render far off
    /// screen and simply appear "missing" while the thing itself is plainly visible through
    /// the floor.
    ///
    /// NOT solved by patching Thing.DrawPos globally, which would be the obvious universal
    /// lever: the see-below renderer prints below things at their real positions and then
    /// translates the emitted vertices, so a pre-localized DrawPos would double-shift
    /// everything it draws. Each UI surface is therefore corrected at its own draw site.
    /// </summary>
    public static class ABBelowSelectionDraw
    {
        /// <summary>Shift a world position up into the current view band, when the thing is
        /// below and genuinely visible through open air.</summary>
        public static bool TryLocalize(Map map, Vector3 world, out Vector3 localized)
        {
            return ABBelowOverlays.TryLocalizeToView(map, world, out localized);
        }
    }

    /// <summary>
    /// Selection brackets. Patched on the shared utility rather than SelectionDrawer, so
    /// pawns, items and buildings are all covered by one interception.
    ///
    /// The method is generic; the closed form actually used is resolved explicitly, since
    /// Harmony cannot infer which instantiation to patch.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SelectionBrackets_ABBelow
    {
        private static MethodBase TargetMethod()
        {
            MethodInfo open = AccessTools.Method(typeof(SelectionDrawerUtility),
                "CalculateSelectionBracketPositionsWorld");
            return open?.MakeGenericMethod(typeof(object));
        }

        private static void Prefix(ref Vector3 worldPos)
        {
            try
            {
                if (ABBelowSelectionDraw.TryLocalize(Find.CurrentMap, worldPos, out Vector3 local))
                {
                    worldPos = local;
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// The path line. Drawn node by node from the pawn's current path; when the pawn is a
    /// band below the camera the whole polyline sits off screen, so the route "disappears
    /// across levels".
    ///
    /// Replaced rather than corrected afterwards because the line is a chain of segments -
    /// every node has to be localized before the polyline is built.
    /// </summary>
    [HarmonyPatch(typeof(PawnPath), nameof(PawnPath.DrawPath))]
    public static class Patch_PawnPath_ABBelowPathLine
    {
        private static bool Prefix(PawnPath __instance, Pawn pathingPawn)
        {
            try
            {
                if (pathingPawn == null || !pathingPawn.Spawned || !__instance.Found)
                {
                    return true;
                }
                Map map = pathingPawn.Map;
                if (!ABBelowSelectionDraw.TryLocalize(map, pathingPawn.DrawPos, out Vector3 _))
                {
                    return true; // pawn is on the viewed band: vanilla
                }
                int left = __instance.NodesLeftCount;
                if (left <= 0)
                {
                    return false;
                }
                float y = AltitudeLayer.Item.AltitudeFor();
                for (int i = 0; i < left - 1; i++)
                {
                    Vector3 a = Localized(map, __instance.Peek(i).ToVector3Shifted(), y);
                    Vector3 b = Localized(map, __instance.Peek(i + 1).ToVector3Shifted(), y);
                    GenDraw.DrawLineBetween(a, b);
                }
                Vector3 from = pathingPawn.DrawPos;
                if (ABBelowSelectionDraw.TryLocalize(map, from, out Vector3 fromLocal))
                {
                    from = fromLocal;
                }
                from.y = y;
                Vector3 first = Localized(map, __instance.Peek(0).ToVector3Shifted(), y);
                if ((from - first).sqrMagnitude > 0.01f)
                {
                    GenDraw.DrawLineBetween(from, first);
                }
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: below path line threw: " + e, 762195883);
                return true;
            }
        }

        private static Vector3 Localized(Map map, Vector3 v, float y)
        {
            if (ABBelowSelectionDraw.TryLocalize(map, v, out Vector3 local))
            {
                v = local;
            }
            v.y = y;
            return v;
        }
    }

    /// <summary>
    /// Forbidden markers for the band below.
    ///
    /// OverlayDrawer builds these straight into a draw batch from the thing's own position,
    /// with no seam to redirect, so rather than fight it the marker is simply drawn again at
    /// the localized position. Vanilla's copy still renders off screen where nobody sees it,
    /// which costs nothing and keeps this purely additive.
    /// </summary>
    [StaticConstructorOnStartup]
    [HarmonyPatch(typeof(OverlayDrawer), nameof(OverlayDrawer.DrawAllOverlays))]
    public static class Patch_OverlayDrawer_ABBelowForbidden
    {
        private static readonly Material ForbiddenMat =
            MaterialPool.MatFrom("Things/Special/ForbiddenOverlay", ShaderDatabase.MetaOverlay);

        /// <summary>Vanilla's OWN registry of things carrying a persistent overlay.
        ///
        /// CompForbiddable.UpdateOverlayHandle registers through overlayDrawer.Enable, which
        /// lands in this dictionary, and DrawAllOverlays does NOT clear it (only the transient
        /// overlaysToDraw is cleared) - so a postfix reads it safely.
        ///
        /// This replaced a full per-cell sweep of the translated view rect, which measured at
        /// 1.41 ms EVERY FRAME - 74% of the entire mod's profiled cost. The old loop paid a
        /// ThingsListAtFast fetch for every open-air cell on screen, which is the worst case
        /// precisely when the feature matters (standing in the sky band looking down at open
        /// surface, where almost no cell rejects early). Iterating the handful of things that
        /// actually HAVE an overlay is the same work vanilla itself does one line earlier.</summary>
        private static readonly AccessTools.FieldRef<OverlayDrawer, Dictionary<Thing, ThingOverlaysHandle>>
            HandlesRef = AccessTools.FieldRefAccess<OverlayDrawer, Dictionary<Thing, ThingOverlaysHandle>>(
                "overlayHandles");

        /// <summary>Cheap pre-filter. The authoritative predicate stays IsForbidden below, so
        /// the drawn result is bit-identical to the old sweep.</summary>
        private const OverlayTypes ForbiddenAny =
            OverlayTypes.Forbidden | OverlayTypes.ForbiddenBig;

        private static void Postfix(OverlayDrawer __instance)
        {
            try
            {
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                int viewBand = ABBandView.CurrentBand(map);
                if (viewBand <= 0)
                {
                    return;
                }
                // The view rect in the VIEWING band; visibility of anything under it is
                // resolved per column. Shifting the rect down one Slot only ever found the
                // level directly below, so interaction icons vanished from level 2 upward.
                CellRect view = Find.CameraDriver.CurrentViewRect;
                Dictionary<Thing, ThingOverlaysHandle> handles = HandlesRef(__instance);
                if (handles == null || handles.Count == 0)
                {
                    return;
                }
                FogGrid fog = map.fogGrid;
                TerrainGrid terrain = map.terrainGrid;

                // Dictionary foreach uses a struct enumerator: no allocation on this per-frame path.
                foreach (KeyValuePair<Thing, ThingOverlaysHandle> entry in handles)
                {
                    Thing t = entry.Key;
                    if (t == null || !t.Spawned || t.Map != map)
                    {
                        continue;
                    }
                    if (entry.Value == null
                        || (entry.Value.OverlayTypes & ForbiddenAny) == OverlayTypes.None)
                    {
                        continue;
                    }
                    IntVec3 c = t.Position;
                    int band = bands.BandOf(c);
                    if (band < 0 || band >= viewBand || fog.IsFogged(c))
                    {
                        continue;
                    }
                    IntVec3 above = bands.Translate(c, viewBand);
                    if (!above.InBounds(map) || !view.Contains(above))
                    {
                        continue; // off screen
                    }
                    if (!ABBands.TryResolveVisibleBelow(map, bands, above,
                            out IntVec3 seen, out int drop)
                        || seen.x != c.x || seen.z != c.z)
                    {
                        continue; // not what this column shows
                    }
                    if (!t.IsForbidden(Faction.OfPlayer))
                    {
                        continue;
                    }
                    Vector3 pos = t.DrawPos;
                    pos.z += drop;
                    if (t.RotatedSize.z == 1)
                    {
                        pos.z -= 0.3f;
                    }
                    else
                    {
                        pos.z -= t.RotatedSize.z * 0.3f;
                    }
                    pos.y = AltitudeLayer.MetaOverlays.AltitudeFor();
                    Graphics.DrawMesh(MeshPool.plane05,
                        Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one), ForbiddenMat, 0);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below forbidden overlay");
            }
        }
    }
}
