using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// LIVE TEXTURE TUNER. Adjust drawSize, per-rotation offsets and draw altitude on any of
    /// our buildings while looking at it, then copy the finished XML out.
    ///
    /// This exists because the alternative is edit-def / rebuild / relaunch / rebuild-colony
    /// for every quarter-cell nudge, on thirty-odd buildings that each have four rotations.
    /// Tuning by eye is inherently iterative; the loop has to be seconds, not minutes.
    ///
    /// ⚠ CHANGING `graphicData` AT RUNTIME DOES NOTHING ON ITS OWN - THE GRAPHIC IS CACHED
    /// TWICE. `GraphicData.Graphic` memoises into a private `cachedGraphic`, and
    /// `ThingDef.graphic` is baked once at PostLoad and never looked at again. Writing a new
    /// drawSize updates neither, so the sprite does not move and the value looks ignored.
    /// `Apply` clears both, then dirties the map mesh for every spawned instance - buildings
    /// are MapMeshOnly, so without that last step nothing redraws until the section happens
    /// to regenerate for some other reason.
    ///
    /// Values are edited on the LIVE def, so they affect every instance immediately and are
    /// lost on quit. That is deliberate: this is a measuring instrument, not a settings
    /// panel. Copy the XML out and bake it into the def.
    /// </summary>
    public class Window_ABTextureTuner : Window
    {
        private static readonly FieldInfo CachedGraphic =
            AccessTools.Field(typeof(GraphicData), "cachedGraphic");

        private readonly List<ThingDef> defs = new List<ThingDef>();

        private readonly HashSet<ThingDef> touched = new HashSet<ThingDef>();

        private int index;

        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(560f, 720f);

        public Window_ABTextureTuner()
        {
            // Non-modal on purpose: the whole point is to watch the building change while
            // dragging a slider, which means the map must stay visible and interactive.
            draggable = true;
            resizeable = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;

            // Everything of ours that draws: risers first (30), then the links.
            foreach (ThingDef d in ABRiserDefs.All)
            {
                defs.Add(d);
            }
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.graphicData != null && d.defName.StartsWith("AB2_") && !defs.Contains(d))
                {
                    defs.Add(d);
                }
            }
            defs.SortBy(d => d.defName);
        }

        private ThingDef Cur => defs.Count > 0 ? defs[Mathf.Clamp(index, 0, defs.Count - 1)] : null;

        public override void DoWindowContents(Rect inRect)
        {
            ThingDef def = Cur;
            if (def == null)
            {
                Widgets.Label(inRect, "No tunable defs found.");
                return;
            }

            Rect view = new Rect(0f, 0f, inRect.width - 20f, 940f);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            Listing_Standard l = new Listing_Standard();
            l.Begin(view);

            // ---- def picker
            Rect head = l.GetRect(30f);
            if (Widgets.ButtonText(head.LeftPart(0.15f), "<"))
            {
                index = (index - 1 + defs.Count) % defs.Count;
            }
            if (Widgets.ButtonText(head.RightPart(0.15f), ">"))
            {
                index = (index + 1) % defs.Count;
            }
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(head.MiddlePart(0.68f, 1f),
                (index + 1) + "/" + defs.Count + "  " + def.defName);
            Text.Anchor = TextAnchor.UpperLeft;
            l.Gap(4f);
            if (touched.Contains(def))
            {
                GUI.color = new Color(0.4f, 0.9f, 0.5f);
                l.Label("  modified");
                GUI.color = Color.white;
            }
            l.GapLine(6f);

            GraphicData g = def.graphicData;
            bool dirty = false;

            // ---- scale
            l.Label("SCALE  drawSize " + Fmt2(g.drawSize));
            float sx = l.Slider(g.drawSize.x, 0.2f, 4f);
            float sy = l.Slider(g.drawSize.y, 0.2f, 4f);
            if (!Mathf.Approximately(sx, g.drawSize.x) || !Mathf.Approximately(sy, g.drawSize.y))
            {
                g.drawSize = new Vector2(Snap(sx), Snap(sy));
                dirty = true;
            }
            if (l.ButtonText("Lock Y to X (square)"))
            {
                g.drawSize = new Vector2(g.drawSize.x, g.drawSize.x);
                dirty = true;
            }
            l.GapLine(6f);

            // ---- base offset
            l.Label("BASE OFFSET  drawOffset " + Fmt3(g.drawOffset));
            float bx = l.Slider(g.drawOffset.x, -2f, 2f);
            float bz = l.Slider(g.drawOffset.z, -2f, 2f);
            if (!Mathf.Approximately(bx, g.drawOffset.x) || !Mathf.Approximately(bz, g.drawOffset.z))
            {
                g.drawOffset = new Vector3(Snap(bx), 0f, Snap(bz));
                dirty = true;
            }
            l.GapLine(6f);

            // ---- per-rotation offsets
            l.Label("PER-ROTATION OFFSET  (null = inherit base)");
            dirty |= RotRow(l, "north", ref g.drawOffsetNorth);
            dirty |= RotRow(l, "east ", ref g.drawOffsetEast);
            dirty |= RotRow(l, "south", ref g.drawOffsetSouth);
            dirty |= RotRow(l, "west ", ref g.drawOffsetWest);
            l.GapLine(6f);

            // ---- draw altitude (the "overdraw" question: what covers what)
            l.Label("DRAW ALTITUDE  altitudeLayer = " + def.altitudeLayer);
            Rect alt = l.GetRect(30f);
            Array layers = Enum.GetValues(typeof(AltitudeLayer));
            int ai = Array.IndexOf(layers, def.altitudeLayer);
            if (Widgets.ButtonText(alt.LeftHalf(), "< lower") && ai > 0)
            {
                def.altitudeLayer = (AltitudeLayer)layers.GetValue(ai - 1);
                dirty = true;
            }
            if (Widgets.ButtonText(alt.RightHalf(), "higher >") && ai < layers.Length - 1)
            {
                def.altitudeLayer = (AltitudeLayer)layers.GetValue(ai + 1);
                dirty = true;
            }
            l.GapLine(6f);

            // ---- output
            if (l.ButtonText("Copy XML for " + def.defName))
            {
                GUIUtility.systemCopyBuffer = XmlFor(def);
                Messages.Message("Copied " + def.defName + " graphicData.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            if (l.ButtonText("Copy XML for ALL modified (" + touched.Count + ")"))
            {
                StringBuilder sb = new StringBuilder();
                foreach (ThingDef d in defs)
                {
                    if (touched.Contains(d))
                    {
                        sb.AppendLine(XmlFor(d)).AppendLine();
                    }
                }
                GUIUtility.systemCopyBuffer = sb.ToString();
                Messages.Message("Copied " + touched.Count + " defs.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            l.Gap(6f);
            l.Label("Values are live on the def and are LOST ON QUIT. Copy before closing.");

            l.End();
            Widgets.EndScrollView();

            if (dirty)
            {
                touched.Add(def);
                Apply(def);
            }
        }

        /// <summary>One nullable per-rotation row. The enable toggle matters: a null offset
        /// inherits the base one, which is a different thing from an explicit zero, and the
        /// XML has to reflect which was meant.</summary>
        private static bool RotRow(Listing_Standard l, string label, ref Vector3? v)
        {
            bool dirty = false;
            Rect r = l.GetRect(26f);
            bool on = v.HasValue;
            bool was = on;
            Widgets.CheckboxLabeled(r.LeftPart(0.32f), "  " + label, ref on);
            if (on != was)
            {
                v = on ? (Vector3?)Vector3.zero : null;
                return true;
            }
            if (!v.HasValue)
            {
                return false;
            }
            Vector3 cur = v.Value;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(r.RightPart(0.66f), Fmt3(cur));
            Text.Anchor = TextAnchor.UpperLeft;
            float x = l.Slider(cur.x, -2f, 2f);
            float z = l.Slider(cur.z, -2f, 2f);
            if (!Mathf.Approximately(x, cur.x) || !Mathf.Approximately(z, cur.z))
            {
                v = new Vector3(Snap(x), 0f, Snap(z));
                dirty = true;
            }
            return dirty;
        }

        /// <summary>Round to 0.05 so the copied XML is a number a human would have typed.</summary>
        private static float Snap(float f)
        {
            return Mathf.Round(f * 20f) / 20f;
        }

        private static string Fmt2(Vector2 v)
        {
            return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")";
        }

        private static string Fmt3(Vector3 v)
        {
            return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + "," + v.z.ToString("0.##") + ")";
        }

        private static string XmlFor(ThingDef def)
        {
            GraphicData g = def.graphicData;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!-- " + def.defName + " -->");
            sb.AppendLine("<altitudeLayer>" + def.altitudeLayer + "</altitudeLayer>");
            sb.AppendLine("<graphicData>");
            sb.AppendLine("  <drawSize>" + Fmt2(g.drawSize) + "</drawSize>");
            if (g.drawOffset != Vector3.zero)
            {
                sb.AppendLine("  <drawOffset>" + Fmt3(g.drawOffset) + "</drawOffset>");
            }
            Emit(sb, "drawOffsetNorth", g.drawOffsetNorth);
            Emit(sb, "drawOffsetEast", g.drawOffsetEast);
            Emit(sb, "drawOffsetSouth", g.drawOffsetSouth);
            Emit(sb, "drawOffsetWest", g.drawOffsetWest);
            sb.AppendLine("</graphicData>");
            return sb.ToString().TrimEnd();
        }

        private static void Emit(StringBuilder sb, string tag, Vector3? v)
        {
            if (v.HasValue)
            {
                sb.AppendLine("  <" + tag + ">" + Fmt3(v.Value) + "</" + tag + ">");
            }
        }

        /// <summary>
        /// Push a graphicData edit through both caches and force a redraw.
        ///
        /// ⚠ ALL THREE STEPS ARE REQUIRED. Clearing only `cachedGraphic` leaves the stale
        /// `ThingDef.graphic` in place; refreshing only that leaves the memoised one; and
        /// doing both without dirtying the mesh changes nothing on screen, because these
        /// buildings are MapMeshOnly and their vertices are already baked into a section.
        /// </summary>
        public static void Apply(ThingDef def)
        {
            try
            {
                CachedGraphic?.SetValue(def.graphicData, null);
                FieldInfo gf = AccessTools.Field(def.GetType(), "graphic")
                    ?? AccessTools.Field(typeof(BuildableDef), "graphic");
                gf?.SetValue(def, def.graphicData.Graphic);

                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    List<Thing> things = maps[m].listerThings.ThingsOfDef(def);
                    for (int i = 0; i < things.Count; i++)
                    {
                        maps[m].mapDrawer.MapMeshDirty(things[i].Position,
                            MapMeshFlagDefOf.Things, true, false);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " texture tuner apply failed: " + e);
            }
        }
    }

    public static class ABTextureTunerAction
    {
        [DebugAction("As above", "AB2: texture tuner", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void Open()
        {
            Find.WindowStack.Add(new Window_ABTextureTuner());
        }
    }
}
