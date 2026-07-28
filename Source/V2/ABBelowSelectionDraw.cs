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

        private static void Postfix()
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
                CellRect below = Find.CameraDriver.CurrentViewRect
                    .MovedBy(new IntVec3(0, 0, -bands.Slot));
                below.ClipInsideMap(map);
                FogGrid fog = map.fogGrid;
                TerrainGrid terrain = map.terrainGrid;

                foreach (IntVec3 c in below)
                {
                    if (!c.InBounds(map) || fog.IsFogged(c))
                    {
                        continue;
                    }
                    IntVec3 above = bands.Translate(c, viewBand);
                    if (!above.InBounds(map) || terrain.TerrainAt(above) != ABDefOf.AB_OpenAir)
                    {
                        continue;
                    }
                    List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (t == null || t.Position != c || !t.IsForbidden(Faction.OfPlayer))
                        {
                            continue;
                        }
                        Vector3 pos = t.DrawPos;
                        pos.z += (viewBand - bands.BandOf(c)) * bands.Slot;
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
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below forbidden overlay");
            }
        }
    }
}
