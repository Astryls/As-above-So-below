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
    [StaticConstructorOnStartup] // vanilla's static analyzer flags Texture2D statics
                                 // without it (run #497); ours load lazily on the main
                                 // thread from dev actions, so the attribute only
                                 // silences the warning - there is no ctor work.
    public static class ABWaterLab
    {
        public static bool ForceFlowBlack;

        public static bool ForceFlowNorth;

        public static bool ForceDepthBlack;

        /// <summary>§96 round 2: publish _MapSize as a SQUARE (x,x) instead of the full
        /// stack (x,z). By elimination (run #448: flow exonerated, depth alive, subcamera
        /// synced) the 1:4 aspect of the stacked map's _MapSize is the last divergent
        /// shader input - and the old "disproof" never tested a square value: both the
        /// full stack and the one-band republish were taller than wide.</summary>
        public static bool ForceSquareMapSize;

        /// <summary>Material-local delivery of the same square: Unity lets a material
        /// property SHADOW a global of the same name, but only when the shader declares
        /// the property - HasProperty says whether this delivery can work at all. If it
        /// does, the eventual fix can scope itself to water materials and leave every
        /// other _MapSize consumer untouched.</summary>
        private static bool waterMatsSquare;

        private static readonly System.Collections.Generic.HashSet<Material> waterMats =
            new System.Collections.Generic.HashSet<Material>();

        private static Texture2D flowBlackTex;

        private static Texture2D flowNorthTex;

        private static Texture2D depthBlackTex;

        public static bool AnyOverride => ForceFlowBlack || ForceFlowNorth || ForceDepthBlack
            || ForceSquareMapSize;

        public static void ApplyOverrides(WaterInfo wi)
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
            if (ForceSquareMapSize && wi?.map != null)
            {
                float x = wi.map.Size.x;
                Shader.SetGlobalVector(ShaderPropertyIDs.MapSize, new Vector4(x, x));
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

        [DebugAction("As above", "AB2: water lab - toggle _MapSize SQUARE (global)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleSquareMapSize()
        {
            ForceSquareMapSize = !ForceSquareMapSize;
            if (!ForceSquareMapSize)
            {
                // Hand the global straight back to vanilla's value rather than waiting a
                // frame for SetTextures to republish it.
                Map map = Find.CurrentMap;
                if (map != null)
                {
                    Shader.SetGlobalVector(ShaderPropertyIDs.MapSize,
                        new Vector4(map.Size.x, map.Size.z));
                }
            }
            Messages.Message("AB2 water lab: _MapSize " + (ForceSquareMapSize
                    ? "FORCED SQUARE (x,x) - watch fog/edge effects for collateral too"
                    : "vanilla (x,z full stack)"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: water lab - toggle _MapSize SQUARE (water mats only)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleSquareWaterMats()
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null)
                {
                    return;
                }
                if (waterMats.Count == 0)
                {
                    foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading)
                    {
                        if (t == null || !t.IsWater)
                        {
                            continue;
                        }
                        try
                        {
                            if (t.DrawMatSingle != null)
                            {
                                waterMats.Add(t.DrawMatSingle);
                            }
                        }
                        catch (Exception)
                        {
                            // a def with no graphic; irrelevant
                        }
                        if (t.waterDepthMaterial != null)
                        {
                            waterMats.Add(t.waterDepthMaterial);
                        }
                    }
                }
                waterMatsSquare = !waterMatsSquare;
                Vector4 v = waterMatsSquare
                    ? new Vector4(map.Size.x, map.Size.x)
                    : new Vector4(map.Size.x, map.Size.z);
                int hasProp = 0;
                foreach (Material m in waterMats)
                {
                    if (m.HasProperty(ShaderPropertyIDs.MapSize))
                    {
                        hasProp++;
                    }
                    m.SetVector(ShaderPropertyIDs.MapSize, v);
                }
                Messages.Message("AB2 water lab: water mats _MapSize "
                    + (waterMatsSquare ? "SQUARE" : "full-stack") + " on " + waterMats.Count
                    + " material(s); " + hasProp + " declare the property (0 = shadowing"
                    + " cannot work, use the GLOBAL toggle)",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " water lab mats toggle threw: " + e);
            }
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
                    + " flowNorth=" + ForceFlowNorth + " depthBlack=" + ForceDepthBlack
                    + " squareMapSize=" + ForceSquareMapSize
                    + " waterMatsSquare=" + waterMatsSquare);
                if (map != null)
                {
                    int withProp = 0;
                    int total = 0;
                    foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading)
                    {
                        if (t == null || !t.IsWater)
                        {
                            continue;
                        }
                        Material m = null;
                        try { m = t.DrawMatSingle; } catch (Exception) { }
                        if (m != null)
                        {
                            total++;
                            bool has = m.HasProperty(ShaderPropertyIDs.MapSize);
                            if (has) { withProp++; }
                            sb.AppendLine("  waterMat " + t.defName + ": shader="
                                + (m.shader != null ? m.shader.name : "NULL")
                                + " hasMapSizeProp=" + has);
                        }
                    }
                    sb.AppendLine("water materials with _MapSize property: " + withProp
                        + " of " + total);
                }
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
        private static void Postfix(WaterInfo __instance)
        {
            if (ABWaterLab.AnyOverride)
            {
                ABWaterLab.ApplyOverrides(__instance);
            }
        }
    }
}
