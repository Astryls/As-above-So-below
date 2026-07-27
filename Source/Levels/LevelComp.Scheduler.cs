using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    // Partial of LevelComp (refactor R3): the per-map tick scheduler + level
    // sync wiring. FinalizeInit, the interval constants and elapsed-time Due()
    // gates, MapComponentTick/Update/OnGUI, and (Un)SubscribeSync live here; the
    // level model (links, stairs registry, scribe) stays in LevelComp.cs.
    public partial class LevelComp
    {
        private bool syncSubscribed;
        private Action<IntVec3> roofChangedHandler;
        private Action<IntVec3> terrainChangedHandler;
        private Action<Thing> thingSpawnedHandler;
        private Action<Thing> thingDespawnedHandler;

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (level == 0 && groundMap == null)
            {
                groundMap = map;
            }
            if (level != 0)
            {
                LevelCensus.NoteLevel(level, 1);
            }
            TrySubscribeSync();
            if (level == 1)
            {
                // Self-heal any air/rooftop drift against the live roof grid;
                // a no-op when the event path kept everything in sync.
                LevelSync.ReconcileRooftops(map);
            }
            if (level == -1 && ABMod.Settings != null && ABMod.Settings.basementRevealed)
            {
                // Reveal-on-load: clear all fog so the whole basement is
                // visible without mining to discover it. Runs on generation
                // AND after a save load (FinalizeInit fires for both). Unfog
                // sends no area-revealed letters, so no spam.
                try
                {
                    map.fogGrid.ClearAllFog();
                }
                catch (Exception e)
                {
                    ABLog.Dev("Basement reveal-on-load unfog failed (ignored): " + e.Message);
                }
            }
        }

        private const int WeatherSyncInterval = 150;

        /// <summary>Matches the VEF pipe net tick (100) so each direct
        /// injection covers exactly one net tick window.</summary>
        private const int PipeBridgeInterval = 100;

        private const int SubscribeRetryInterval = 250;

        /// <summary>Cadence of the stuck-hostile scan on pocket levels. Gated
        /// behind a cheap any-hostiles check, so calm maps pay one lookup.
        /// Never visibility-throttled: NPC behavior is simulation.</summary>
        private const int HostileScanInterval = 250;

        /// <summary>Cadence of the cross-gap auto-engage scan (sky comp only, covers
        /// the whole sky/surface pair both directions). Simulation: never
        /// visibility-throttled. Tightened to ~1s (one-map reaction): a hostile picks
        /// up a cross-level target almost as fast as a same-map one. The scan is
        /// bounded (capped engages/probes + per-pawn failure cooldowns) so it stays
        /// cheap at this cadence.</summary>
        private const int AutoEngageInterval = 60;

        /// <summary>Low-frequency safety net over the event-driven rooftop sync:
        /// a time-sliced whole-map sweep that converges the air/rooftop state
        /// even if roof events misfire for any reason.</summary>
        private const int RooftopSweepInterval = 2000;

        /// <summary>Interval stretch for purely visual mirroring systems
        /// (weather, sweep) while this map is not the one on screen. On-view
        /// catch-up fires immediately, so the player never sees stale state.</summary>
        private const int HiddenWeatherMultiplier = 4;

        private const int HiddenSweepMultiplier = 5;

        /// <summary>Cells reconciled per tick while a rooftop sweep is active;
        /// a full 250x250 map converges in ~16 ticks with no single-tick spike.</summary>
        private const int SweepCellsPerTick = 4096;

        // Elapsed-time scheduling instead of TicksGame modulo: modulo beats are
        // silently missed when a debug time skip, save or load, or another
        // mod's tick throttling makes component ticks non-contiguous, and an
        // interval that stretches while hidden needs a due-tick anyway. The
        // dues are deliberately not scribed; after a load each fires once at
        // its stagger offset and re-seats the cadence.
        private int nextWeatherDue = -1;

        private int nextSweepDue = -1;

        private int nextHostileDue = -1;

        private int nextAutoEngageDue = -1;

        private int nextVisionDue = -1;

        private int nextGroundHostileDue = -1;

        private int nextAnimalDue = -1;

        private int nextPetReturnDue = -1;

        /// <summary>Pet food-trip return sweep cadence; the tick gate is a
        /// static count read, so idle cost is nothing when no trips exist.</summary>
        private const int PetReturnScanInterval = 600;

        /// <summary>Ambient wildlife descent cadence; long because the event itself
        /// is rare by design (per-scan roll + global spacing inside the scanner).</summary>
        private const int AnimalWanderInterval = 1200;

        private int nextPipesDue = -1;

        /// <summary>Cursor of the active time-sliced rooftop sweep; -1 = idle.</summary>
        private int sweepCursor = -1;

        private bool wasVisible;

        /// <summary>One-shot below-print convergence latch (per session, not
        /// scribed): see the visibility block in MapComponentTick.</summary>
        private bool belowPrintsHealed;

        /// <summary>One-shot fog-mesh heal (per session): pocket levels can
        /// carry a STALE all-fogged fog bake from map birth - gen clears the
        /// fog GRID after the drawer's initial bake without dirtying sections.
        /// Invisible for rounds because the fog rendered multiplied by the
        /// near-black underground sky; disableSkyLighting (2026-07-24)
        /// restored fog's true gray and unveiled it ("basement appears as all
        /// fog of war", diagnostic: fog DATA 0%). One whole-map fog regen on
        /// first view per session heals every such case, old saves included.</summary>
        private bool fogMeshHealed;

        private static int Stagger(Map map, int interval) => map.uniqueID % interval;

        /// <summary>Lazy-init + elapsed-time due check with a per-map stagger.</summary>
        private bool Due(ref int due, int now, int interval)
        {
            if (due < 0)
            {
                due = now + Stagger(map, interval) + 1;
                return false;
            }
            if (now < due)
            {
                return false;
            }
            due = now + interval;
            return true;
        }

        /// <summary>Per-frame engagement visuals for cross-level combat (lines, aim
        /// pies). Only the viewed map draws; both callees early-out on empty sets, so
        /// idle cost is two count reads per frame.</summary>
        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (!ABGuard.On(ABGuard.Ui) || map != Find.CurrentMap
                || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                CrossLevelCombatUI.DrawEngagementVisuals(map);
                CrossLevelTurret.DrawVisuals(map);
                ABBelowGotoDrag.FrameUpdate(map);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "cross level engagement visuals");
            }
        }

        /// <summary>Hovering pawn labels for the cross-level goto preview -
        /// vanilla draws its controller labels from Selector.SelectorOnGUI,
        /// which never sees our drag. Idle cost: one count read.</summary>
        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            if (!ABGuard.On(ABGuard.Ui) || map != Find.CurrentMap
                || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            ABBelowGotoDrag.OnGUIUpdate(map);
        }

        /// <summary>Sky comps sync weather from the ground; the ground comp drives
        /// pipe network bridging for every stairwell pair (each pair has one end
        /// on the ground map under the three-level cap). Visual mirroring
        /// (weather, rooftop sweep) stretches its cadence while the map is not
        /// on screen and catches up the moment it becomes visible; simulation
        /// (pipes, climate, hostiles) never throttles.</summary>
        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            bool visible = map == Find.CurrentMap;
            if (visible && !wasVisible)
            {
                if (level != 0 && !fogMeshHealed && ABGuard.On(ABGuard.Rendering)
                    && LevelRenderer.DrawerReady(map))
                {
                    fogMeshHealed = true;
                    try
                    {
                        map.mapDrawer.WholeMapChanged((ulong)MapMeshFlagDefOf.FogOfWar);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Rendering, e, "fog mesh heal");
                    }
                }
                // The player just switched here: sync the visual mirrors now
                // instead of waiting out a stretched hidden-cadence window.
                if (level == 1)
                {
                    nextWeatherDue = 0;
                    if (sweepCursor < 0)
                    {
                        sweepCursor = 0;
                    }
                    // Below-print convergence (run-33 regression: prints came up
                    // empty on the initial whole-map regen and only recovered per
                    // section when something re-dirtied it). One forced reprint
                    // of the below-things layer the first time this sky map is
                    // viewed each session; in-view sections regen next frame,
                    // the rest as they scroll in. Not scribed - runs once per
                    // session including after loads.
                    if (!belowPrintsHealed && ABGuard.On(ABGuard.Rendering)
                        && LevelRenderer.DrawerReady(map))
                    {
                        belowPrintsHealed = true;
                        try
                        {
                            map.mapDrawer.WholeMapChanged((ulong)ABDefOf.AB_BelowThings);
                            ABLog.Dev("Below-print convergence pass for sky map " + map.uniqueID + ".");
                        }
                        catch (Exception e)
                        {
                            ABGuard.Disable(ABGuard.Rendering, e, "below print convergence");
                        }
                    }
                }
            }
            wasVisible = visible;
            if (level != 0 && !syncSubscribed && now % SubscribeRetryInterval == 0)
            {
                // FinalizeInit's subscription can bail silently when a link or
                // the events object is not ready yet; retry until it lands so
                // the roof and terrain cascades can never stay dead for a whole
                // session. One bool read per tick once subscribed.
                TrySubscribeSync();
            }
            // Pets shipped to another level for food walk home once fed; runs
            // on every level because meal trips can go up or down the column.
            if (CrossLevelAnimals.AnyPetTrips && ABGuard.On(ABGuard.Logistics)
                && Due(ref nextPetReturnDue, now, PetReturnScanInterval))
            {
                try
                {
                    CrossLevelAnimals.ScanPetReturns(this);
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.Logistics, e, "pet return scan");
                }
            }
            if (level != 0 && ABGuard.On(ABGuard.HostileMove)
                && Due(ref nextHostileDue, now, HostileScanInterval))
            {
                try
                {
                    HostileDescend.ScanPocketMap(this);
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.HostileMove, e, "hostile descend scan");
                }
            }
            if (level == 1)
            {
                // Tick-accurate cross-level turret bursts. First line inside is a
                // static count early-out: zero recurring cost with no orders live.
                if (ABGuard.On(ABGuard.Combat))
                {
                    try
                    {
                        Map g = lowerMap ?? groundMap;
                        if (g != null && !g.Disposed)
                        {
                            CrossLevelTurret.TickPair(map, g);
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Combat, e, "cross level turret tick");
                    }
                }
                if (ABGuard.On(ABGuard.Combat) && Due(ref nextAutoEngageDue, now, AutoEngageInterval))
                {
                    try
                    {
                        Map ground = lowerMap ?? groundMap;
                        if (ground != null && !ground.Disposed
                            && (map.mapPawns.AllPawnsSpawned.Count > 0 || ground.mapPawns.AllPawnsSpawned.Count > 0))
                        {
                            CrossLevelAutoEngage.ScanPair(map, ground);
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Combat, e, "cross gap auto engage scan");
                    }
                }
                // Cross-level fog-of-war vision (CAI 5000): pawns watching
                // through the gap light the other level. Static count early-out
                // when CAI is absent; the pass itself no-ops unless CAI Fog Of
                // War is on and a level of this pair is on screen.
                if (ABGuard.On(ABGuard.CombatAI) && ABCombatAICompat.Active
                    && Due(ref nextVisionDue, now, CrossLevelVision.ScanInterval))
                {
                    try
                    {
                        Map ground = lowerMap ?? groundMap;
                        if (ground != null && !ground.Disposed)
                        {
                            CrossLevelVision.ScanPair(map, ground);
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.CombatAI, e, "cross level vision scan");
                    }
                }
                int weatherInterval = visible ? WeatherSyncInterval : WeatherSyncInterval * HiddenWeatherMultiplier;
                if (ABGuard.On(ABGuard.Weather) && Due(ref nextWeatherDue, now, weatherInterval))
                {
                    try
                    {
                        Map ground = lowerMap ?? groundMap;
                        if (ground != null && !ground.Disposed)
                        {
                            WeatherDef target = ground.weatherManager.curWeather;
                            if (target != null && map.weatherManager.curWeather != target)
                            {
                                map.weatherManager.TransitionTo(target);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Weather, e, "weather sync");
                    }
                }
                int sweepInterval = visible ? RooftopSweepInterval : RooftopSweepInterval * HiddenSweepMultiplier;
                if (sweepCursor < 0 && Due(ref nextSweepDue, now, sweepInterval))
                {
                    sweepCursor = 0;
                }
                if (sweepCursor >= 0
                    && !LevelSync.ReconcileRooftopsSlice(map, ref sweepCursor, SweepCellsPerTick))
                {
                    sweepCursor = -1;
                }
            }
            else if (level == 0)
            {
                if (stairsList == null || stairsList.Count == 0)
                {
                    return;
                }
                if (ABGuard.On(ABGuard.HostileMove)
                    && Due(ref nextGroundHostileDue, now, HostileScanInterval))
                {
                    try
                    {
                        HostileDescend.ScanGroundHostiles(this);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.HostileMove, e, "hostile ascent scan");
                    }
                }
                if (ABGuard.On(ABGuard.HostileMove) && lowerMap != null
                    && Due(ref nextAnimalDue, now, AnimalWanderInterval))
                {
                    try
                    {
                        CrossLevelAnimals.ScanSurfaceAmbient(this);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.HostileMove, e, "animal wander scan");
                    }
                }
                // Stairwell heat exchange removed by user directive (2026-07-21):
                // stairs no longer act as heat exchangers between levels. Pocket-map
                // ambient temperature (ClimatePatches) is unaffected.
                if (ABGuard.On(ABGuard.Pipes) && Due(ref nextPipesDue, now, PipeBridgeInterval))
                {
                    try
                    {
                        ABPipeCompat.TickGroundPairs(this);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Pipes, e, "pipe network bridge");
                    }
                }
            }
        }

        /// <summary>Sky level comps listen to the ground map's roof changes and their
        /// own terrain/spawn events; both sky and basement comps listen for mineable
        /// removal to guarantee the fog reveal.</summary>
        private void TrySubscribeSync()
        {
            if (syncSubscribed || level == 0 || map.events == null)
            {
                return;
            }
            Map self = map;
            if (level == 1)
            {
                Map ground = lowerMap ?? groundMap;
                if (ground == null || ground.events == null)
                {
                    return;
                }
                roofChangedHandler = c => LevelSync.OnGroundRoofChanged(ground, c);
                terrainChangedHandler = c => LevelSync.OnSkyTerrainChanged(self, c);
                thingSpawnedHandler = t => LevelSync.OnSkyThingSpawned(self, t);
                ground.events.RoofChanged += roofChangedHandler;
                map.events.TerrainChanged += terrainChangedHandler;
                map.events.ThingSpawned += thingSpawnedHandler;
            }
            thingDespawnedHandler = t => LevelSync.OnLevelMineableDespawned(self, t);
            map.events.ThingDespawned += thingDespawnedHandler;
            syncSubscribed = true;
            ABLog.Dev("Level sync subscribed for map " + map.uniqueID + " (level " + level + ").");
            // Surface ceiling hints regenerate from the Roofs flag; after a load the
            // surface sections can build before this sky map's links restore, so
            // nudge a one-time whole-map regen. Cosmetic: failure is swallowed.
            if (level == 1)
            {
                try
                {
                    (lowerMap ?? groundMap)?.mapDrawer?.WholeMapChanged(MapMeshFlagDefOf.Roofs);
                }
                catch (Exception e)
                {
                    ABLog.Dev("Ceiling hint load nudge skipped: " + e.Message);
                }
            }
        }

        private void UnsubscribeSync()
        {
            if (!syncSubscribed)
            {
                return;
            }
            Map ground = lowerMap ?? groundMap;
            if (ground?.events != null && roofChangedHandler != null)
            {
                ground.events.RoofChanged -= roofChangedHandler;
            }
            if (map?.events != null)
            {
                if (terrainChangedHandler != null)
                {
                    map.events.TerrainChanged -= terrainChangedHandler;
                }
                if (thingSpawnedHandler != null)
                {
                    map.events.ThingSpawned -= thingSpawnedHandler;
                }
                if (thingDespawnedHandler != null)
                {
                    map.events.ThingDespawned -= thingDespawnedHandler;
                }
            }
            syncSubscribed = false;
        }
    }
}
