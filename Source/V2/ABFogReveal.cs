using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Reveals whatever a newly carved pocket broke into, the way vanilla reveals a cavern
    /// when the last wall between it and a mined tunnel comes down.
    ///
    /// ⚠ WHY THIS EXISTS AND WHY IT IS NOT `FloodFillerFog.FloodUnfog`.
    ///
    /// 1. THE ROOT MUST BE FOGGED. `FloodFillerFog.FloodUnfog` opens with
    ///    `FloodFiller.FloodFill(root, PassCheck, ...)`, and FloodFill's very first act is
    ///    `if (root.IsValid &amp;&amp; extraRoots == null &amp;&amp; !passCheck(root)) return;`. FloodUnfog's
    ///    PassCheck begins `if (!fogGridDirect.IsSet(index)) return false;` - so flooding
    ///    from an ALREADY UNFOGGED cell reveals exactly nothing and reports no error.
    ///    Vanilla never trips over this because every one of its call sites (both
    ///    `FogGrid.FloodUnfogAdjacent` overloads) tests `item.Fogged(map)` before it floods.
    ///    Building_ABStairs2 used to flood from a cell inside the landing apron - which
    ///    CarveLanding had just unfogged cell-by-cell - so the call was inert on every
    ///    single stair build. That is the whole "basements do not consistently defog when
    ///    built into a Biomes! Caverns cave" report: the pocket was lit, the cave it opened
    ///    onto stayed black, and the only thing that ever cleared it was the player later
    ///    mining a rock face that happened to touch the cave. Seed from the FOGGED FRONTIER
    ///    around the pocket, never from the pocket itself.
    ///
    /// 2. THE VANILLA FLOOD WOULD LEAK ACROSS THE GUTTER. FloodUnfog's PassCheck tests fog
    ///    and `edifice.def.MakeFog` and NOTHING ELSE - not walkability, not terrain. The
    ///    seam rows between two bands are impassable open air with every thing cleared out
    ///    (CarveGutters) and they are never unfogged, so to a fog flood they are a wide open
    ///    corridor running the full width of the map into the band above and the band below.
    ///    One breach next to a band edge could therefore black-flash the entire stack open.
    ///    Every walk here is clamped to `RectOfBand`, which the gutter is outside of.
    ///
    /// Connectivity, blocker handling, pawn wake and the area-revealed letter otherwise
    /// copy vanilla exactly: 4-way spread (`GenAdj.CardinalDirections`, matching
    /// FloodFiller's `CardinalDirectionsAround`), full-fillage edifices stop the spread but
    /// are themselves unfogged so the rock face draws instead of a black hole, and sleeping
    /// mechanoids in the revealed space get a ThreatBig letter.
    /// </summary>
    public static class ABFogReveal
    {
        /// <summary>Cells revealed before the neutral "area revealed" letter fires. Vanilla's
        /// own threshold. Vanilla also sends it whenever the reveal was off-screen, which is
        /// useless here - a far landing is on ANOTHER BAND and therefore always off-screen,
        /// so that clause would make every stairwell send a letter.</summary>
        private const int LetterCellThreshold = 600;

        /// <summary>Treats <paramref name="pocket"/> (an already-open, already-unfogged rect,
        /// i.e. a freshly carved stair landing) as a mining breach and reveals everything
        /// connected to it inside band <paramref name="band"/>. Returns the number of cells
        /// revealed, 0 if the pocket opened onto nothing but solid rock.</summary>
        public static int RevealBreach(Map map, ABBandMap bands, int band, CellRect pocket)
        {
            if (map == null || bands == null || !bands.BandExists(band))
            {
                return 0;
            }
            // Map generation fogs and re-fogs bands wholesale after every gen step, and the
            // fog grid is scribed, so a reveal outside play is at best wasted and at worst
            // undone. Vanilla's Notify_FogBlockerRemoved bails on the same test.
            if (Current.ProgramState != ProgramState.Playing)
            {
                return 0;
            }

            FogGrid fog = map.fogGrid;
            CellRect bandRect = bands.RectOfBand(band);
            CellIndices indices = map.cellIndices;
            HashSet<int> queued = new HashSet<int>();
            Queue<IntVec3> frontier = new Queue<IntVec3>();
            List<IntVec3> opened = new List<IntVec3>();
            bool mechanoid = false;

            bool Spreads(IntVec3 c)
            {
                if (!c.InBounds(map) || !bandRect.Contains(c) || !fog.IsFogged(c))
                {
                    return false;
                }
                Building edifice = c.GetEdifice(map);
                return edifice == null || !edifice.def.MakeFog;
            }

            void Enqueue(IntVec3 c)
            {
                if (Spreads(c) && queued.Add(indices.CellToIndex(c)))
                {
                    frontier.Enqueue(c);
                }
            }

            // Seed: the fogged, non-blocking ring around the pocket. 8-way here (a diagonal
            // touch is still a breach), 4-way once inside, matching the flood proper.
            foreach (IntVec3 p in pocket)
            {
                if (!p.InBounds(map))
                {
                    continue;
                }
                for (int i = 0; i < 8; i++)
                {
                    Enqueue(p + GenAdj.AdjacentCells[i]);
                }
            }

            while (frontier.Count > 0)
            {
                IntVec3 c = frontier.Dequeue();
                fog.Unfog(c);
                opened.Add(c);
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    Pawn pawn = things[i] as Pawn;
                    if (pawn == null)
                    {
                        continue;
                    }
                    // Same as FloodFillerFog's processor: whatever was asleep in the dark is
                    // now a thing the colony has walked in on.
                    if (pawn.mindState != null)
                    {
                        pawn.mindState.Active = true;
                    }
                    if (pawn.def?.race != null && pawn.def.race.IsMechanoid)
                    {
                        mechanoid = true;
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    Enqueue(c + GenAdj.CardinalDirections[i]);
                }
            }

            // Blockers bounding the revealed space: unfogged but not spread through, so the
            // player sees a rock wall rather than the black edge of the fog. Vanilla does
            // this both for the flood's own frontier and around the breach cell itself, so
            // the landing pocket gets the same treatment as the cave beyond it.
            for (int i = 0; i < opened.Count; i++)
            {
                RevealBoundingWalls(map, fog, bandRect, opened[i]);
            }
            foreach (IntVec3 p in pocket)
            {
                if (p.InBounds(map) && !fog.IsFogged(p))
                {
                    RevealBoundingWalls(map, fog, bandRect, p);
                }
            }

            if (opened.Count > 0)
            {
                ABLog.Dev("Landing breach on band " + band + " revealed " + opened.Count
                    + " cells" + (mechanoid ? " (mechanoids present)." : "."));
                TargetInfo where = new TargetInfo(pocket.CenterCell, map);
                if (mechanoid)
                {
                    Find.LetterStack.ReceiveLetter("LetterLabelAreaRevealed".Translate(),
                        "AreaRevealedWithMechanoids".Translate(), LetterDefOf.ThreatBig, where);
                }
                else if (opened.Count >= LetterCellThreshold)
                {
                    Find.LetterStack.ReceiveLetter("LetterLabelAreaRevealed".Translate(),
                        "AreaRevealed".Translate(), LetterDefOf.NeutralEvent, where);
                }
            }
            return opened.Count;
        }

        /// <summary>
        /// A pawn just stepped out of a vertical link onto another band: reveal what it can
        /// see, exactly as walking through any other door would.
        ///
        /// ⚠ WHY VANILLA DOES NOT DO THIS FOR US, EVEN THOUGH THE STAIRS ARE A DOOR.
        /// `Building_ABStairs2` derives from `Building_Door`, and vanilla's door traffic does
        /// unfog: `Building_Door` calls `FogGrid.Notify_PawnEnteringDoor`, which floods from
        /// `door.Position`. Both halves of that miss here, and each one alone would be enough
        /// to make the feature silently do nothing:
        ///   1. WRONG BAND. It floods from the door the pawn ENTERED, which is the near
        ///      anchor on the band it just left. The fog the player wants lifted is around
        ///      the FAR anchor, hundreds of cells away in z, and the flood is walkable-space
        ///      based so it can never get there.
        ///   2. WRONG ROOT ANYWAY. The near anchor is standing in the player's own colony and
        ///      is therefore already unfogged - and per this file's lesson 1, a fog flood
        ///      seeded on an unfogged root returns immediately and reveals nothing, with no
        ///      error. So the vanilla call was inert on both counts.
        /// The transit is a teleport, so nothing about the arrival looks like door traffic to
        /// the engine and no other vanilla hook fires either. Hence this.
        ///
        /// ⚠ THE LANDING CELL MUST BE OPENED BEFORE FLOODING, same trap as lesson 1: the pawn
        /// arrives INTO fog, so the cell it now stands on is the one cell that must not be the
        /// flood root while still fogged.
        ///
        /// Faction gate copies `Notify_PawnEnteringDoor` exactly (player or player-hosted),
        /// so a raider taking the stairs does not map the colony's basement for them.
        /// </summary>
        public static int RevealArrival(Pawn pawn, IntVec3 at)
        {
            if (pawn == null || Current.ProgramState != ProgramState.Playing)
            {
                return 0;
            }
            if (pawn.Faction != Faction.OfPlayer && pawn.HostFaction != Faction.OfPlayer)
            {
                return 0;
            }
            Map map = pawn.Map;
            if (map == null || !at.InBounds(map))
            {
                return 0;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return 0;
            }
            int band = bands.BandOf(at);
            if (!bands.BandExists(band) || bands.InGutter(at))
            {
                return 0;
            }
            if (map.fogGrid.IsFogged(at))
            {
                map.fogGrid.Unfog(at);
            }
            return RevealBreach(map, bands, band, CellRect.SingleCell(at));
        }

        private static void RevealBoundingWalls(Map map, FogGrid fog, CellRect bandRect, IntVec3 c)
        {
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map) || !bandRect.Contains(n) || !fog.IsFogged(n))
                {
                    continue;
                }
                Building edifice = n.GetEdifice(map);
                if (edifice != null && edifice.def.MakeFog)
                {
                    fog.Unfog(n);
                }
            }
        }
    }
}
