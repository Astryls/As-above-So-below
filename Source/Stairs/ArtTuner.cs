using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Dev-only art placement tuner for the vertical-link buildings
    /// (stairs, ladders, elevator). Opened from a dev gizmo on any
    /// Building_ABStairs; edits the def's graphicData LIVE (draw size plus
    /// per-facing draw offsets), refreshing every spawned instance on every
    /// level, and bakes the tuned values to Tools/ArtTuning.xml in the mod
    /// folder so they can be folded back into the def XML. Session-only until
    /// baked; purely a tuning harness, never shipped behavior.</summary>
    public static class ABArtTuner
    {
        private class Snapshot
        {
            public Vector2 drawSize;
            public Vector3 drawOffset;
            public Vector3? north;
            public Vector3? east;
            public Vector3? south;
            public Vector3? west;
        }

        private static readonly Dictionary<ThingDef, Snapshot> originals =
            new Dictionary<ThingDef, Snapshot>();

        /// <summary>Copy/paste clipboard: one def's tuning, applied to any
        /// other def (user request 2026-07-23 - tune stairs down once, paste
        /// onto stairs up, wide stairs, ladders...).</summary>
        private static Snapshot clipboard;

        internal static bool CanPaste => clipboard != null;

        internal static void CopyFrom(ThingDef def)
        {
            if (def?.graphicData != null)
            {
                clipboard = Capture(def.graphicData);
                Messages.Message("Copied art tuning of " + def.defName + ".", MessageTypeDefOf.NeutralEvent, historical: false);
            }
        }

        internal static void PasteTo(ThingDef def)
        {
            if (clipboard == null || def?.graphicData == null)
            {
                return;
            }
            EnsureSnapshot(def);
            GraphicData gd = def.graphicData;
            gd.drawSize = clipboard.drawSize;
            gd.drawOffset = clipboard.drawOffset;
            gd.drawOffsetNorth = clipboard.north;
            gd.drawOffsetEast = clipboard.east;
            gd.drawOffsetSouth = clipboard.south;
            gd.drawOffsetWest = clipboard.west;
            Apply(def);
        }

        private static Snapshot Capture(GraphicData gd)
        {
            return new Snapshot
            {
                drawSize = gd.drawSize,
                drawOffset = gd.drawOffset,
                north = gd.drawOffsetNorth,
                east = gd.drawOffsetEast,
                south = gd.drawOffsetSouth,
                west = gd.drawOffsetWest
            };
        }

        /// <summary>Defs edited this session, in bake order.</summary>
        private static readonly List<ThingDef> touched = new List<ThingDef>();

        private static FieldInfo cachedGraphicField;

        public static void Open(ThingDef def)
        {
            if (def?.graphicData == null)
            {
                return;
            }
            EnsureSnapshot(def);
            Find.WindowStack.Add(new Window_ABArtTuner(def));
        }

        private static void EnsureSnapshot(ThingDef def)
        {
            if (originals.ContainsKey(def))
            {
                return;
            }
            originals[def] = Capture(def.graphicData);
        }

        /// <summary>Push the current graphicData values to the world: rebuild
        /// the def-level cached graphic and refresh every spawned instance
        /// (per-thing graphics are cached and stairs print into the static map
        /// mesh; Notify_ColorChanged clears both).</summary>
        public static void Apply(ThingDef def)
        {
            try
            {
                if (!touched.Contains(def))
                {
                    touched.Add(def);
                }
                if (cachedGraphicField == null)
                {
                    cachedGraphicField = typeof(GraphicData).GetField("cachedGraphic",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }
                cachedGraphicField?.SetValue(def.graphicData, null);
                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    List<Thing> things = maps[m].listerThings.ThingsOfDef(def);
                    for (int i = 0; i < things.Count; i++)
                    {
                        things[i].Notify_ColorChanged();
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Art tuner apply failed: " + e);
            }
        }

        public static void Reset(ThingDef def)
        {
            if (!originals.TryGetValue(def, out Snapshot s))
            {
                return;
            }
            GraphicData gd = def.graphicData;
            gd.drawSize = s.drawSize;
            gd.drawOffset = s.drawOffset;
            gd.drawOffsetNorth = s.north;
            gd.drawOffsetEast = s.east;
            gd.drawOffsetSouth = s.south;
            gd.drawOffsetWest = s.west;
            Apply(def);
        }

        /// <summary>Effective offset for a facing: the per-rot override when
        /// set, else the shared drawOffset (mirrors Graphic.DrawOffset).</summary>
        public static Vector3 GetRotOffset(GraphicData gd, int rotInt)
        {
            switch (rotInt)
            {
                case 0: return gd.drawOffsetNorth ?? gd.drawOffset;
                case 1: return gd.drawOffsetEast ?? gd.drawOffset;
                case 2: return gd.drawOffsetSouth ?? gd.drawOffset;
                default: return gd.drawOffsetWest ?? gd.drawOffset;
            }
        }

        public static void SetRotOffset(GraphicData gd, int rotInt, Vector3 v)
        {
            switch (rotInt)
            {
                case 0: gd.drawOffsetNorth = v; break;
                case 1: gd.drawOffsetEast = v; break;
                case 2: gd.drawOffsetSouth = v; break;
                default: gd.drawOffsetWest = v; break;
            }
        }

        /// <summary>Write every touched def's tuned values to
        /// Tools/ArtTuning.xml in the mod folder (and the log, as a backstop).
        /// Returns the file path, or null when nothing was written.</summary>
        public static string BakeAll()
        {
            try
            {
                if (touched.Count == 0)
                {
                    Messages.Message("Art tuner: nothing changed yet.", MessageTypeDefOf.RejectInput, historical: false);
                    return null;
                }
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("<!-- As above, So below: baked art tuning " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " -->");
                sb.AppendLine("<ArtTuning>");
                for (int i = 0; i < touched.Count; i++)
                {
                    ThingDef def = touched[i];
                    GraphicData gd = def.graphicData;
                    sb.AppendLine("  <" + def.defName + ">");
                    sb.AppendLine("    <drawSize>" + FormatVec2(gd.drawSize) + "</drawSize>");
                    if (def.rotatable)
                    {
                        sb.AppendLine("    <drawOffsetNorth>" + FormatVec3(GetRotOffset(gd, 0)) + "</drawOffsetNorth>");
                        sb.AppendLine("    <drawOffsetEast>" + FormatVec3(GetRotOffset(gd, 1)) + "</drawOffsetEast>");
                        sb.AppendLine("    <drawOffsetSouth>" + FormatVec3(GetRotOffset(gd, 2)) + "</drawOffsetSouth>");
                        sb.AppendLine("    <drawOffsetWest>" + FormatVec3(GetRotOffset(gd, 3)) + "</drawOffsetWest>");
                    }
                    else
                    {
                        sb.AppendLine("    <drawOffset>" + FormatVec3(gd.drawOffset) + "</drawOffset>");
                    }
                    sb.AppendLine("  </" + def.defName + ">");
                }
                sb.AppendLine("</ArtTuning>");
                string text = sb.ToString();
                Log.Message(ABLog.Tag + " Baked art tuning:\n" + text);
                string root = touched[0].modContentPack?.RootDir;
                if (root.NullOrEmpty())
                {
                    Messages.Message("Art tuner: mod folder not found; values are in the log.", MessageTypeDefOf.CautionInput, historical: false);
                    return null;
                }
                string dir = Path.Combine(root, "Tools");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ArtTuning.xml");
                File.WriteAllText(path, text);
                Messages.Message("Art tuning baked to " + path, MessageTypeDefOf.PositiveEvent, historical: false);
                return path;
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Art tuner bake failed: " + e);
                return null;
            }
        }

        private static string FormatVec2(Vector2 v)
        {
            return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + ")";
        }

        private static string FormatVec3(Vector3 v)
        {
            return "(" + v.x.ToString("0.##") + "," + v.y.ToString("0.##") + "," + v.z.ToString("0.##") + ")";
        }
    }

    /// <summary>The tuning panel: draw size (with optional square lock) and
    /// draw offsets - per facing for rotatable defs, shared for the elevator.
    /// Every change applies live to all spawned instances on all levels.
    /// Fixed row layout (no dynamic rows mid-frame), camera stays free.</summary>
    public class Window_ABArtTuner : Window
    {
        private readonly ThingDef def;
        private bool squareLock;

        private static readonly string[] RotLabels = { "North", "East", "South", "West" };

        public override Vector2 InitialSize =>
            new Vector2(440f, def.rotatable ? 596f : 366f);

        public Window_ABArtTuner(ThingDef def)
        {
            this.def = def;
            draggable = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            focusWhenOpened = false;
            onlyOneOfTypeAllowed = true;
            squareLock = Mathf.Abs(def.graphicData.drawSize.x - def.graphicData.drawSize.y) < 0.005f;
        }

        public override void DoWindowContents(Rect inRect)
        {
            GraphicData gd = def.graphicData;
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            Text.Font = GameFont.Medium;
            listing.Label("Art tuner: " + def.LabelCap + " (" + def.defName + ")");
            Text.Font = GameFont.Small;
            listing.Gap(4f);

            bool changed = false;
            float sx = gd.drawSize.x;
            float sy = gd.drawSize.y;
            if (AdjustRow(listing, "Draw size X", ref sx, 0.25f, 4f))
            {
                if (squareLock)
                {
                    sy = sx;
                }
                changed = true;
            }
            if (AdjustRow(listing, "Draw size Y", ref sy, 0.25f, 4f))
            {
                if (squareLock)
                {
                    sx = sy;
                }
                changed = true;
            }
            if (changed)
            {
                gd.drawSize = new Vector2(sx, sy);
            }
            listing.CheckboxLabeled("Keep draw size square", ref squareLock);
            listing.GapLine(6f);

            if (def.rotatable)
            {
                for (int rot = 0; rot < 4; rot++)
                {
                    Vector3 off = ABArtTuner.GetRotOffset(gd, rot);
                    float ox = off.x;
                    float oz = off.z;
                    bool rowChanged = AdjustRow(listing, RotLabels[rot] + " offset X", ref ox, -1.5f, 1.5f);
                    rowChanged |= AdjustRow(listing, RotLabels[rot] + " offset Z", ref oz, -1.5f, 1.5f);
                    if (rowChanged)
                    {
                        ABArtTuner.SetRotOffset(gd, rot, new Vector3(ox, off.y, oz));
                        changed = true;
                    }
                }
            }
            else
            {
                Vector3 off = gd.drawOffset;
                float ox = off.x;
                float oz = off.z;
                bool rowChanged = AdjustRow(listing, "Offset X", ref ox, -1.5f, 1.5f);
                rowChanged |= AdjustRow(listing, "Offset Z", ref oz, -1.5f, 1.5f);
                if (rowChanged)
                {
                    gd.drawOffset = new Vector3(ox, off.y, oz);
                    changed = true;
                }
            }

            if (changed)
            {
                ABArtTuner.Apply(def);
            }

            listing.GapLine(6f);
            Rect copyRow = listing.GetRect(30f);
            float cw = (copyRow.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(copyRow.x, copyRow.y, cw, 30f), "Copy"))
            {
                ABArtTuner.CopyFrom(def);
            }
            if (Widgets.ButtonText(new Rect(copyRow.x + cw + 8f, copyRow.y, cw, 30f),
                ABArtTuner.CanPaste ? "Paste" : "Paste (empty)"))
            {
                ABArtTuner.PasteTo(def);
            }
            listing.Gap(6f);
            Rect buttons = listing.GetRect(30f);
            float w = (buttons.width - 16f) / 3f;
            if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, w, 30f), "Reset def"))
            {
                ABArtTuner.Reset(def);
            }
            if (Widgets.ButtonText(new Rect(buttons.x + w + 8f, buttons.y, w, 30f), "Bake all"))
            {
                ABArtTuner.BakeAll();
            }
            if (Widgets.ButtonText(new Rect(buttons.x + 2f * (w + 8f), buttons.y, w, 30f), "Close"))
            {
                Close();
            }
            listing.End();
        }

        /// <summary>Label + fine-nudge buttons (0.01) + slider, snapped to
        /// 0.01. Returns true when the value changed this frame.</summary>
        private static bool AdjustRow(Listing_Standard listing, string label, ref float val, float min, float max)
        {
            Rect r = listing.GetRect(26f);
            float orig = val;
            Widgets.Label(new Rect(r.x, r.y, 160f, r.height), label + ": " + val.ToString("0.00"));
            Rect minus = new Rect(r.x + 164f, r.y + 1f, 24f, 24f);
            Rect plus = new Rect(r.xMax - 24f, r.y + 1f, 24f, 24f);
            Rect slider = new Rect(minus.xMax + 6f, r.y + 3f, plus.x - minus.xMax - 12f, r.height);
            if (Widgets.ButtonText(minus, "-"))
            {
                val -= 0.01f;
            }
            if (Widgets.ButtonText(plus, "+"))
            {
                val += 0.01f;
            }
            float slid = Widgets.HorizontalSlider(slider, val, min, max);
            if (Mathf.Abs(slid - val) > 0.0005f)
            {
                val = slid;
            }
            val = Mathf.Clamp(Mathf.Round(val * 100f) / 100f, min, max);
            return Mathf.Abs(val - orig) > 0.0005f;
        }
    }
}
