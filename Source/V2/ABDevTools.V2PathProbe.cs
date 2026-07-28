using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// "Why is THIS pawn stuck here?" - a probe for the selected pawn.
    ///
    /// The failure this exists to catch is the one the architecture is most exposed to:
    /// Reachability is REGION based (a region is a cell set), while path production is CELL
    /// based and additionally refuses to cut a diagonal corner when either flanking cell is
    /// unwalkable (PathUtility.BlocksDiagonalMovement, applied in Pawn_PathFollower). A sky
    /// band is full of impassable AB_OpenAir holes, so a stairwell can easily sit where the
    /// only geometric link is diagonal: the region graph says CONNECTED, the pathfinder says
    /// NotFound, PathRequest.ValidateInt keeps letting the job through because it gates on
    /// CanReach, and the pawn re-issues forever while standing still on a corner.
    ///
    /// CanReach=True together with path=NOT FOUND is the signature. It cannot be inferred
    /// from watching the pawn, because a pawn blocked by traffic looks identical.
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: why is this pawn stuck",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2WhyStuck()
        {
            Pawn pawn = Find.Selector?.SingleSelectedThing as Pawn;
            if (pawn == null)
            {
                Messages.Message("AB2: select exactly one pawn first.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            Map map = pawn.Map;
            if (map == null)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(pawn.LabelShortCap + " at " + pawn.Position
                + " band " + ABBands.BandOf(map, pawn.Position));
            sb.AppendLine("  job=" + (pawn.CurJob?.def?.defName ?? "none")
                + " moving=" + (pawn.pather != null && pawn.pather.Moving)
                + " carrying=" + (pawn.carryTracker?.CarriedThing?.LabelCap ?? "nothing"));
            sb.AppendLine("  pendingTransit=" + ABWormholePather.HasPending(pawn));

            LocalTargetInfo dest = pawn.CurJob?.targetA ?? LocalTargetInfo.Invalid;
            if (pawn.pather != null && pawn.pather.Destination.IsValid)
            {
                dest = pawn.pather.Destination;
            }

            if (dest.IsValid)
            {
                IntVec3 c = dest.Cell;
                sb.AppendLine("  destination " + c + " band " + ABBands.BandOf(map, c));

                bool canReach = pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly);
                sb.AppendLine("  CanReach = " + canReach);

                PawnPath path = null;
                try
                {
                    path = map.pathFinder.FindPathNow(pawn.Position, dest,
                        TraverseParms.For(pawn), null, PathEndMode.OnCell);
                    sb.AppendLine("  FindPathNow = " + (path != null && path.Found
                        ? "FOUND (" + path.NodesLeftCount + " nodes)"
                        : "NOT FOUND"));
                }
                finally
                {
                    // The pool is small; never leak a probe path.
                    if (path != null) path.ReleaseToPool();
                }

                if (canReach)
                {
                    sb.AppendLine("  >> CanReach=True with NOT FOUND means the region graph is"
                        + " connected but no walkable route exists (usually a diagonal-only"
                        + " link past an impassable cell). That is the re-issue loop.");
                }
            }
            else
            {
                sb.AppendLine("  no valid destination");
            }

            // The 8 neighbours: what actually constrains movement off this cell.
            sb.AppendLine("  neighbours (walkable / terrain / edifice):");
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = pawn.Position + GenAdj.AdjacentCells[i];
                if (!n.InBounds(map))
                {
                    sb.AppendLine("    " + n + "  OUT OF BOUNDS");
                    continue;
                }
                Building edifice = n.GetEdifice(map);
                sb.AppendLine("    " + n
                    + "  walkable=" + n.Walkable(map)
                    + "  terrain=" + map.terrainGrid.TerrainAt(n).defName
                    + "  edifice=" + (edifice != null ? edifice.def.defName : "-")
                    + "  band=" + ABBands.BandOf(map, n));
            }

            Log.Warning(ABLog.Tag + " V2 stuck probe:\n" + sb);
            Messages.Message("AB2: stuck probe written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
