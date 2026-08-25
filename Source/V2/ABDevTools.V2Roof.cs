using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// One-click answer for "the roof/floor did not change".
    ///
    /// WHY THIS EXISTS. A report of that shape has at least eight candidate causes split
    /// across two systems that this mod does NOT own: vanilla's roof WORK pipeline
    /// (area painted? roof holder in range? a colonist with Construction who can reach and
    /// reserve the cell?) and this mod's derive pipeline (is the terrain ours to rewrite?
    /// is the cell protected? occupied?). Guessing between them costs a full test cycle per
    /// guess, and three were already spent. This prints every gate in both systems for one
    /// clicked cell and names the first one that says no.
    ///
    /// Click the cell you expected to change. It works from either side: click a rooftop
    /// tile on a sky level and the CELL BELOW section explains what governs it; click the
    /// roof cell on the level below and the CELL ABOVE section does.
    /// </summary>
    public static partial class ABDevTools
    {
        // ⚠ RENAMED FROM "AB2: roof + floor probe". The debug PALETTE stores pinned entries
        // as a backslash-joined path string and could not resolve this one - run #460 logged
        // `Could not find node from path 'As above\AB2: roof + floor probe'. Removing.` while
        // every other action in this category pinned fine. The `+` is the only character this
        // label had that no other AB2 action uses, so the probe that exists to save a test
        // cycle was the one tool that could not be put one click away. Keep action labels to
        // letters, digits, spaces and colons.
        [DebugAction("As above", "AB2: roof and floor probe", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2RoofProbe()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                Messages.Message("AB2: probe cell is out of bounds.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(ABLog.Tag).AppendLine(" V2 roof + floor probe");
            ABBandMap bands = ABBands.CompOf(map);

            // ---- what is actually here ------------------------------------------------
            sb.Append("CELL ").Append(c);
            if (bands != null && bands.Banded)
            {
                sb.Append("  band=").Append(bands.BandOf(c))
                    .Append(" (level ").Append(bands.BandOf(c) - bands.surfaceBand).Append(")")
                    .Append(bands.InGutter(c) ? " IN GUTTER" : string.Empty);
            }
            else
            {
                sb.Append("  (map is NOT banded)");
            }
            sb.AppendLine();
            sb.Append("  terrain=").Append(map.terrainGrid.TerrainAt(c)?.defName ?? "null")
                .Append("  roof=").Append(map.roofGrid.RoofAt(c)?.defName ?? "none")
                .Append("  edifice=").Append(c.GetEdifice(map)?.def?.defName ?? "none")
                .AppendLine();

            // ---- vanilla's roof WORK pipeline ------------------------------------------
            bool buildArea = map.areaManager.BuildRoof[c];
            bool noRoofArea = map.areaManager.NoRoof[c];
            bool roofed = c.Roofed(map);
            sb.Append("AREAS  BuildRoof=").Append(buildArea)
                .Append("  NoRoof=").Append(noRoofArea)
                .Append("  currentlyRoofed=").Append(roofed).AppendLine();

            sb.Append("BUILD-ROOF JOB: ");
            if (!buildArea)
            {
                sb.AppendLine("no job - cell is not in a Build roof area. Paint one here.");
            }
            else if (roofed)
            {
                sb.AppendLine("no job - already roofed.");
            }
            else if (!RoofCollapseUtility.WithinRangeOfRoofHolder(c, map))
            {
                sb.AppendLine("no job - no roof holder within 6.9 cells."
                    + " Vanilla grows roof outward from a holder: build a wall or column"
                    + " nearer, or roof the cells beside the holder first.");
            }
            else if (!RoofCollapseUtility.ConnectedToRoofHolder(c, map, true))
            {
                sb.AppendLine("no job - not connected to a roof holder through roofed cells."
                    + " This is vanilla's incremental rule, not a mod limit.");
            }
            else
            {
                Thing blocker = RoofUtility.FirstBlockingThing(c, map);
                sb.Append("OFFERED").Append(blocker != null
                    ? " (blocked by " + blocker.def.defName + ", pawn must clear it first)"
                    : string.Empty).AppendLine();
            }

            sb.Append("REMOVE-ROOF JOB: ");
            if (!noRoofArea)
            {
                sb.AppendLine("no job - cell is not in a Remove roof area. Paint one here."
                    + " NOTE: paint it on the level the ROOF is on, which for a sky platform"
                    + " is the level BELOW the platform.");
            }
            else if (!roofed)
            {
                sb.AppendLine("no job - nothing roofed here to remove.");
            }
            else
            {
                sb.AppendLine("OFFERED");
            }

            // ---- is anyone able to take it --------------------------------------------
            int builders = 0;
            int canDo = 0;
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn p = colonists[i];
                if (p.workSettings == null
                    || !p.workSettings.WorkIsActive(WorkTypeDefOf.Construction))
                {
                    continue;
                }
                builders++;
                if (p.CanReserve(c, 1, -1, ReservationLayerDefOf.Ceiling)
                    && p.CanReach(c, PathEndMode.Touch, Danger.Deadly))
                {
                    canDo++;
                }
            }
            sb.Append("WORKERS  colonists=").Append(colonists.Count)
                .Append("  withConstruction=").Append(builders)
                .Append("  ableToReachAndReserve=").Append(canDo).AppendLine();
            if (builders == 0)
            {
                sb.AppendLine("  WARNING: NOBODY has Construction enabled - roof work can never"
                    + " start. Roof zones are ordinary construction work and are NOT"
                    + " instant, even in god mode.");
            }

            // ---- this mod's derive pipeline, both directions ---------------------------
            sb.Append("CELL ABOVE (what roofing/unroofing HERE does to the level above): ")
                .AppendLine(ABSkySync.DebugSyncInfo(map, c));
            if (bands != null && bands.Banded)
            {
                int bandBelow = bands.BandOf(c) - 1;
                if (bands.BandExists(bandBelow))
                {
                    IntVec3 below = bands.Translate(c, bandBelow);
                    sb.Append("CELL BELOW (what governs THIS cell, from ").Append(below)
                        .Append("): ").AppendLine(ABSkySync.DebugSyncInfo(map, below));
                }
            }

            Log.Warning(sb.ToString());
            Messages.Message("AB2: roof probe written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
