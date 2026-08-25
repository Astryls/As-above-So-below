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
        /// <summary>`Pawn_PathFollower.peMode` is private. Read it rather than assuming,
        /// because the mode IS the question: OnCell and Touch disagree on every impassable
        /// target. Cached once; null means we fall back to Touch, which is what every
        /// construction and haul WorkGiver uses.</summary>
        private static readonly System.Reflection.FieldInfo StuckProbePeModeField =
            typeof(Pawn_PathFollower).GetField("peMode",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);

        /// <summary>The inputs `Region.DangerFor` actually reads, for one cell. Every access
        /// is guarded: an unwalkable cell has no region and a region can outlive its room,
        /// and a probe that throws tells you nothing.</summary>
        private static string StuckProbeDangerLine(Map map, IntVec3 c, Pawn pawn)
        {
            if (!c.IsValid || !c.InBounds(map))
            {
                return "out of bounds";
            }
            Region reg = c.GetRegion(map, RegionType.Set_All);
            if (reg == null)
            {
                return "NO REGION (cell is unwalkable or outside the region grid)";
            }
            Room room = reg.Room;
            if (room == null)
            {
                return "region " + reg.id + " but NO ROOM";
            }
            return "room temp " + room.Temperature.ToString("F1") + "C"
                + ", vacuum " + room.Vacuum.ToString("F2")
                + ", regionDanger=" + reg.DangerFor(pawn);
        }

        [DebugAction("As above", "AB2: why is this pawn stuck",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2WhyStuck()
        {
            Pawn pawn = Find.Selector?.SingleSelectedThing as Pawn;
            var sb = new StringBuilder();

            // ⚠ NEVER BAIL SILENTLY FROM A DIAGNOSTIC. This guard used to call
            // Messages.Message, which is an in-game TOAST and never reaches the log
            // bridge. From outside the game "the user never ran it" and "it ran and
            // found nothing" were then indistinguishable, and that ambiguity burned a
            // whole test cycle chasing a pawn that was never probed. Selecting exactly
            // one pawn is also the wrong thing to demand while hunting a pawn that is
            // stuck: the interesting pawn may be on another band, or the click may land
            // on the level below. No selection now means census the colony, then
            // auto-pick the pawn that most looks stuck.
            if (pawn == null)
            {
                Map cur = Find.CurrentMap;
                if (cur == null)
                {
                    Log.Warning(ABLog.Tag + " V2 stuck probe: no current map.");
                    return;
                }
                sb.AppendLine("NO SINGLE PAWN SELECTED. Colony census first, then an "
                    + "auto-picked target.");
                var candidates = cur.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
                Pawn best = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Pawn p = candidates[i];
                    bool pMoving = p.pather != null && p.pather.Moving;
                    IntVec3 pDest = p.pather != null && p.pather.Destination.IsValid
                        ? p.pather.Destination.Cell
                        : IntVec3.Invalid;
                    sb.AppendLine("  " + p.LabelShortCap + " at " + p.Position
                        + " band " + ABBands.BandOf(cur, p.Position)
                        + " | job=" + (p.CurJob?.def?.defName ?? "none")
                        + " moving=" + pMoving
                        + " dest=" + (pDest.IsValid
                            ? pDest + " band " + ABBands.BandOf(cur, pDest)
                            : "-"));
                    // Stopped dead while STILL HOLDING a destination is the exact
                    // signature of the re-issue loop we are hunting.
                    if (best == null && !pMoving && pDest.IsValid && pDest != p.Position)
                    {
                        best = p;
                    }
                }
                if (best == null && candidates.Count > 0)
                {
                    best = candidates[0];
                    sb.AppendLine("  >> nobody matched the stuck signature (stopped while "
                        + "holding a destination); falling back to the first pawn.");
                }
                if (best == null)
                {
                    Log.Warning(ABLog.Tag + " V2 stuck probe:\n" + sb
                        + "  no player pawns spawned on this map.");
                    return;
                }
                sb.AppendLine("  >> auto-picked " + best.LabelShortCap);
                sb.AppendLine();
                pawn = best;
            }

            Map map = pawn.Map;
            if (map == null)
            {
                Log.Warning(ABLog.Tag + " V2 stuck probe: " + pawn.LabelShortCap
                    + " is not spawned on a map.");
                return;
            }

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

                // ⚠ THE MODE MUST MATCH THE JOB. This probe used to hard-code OnCell, which
                // asks "can the pawn stand ON the target". That is trivially false for a wall
                // frame or any impassable construction target, so it reported CanReach=False,
                // NOT FOUND and component -1 all at once on a perfectly healthy job and hid
                // the real difference. Read the mode the pather is actually using.
                PathEndMode peMode = PathEndMode.Touch;
                string peModeSrc = "assumed Touch (reflection unavailable)";
                if (StuckProbePeModeField != null && pawn.pather != null)
                {
                    try
                    {
                        peMode = (PathEndMode)StuckProbePeModeField.GetValue(pawn.pather);
                        peModeSrc = "live from pather";
                    }
                    catch (System.Exception)
                    {
                        // Fail open to Touch: a wrong-but-sane mode beats a dead probe.
                    }
                }
                sb.AppendLine("  pather peMode = " + peMode + " (" + peModeSrc + ")");

                // ⚠ THE AUTO-VS-FORCED MATRIX, AND THE WHOLE POINT OF THIS PROBE.
                // GenConstruct.CanConstruct reaches with
                //   (PathEndMode.Touch, forced ? Danger.Deadly : pawn.NormalMaxDanger())
                // and NormalMaxDanger ALSO returns Deadly while FloatMenuMakerMap is building
                // the right-click menu. So PathEndMode is IDENTICAL on both routes and DANGER
                // is the only thing that changes. "Won't do it on its own, does it when
                // forced" is therefore a DANGER verdict until proven otherwise, not a
                // connectivity failure.
                bool reachTouchSome = pawn.CanReach(dest, PathEndMode.Touch, Danger.Some);
                bool reachTouchDeadly = pawn.CanReach(dest, PathEndMode.Touch, Danger.Deadly);
                Danger normalDanger = pawn.NormalMaxDanger();
                bool reachModeNormal = pawn.CanReach(dest, peMode, normalDanger);
                bool reachOnCell = pawn.CanReach(dest, PathEndMode.OnCell, Danger.Deadly);
                sb.AppendLine("  reachability matrix:");
                sb.AppendLine("    Touch  + Danger.Some   (NORMAL WORK) = " + reachTouchSome);
                sb.AppendLine("    Touch  + Danger.Deadly (FORCED)      = " + reachTouchDeadly);
                sb.AppendLine("    " + peMode + " + NormalMaxDanger (this pawn now) = "
                    + reachModeNormal + "   [NormalMaxDanger=" + normalDanger + "]");
                sb.AppendLine("    OnCell + Danger.Deadly (what this probe used to ask) = "
                    + reachOnCell);
                if (!reachTouchSome && reachTouchDeadly)
                {
                    sb.AppendLine("    >> DANGER IS THE DIFFERENTIATOR. Normal work refuses this"
                        + " route and forced accepts the SAME route, which is exactly the"
                        + " reported symptom. This is NOT a connectivity bug. Region.DangerFor"
                        + " returns Deadly when room temperature is outside"
                        + " SafeTemperatureRange expanded by 80C, OR when room.Vacuum > 0.5"
                        + " and the pawn is concerned by vacuum. The next block says which.");
                }
                else if (!reachTouchSome && !reachTouchDeadly)
                {
                    sb.AppendLine("    >> UNREACHABLE AT ANY DANGER LEVEL. Danger is NOT the"
                        + " cause; this is a real connectivity failure.");
                }

                // What Region.DangerFor actually reads, for both endpoints.
                FloatRange safeRange = pawn.SafeTemperatureRange();
                sb.AppendLine("  danger inputs:");
                sb.AppendLine("    pawn cell " + pawn.Position + ": "
                    + StuckProbeDangerLine(map, pawn.Position, pawn));
                sb.AppendLine("    dest cell " + c + ": "
                    + StuckProbeDangerLine(map, c, pawn));
                sb.AppendLine("    SafeTemperatureRange = " + safeRange
                    + ", expandedBy(80) = " + safeRange.ExpandedBy(80f)
                    + ", ConcernedByVacuum = " + pawn.ConcernedByVacuum);

                // The permissive answer, for the CanReach-vs-pathfinder signature below.
                bool canReach = reachTouchDeadly;

                // §34: the five ways the same-island router can decline, separated. Without
                // these the failure modes are indistinguishable from outside - "the pawn just
                // stands there" looks identical whether the component map missed the split,
                // the router found no wormhole chain, or segmentation was never attempted.
                // §59: every line below is the PAWN'S view. Probing the optimistic partition
                // here would have reported "same island, no segmentation wanted" for the
                // forbidden-door stall - i.e. the probe would have agreed with the bug.
                bool forbidAware = ABBandComponents.RespectsForbiddenDoors(pawn);
                int pc = ABBandComponents.ComponentOf(map, pawn.Position, forbidAware);
                int dc = ABBandComponents.ComponentOf(map, c, forbidAware);
                bool sameBand = ABBands.SameBand(map, pawn.Position, c);
                bool knownDiff = ABBandComponents.KnownDifferentComponents(map, pawn.Position,
                    c, forbidAware);
                sb.AppendLine("  component: pawn=" + pc + " dest=" + dc
                    + "  sameBand=" + sameBand + "  knownDifferentIslands=" + knownDiff
                    + "  respectsForbiddenDoors=" + forbidAware);
                if (forbidAware)
                {
                    sb.AppendLine("    optimistic (forbid-blind) islands: pawn="
                        + ABBandComponents.ComponentOf(map, pawn.Position) + " dest="
                        + ABBandComponents.ComponentOf(map, c)
                        + "  - if these AGREE while the pair above DISAGREE, a forbidden door"
                        + " is what splits them.");
                }
                if (pc < 0 || dc < 0)
                {
                    sb.AppendLine("    >> AN ENDPOINT IS UNWALKABLE OR OFF-BAND (component -1). "
                        + "ComponentOf returns -1 for BOTH cases, and this is NORMAL for a "
                        + "wall frame, a door under construction, or any impassable build "
                        + "target: those are reached with Touch, not OnCell, so -1 here is "
                        + "NOT by itself evidence of a fault. Read the matrix above instead. "
                        + "Segmentation is deliberately DECLINED - unknown must mean leave "
                        + "it alone.");
                }
                else if (sameBand && !knownDiff)
                {
                    sb.AppendLine("    >> SAME ISLAND: no segmentation wanted. If the pawn "
                        + "cannot walk it, the component map disagrees with the pathfinder "
                        + "and THAT is the bug.");
                }
                else if (sameBand)
                {
                    sb.AppendLine("    >> SAME BAND, DIFFERENT ISLAND: phase 3 SHOULD segment "
                        + "this. See the transit line below.");
                }
                bool gotTransit = ABWormhole.TryGetTransit(map, pawn.Position, c, pawn,
                    out Building_Door tNear, out Building_Door tFar);
                sb.AppendLine("  TryGetTransit = " + (gotTransit
                    ? ("YES via " + tNear.Position + " (comp "
                       + ABBandComponents.ComponentOf(map, tNear.Position, forbidAware) + ") -> "
                       + tFar.Position + " (comp "
                       + ABBandComponents.ComponentOf(map, tFar.Position, forbidAware) + ")")
                    : "NONE - no wormhole chain joins these islands"));
                sb.AppendLine("  wormhole pairs on map = " + ABWormhole.PairCount(map));

                PawnPath path = null;
                bool found = false;
                try
                {
                    // Same mode the job uses, and the permissive danger so this line reports
                    // GEOMETRY only. With OnCell it said NOT FOUND for every impassable
                    // target; with the pawn's normal danger it would just restate the matrix.
                    path = map.pathFinder.FindPathNow(pawn.Position, dest,
                        TraverseParms.For(pawn, Danger.Deadly), null, peMode);
                    found = path != null && path.Found;
                    sb.AppendLine("  FindPathNow = " + (found
                        ? "FOUND (" + path.NodesLeftCount + " nodes)"
                        : "NOT FOUND"));
                }
                finally
                {
                    // The pool is small; never leak a probe path.
                    if (path != null) path.ReleaseToPool();
                }

                // Gated on BOTH conditions. An earlier version printed this whenever CanReach
                // was true, so it fired on a perfectly healthy pawn with a found path and
                // asserted a conclusion the data did not support - a diagnostic that lies is
                // worse than none.
                if (canReach && !found)
                {
                    sb.AppendLine("  >> CanReach(Touch,Deadly)=True with NOT FOUND at the same"
                        + " mode: the region graph is connected but no walkable route exists"
                        + " (usually a diagonal-only link past an impassable cell). That is"
                        + " the re-issue loop.");
                }
                else if (found)
                {
                    sb.AppendLine("  >> A path EXISTS, so this is not a connectivity failure."
                        + " If the pawn is not advancing along it, the problem is movement or"
                        + " repeated re-targeting, not reachability.");
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

        /// <summary>
        /// Counters for the three pathing scope patches in ABPathBandScope.
        ///
        /// ⚠ RUN THIS BEFORE THEORISING ABOUT PATHING COST, for the same reason the riser
        /// report exists. All three patches are guard clauses that early-return, and a guard
        /// that never fires is indistinguishable from a feature that was never built. The
        /// numbers separate the cases outright: guardCalls at zero means the map is not
        /// banded (or Banded is still false, which it is during generation), rejections at
        /// zero with guardCalls climbing means nothing ever asked for a cross band path, and
        /// a filter skip share near zero means the colony lives on one band.
        ///
        /// Log.Warning rather than Log.Message on purpose: info level is filtered out of the
        /// bridge, so a diagnostic that has to reach the log must be a warning.
        /// </summary>
        [DebugAction("As above", "AB2: pathing report",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2PathingReport()
        {
            // §60: the wildlife cap rides along here because it is the same question the
            // rest of this report answers - "did a guard fire, and how often". A counter
            // nobody can read is indistinguishable from an unimplemented feature (§14).
            string fauna = "\n  §60 animal ecosystem cap: applied "
                + Patch_WildAnimalSpawner_ABSliceEcosystem.capsApplied + "x"
                + ", last scale " + Patch_WildAnimalSpawner_ABSliceEcosystem.lastScale
                    .ToString("0.000")
                + " (1.000 means NOT capped: unbanded map, or the getter never ran)";
            Log.Warning(ABLog.Tag + " " + ABPathBandScope.Report() + fauna);
            Messages.Message("AB2: pathing report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Why the drafted destination ghost did or did not draw. Four causes look
        /// identical in game; this separates them. Hold the goto drag, then run this.</summary>
        [DebugAction("As above", "AB2: goto ghost report",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2GotoGhostReport()
        {
            Log.Warning(ABLog.Tag + " AB2 GOTO GHOST REPORT\n  ghosts drawn this session: "
                + Patch_MultiPawnGotoController_ABDrawInViewBand.ghostsDrawn
                + "\n  last outcome: "
                + Patch_MultiPawnGotoController_ABDrawInViewBand.lastGhostSkip);
            Messages.Message("AB2: goto ghost report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// Per-band component census, plus the selected pawn's component against its
        /// destination.
        ///
        /// ⚠ THIS DOC USED TO SAY "nothing in the mod reads this data yet". THAT IS LONG
        /// DEAD: §34 routes every cross-island trip on it and §59 added a second, forbid-aware
        /// partition on top. Movement depends on this data completely.
        ///
        /// What to look for: a sky or basement band reporting `islands &gt; 1` is a fragmented
        /// band, and a selected pawn showing `sameBand=True knownDifferentIslands=True` is the
        /// exact stall this design exists to fix. §59 adds a `forbid-aware:` line per band
        /// (printed only where a forbidden door actually exists) and prints the selected
        /// pawn's island on BOTH partitions - when those two disagree, a forbidden door is
        /// what splits them.
        /// </summary>
        [DebugAction("As above", "AB2: component report",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ComponentReport()
        {
            Map map = Find.CurrentMap;
            Pawn sel = Find.Selector?.SingleSelectedThing as Pawn;
            Log.Warning(ABLog.Tag + " " + ABBandComponents.Report(map, sel));
            Messages.Message("AB2: component report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Node-by-node dump of the cross-level route preview, for the "line goes
        /// half way then straight down" report. Select the transiting pawn first.</summary>
        [DebugAction("As above", "AB2: route line dump",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2RouteLineDump()
        {
            Pawn pawn = Find.Selector?.SingleSelectedThing as Pawn;
            if (pawn == null)
            {
                Messages.Message("AB2: select exactly one pawn first.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            Log.Warning(ABLog.Tag + " AB2 ROUTE LINE DUMP for " + pawn.LabelShortCap + "\n"
                + ABTransitVisuals.DescribeRoute(pawn));
            Messages.Message("AB2: route dump written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: reset pathing counters",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2PathingReset()
        {
            ABPathBandScope.ResetStats();
            Messages.Message("AB2: pathing counters reset.",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
