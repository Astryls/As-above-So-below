using System;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Global per-game logic: level view hotkeys and kill switch reset.
    /// Auto-discovered by RimWorld via the (Game) constructor.
    /// </summary>
    public class ABGameComp : GameComponent
    {
        public ABGameComp(Game game)
        {
        }

        // Frame stamp so the multiple OnGUI passes per frame drive at most one
        // shift+wheel level switch. Session state, not scribed.
        private int lastScrollLevelFrame = -1;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ABGuard.Reset();
            // Per-game static state (combat handoffs, turret orders, ritual/
            // hospitality caches, etc.) is self-registered via [ABGameReset] and
            // cleared here (refactor R1) — Core no longer lists each feature.
            ABGameHooks.RunResets();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // Scribe hooks self-register via [ABGameExpose] (refactor R1); e.g.
            // pet food-trip records must survive save/load or a pet saved between
            // its meal and the walk home would be stranded for good.
            ABGameHooks.RunExposes();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            // Per-tick feature work is self-registered via [ABGameTick] and run
            // here in a fixed order (refactor R1). Each callee self-guards and
            // early-outs cheaply when idle, exactly as the old explicit list did.
            ABGameHooks.RunTicks();
        }

        public override void GameComponentOnGUI()
        {
            if (!ABGuard.On(ABGuard.Ui) || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            try
            {
                ABSettings settings = ABMod.Settings;
                if (settings != null && settings.cameraLockKeybind
                    && ABDefOf.AB_CameraLevelLock != null && ABDefOf.AB_CameraLevelLock.KeyDownEvent)
                {
                    LevelCamera.ToggleLevelLock();
                    Event.current.Use();
                }
                else if (ABDefOf.AB_ViewLevelUp.KeyDownEvent)
                {
                    Map up = Find.CurrentMap.UpperMap();
                    if (up != null)
                    {
                        LevelCamera.JumpPreservingView(up);
                    }
                }
                else if (ABDefOf.AB_ViewLevelDown.KeyDownEvent)
                {
                    Map down = Find.CurrentMap.LowerMap();
                    if (down != null)
                    {
                        LevelCamera.JumpPreservingView(down);
                    }
                }

                // Left Control + mouse wheel = move through levels instead of
                // zooming. This is a SEPARATE check (not part of the KeyDown
                // else-if chain) because it is driven by the wheel, not a key.
                // Modifier is Ctrl, NOT Shift: the OS/Unity IMGUI reroutes
                // Shift+wheel onto the horizontal axis, which zeroes the vertical
                // wheel entirely (Event.current.delta.y AND, on some systems,
                // Input.mouseScrollDelta.y) - that killed the earlier Shift
                // version and is why Shift+wheel also stops the camera zooming.
                // Ctrl+wheel is not rerouted, so the vertical wheel is intact.
                // Read Input.mouseScrollDelta.y (raw device wheel, +up). Frame-
                // stamped so the many OnGUI passes per frame switch at most once.
                // Skipped when the cursor is over a window so window scroll views
                // keep working. JumpPreservingView is a manual switch, so it
                // works even under the camera level lock.
                if (settings != null && settings.scrollLevelKeybind
                    && Input.GetKey(KeyCode.LeftControl)
                    && Find.WindowStack.GetWindowAt(UI.MousePositionOnUIInverted) == null)
                {
                    float wheel = Input.mouseScrollDelta.y;
                    if (wheel != 0f)
                    {
                        if (Time.frameCount != lastScrollLevelFrame)
                        {
                            lastScrollLevelFrame = Time.frameCount;
                            Map target = wheel > 0f
                                ? Find.CurrentMap.UpperMap()
                                : Find.CurrentMap.LowerMap();
                            if (target != null)
                            {
                                LevelCamera.JumpPreservingView(target);
                            }
                            if (settings.verboseLogging)
                            {
                                Log.Message("[AB] ctrl+wheel " + wheel.ToString("0.##")
                                    + " -> " + (target != null ? target.ToString() : "no level in that direction"));
                            }
                        }
                        // Consume any IMGUI scroll event this pass so it cannot
                        // also zoom or scroll something else.
                        if (Event.current.type == EventType.ScrollWheel)
                        {
                            Event.current.Use();
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level view hotkeys");
            }
        }
    }
}
