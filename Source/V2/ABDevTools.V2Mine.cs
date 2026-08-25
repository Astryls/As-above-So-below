using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// One-click answer for "right-click mine says No path on a rock I can clearly walk up to".
    ///
    /// \u26a0 THE FIRST THING TO KNOW, BECAUSE IT REDIRECTS THE WHOLE INVESTIGATION: THAT
    /// "No path" IS NOT A PATHFINDING RESULT AND NOTHING CROSS-BAND IS INVOLVED IN
    /// PRODUCING IT. The float menu shows it when `MineAIUtility.JobOnThing` returns null
    /// after calling `JobFailReason.Is(NoPathTrans)`, and the only way to reach that line is
    /// to fail BOTH of its adjacency loops:
    ///
    ///   loop 1  some cell of the 8 around the rock is `Standable` (terrain passable AND no
    ///           thing there whose passability is anything other than Standable);
    ///   loop 2  failing that, some cell is `WalkableBy` the pawn but NOT `Standable`, and
    ///           holds a haulable PassThroughOnly thing that can be hauled aside first.
    ///
    /// `pawn.CanReach` is never consulted. So a No-path on an exposed rock is a statement
    /// about the eight cells touching it, not about stairs, wormholes, regions or bands -
    /// and the productive question is "which neighbour did the engine reject, and why",
    /// which is exactly what this prints.
    ///
    /// \u26a0 AND `Walkable` IS NOT `Standable`. That gap is the single most likely culprit on
    /// this mod's maps and it is invisible in game: a cell holding a stalagmite, a cave
    /// plant, a chunk, a pillar or any other PassThroughOnly thing is walkable, looks open,
    /// and is NOT standable. The per-neighbour table below prints both flags side by side
    /// and names the thing responsible, so the difference stops being a guess.
    ///
    /// Usage: select the pawn you tried to order, then click the rock. Works on any band.
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: mining probe", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2MineProbe()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                Messages.Message("AB2: mining probe cell is out of bounds.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(ABLog.Tag).AppendLine(" V2 mining probe");
            ABBandMap bands = ABBands.CompOf(map);

            Mineable rock = c.GetFirstMineable(map);
            sb.Append("CLICKED ").Append(c);
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

            if (rock == null)
            {
                sb.AppendLine("  no Mineable here - nothing for WorkGiver_Miner to target.");
                Emit(sb);
                return;
            }
            sb.Append("  mineable=").Append(rock.def.defName)
                .Append(" terrain=").Append(map.terrainGrid.TerrainAt(c)?.defName ?? "null")
                .AppendLine();

            // ---- the two gates BEFORE the adjacency scan -------------------------------
            bool mineDesig = map.designationManager
                .DesignationAt(rock.Position, DesignationDefOf.Mine) != null;
            bool veinDesig = map.designationManager
                .DesignationAt(rock.Position, DesignationDefOf.MineVein) != null;
            sb.Append("  designation: Mine=").Append(mineDesig)
                .Append(" MineVein=").Append(veinDesig).AppendLine();
            if (!mineDesig && !veinDesig)
            {
                sb.AppendLine("  >> JobOnThing returns null HERE, with NO fail reason, so the"
                    + " float menu shows no mining option at all (not 'No path').");
            }
            // ShouldSkip hides the whole work giver unless SOMETHING on the map is designated.
            bool anyDesig = map.designationManager
                    .AnySpawnedDesignationOfDef(DesignationDefOf.Mine)
                || map.designationManager
                    .AnySpawnedDesignationOfDef(DesignationDefOf.MineVein);
            sb.Append("  map has any mine designation (WorkGiver_Miner.ShouldSkip): ")
                .Append(anyDesig).AppendLine();

            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null)
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    pawn = p;
                    break;
                }
            }
            if (pawn == null)
            {
                sb.AppendLine("  no pawn selected and no free colonist - stopping.");
                Emit(sb);
                return;
            }
            sb.Append("PAWN ").Append(pawn.LabelShortCap).Append(" at ").Append(pawn.Position);
            if (bands != null && bands.Banded)
            {
                sb.Append(" band=").Append(bands.BandOf(pawn.Position));
            }
            sb.AppendLine();
            sb.Append("  CanReserve=").Append(pawn.CanReserve(rock, 1, -1, null, true))
                .AppendLine();

            // ---- the adjacency scan, printed cell by cell ------------------------------
            //
            // This is the table the whole tool exists for. Every column is one of the exact
            // tests MineAIUtility runs, in its order, so a row that fails can be read off
            // against the engine's own logic instead of inferred.
            bool loop1 = false;
            bool loop2 = false;
            sb.AppendLine("NEIGHBOURS (loop 1 needs InBounds + Standable + adjacent):");
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = rock.Position + GenAdj.AdjacentCells[i];
                sb.Append("  ").Append(n);
                if (!n.InBounds(map))
                {
                    sb.AppendLine("  OUT OF BOUNDS");
                    continue;
                }
                bool gutter = bands != null && bands.Banded && bands.InGutter(n);
                bool walkable = n.Walkable(map);
                bool standable = n.Standable(map);
                bool immediate = ReachabilityImmediate.CanReachImmediate(n, rock, map,
                    PathEndMode.Touch, pawn);
                sb.Append("  terrain=")
                    .Append(map.terrainGrid.TerrainAt(n)?.defName ?? "null")
                    .Append(gutter ? " GUTTER" : string.Empty)
                    .Append("  walkable=").Append(walkable)
                    .Append(" standable=").Append(standable)
                    .Append(" reachImmediate=").Append(immediate);

                if (walkable && !standable)
                {
                    // The interesting row: name the blocker, because "walkable but not
                    // standable" is always some THING and the player cannot see which.
                    List<Thing> things = n.GetThingList(map);
                    for (int k = 0; k < things.Count; k++)
                    {
                        if (things[k].def.passability != Traversability.Standable)
                        {
                            sb.Append("  BLOCKED BY ").Append(things[k].def.defName)
                                .Append(" (passability=").Append(things[k].def.passability)
                                .Append(", designateHaulable=")
                                .Append(things[k].def.designateHaulable).Append(")");
                            if (things[k].def.designateHaulable
                                && things[k].def.passability == Traversability.PassThroughOnly)
                            {
                                loop2 = true;
                            }
                        }
                    }
                }
                if (standable && immediate)
                {
                    loop1 = true;
                    sb.Append("  <== SATISFIES LOOP 1");
                }
                sb.AppendLine();
            }

            // ---- verdict ---------------------------------------------------------------
            sb.AppendLine("VERDICT:");
            if (loop1)
            {
                sb.AppendLine("  loop 1 passes -> MineAIUtility returns a Mine job. If the float"
                    + " menu still said 'No path', the option you clicked was for a DIFFERENT"
                    + " thing (see the click-through note below).");
            }
            else if (loop2)
            {
                sb.AppendLine("  loop 1 fails, loop 2 passes -> the pawn is offered a HAUL job"
                    + " first, to clear the blocker. Not an error.");
            }
            else
            {
                sb.AppendLine("  >> BOTH LOOPS FAIL -> JobFailReason 'No path'. This is the"
                    + " reported bug. Every neighbour above is either out of bounds, gutter,"
                    + " unwalkable rock, or blocked by a non-haulable thing.");
            }

            // Click-through is the other way a No-path option appears over an exposed rock:
            // GenUI.ThingsUnderMouse is extended to see through open air, so a click on a
            // sky cell can produce menu entries for a rock on the level BELOW, which may be
            // fully buried. Printing the alternative target makes that unambiguous.
            if (bands != null && bands.Banded)
            {
                int below = bands.BandOf(c) - 1;
                if (bands.BandExists(below)
                    && ABBands.ShowsBelow(map.terrainGrid.TerrainAt(c)))
                {
                    IntVec3 seeThrough = bands.Translate(c, below);
                    Mineable other = seeThrough.InBounds(map)
                        ? seeThrough.GetFirstMineable(map)
                        : null;
                    sb.Append("CLICK-THROUGH: this cell shows the level below at ")
                        .Append(seeThrough).Append("; mineable there = ")
                        .AppendLine(other != null ? other.def.defName : "none");
                    if (other != null)
                    {
                        sb.AppendLine("  >> the float menu builds mining options for BOTH"
                            + " rocks. A 'No path' entry may belong to the buried one.");
                    }
                }
            }

            Emit(sb);
        }

        private static void Emit(StringBuilder sb)
        {
            Log.Warning(sb.ToString());
            Messages.Message("AB2: mining probe written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
