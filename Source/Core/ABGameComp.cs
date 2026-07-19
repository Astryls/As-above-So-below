using System;
using RimWorld.Planet;
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
        }

        public override void GameComponentOnGUI()
        {
            if (!ABGuard.On(ABGuard.Ui) || Find.CurrentMap == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            try
            {
                if (ABDefOf.AB_ViewLevelUp.KeyDownEvent)
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
                LevelWidget.Draw();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level view hotkeys");
            }
        }
    }
}
