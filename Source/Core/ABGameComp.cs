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

        // GameComponentOnGUI REMOVED with V1.
        //
        // It hosted the level-view hotkeys (AB_ViewLevelUp/Down, camera lock, ctrl+wheel),
        // all of which drove LevelCamera.JumpPreservingView between pocket maps via
        // Map.UpperMap()/LowerMap(). None of that model exists in V2: bands are ranges of one
        // map, and the equivalent input lives in ABBandInput (PageUp/PageDown, ctrl+wheel)
        // driving ABBandView.SetBand. Keeping a second input path would have meant two
        // handlers competing for the same wheel events.
    }
}
