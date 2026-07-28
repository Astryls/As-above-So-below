using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Shader / material probe.
    ///
    /// Unity substitutes its ERROR SHADER - a flat magenta-red fill - when a material's
    /// shader fails to resolve or is unsupported on the current target. Crucially it does
    /// this SILENTLY: unlike a null material (which logs "Material is null"), an
    /// unsupported shader produces no log line at all, which is exactly why a red region
    /// can appear on screen with a completely clean Player.log.
    ///
    /// That silence is what makes this probe necessary. Rather than guess which layer owns
    /// the red area, this walks every material this mod can draw with and reports whether
    /// its shader actually exists and is supported. A broken one names itself.
    ///
    /// The existing check in SectionLayer_ABBelowShadows (MatBases.SunShadow.shader
    /// .isSupported) shows this class of failure has bitten this codebase before.
    /// </summary>
    public static class ABDevToolsV2Shaders
    {
        [DebugAction("As above", "AB2: shader report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ShaderReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Unity renders a flat magenta/red fill when a shader is missing or");
            sb.AppendLine("unsupported, and does NOT log it. Any line below marked BAD is a");
            sb.AppendLine("candidate for a red region on screen.");
            sb.AppendLine();

            Report(sb, "ShaderDatabase.SolidColorBehind (see-below base)", ShaderDatabase.SolidColorBehind);
            Report(sb, "ShaderDatabase.TerrainHard", ShaderDatabase.TerrainHard);
            Report(sb, "ShaderDatabase.Cutout", ShaderDatabase.Cutout);
            Report(sb, "ShaderDatabase.VertexColor (mountain skirt)", ShaderDatabase.VertexColor);
            Report(sb, "ShaderDatabase.MetaOverlay", ShaderDatabase.MetaOverlay);
            Report(sb, "ShaderDatabase.Transparent", ShaderDatabase.Transparent);

            sb.AppendLine();
            ReportMat(sb, "MatBases.SunShadow", MatBases.SunShadow);
            ReportMat(sb, "MatBases.LightOverlay", MatBases.LightOverlay);

            // The mountain cap clones a material per atlas submaterial and forces its render
            // queue. A clone inherits its source's shader, so a bad source produces a bad
            // clone - and the clone is what actually gets drawn.
            sb.AppendLine();
            sb.AppendLine("Rock materials on this tile (mountain cap draws clones of these):");
            Map map = Find.CurrentMap;
            if (map != null && Find.World != null)
            {
                List<ThingDef> rocks = Find.World.NaturalRockTypesIn(map.Tile) is var r && r != null
                    ? new List<ThingDef>(r) : new List<ThingDef>();
                if (rocks.Count == 0)
                {
                    sb.AppendLine("  (none reported for this tile)");
                }
                for (int i = 0; i < rocks.Count; i++)
                {
                    ThingDef rock = rocks[i];
                    Graphic g = rock?.graphic;
                    Material m = g?.MatSingle;
                    ReportMat(sb, "  " + (rock?.defName ?? "?")
                        + " [" + (g?.GetType().Name ?? "no graphic") + "]", m);
                }
            }

            Log.Warning(ABLog.Tag + " V2 shader report:\n" + sb);
            Messages.Message("AB2: shader report written to log.", MessageTypeDefOf.TaskCompletion, false);
        }

        private static void Report(StringBuilder sb, string label, Shader s)
        {
            if (s == null)
            {
                sb.AppendLine("  BAD  " + label + " = NULL SHADER");
                return;
            }
            sb.AppendLine("  " + (s.isSupported ? "ok   " : "BAD  ") + label
                + " = " + s.name + (s.isSupported ? "" : "  <-- NOT SUPPORTED"));
        }

        private static void ReportMat(StringBuilder sb, string label, Material m)
        {
            if (m == null)
            {
                sb.AppendLine("  BAD  " + label + " = NULL MATERIAL");
                return;
            }
            Shader s = m.shader;
            if (s == null)
            {
                sb.AppendLine("  BAD  " + label + " = material with NULL shader");
                return;
            }
            // "Hidden/InternalErrorShader" is the exact name Unity swaps in, so seeing it
            // here identifies the offender beyond argument.
            bool bad = !s.isSupported || s.name.Contains("InternalErrorShader");
            sb.AppendLine("  " + (bad ? "BAD  " : "ok   ") + label + " = " + s.name
                + " queue=" + m.renderQueue + (bad ? "  <-- THIS DRAWS AS RED" : ""));
        }
    }
}
