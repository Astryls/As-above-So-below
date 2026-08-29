using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WHERE A VERTICAL LINK IS ENTERED AND LEFT: one tagged cell per def, per rotation.
    ///
    /// ⚠ THE PROBLEM THIS EXISTS TO FIX. Departures already walked onto the notch
    /// (ABWormholePather.EntryCellFor derived the edge from Rotation), but ARRIVALS never
    /// asked the question at all: LandingCell set the pawn down on `far.Position`, which for
    /// a 2x2 or 3x3 link is a cell INSIDE the footprint - i.e. inside a Building_Door, which
    /// carries a door's path cost and a door's "get out of the way" semantics - and its only
    /// fallback was a random standable cell BESIDE the stairs. Field report: "they stall on
    /// the building, or they're on the sides and it feels wrong."
    ///
    /// So an arrival now lands on the tile IN FRONT OF THE OPENING, which is also what makes
    /// the emerge clip legible: the pawn is drawn climbing out of the mouth and stepping off
    /// the art, instead of materialising in the middle of it.
    ///
    /// ⚠⚠ THE OPEN SIDE IS A PROPERTY OF THE ART, NOT OF THE ROTATION, AND THE ART IS KNOWN
    /// TO BE INCONSISTENT (§77c: the grand staircase's east and west sprites are the north
    /// composition unrotated; AB_LadderUp_east is a sliver drawn off its own cell). Deriving
    /// the edge from Rotation.Opposite is right for honest art and wrong for exactly those
    /// sprites, and no amount of C# can tell which is which - only looking at the PNG can.
    /// Hence the table: 6 defs x 4 rotations, tagged by hand in Tools/LinkApproachTagger.html
    /// and pasted into each def's ABBandStairsExt as `approachCells`.
    ///
    /// ⚠ AN UNTAGGED DEF IS NOT A BROKEN DEF. Every lookup falls back to the derived default,
    /// which reproduces the pre-existing edge math EXACTLY (opposite edge, CenterCell along
    /// it). Shipping the table half-filled changes nothing for the rows that are missing.
    ///
    /// ⚠ ELEVATORS ARE EXCLUDED BY DESIGN (user's call). AB2_Elevator is Graphic_Single with
    /// no open side to point at, and its car is entered from every direction; it keeps the
    /// old land-on-the-anchor behaviour.
    ///
    /// COORDINATES: the tag is (dx,dz) from the footprint's SOUTH-WEST corner, so it survives
    /// the even-size Position quirk (GenAdj.AdjustForRotation means Position is NOT the
    /// geometric centre of a 2x2 - deriving anything from it lands a cell off on half the
    /// rotations). It always names a cell exactly one step OUTSIDE one edge; a diagonal or
    /// interior tag is rejected and the derived default is used instead.
    /// </summary>
    public struct ABApproach
    {
        /// <summary>The footprint cell at the opening - what a pawn stands on to start a
        /// descent (§78c: ON the art, so the climb clip starts on the treads).</summary>
        public IntVec3 mouth;

        /// <summary>The cell just outside the opening - where an arrival is set down.</summary>
        public IntVec3 outside;

        /// <summary>Unit step, mouth -> outside. Also the direction the opening faces.</summary>
        public IntVec3 outward;

        /// <summary>True when this came from the def table rather than the rotation math.
        /// Diagnostics only; nothing branches on it.</summary>
        public bool tagged;
    }

    public static class ABLinkApproach
    {
        /// <summary>Links whose art has an open side worth pointing at: everything except
        /// the elevator.</summary>
        public static bool Directional(Thing link)
        {
            if (link?.def == null || !link.def.rotatable)
            {
                return false;
            }
            return !(link is Building_ABStairs2 s) || !s.LinksAllLevels;
        }

        public static bool TryGet(Thing link, out ABApproach a)
        {
            a = default;
            if (link == null || !link.Spawned || !Directional(link))
            {
                return false;
            }
            CellRect r = link.OccupiedRect();
            if (r.Width <= 0 || r.Height <= 0)
            {
                return false;
            }
            List<IntVec2> tags = link.def.GetModExtension<ABBandStairsExt>()?.approachCells;
            if (tags != null && tags.Count == 4 && FromTag(r, tags[link.Rotation.AsInt & 3], ref a))
            {
                a.tagged = true;
                return true;
            }
            Derive(r, link.Rotation, ref a);
            return true;
        }

        /// <summary>
        /// The tagged cell, validated. A tag that is not exactly one step outside exactly one
        /// edge (an interior cell, a diagonal corner, a typo two cells out) is REFUSED rather
        /// than clamped - a silently corrected tag would look like the tagger lied.
        /// </summary>
        private static bool FromTag(CellRect r, IntVec2 tag, ref ABApproach a)
        {
            int dx = tag.x, dz = tag.z;
            bool inX = dx >= 0 && dx < r.Width;
            bool inZ = dz >= 0 && dz < r.Height;
            IntVec3 outward;
            if (inZ && dx == -1) outward = new IntVec3(-1, 0, 0);
            else if (inZ && dx == r.Width) outward = new IntVec3(1, 0, 0);
            else if (inX && dz == -1) outward = new IntVec3(0, 0, -1);
            else if (inX && dz == r.Height) outward = new IntVec3(0, 0, 1);
            else return false;

            a.outside = new IntVec3(r.minX + dx, 0, r.minZ + dz);
            a.outward = outward;
            a.mouth = a.outside - outward;
            return true;
        }

        /// <summary>
        /// The pre-table behaviour, verbatim: the art leads the way it faces and is entered
        /// from the OPPOSITE edge (measured - every *_south sprite has its notch on its NORTH
        /// edge), centred on the run via CellRect.CenterCell.
        /// </summary>
        private static void Derive(CellRect r, Rot4 rot, ref ABApproach a)
        {
            IntVec3 f = rot.Opposite.FacingCell;
            IntVec3 mouth;
            if (f.z > 0)      mouth = new IntVec3(r.CenterCell.x, 0, r.maxZ);
            else if (f.z < 0) mouth = new IntVec3(r.CenterCell.x, 0, r.minZ);
            else if (f.x > 0) mouth = new IntVec3(r.maxX, 0, r.CenterCell.z);
            else              mouth = new IntVec3(r.minX, 0, r.CenterCell.z);
            a.mouth = mouth;
            a.outward = f;
            a.outside = mouth + f;
            a.tagged = false;
        }

        /// <summary>
        /// WHERE A PAWN ORDERED TO USE THIS LINK SHOULD COME TO REST ON THE FAR SIDE: the
        /// tile just outside the opening, OFF the footprint. IntVec3.Invalid when there is
        /// no usable one, and then the caller must fall back to the link's own cell.
        ///
        /// ⚠⚠ THE ARRIVAL WAS ALREADY RIGHT; THE ORDER WAS NOT. §85's LandingCell has set
        /// transiting pawns down on this exact tile for two windows - but the float-menu
        /// travel order aimed its Goto at `far.Position`, a cell INSIDE the far link's own
        /// footprint. So the pawn crossed, landed correctly in front of the opening, and
        /// then its still-live job walked it BACK ONTO THE STAIRCASE and parked it there.
        /// Field report: "they should path off the building, not go to the building cell",
        /// most visible under Perspective Shift, where a pawn standing on the art is drawn
        /// inside the staircase rather than beside it.
        ///
        /// ⚠ RULE 57: THE ORDER AND THE LANDING MUST NOT EACH ANSWER "WHICH SIDE". This
        /// lives next to TryGet so both read the SAME per-def table. Deriving the tile a
        /// second time at the order site is how the two ends of one journey come to
        /// disagree about which edge is open.
        ///
        /// ⚠ ELEVATORS RETURN Invalid AND THAT IS THE FEATURE (user's call, §85). TryGet is
        /// false for a non-Directional link, so an elevator order keeps aiming at the car
        /// and the pawn arrives inside it, exactly as it always has.
        ///
        /// ⚠ OCCUPANCY IS DELIBERATELY NOT TESTED, twice over. Standable ignores pawns by
        /// design, and a pawn standing here at ORDER time says nothing about who is standing
        /// here when the traveller actually arrives - possibly hundreds of ticks later. A
        /// blocked tile makes a queue; that is the correct behaviour for a staircase
        /// (§85.19) and rejecting it would send the order back to the footprint for the one
        /// case that most wants to be off it.
        ///
        /// ⚠ SAME-BAND IS NOT REDUNDANT. A link built flush against a band edge has a
        /// neighbour cell that is a different LEVEL sharing an x/z edge; aiming a journey at
        /// it would route the pawn to the wrong band entirely.
        /// </summary>
        public static IntVec3 DisembarkCell(Thing link)
        {
            if (link == null || !link.Spawned)
            {
                return IntVec3.Invalid;
            }
            Map map = link.Map;
            if (map == null || !TryGet(link, out ABApproach a))
            {
                return IntVec3.Invalid; // elevator, or nothing rotatable to point at
            }
            IntVec3 c = a.outside;
            // Standable, NOT Walkable - the same distinction ApproachLanding draws for its
            // outside candidates. This cell is OFF the footprint, so walkable-but-not-
            // standable out here is somebody ELSE's doorway, and ending a journey parked in
            // one is the jam §85.4 exists to avoid.
            if (!c.InBounds(map) || !c.Standable(map)
                || !ABBands.SameBand(map, c, link.Position))
            {
                return IntVec3.Invalid;
            }
            return c;
        }

        /// <summary>
        /// The world point the transit clips are anchored on: the MIDDLE OF THE OPEN EDGE,
        /// not the middle of the building.
        ///
        /// ⚠ LATERALLY CENTRED ON PURPOSE, even though the tagged cell is not. On a 2x2 the
        /// opening spans both edge cells, so anchoring the clip on whichever one was tagged
        /// would draw the pawn emerging half a cell off the sprite's centreline. The tag
        /// answers "which cell does the body occupy"; this answers "where is the hole drawn".
        /// Identical to TrueCenter for a 1x1 ladder, which is what it used to be for
        /// everything.
        /// </summary>
        public static Vector3 MouthPoint(Thing link)
        {
            if (link == null)
            {
                return Vector3.zero;
            }
            Vector3 c = link.TrueCenter();
            if (!TryGet(link, out ABApproach a))
            {
                return c;
            }
            CellRect r = link.OccupiedRect();
            float half = (a.outward.x != 0 ? r.Width : r.Height) * 0.5f - 0.5f;
            return c + new Vector3(a.outward.x * half, 0f, a.outward.z * half);
        }
    }
}
