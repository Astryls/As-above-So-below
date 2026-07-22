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

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            ABGuard.Reset();
            // Static combat state never crosses into a freshly loaded/started game:
            // pending job-target handoffs and turret bombardment orders both hold
            // Thing references from the previous session.
            CrossLevelCombat.PendingTargets.Clear();
            CrossLevelTurret.ClearAll();
            CrossLevelCombatUI.ActiveShooters.Clear();
            CrossLevelAnimals.ClearAll();
            ABRitualAttendance.ClearAll();
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            // Single static count read when no cross-level ritual gather is pending.
            ABRitualAttendance.Tick();
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
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level view hotkeys");
            }
        }
    }
}
