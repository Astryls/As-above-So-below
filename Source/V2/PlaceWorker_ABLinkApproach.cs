using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Refuses to place a vertical link that pawns could not actually walk into.
    ///
    /// THE BUG THIS CLOSES. Pawns stalled near stairwells for seconds at a time, retrying
    /// endlessly. The cause is placement geometry, not the link: a stairwell flush against
    /// walls can be left with only DIAGONAL approaches, and
    /// <c>PathUtility.BlocksDiagonalMovement</c> trips on unwalkable cells and does NOT
    /// special-case doors. The pawn paths to the anchor, cannot take the final step,
    /// <c>Pawn_PathFollower.ResetToCurrentPosition()</c> fires (its signature is
    /// <c>nextCell == pawn.Position</c>, which is exactly what the stuck watchdog reported),
    /// the path is requested again, and it loops until something else moves.
    ///
    /// The links themselves are NOT the blocker and were ruled out by reading the code:
    /// <c>Building_ABStairs2</c> overrides <c>FreePassage => true</c> and
    /// <c>PawnCanOpen => true</c>, and <c>Building_Door.BlocksPawn</c> is
    /// <c>openInt ? false : !PawnCanOpen(p)</c> - so it returns false unconditionally and a
    /// pawn can never be blocked BY a link. Only the cells around it can do that.
    ///
    /// So this is a placement-time guard rather than a pather fix: catch the bad geometry
    /// when the player draws it, at the one moment they can trivially move it one cell.
    ///
    /// WHAT COUNTS AS AN APPROACH. Any cardinal neighbour of any footprint cell that a pawn
    /// could stand in. Deliberately generous in two directions:
    ///  - Cells under a MINE designation count. Placing a ladder inside a mountain you are
    ///    about to hollow out is normal play, and rejecting it would be infuriating.
    ///  - Only ONE approach is required. Two is better and the footprints are sized for it
    ///    (1x1 = 4, 2x2 = 8, 3x3 = 12 cardinal approaches), but demanding more would reject
    ///    legitimate corridor and doorway placements.
    ///
    /// Pending blueprints and frames of impassable things are treated as blocking, copying
    /// PlaceWorker_Cooler: sealing a stairwell in with planned walls is precisely the
    /// mistake that produced the bug, and it should be caught while it is still a plan.
    ///
    /// The FAR side is deliberately not validated. The counterpart lands in a band that is
    /// usually still solid rock - opening it is the entire point of building the link - so
    /// requiring clear approaches over there would reject the normal case.
    /// </summary>
    public class PlaceWorker_ABLinkApproach : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 center,
            Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (map == null || def == null)
            {
                return true;
            }

            // A LINK THAT CANNOT CONNECT ANYWHERE IS REFUSED HERE, at the moment the
            // player can still change their mind - not warned about after a colonist has
            // hauled the materials and built it. The old shape (§29e) was a RejectInput
            // message fired from SpawnSetup's TryEstablish: "no level in that direction",
            // delivered AFTER the work was spent, with the finished ladder-to-nowhere
            // left standing. That message survives as the backstop for paths that bypass
            // PlaceWorkers entirely (dev spawns, quest spawns); this gate is the fix.
            // CLAMP AT SELECTION (§35) applies to placement too.
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return new AcceptanceReport("AB_NotBandedHere".Translate());
            }
            ABBandStairsExt ext = def.GetModExtension<ABBandStairsExt>();
            if (ext != null && !ext.linksAllLevels)
            {
                int target = bands.BandOf(center) + ext.levelDelta;
                if (!bands.BandExists(target))
                {
                    return new AcceptanceReport((ext.levelDelta > 0
                        ? "AB_NoLevelAbove"
                        : "AB_NoLevelBelow").Translate());
                }
            }
            // The elevator needs no existence check on a banded map: banded means at
            // least two bands, so there is always another level to serve.

            CellRect footprint = GenAdj.OccupiedRect(center, rot, def.Size);
            if (AnyApproach(footprint, map, thingToIgnore))
            {
                return true;
            }
            return new AcceptanceReport("AB_LinkNeedsApproach".Translate());
        }

        /// <summary>Outlines the cells a pawn will be able to walk in from, so a bad spot is
        /// visible before the click rather than only after the rejection message.</summary>
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol,
            Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null || def == null)
            {
                return;
            }
            CellRect footprint = GenAdj.OccupiedRect(center, rot, def.Size);
            // Reused across frames: DrawGhost runs every frame for as long as the placement
            // cursor is up, and DrawFieldEdges only reads the list. One allocation per frame
            // of an at-most-perimeter-sized list is small but it is pure garbage, and this
            // is the one draw path in the mod that a player holds open for minutes.
            ghostOpen.Clear();
            foreach (IntVec3 c in ApproachCells(footprint))
            {
                if (IsUsableApproach(c, map, thing))
                {
                    ghostOpen.Add(c);
                }
            }
            if (ghostOpen.Count > 0)
            {
                GenDraw.DrawFieldEdges(ghostOpen, Color.white);
            }
        }

        /// <summary>Scratch for <see cref="DrawGhost"/>. Main thread only - PlaceWorkers are
        /// drawn from the UI pass and never off it.</summary>
        private static readonly List<IntVec3> ghostOpen = new List<IntVec3>();

        private static bool AnyApproach(CellRect footprint, Map map, Thing thingToIgnore)
        {
            foreach (IntVec3 c in ApproachCells(footprint))
            {
                if (IsUsableApproach(c, map, thingToIgnore))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Every cardinal neighbour of the footprint, excluding the footprint
        /// itself. Diagonals are excluded ON PURPOSE - a diagonal-only approach is the exact
        /// geometry that causes the stall, so counting it would defeat the check.</summary>
        private static IEnumerable<IntVec3> ApproachCells(CellRect footprint)
        {
            foreach (IntVec3 c in footprint)
            {
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[i];
                    if (!footprint.Contains(n))
                    {
                        yield return n;
                    }
                }
            }
        }

        private static bool IsUsableApproach(IntVec3 c, Map map, Thing thingToIgnore)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            // Rock the player has already marked for mining is about to become floor.
            if (map.designationManager.DesignationAt(c, DesignationDefOf.Mine) != null)
            {
                return true;
            }
            if (c.Impassable(map))
            {
                return false;
            }
            // Planned walls block just as surely as built ones - and catching it now is the
            // whole point, because once they are built the stairwell is already jammed.
            if (PlannedImpassable(c.GetFirstThing<Blueprint>(map), thingToIgnore)
                || PlannedImpassable(c.GetFirstThing<Frame>(map), thingToIgnore))
            {
                return false;
            }
            return true;
        }

        private static bool PlannedImpassable(Thing t, Thing thingToIgnore)
        {
            if (t == null || t == thingToIgnore)
            {
                return false;
            }
            ThingDef built = (t.def?.entityDefToBuild) as ThingDef;
            return built != null && built.passability == Traversability.Impassable;
        }
    }
}
