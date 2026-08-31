using System;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §96 WATER LAB - live A/B overrides for the two-pass water pipeline.
    ///
    /// Field report (w17): banded water does not match vanilla and reads as VERTICAL
    /// streaks - on a LAKE. This file's neighbour (ABWaterV2) already records one disproved
    /// theory (_MapSize republishing - vanilla's full-stack value is CORRECT) and one
    /// unverified one (the ghost river, which the anchoring fixes were expected to remove -
    /// the field says the symptom survived them, rule 17). The compiled Map/TerrainWater
    /// shader cannot be read from here, so instead of a third theory this is an INSTRUMENT:
    /// each toggle overrides exactly one shader input, and the look of the water under each
    /// override is a fingerprint that names the mechanism (rule 37 - name the enforcement
    /// point before naming the symptom).
    ///
    /// THE DECISION TREE:
    ///   - "flow OFF (black)" kills every flow influence, texture and uv-math alike.
    ///     Verticality SURVIVES it -> flow is fully exonerated in one move.
    ///     Verticality DIES -> ghost flow data confirmed; the fix is data hygiene.
    ///   - "flow NORTH" forces a uniform (0,1) flow: calibrates what flow-driven
    ///     verticality LOOKS like against the user's screenshot.
    ///   - "depth OFF (black)" blanks the WaterDepth subcamera's contribution: calibrates
    ///     what DEAD-DEPTH water looks like. If that matches the broken look, the depth
    ///     pass is not reaching those cells and the subcamera/section side is the suspect.
    ///   - The report prints the per-band BASE-river census (rule 21: our carve rewrites
    ///     the TOP grid; a sky-band ghost river surviving in the BASE grid is exactly what
    ///     SetTextures' `BaseTerrainAt(c).IsRiver` pixel filter would resurrect), the
    ///     subcamera sync deltas, and the live shader globals.
    ///
    /// The overrides are applied in a postfix on WaterInfo.SetTextures because vanilla
    /// republishes the globals EVERY frame from Map.MapUpdate - a one-shot SetGlobalTexture
    /// from a dev action would be overwritten one frame later. Session-local, default off,
    /// never scribed; the postfix costs two static bool reads per frame when idle.
    /// </summary>
    public static class ABWaterLab
    {
        public static bool ForceFlowBlack;

        public static bool ForceFlowNorth;

        public static bool ForceDepthBlack;

        private static Texture2D flowBlackTex;

        private static Texture2D flowNorthTex;

        private static Texture2D depthBlackTex;

        public static bool AnyOverride => ForceFlowBlack || ForceFlowNorth || ForceDepthBlack;

        public static void ApplyOverrides()
        {
            EnsureTextures();
            if (ForceFlowBlack)
            {
                Shader.SetGlobalTexture(ShaderPropertyIDs.WaterOffsetTex, flowBlackTex);
            }
            else if (ForceFlowNorth)
            {
                Shader.SetGlobalTexture(ShaderPropertyIDs.WaterOffsetTex, flowNorthTex);
            }
            if (ForceDepthBlack)
            {
                Shader.SetGlobalTexture(ShaderPropertyIDs.WaterOutputTex, depthBlackTex);
            }
        }

        private static void EnsureTextures()
        {
            if (flowBlackTex != null)
            {
                return;
            }
            // Same format family as vanilla's riverFlowTexture (RGFloat, clamped): the
            // shader reads (r,g) as the flow vector, so black = no flow anywhere and
            // (0,1) = everything flows due north at river strength.
            flowBlackTex = MakeTex(TextureFormat.RGFloat, new Color(0f, 0f, 0f));
            flowNorthTex = MakeTex(TextureFormat.RGFloat, new Color(0f, 1f, 0f));
            depthBlackTex = MakeTex(TextureFormat.ARGB32, new Color(0f, 0f, 0f, 0f));
        }

        private static Texture2D MakeTex(TextureFormat format, Color c)
        {
            Texture2D t = new Texture2D(1, 1, format, mipChain: false);
            t.wrapMode = TextureWrapMode.Repeat;
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------ dev actions

        [DebugAction("As above", "AB2: water lab - toggle flow OFF (black)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleFlowBlack()
        {
            ForceFlowBlack = !ForceFlowBlack;
            if (ForceFlowBlack)
            {
                ForceFlowNorth = false;
            }
            Messages.Message("AB2 water lab: flow " + (ForceFlowBlack ? "FORCED BLACK" : "vanilla"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: water lab - toggle flow NORTH",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleFlowNorth()
        {
            ForceFlowNorth = !ForceFlowNorth;
            if (ForceFlowNorth)
            {
                ForceFlowBlack = false;
            }
            Messages.Message("AB2 water lab: flow " + (ForceFlowNorth ? "FORCED NORTH (0,1)" : "vanilla"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: water lab - toggle depth OFF (black)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleDepthBlack()
        {
            ForceDepthBlack = !ForceDepthBlack;
            Messages.Message("AB2 water lab: depth " + (ForceDepthBlack ? "FORCED BLACK" : "vanilla"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: water lab - paint test pond",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void PaintTestPond()
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                IntVec3 center = Find.CameraDriver.MapPosition;
                int painted = 0;
                for (int dx = -8; dx <= 8; dx++)
                {
                    for (int dz = -8; dz <= 8; dz++)
                    {
                        IntVec3 c = new IntVec3(center.x + dx, 0, center.z + dz);
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > 8f || !c.InBounds(map))
                        {
                            continue;
                        }
                        if (bands != null && bands.Banded && bands.InGutter(c))
                        {
                            continue;
                        }
                        map.terrainGrid.SetTerrain(c,
                            d <= 5f ? TerrainDefOf.WaterDeep : TerrainDefOf.WaterShallow);
                        painted++;
                    }
                }
                Messages.Message("AB2 water lab: painted " + painted + " water cells at "
                    + center + ".", MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " water lab pond threw: " + e);
            }
        }

        [DebugAction("As above", "AB2: water lab - report+",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ReportPlus()
        {
            try
            {
                Map map = Find.CurrentMap;
                StringBuilder sb = new StringBuilder();
                sb.Append(ABWaterBand.Report(map));
                sb.AppendLine("--- lab state ---");
                sb.AppendLine("overrides: flowBlack=" + ForceFlowBlack
                    + " flowNorth=" + ForceFlowNorth + " depthBlack=" + ForceDepthBlack);
                sb.AppendLine("drawTerrainWater=" + DebugViewSettings.drawTerrainWater
                    + " belowWaterPass=" + ABV2Debug.DrawBelowWater);
                ABBandMap bands = map != null ? ABBands.CompOf(map) : null;
                if (bands != null && bands.Banded)
                {
                    sb.AppendLine("view band=" + ABBandView.CurrentBand(map)
                        + " level=" + ABBandView.CurrentLevel(map));
                }
                Texture flow = Shader.GetGlobalTexture(ShaderPropertyIDs.WaterOffsetTex);
                sb.AppendLine("global _WaterOffsetTex: " + (flow == null ? "NULL (shader default)"
                    : flow.width + "x" + flow.height + " '" + flow.name + "'"));
                Camera main = Find.Camera;
                Camera sub = Current.SubcameraDriver != null
                    ? Current.SubcameraDriver.GetSubcamera(SubcameraDefOf.WaterDepth) : null;
                if (sub == null)
                {
                    sb.AppendLine("WaterDepth subcamera: NULL");
                }
                else
                {
                    Vector3 dp = sub.transform.position - main.transform.position;
                    sb.AppendLine("WaterDepth subcamera: enabled=" + sub.enabled
                        + " posDelta=" + dp.ToString("F3")
                        + " orthoDelta=" + (sub.orthographicSize - main.orthographicSize).ToString("F3")
                        + " rt=" + (sub.targetTexture == null ? "NULL"
                            : sub.targetTexture.width + "x" + sub.targetTexture.height)
                        + " screen=" + Screen.width + "x" + Screen.height);
                }
                Log.Warning(ABLog.Tag + " V2 water lab report:\n" + sb);
                Messages.Message("AB2: water lab report written to log.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " water lab report threw: " + e);
            }
        }
    }

    /// <summary>Vanilla republishes the water globals every frame from Map.MapUpdate; the
    /// lab must win that race, so it re-overrides in a postfix. Two static bool reads per
    /// frame when idle.</summary>
    [HarmonyPatch(typeof(WaterInfo), nameof(WaterInfo.SetTextures))]
    public static class Patch_WaterInfo_ABWaterLab
    {
        private static void Postfix()
        {
            if (ABWaterLab.AnyOverride)
            {
                ABWaterLab.ApplyOverrides();
            }
        }
    }
}
