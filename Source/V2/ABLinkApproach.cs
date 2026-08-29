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
