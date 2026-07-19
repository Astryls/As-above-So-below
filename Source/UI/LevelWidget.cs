using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Small over-map level switcher (T10 #1), bottom right above the time
    /// controls. One row per level in the current column, top to bottom sky,
    /// surface, basement. The current level carries the suite accent strip;
    /// click a row to jump (camera view preserved), scroll over the widget to
    /// step a level. Hidden when the column has a single level. Styled per the
    /// Modern Suite over-map contract: opaque plate, 1px border, GameFont.Small,
    /// state as a left strip only.
    /// </summary>
    public static class LevelWidget
    {
        private const float RowHeight = 24f;

        private const float Pad = 6f;

        private static readonly List<KeyValuePair<int, Map>> tmpLevels = new List<KeyValuePair<int, Map>>();

        public static void Draw()
        {
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.showLevelWidget)
            {
                return;
            }
            Map cur = Find.CurrentMap;
            LevelComp controller = cur?.Controller();
            if (controller == null || controller.MapByLevel.Count <= 1)
            {
                return;
            }
            tmpLevels.Clear();
            foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel.OrderByDescending(k => k.Key))
            {
                if (kvp.Value != null && !kvp.Value.Disposed)
                {
                    tmpLevels.Add(kvp);
                }
            }
            if (tmpLevels.Count <= 1)
            {
                return;
            }
            Text.Font = GameFont.Small;
            float width = 0f;
            for (int i = 0; i < tmpLevels.Count; i++)
            {
                width = Mathf.Max(width, Text.CalcSize(LabelFor(tmpLevels[i].Key)).x);
            }
            width += Pad * 2f + 10f;
            float height = tmpLevels.Count * RowHeight + 2f;
            Rect box = new Rect(UI.screenWidth - width - 8f, UI.screenHeight - 84f - height, width, height);
            Widgets.DrawBoxSolidWithOutline(box, ABTheme.PanelBG, ABTheme.BGL);
            // Scroll over the widget steps one level.
            if (Mouse.IsOver(box) && Event.current.type == EventType.ScrollWheel)
            {
                Step(cur, Event.current.delta.y < 0f ? 1 : -1);
                Event.current.Use();
            }
            float y = box.y + 1f;
            for (int i = 0; i < tmpLevels.Count; i++)
            {
                DrawRow(new Rect(box.x + 1f, y, box.width - 2f, RowHeight), tmpLevels[i].Key, tmpLevels[i].Value, cur);
                y += RowHeight;
            }
        }

        private static void DrawRow(Rect rect, int level, Map map, Map cur)
        {
            bool current = map == cur;
            bool hover = Mouse.IsOver(rect);
            if (hover)
            {
                Widgets.DrawBoxSolid(rect, Color.Lerp(ABTheme.PanelBG, ABTheme.BGL, 0.45f));
            }
            else if (current)
            {
                Widgets.DrawBoxSolid(rect, Color.Lerp(ABTheme.PanelBG, ABTheme.BGL, 0.22f));
            }
            if (current)
            {
                Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 2f, rect.height), ABTheme.Accent);
            }
            Rect label = new Rect(rect.x + Pad + 4f, rect.y, rect.width - Pad - 4f, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = current ? Color.white : ABTheme.TextDim;
            Widgets.Label(label, LabelFor(level));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, "AB_LevelWidgetTip".Translate());
            if (!current && Widgets.ButtonInvisible(rect))
            {
                LevelCamera.JumpPreservingView(map);
            }
        }

        private static void Step(Map cur, int dir)
        {
            Map next = dir > 0 ? cur.UpperMap() : cur.LowerMap();
            if (next != null && !next.Disposed)
            {
                LevelCamera.JumpPreservingView(next);
            }
        }

        private static string LabelFor(int level)
        {
            if (level > 0)
            {
                return "AB_LevelSky".Translate();
            }
            return level == 0 ? "AB_LevelSurface".Translate() : "AB_LevelBasement".Translate();
        }
    }
}
