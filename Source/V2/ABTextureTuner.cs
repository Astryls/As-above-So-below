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
    /// LIVE TEXTURE TUNER. Select a building, drag a slider, watch it move.
    ///
    /// ⚠ IT FOLLOWS THE SELECTION RATHER THAN OFFERING A DEF LIST, AND THAT IS THE WHOLE
    /// DESIGN. An earlier version made you page through thirty-seven defs by name and edit
    /// four rotation rows at once - which is backwards, because the building you want to fix
    /// is already on screen and already facing the direction you care about. Selecting it
    /// answers both questions for free: WHICH def, and WHICH rotation.
    ///
    /// So there is exactly one offset pair on screen - the one belonging to the rotation the
    /// selected thing is actually in. Rotate the building (or place another facing a
    /// different way) and the panel follows.
    ///
    /// ⚠ CHANGING `graphicData` AT RUNTIME DOES NOTHING ON ITS OWN - THE GRAPHIC IS CACHED
    /// TWICE. `GraphicData.Graphic` memoises into a private `cachedGraphic`, and
    /// `ThingDef.graphic` is baked at PostLoad and never re-read. Clear only one and the
    /// slider looks ignored. Clear both without dirtying the map mesh and it still will not
    /// redraw, because these buildings are MapMeshOnly and their vertices are already baked
    /// into a section. `Apply` does all three.
    ///
    /// Edits are live on the DEF, so every instance changes at once, and they are lost on
    /// quit. This is a measuring instrument - copy the XML out and bake it.
    /// </summary>
    public class Window_ABTextureTuner : Window
    {
        private static readonly FieldInfo CachedGraphic =
            AccessTools.Field(typeof(GraphicData), "cachedGraphic");

        private readonly HashSet<ThingDef> touched = new HashSet<ThingDef>();

        public override Vector2 InitialSize => new Vector2(400f, 400f);

        protected override float Margin => 12f;

        public Window_ABTextureTuner()
        {
            // Non-modal, and it must stay that way: the point is to watch the building while
            // dragging, so the map has to remain visible, clickable and camera-movable.
            draggable = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            focusWhenOpened = false;
        }

        /// <summary>Bottom-left, clear of the architect menu and the inspect pane.</summary>
        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(12f, UI.screenHeight - InitialSize.y - 160f,
                InitialSize.x, InitialSize.y);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Thing sel = Find.Selector?.SingleSelectedThing;
            ThingDef def = sel?.def;
            if (def?.graphicData == null)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(inRect, "Select a building.\n\nIts scale, offset and draw altitude\nappear here and update live.");
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            GraphicData g = def.graphicData;
            Rot4 rot = sel.Rotation;
            bool dirty = false;

            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);

            Text.Font = GameFont.Small;
            GUI.color = new Color(0.95f, 0.75f, 0.45f);
            l.Label(def.defName);
            GUI.color = Color.white;
            l.Label("facing " + RotName(rot) + (touched.Contains(def) ? "   (modified)" : ""));
            l.GapLine(8f);

            // ---- scale. One slider: these are square sprites in square cells, and two
            // independent axes invite a stretch nobody wants.
            l.Label("Scale   " + g.drawSize.x.ToString("0.00"));
            float s = l.Slider(g.drawSize.x, 0.2f, 3f);
            if (!Mathf.Approximately(s, g.drawSize.x))
            {
                float v = Snap(s);
                g.drawSize = new Vector2(v, v);
                dirty = true;
            }

            // ---- offset for THIS rotation only
            Vector3 off = OffsetFor(g, rot);
            l.Gap(6f);
            l.Label("Offset X   " + off.x.ToString("0.00"));
            float ox = l.Slider(off.x, -1.5f, 1.5f);
            l.Label("Offset Z   " + off.z.ToString("0.00"));
            float oz = l.Slider(off.z, -1.5f, 1.5f);
            if (!Mathf.Approximately(ox, off.x) || !Mathf.Approximately(oz, off.z))
            {
                SetOffset(g, rot, new Vector3(Snap(ox), 0f, Snap(oz)));
                dirty = true;
            }

            l.Gap(6f);
            if (l.ButtonText("Push out from wall (0.9)"))
            {
                SetOffset(g, rot, PushOut(rot, 0.9f));
                dirty = true;
            }
            if (l.ButtonText("Zero this rotation"))
            {
                SetOffset(g, rot, Vector3.zero);
                dirty = true;
            }

            // ---- overdraw
            l.GapLine(8f);
            l.Label("Draw altitude   " + def.altitudeLayer);
            Rect alt = l.GetRect(28f);
            Array layers = Enum.GetValues(typeof(AltitudeLayer));
            int ai = Array.IndexOf(layers, def.altitudeLayer);
            if (Widgets.ButtonText(alt.LeftHalf().ContractedBy(2f), "under") && ai > 0)
            {
                def.altitudeLayer = (AltitudeLayer)layers.GetValue(ai - 1);
                dirty = true;
            }
            if (Widgets.ButtonText(alt.RightHalf().ContractedBy(2f), "over") && ai < layers.Length - 1)
            {
                def.altitudeLayer = (AltitudeLayer)layers.GetValue(ai + 1);
                dirty = true;
            }

            l.GapLine(8f);
            if (l.ButtonText("Copy XML" + (touched.Count > 1 ? "  (all " + touched.Count + ")" : "")))
            {
                StringBuilder sb = new StringBuilder();
                if (touched.Count > 1)
                {
                    foreach (ThingDef d in touched)
                    {
                        sb.AppendLine(XmlFor(d)).AppendLine();
                    }
                }
                else
                {
                    sb.Append(XmlFor(def));
                }
                GUIUtility.systemCopyBuffer = sb.ToString().TrimEnd();
                Messages.Message("Copied. Values are lost on quit.",
                    MessageTypeDefOf.TaskCompletion, false);
            }

            l.End();

            if (dirty)
            {
                touched.Add(def);
                Apply(def);
            }
        }

        // ---- offsets -------------------------------------------------------

        /// <summary>The effective offset for a rotation: its own if set, otherwise the base.
        /// Editing always writes the per-rotation field, so touching one facing never
        /// silently moves the other three.</summary>
        private static Vector3 OffsetFor(GraphicData g, Rot4 rot)
        {
            Vector3? v = rot.AsInt == 0 ? g.drawOffsetNorth
                       : rot.AsInt == 1 ? g.drawOffsetEast
                       : rot.AsInt == 2 ? g.drawOffsetSouth
                       : g.drawOffsetWest;
            return v ?? g.drawOffset;
        }

        private static void SetOffset(GraphicData g, Rot4 rot, Vector3 v)
        {
            switch (rot.AsInt)
            {
                case 0: g.drawOffsetNorth = v; break;
                case 1: g.drawOffsetEast = v; break;
                case 2: g.drawOffsetSouth = v; break;
                default: g.drawOffsetWest = v; break;
            }
        }

        /// <summary>Offset that pushes the sprite out of the wall along the way it faces -
        /// the shortcut for the wall-mounted case, which is most of what needs tuning.</summary>
        private static Vector3 PushOut(Rot4 rot, float d)
        {
            switch (rot.AsInt)
            {
                case 0: return new Vector3(0f, 0f, d);
                case 1: return new Vector3(d, 0f, 0f);
                case 2: return new Vector3(0f, 0f, -d);
                default: return new Vector3(-d, 0f, 0f);
            }
        }

        private static string RotName(Rot4 r)
        {
            return r.AsInt == 0 ? "north" : r.AsInt == 1 ? "east" : r.AsInt == 2 ? "south" : "west";
        }

        /// <summary>Round to 0.05 so the copied XML is a number a human would have typed.</summary>
        private static float Snap(float f)
        {
            return Mathf.Round(f * 20f) / 20f;
        }

        private static string F3(Vector3 v)
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
            sb.AppendLine("  <drawSize>(" + g.drawSize.x.ToString("0.##") + ","
                + g.drawSize.y.ToString("0.##") + ")</drawSize>");
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
                sb.AppendLine("  <" + tag + ">" + F3(v.Value) + "</" + tag + ">");
            }
        }

        /// <summary>
        /// Push a graphicData edit through both caches and force a redraw.
        ///
        /// ⚠ ALL THREE STEPS ARE REQUIRED - see the class note. Miss one and the slider
        /// appears to do nothing, which reads as a broken tool rather than a missing refresh.
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
