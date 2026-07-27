using System;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - level navigation input. A separate GameComponent (auto-registered by
    /// Game.FillComponents) so the V2 branch never edits V1's ABGameComp.
    ///
    /// Ctrl + wheel rather than Shift + wheel: Shift+wheel is rerouted onto the
    /// horizontal axis by OS/Unity IMGUI, which zeroes the vertical delta and kills
    /// vanilla zoom as collateral. That lesson is inherited from V1 and cost two
    /// attempts to learn.
    /// </summary>
    public class ABBandInput : GameComponent
    {
        private static KeyBindingDef up;

        private static KeyBindingDef down;

        private static bool resolved;

        private int lastScrollFrame = -1;

        public ABBandInput(Game game)
        {
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }
            resolved = true;
            up = DefDatabase<KeyBindingDef>.GetNamedSilentFail("AB_ViewLevelUp");
            down = DefDatabase<KeyBindingDef>.GetNamedSilentFail("AB_ViewLevelDown");
        }

        public override void GameComponentOnGUI()
        {
            try
            {
                Map map = Find.CurrentMap;
                if (map == null || !ABBands.Banded(map) || Find.CurrentMap.Disposed)
                {
                    return;
                }
                Resolve();
                if (up != null && up.KeyDownEvent)
                {
                    ABBandView.TryStep(map, 1);
                    Event.current.Use();
                    return;
                }
                if (down != null && down.KeyDownEvent)
                {
                    ABBandView.TryStep(map, -1);
                    Event.current.Use();
                    return;
                }
                HandleScroll(map);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2: band input threw: " + e);
            }
        }

        private void HandleScroll(Map map)
        {
            if (Event.current.type != EventType.ScrollWheel)
            {
                return;
            }
            if (!Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
            {
                return;
            }
            // Let windows keep their own scrolling.
            if (Find.WindowStack != null && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) != null)
            {
                return;
            }
            if (Time.frameCount == lastScrollFrame)
            {
                return;
            }
            lastScrollFrame = Time.frameCount;
            float d = Input.mouseScrollDelta.y;
            if (Mathf.Abs(d) < 0.01f)
            {
                return;
            }
            ABBandView.TryStep(map, d > 0f ? 1 : -1);
            Event.current.Use();
        }
    }
}
