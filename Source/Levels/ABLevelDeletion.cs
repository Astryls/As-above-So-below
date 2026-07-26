using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Safe removal of a single level (sky or basement) without abandoning the
    /// colony. The pocket map is destroyed through the vanilla PocketMapUtility
    /// path, which fires LevelComp.MapRemoved to sever stair links and clean up
    /// column references. By default the colony is EVACUATED to the surface first
    /// (colonists, slaves, player animals and mechs, plus prisoners and guests);
    /// the caller can skip evacuation to delete everything on the level outright.
    /// The ground (level 0) map is never removable this way. Kill switch: world.
    /// </summary>
    public static class ABLevelDeletion
    {
        public static void DeleteLevel(Map level, bool evacuate)
        {
            if (!ABGuard.On(ABGuard.World))
            {
                return;
            }
            try
            {
                if (level == null || level.Disposed)
                {
                    return;
                }
                LevelComp comp = level.Levels();
                if (comp == null || comp.level == 0)
                {
                    Messages.Message("AB_RemoveLevelCantGround".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                Map ground = comp.groundMap;
                if (ground == null || ground.Disposed || ground == level)
                {
                    ground = level.GroundMap();
                }
                if (ground == null || ground.Disposed || ground == level)
                {
                    Messages.Message("AB_RemoveLevelNoGround".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }

                // Switch the view off the doomed map first so DeinitAndRemoveMap
                // does not bounce the camera out to the world view.
                if (Find.CurrentMap == level)
                {
                    try
                    {
                        LevelCamera.JumpPreservingView(ground);
                    }
                    catch
                    {
                        Current.Game.CurrentMap = ground;
                    }
                }

                int moved = evacuate ? EvacuateTo(level, ground) : 0;

                PocketMapUtility.DestroyPocketMap(level);

                TaggedString msg = evacuate
                    ? "AB_RemoveLevelDoneEvac".Translate(moved)
                    : "AB_RemoveLevelDone".Translate();
                Messages.Message(msg, MessageTypeDefOf.TaskCompletion, historical: false);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.World, e, "level deletion");
            }
        }

        private static int EvacuateTo(Map level, Map ground)
        {
            int moved = 0;
            IntVec3 anchor = FindAnchor(level, ground);
            // Snapshot: DeSpawn mutates the live list.
            List<Pawn> pawns = level.mapPawns.AllPawnsSpawned.ToList();
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (!ShouldEvacuate(p))
                {
                    continue;
                }
                try
                {
                    // Detach carried cargo before despawn (job cleanup would drop
                    // it on the doomed map), hand it back after the move.
                    Thing carried = p.carryTracker?.CarriedThing;
                    if (carried != null)
                    {
                        p.carryTracker.innerContainer.Remove(carried);
                    }
                    IntVec3 cell = SafeCell(anchor, ground);
                    p.DeSpawn();
                    if (!cell.IsValid)
                    {
                        cell = ground.Center;
                    }
                    GenSpawn.Spawn(p, cell, ground);
                    if (carried != null && !carried.Destroyed)
                    {
                        if (p.carryTracker == null || !p.carryTracker.TryStartCarry(carried))
                        {
                            GenPlace.TryPlaceThing(carried, cell, ground, ThingPlaceMode.Near);
                        }
                    }
                    moved++;
                }
                catch (Exception e)
                {
                    ABLog.Dev("Evacuation of " + (p != null ? p.LabelShort : "null") + " failed: " + e.Message);
                }
            }
            return moved;
        }

        /// <summary>Player colonists, slaves, player animals and player-side
        /// mechs, plus prisoners and guests of the colony. Wild animals and
        /// hostiles are not evacuated - they go with the map.</summary>
        private static bool ShouldEvacuate(Pawn p)
        {
            if (p == null || p.Dead)
            {
                return false;
            }
            if (p.Faction == Faction.OfPlayer)
            {
                return true;
            }
            if (p.IsPrisonerOfColony)
            {
                return true;
            }
            return p.HostFaction == Faction.OfPlayer;
        }

        /// <summary>A ground stairwell that links to the doomed level makes the
        /// natural landing spot; falls back to the ground map centre.</summary>
        private static IntVec3 FindAnchor(Map level, Map ground)
        {
            LevelComp gc = ground.Levels();
            if (gc != null)
            {
                List<Building_ABStairs> stairs = gc.Stairs;
                for (int i = 0; i < stairs.Count; i++)
                {
                    Building_ABStairs s = stairs[i];
                    if (s != null && s.Spawned && s.CounterpartTowards(level) != null)
                    {
                        return s.Position;
                    }
                }
            }
            return ground.Center;
        }

        private static IntVec3 SafeCell(IntVec3 anchor, Map ground)
        {
            if (CellFinder.TryFindRandomCellNear(anchor, ground, 12,
                c => c.Standable(ground) && !c.Fogged(ground), out IntVec3 near))
            {
                return near;
            }
            if (CellFinderLoose.TryGetRandomCellWith(
                c => c.Standable(ground) && !c.Fogged(ground), ground, 1000, out IntVec3 loose))
            {
                return loose;
            }
            return anchor.InBounds(ground) ? anchor : ground.Center;
        }
    }
}
