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
            // Per-game static state (combat handoffs, turret orders, ritual/
            // hospitality caches, etc.) is self-registered via [ABGameReset] and
            // cleared here (refactor R1) - Core no longer lists each feature.
            ABGameHooks.RunResets();
        }

        /// <summary>
        /// The LAST thing Game.InitNewGame does, and therefore the only hook that runs after
        /// vanilla's own <c>JumpToCurrentMapLoc(MapGenerator.PlayerStartSpot)</c>.
        ///
        /// That ordering is the whole reason this lives here rather than in FinalizeInit:
        /// a camera move made during FinalizeInit is simply overwritten a few lines later.
        /// See ABBandView.LandOnColony for why the start spot can disagree with where the
        /// colony actually is on a banded map.
        /// </summary>
        public override void StartedNewGame()
        {
            base.StartedNewGame();
            try
            {
                ABBandView.LandOnColony(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: new-game camera anchor failed: " + e);
            }
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
