using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Lets idle colonists find work on directly linked levels. When the local
    /// work scan comes up empty, the pawn is virtually placed at a linked
    /// stairwell's exit and the vanilla work scan runs on the other map; if it
    /// finds anything, the pawn takes the stairs and re-scans on arrival. A
    /// per-pawn cooldown prevents oscillating between levels, and the cooldown
    /// is charged even for empty scans so idle pawns do not re-scan other maps
    /// every think cycle.
    /// </summary>
    public static class CrossLevelWork
    {
        private static int MigrationCooldownTicks => ABMod.Settings?.jobMigrationCooldown ?? 1200;

        /// <summary>Cadence for re-running the EXPENSIVE better-work virtual
        /// scan after it found nothing actionable. This is NOT a priority
        /// throttle - strictly-higher-priority work preempts immediately at
        /// every job transition (see TryMigrateForBetterWork). It only stops a
        /// pawn doing rapid tiny jobs from full-scanning linked levels every
        /// few ticks for a fail-open-plausible work type that has no real
        /// target. Bypassed instantly by any work-version bump (new
        /// designation, blueprint, bill, or fire), so fresh orders never wait
        /// it out.</summary>
        private const int NegativeProbeMemoTicks = 250;

        /// <summary>Colony-wide cap on priority probes per tick. Smooths the
        /// stampede after mass job-end moments (morning wake-ups, version
        /// bumps); a denied pawn simply keeps its local job and retries on a
        /// later think cycle.</summary>
        private static int MaxProbesPerTick => ABMod.Settings?.jobProbeBudget ?? 2;

        /// <summary>Cap on HasJobOnCell evaluations per cell-scanning giver
        /// during a probe (grow zones can be huge). A capped miss is caught
        /// later by the idle-migration path, never lost.</summary>
        private const int MaxCellsPerGiverProbe = 300;

        /// <summary>Retry cadence after an emergency virtual scan that found
        /// nothing actionable (e.g. someone is down but no bed exists anywhere).
        /// First response is instant: the cooldown is only charged when the
        /// plausibility pre-check already sees an emergency.</summary>
        private const int EmergencyMigrationCooldownTicks = 600;

        /// <summary>True while the virtual scan runs so the postfix that calls us
        /// does not recurse.</summary>
        internal static bool VirtualScanActive;

        private static readonly ABPawnCooldown migrationCooldown = new ABPawnCooldown();

        private static readonly ABPawnCooldown emergencyCooldown = new ABPawnCooldown();

        /// <summary>Post-arrival commitment window. As of the 2026-07-25
        /// one-big-map parity rework the colonist better-work path NO LONGER
        /// gates on this - strictly-higher-priority work preempts immediately
        /// and rank-exactness (a total order) is what prevents ping-pong, not a
        /// timer. This survives only for the rank-BLIND probe path (Misc.
        /// Robots, whose think node owns its own giver order so "strictly
        /// better" can't be computed): there a fresh arrival must settle before
        /// re-probing. Still charged from the ARRIVAL tick by NoteArrived (a
        /// probe-time charge erodes during the walk + climb). Kept as a cheap
        /// re-add point should reservation-race bounce ever surface in the
        /// colonist path during testing.</summary>
        private static readonly ABPawnCooldown arrivalCommitment = new ABPawnCooldown();

        /// <summary>Called by the stair transfer for every player-faction
        /// arrival: charges the commitment AND re-arms the idle-migration
        /// cooldown from the ARRIVAL tick (probe-time charges erode in
        /// transit). Player orders, needs bridges, and local WorkGivers
        /// (including the cross-level ferries) are unaffected - this only
        /// gates the probe-based migration paths.</summary>
        internal static void NoteArrived(Pawn pawn)
        {
            if (pawn == null || pawn.Faction != Faction.OfPlayer)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            arrivalCommitment.ChargeUntil(pawn, now + MigrationCooldownTicks);
            migrationCooldown.ChargeUntil(pawn, now + MigrationCooldownTicks);
        }

        /// <summary>Per-pawn cooldown that also stores the work version it was
        /// charged at: a version bump (new designations anywhere) re-arms every
        /// pawn at once, O(1), no pawn iteration.</summary>
        private sealed class VersionedCooldown
        {
            private struct Entry
            {
                public int until;
                public int version;
            }

            private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();

            public bool Ready(Pawn pawn, int now)
            {
                if (!entries.TryGetValue(pawn.thingIDNumber, out Entry e))
                {
                    return true;
                }
                return now >= e.until || e.version != LevelWorkSummary.WorkVersion;
            }

            public void Charge(Pawn pawn, int untilTick)
            {
                if (entries.Count > 512)
                {
                    entries.Clear();
                }
                entries[pawn.thingIDNumber] = new Entry
                {
                    until = untilTick,
                    version = LevelWorkSummary.WorkVersion
                };
            }
        }

        private static readonly VersionedCooldown betterWorkNegativeMemo = new VersionedCooldown();

        private static int probeBudgetTick = -1;

        private static int probeBudgetUsed;

        private static bool TryClaimProbeBudget(int now)
        {
            if (now != probeBudgetTick)
            {
                probeBudgetTick = now;
                probeBudgetUsed = 0;
            }
            if (probeBudgetUsed >= MaxProbesPerTick)
            {
                return false;
            }
            probeBudgetUsed++;
            return true;
        }

        /// <summary>Non-humanlike workers (Misc. Robots, Biotech mechs) run on a
        /// battery; never ship them to another level when it is low - their
        /// recharge AI wants them near home. Shared by the cross-level haul and
        /// fetch givers.</summary>
        public static bool LowPowerWorker(Pawn pawn)
        {
            if (pawn.RaceProps == null || pawn.RaceProps.Humanlike || pawn.needs == null)
            {
                return false;
            }
            Need_Rest rest = pawn.needs.rest;
            if (rest != null && rest.CurLevelPercentage < 0.45f)
            {
                return true;
            }
            Need_MechEnergy energy = pawn.needs.energy;
            return energy != null && energy.CurLevelPercentage < 0.35f;
        }

        public static ThinkResult? TryMigrateForWork(JobGiver_Work giver, Pawn pawn)
        {
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!migrationCooldown.Ready(pawn, now))
            {
                return null;
            }
            migrationCooldown.ChargeUntil(pawn, now + MigrationCooldownTicks);

            // One-big-map parity: a fully-idle pawn goes to the globally
            // highest-priority work across BOTH linked levels, not merely the
            // first level that happens to have anything. Scan both and keep the
            // better-ranked hit; ties fall to the upper level (arbitrary but
            // stable). Gated by the idle migration cooldown, so scanning both
            // is paid at most once per window per idle pawn.
            ThinkResult? upper = TryTowards(giver, pawn, comp.upperMap, out int upperRank);
            ThinkResult? lower = TryTowards(giver, pawn, comp.lowerMap, out int lowerRank);
            ThinkResult? work;
            if (upper.HasValue && lower.HasValue)
            {
                work = RankBeats(upperRank, lowerRank) ? upper : lower;
            }
            else
            {
                work = upper ?? lower;
            }
            if (work.HasValue)
            {
                return work;
            }
            return TryReturnHome(giver, pawn, comp);
        }

        /// <summary>True when rank a is at least as good (high priority) as b.
        /// Lower index = higher priority; -1 (unknown non-scan giver) is
        /// treated as the top. Ties resolve to a (the upper level).</summary>
        private static bool RankBeats(int a, int b)
        {
            int ra = a < 0 ? int.MinValue : a;
            int rb = b < 0 ? int.MinValue : b;
            return ra <= rb;
        }

        /// <summary>Priority-aware migration: the local scan DID find a job, but
        /// only at a rank the pawn considers low. If a linked level plausibly
        /// holds work from a giver strictly EARLIER in the pawn's own ordered
        /// giver list (which encodes both manual priorities and natural work
        /// order), probe just that truncated prefix remotely and take the
        /// stairs on a hit. Gate chain runs cheapest-first: pure memory checks,
        /// then the cooldown, then the per-level summary bits, and only then
        /// the real (truncated) scan.</summary>
        public static ThinkResult? TryMigrateForBetterWork(JobGiver_Work giver, Pawn pawn, ThinkResult local)
        {
            Job localJob = local.Job;
            if (localJob == null)
            {
                return null;
            }
            WorkGiverDef localDef = localJob.workGiverDef;
            if (localDef == null)
            {
                // Non-scan jobs carry no giver stamp; without a rank to compare
                // against, stay conservative and keep the local job.
                return null;
            }
            List<WorkGiver> order = pawn.workSettings?.WorkGiversInOrderNormal;
            if (order == null || order.Count == 0)
            {
                return null;
            }
            int stop = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].def == localDef)
                {
                    stop = i;
                    break;
                }
            }
            if (stop <= 0)
            {
                // Top-ranked already (or an unknown giver): nothing can beat it.
                return null;
            }
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;

            // One-big-map priority (2026-07-25 parity rework): strictly-higher-
            // priority work on a linked level must preempt the local job
            // IMMEDIATELY - no cooldown, no arrival-commitment gate. This is
            // safe from thrash because the metric is rank-EXACT: a level is
            // only entered for work strictly better-ranked than what the pawn
            // holds, and rank is a total order, so the destination can never
            // simultaneously rank the origin as better (no ping-pong). The
            // only natural throttle is vanilla's own - JobGiver_Work re-runs
            // just at job transitions, so a busy pawn finishes its current job
            // before this fires again ("complete before moving on").
            //
            // Cheap gate first, UNBUDGETED: is any higher-ranked work type even
            // plausible on a linked level? Pure bool-array reads behind a
            // per-map TTL. Nothing plausible -> keep the local job for free.
            Map upper = comp.upperMap;
            Map lower = comp.lowerMap;
            bool plausibleUpper = upper != null && !upper.Disposed
                && LevelWorkSummary.AnyPlausibleBefore(upper, order, stop);
            bool plausibleLower = lower != null && !lower.Disposed
                && LevelWorkSummary.AnyPlausibleBefore(lower, order, stop);
            if (!plausibleUpper && !plausibleLower)
            {
                return null;
            }
            // Fail-open work types (no detector: BasicWorker, modded types)
            // stay perpetually "plausible", so a pawn doing rapid tiny jobs
            // would full-scan linked levels every transition. A short memo,
            // charged only after an expensive scan finds nothing and bypassed
            // by any work-version bump, caps that without ever delaying a real
            // preemption (a real hit migrates the pawn away immediately).
            if (!betterWorkNegativeMemo.Ready(pawn, now))
            {
                return null;
            }
            // Only the full virtual scan is expensive; it alone claims the
            // colony-wide budget. A denied pawn keeps its local job and retries
            // next transition - with no cooldown that is near-immediate, so a
            // version-bump stampede clears in a handful of ticks.
            if (!TryClaimProbeBudget(now))
            {
                return null;
            }
            ThinkResult? hit = (plausibleUpper ? TryTowardsBetter(giver, pawn, upper, order, stop) : null)
                ?? (plausibleLower ? TryTowardsBetter(giver, pawn, lower, order, stop) : null);
            if (!hit.HasValue)
            {
                betterWorkNegativeMemo.Charge(pawn, now + NegativeProbeMemoTicks);
            }
            return hit;
        }

        private static ThinkResult? TryTowardsBetter(JobGiver_Work giver, Pawn pawn, Map target,
            List<WorkGiver> order, int stop)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            // Plausibility (any better-ranked work type on this level) is
            // pre-checked by the caller so it can gate the probe budget; no
            // redundant summary walk here.
            // Island-aware (2026-07-24): probe from one exit per distinct
            // island so better-ranked work behind a different stairwell than
            // the nearest is not invisible.
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            Building_ABStairs stairs = null;
            Building_ABStairs exit = null;
            IntVec3 workDest = IntVec3.Invalid;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (ProbeBetterWorkAt(pawn, target, pairs[i].exit.Position, order, stop, out workDest))
                {
                    stairs = pairs[i].stairs;
                    exit = pairs[i].exit;
                    break;
                }
            }
            if (exit == null)
            {
                return null;
            }
            StairRouter.Reroute(pawn, target, workDest, ref stairs, ref exit);
            // Charge the shared migration cooldown so the idle path cannot
            // immediately bounce the pawn back after arrival.
            migrationCooldown.ChargeUntil(pawn, Find.TickManager.TicksGame + MigrationCooldownTicks);
            ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level()
                + ": higher-priority work found, taking stairs.");
            return new ThinkResult(MakeStairsJob(stairs, exit), giver, JobTag.Misc);
        }

        /// <summary>Existence probe of an ARBITRARY giver list, island-aware:
        /// the robot-compat variant of the colonist probe (their think node
        /// owns its own giver order, so rank truncation does not apply - the
        /// whole list is checked, from one exit per distinct island). Claims
        /// the global probe budget once for the whole sweep. Added for the run
        /// #71 bounce fix: summary bits alone migrate robots toward levels
        /// where nothing is actually doable (blueprints with no local
        /// materials), and the idle go-home node immediately routes them back.</summary>
        internal static bool ProbeWorkAt(Pawn pawn, Map target, List<WorkGiver> order,
            out IntVec3 workDest, out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            workDest = IntVec3.Invalid;
            stairs = null;
            exit = null;
            int probeNow = Find.TickManager.TicksGame;
            if (order == null || order.Count == 0
                || !arrivalCommitment.Ready(pawn, probeNow)
                || !TryClaimProbeBudget(probeNow))
            {
                return false;
            }
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            for (int i = 0; i < pairs.Count; i++)
            {
                if (ProbeBetterWorkAt(pawn, target, pairs[i].exit.Position, order, order.Count, out workDest))
                {
                    stairs = pairs[i].stairs;
                    exit = pairs[i].exit;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Runs the pawn's own giver order, truncated to ranks strictly
        /// better than the local job's, with the pawn virtually placed at the
        /// stairwell exit on the target map. Existence check only: any hit
        /// justifies the trip and the real job re-resolves after arrival.</summary>
        private static bool ProbeBetterWorkAt(Pawn pawn, Map target, IntVec3 entryCell,
            List<WorkGiver> order, int stop, out IntVec3 workDest)
        {
            workDest = IntVec3.Invalid;
            if (!ABVirtualPosition.TrySwap(pawn, target, entryCell, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool found = false;
            VirtualScanActive = true;
            try
            {
                for (int i = 0; i < stop && !found; i++)
                {
                    WorkGiver wg = order[i];
                    WorkGiverDef def = wg.def;
                    if (def?.workType == null
                        || LevelWorkSummary.IsOwnCrossLevelGiver(def)
                        || !LevelWorkSummary.Plausible(target, def.workType))
                    {
                        continue;
                    }
                    if (wg.MissingRequiredCapacity(pawn) != null || wg.ShouldSkip(pawn))
                    {
                        continue;
                    }
                    Job nonScan = wg.NonScanJob(pawn);
                    if (nonScan != null)
                    {
                        // Existence proven; the job itself is discarded.
                        workDest = nonScan.targetA.IsValid && nonScan.targetA.HasThing
                            ? nonScan.targetA.Thing.PositionHeld
                            : (nonScan.targetA.IsValid ? nonScan.targetA.Cell : IntVec3.Invalid);
                        found = true;
                        break;
                    }
                    if (wg is WorkGiver_Scanner scanner)
                    {
                        found = ProbeGiver(scanner, pawn, out workDest);
                    }
                }
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
                VirtualScanActive = false;
            }
            return found;
        }

        /// <summary>Compact existence version of vanilla's per-giver scan. The
        /// closest valid thing (or first valid cell) is enough; prioritized
        /// scanners lose their fine ordering here, which only affects WHERE the
        /// pawn re-scans from after arrival, not whether the work is real.</summary>
        private static bool ProbeGiver(WorkGiver_Scanner scanner, Pawn pawn, out IntVec3 workDest)
        {
            workDest = IntVec3.Invalid;
            if (scanner.def.scanThings)
            {
                bool Validator(Thing th) => !th.IsForbidden(pawn) && scanner.HasJobOnThing(pawn, th);
                IEnumerable<Thing> potential = scanner.PotentialWorkThingsGlobal(pawn);
                Thing hit;
                if (scanner.AllowUnreachable)
                {
                    IEnumerable<Thing> search = potential
                        ?? pawn.Map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
                    hit = GenClosest.ClosestThing_Global(pawn.Position, search, 99999f, Validator);
                }
                else
                {
                    hit = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map,
                        scanner.PotentialWorkThingRequest, scanner.PathEndMode,
                        TraverseParms.For(pawn, scanner.MaxPathDanger(pawn)), 9999f, Validator,
                        potential, 0, scanner.MaxRegionsToScanBeforeGlobalSearch, potential != null);
                }
                if (hit != null)
                {
                    workDest = hit.PositionHeld;
                    return true;
                }
            }
            if (scanner.def.scanCells)
            {
                int examined = 0;
                Danger maxDanger = scanner.MaxPathDanger(pawn);
                foreach (IntVec3 cell in scanner.PotentialWorkCellsGlobal(pawn))
                {
                    if (++examined > MaxCellsPerGiverProbe)
                    {
                        break;
                    }
                    if (cell.IsForbidden(pawn) || !scanner.HasJobOnCell(pawn, cell))
                    {
                        continue;
                    }
                    if (!scanner.AllowUnreachable && !pawn.CanReach(cell, scanner.PathEndMode, maxDanger))
                    {
                        continue;
                    }
                    workDest = cell;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Migration for the emergency work pass (rescue, tend,
        /// firefight). Vanilla caches emergency work givers into a separate list
        /// that the normal pass never scans, so without this a doctor idling on
        /// another level never sees a downed pawn below or above. A cheap
        /// plausibility pre-check runs BEFORE any cooldown is charged: response
        /// is immediate when someone actually goes down, while calm-day empty
        /// passes cost two short list walks and no cooldown churn. The cooldown
        /// is separate from the normal migration cooldown so a failed emergency
        /// scan can never starve the real work scanner.</summary>
        public static ThinkResult? TryMigrateForEmergencyWork(JobGiver_Work giver, Pawn pawn)
        {
            if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
            {
                return null;
            }
            int now = Find.TickManager.TicksGame;
            if (!emergencyCooldown.Ready(pawn, now))
            {
                return null;
            }
            bool up = EmergencyWorkPlausible(pawn, comp.upperMap);
            bool low = EmergencyWorkPlausible(pawn, comp.lowerMap);
            if (!up && !low)
            {
                return null;
            }
            emergencyCooldown.ChargeUntil(pawn, now + EmergencyMigrationCooldownTicks);
            ThinkResult? work = up ? TryTowards(giver, pawn, comp.upperMap) : null;
            if (!work.HasValue && low)
            {
                work = TryTowards(giver, pawn, comp.lowerMap);
            }
            return work;
        }

        /// <summary>Cheap plausibility check for emergency work on a linked
        /// level: any fire, or a faction pawn or colony prisoner who is downed
        /// out of bed or has wounds a player doctor should tend. Cheapest checks
        /// first; this runs on every empty emergency scan of every colonist, so
        /// it must stay allocation-free. The virtual scan stays the authority on
        /// whether the work is actually doable.</summary>
        private static bool EmergencyWorkPlausible(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return false;
            }
            if (target.listerThings.ThingsOfDef(ThingDefOf.Fire).Count > 0)
            {
                return true;
            }
            if (AnyEmergencyPatient(target.mapPawns.SpawnedPawnsInFaction(pawn.Faction)))
            {
                return true;
            }
            return AnyEmergencyPatient(target.mapPawns.PrisonersOfColonySpawned);
        }

        private static bool AnyEmergencyPatient(List<Pawn> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Pawn p = list[i];
                if (p.Downed && !p.InBed())
                {
                    return true;
                }
                if (p.health != null && p.health.HasHediffsNeedingTendByPlayer())
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Truly idle colonists drift back toward the ground level, where
        /// food, beds, and recreation usually live, instead of roaming a work level
        /// forever.</summary>
        private static ThinkResult? TryReturnHome(JobGiver_Work giver, Pawn pawn, LevelComp comp)
        {
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.idleReturnHome || comp.level == 0)
            {
                return null;
            }
            Map home = comp.level > 0 ? comp.lowerMap : comp.upperMap;
            if (!TryStairsJobToward(pawn, home, out Job job))
            {
                return null;
            }
            return new ThinkResult(job, giver, JobTag.Misc);
        }

        private static ThinkResult? TryTowards(JobGiver_Work giver, Pawn pawn, Map target)
        {
            return TryTowards(giver, pawn, target, out int _);
        }

        /// <summary>Idle-migration scan of one linked level. Also reports the
        /// RANK (index in the pawn's ordered giver list) of the best job found
        /// there, so a fully-idle pawn can be sent to the globally-highest-
        /// priority work across levels rather than the first level that has
        /// anything (one-big-map parity). rank is int.MaxValue when no work,
        /// or -1 for a non-scan job whose giver is unknown (treated as top).</summary>
        private static ThinkResult? TryTowards(JobGiver_Work giver, Pawn pawn, Map target, out int rank)
        {
            rank = int.MaxValue;
            if (target == null || target.Disposed)
            {
                return null;
            }
            // Island-aware (2026-07-24): run the vanilla scan from one exit per
            // distinct island of the target level, so work behind a different
            // stairwell than the nearest is found. The full JobGiver_Work scan
            // is the expensive probe here, so islands are capped by the
            // enumerator and the first hit wins.
            List<StairIslands.Pair> pairs = StairIslands.EntryPairs(pawn, target);
            if (pairs.Count == 0)
            {
                ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level()
                    + ": no reachable linked stairs on this level.");
                return null;
            }
            Building_ABStairs stairs = null;
            Building_ABStairs exit = null;
            IntVec3 workDest = IntVec3.Invalid;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (WorkTargetAt(giver, pawn, target, pairs[i].exit.Position, out workDest, out rank))
                {
                    stairs = pairs[i].stairs;
                    exit = pairs[i].exit;
                    break;
                }
            }
            if (exit == null)
            {
                ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level()
                    + ": no work reachable from any stairwell island (" + pairs.Count + " tried).");
                return null;
            }
            // The scan discovered where the work actually is: swap to the
            // stairwell that minimizes the whole trip, not just the walk here.
            StairRouter.Reroute(pawn, target, workDest, ref stairs, ref exit);
            ABLog.Dev("Migrate " + pawn.LabelShort + " -> level " + target.Level() + ": work found, taking stairs.");
            return new ThinkResult(MakeStairsJob(stairs, exit), giver, JobTag.Misc);
        }

        /// <summary>Reachable stairs plus their far-side exit toward a target
        /// level. The shared shape behind every "send this pawn through the
        /// stairs" consumer; previously duplicated six times.</summary>
        public static bool TryResolveStairs(Pawn pawn, Map target, out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            return TryResolveStairs(pawn, target, IntVec3.Invalid, out stairs, out exit);
        }

        /// <summary>Destination-aware variant: when dest is a valid cell on the
        /// target map, the pair minimizing the whole trip wins; otherwise this
        /// is the classic nearest-to-pawn pick. Lenient about the exit actually
        /// reaching dest - correct for player orders, wrong for autonomous
        /// deliveries (use the Strict variant there).</summary>
        public static bool TryResolveStairs(Pawn pawn, Map target, IntVec3 dest, out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            stairs = null;
            exit = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            if (dest.IsValid && StairRouter.TryBestToward(pawn, target, dest, out stairs, out exit))
            {
                return true;
            }
            stairs = NearestUsableStairs(pawn, target, checkReachability: true);
            exit = stairs?.CounterpartTowards(target);
            return exit != null;
        }

        /// <summary>Strict resolve for autonomous flows carrying goods to a
        /// KNOWN destination: only exits that region-reach dest qualify, and
        /// failure means the caller must skip this goal entirely (vanilla
        /// parity: an unreachable site simply gets no deliveries). With an
        /// invalid dest this degrades to the classic nearest pick, because
        /// strictness is meaningless without a goal.</summary>
        public static bool TryResolveStairsStrict(Pawn pawn, Map target, IntVec3 dest, out Building_ABStairs stairs, out Building_ABStairs exit)
        {
            stairs = null;
            exit = null;
            if (target == null || target.Disposed)
            {
                return false;
            }
            if (dest.IsValid)
            {
                return StairRouter.TryBestToward(pawn, target, dest, requireReach: true, out stairs, out exit);
            }
            stairs = NearestUsableStairs(pawn, target, checkReachability: true);
            exit = stairs?.CounterpartTowards(target);
            return exit != null;
        }

        public static Job MakeStairsJob(Building_ABStairs stairs, Building_ABStairs exit)
        {
            Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, stairs);
            job.targetC = exit;
            return job;
        }

        public static bool TryStairsJobToward(Pawn pawn, Map target, out Job job)
        {
            return TryStairsJobToward(pawn, target, IntVec3.Invalid, out job);
        }

        public static bool TryStairsJobToward(Pawn pawn, Map target, IntVec3 dest, out Job job)
        {
            job = null;
            if (!TryResolveStairs(pawn, target, dest, out Building_ABStairs stairs, out Building_ABStairs exit))
            {
                return false;
            }
            job = MakeStairsJob(stairs, exit);
            return true;
        }

        private struct StairsMemoEntry
        {
            public int tick;
            public Building_ABStairs stairs;
        }

        private static readonly Dictionary<long, StairsMemoEntry> stairsMemo = new Dictionary<long, StairsMemoEntry>();

        /// <summary>Per-tick memo over NearestUsableStairs with reachability. The
        /// haul scanner calls the reachability variant once per candidate ITEM,
        /// but the verdict only depends on (pawn, target map): within one tick a
        /// full-map scan pays the region search twice instead of N times.
        /// Negative results are memoized too (the expensive case is usually "no
        /// reachable stairs", recomputed per item). One-tick staleness on
        /// mid-tick construction or destruction is accepted; the returned stairs
        /// are re-validated cheaply on every hit.</summary>
        public static Building_ABStairs NearestUsableStairsCached(Pawn pawn, Map target)
        {
            if (pawn == null || target == null)
            {
                return null;
            }
            long key = ((long)pawn.thingIDNumber << 32) | (uint)target.uniqueID;
            int now = Find.TickManager.TicksGame;
            if (stairsMemo.TryGetValue(key, out StairsMemoEntry entry) && entry.tick == now)
            {
                Building_ABStairs cached = entry.stairs;
                return cached != null && cached.Spawned && cached.CounterpartTowards(target) != null
                    ? cached
                    : null;
            }
            if (stairsMemo.Count > 1024)
            {
                stairsMemo.Clear();
            }
            Building_ABStairs found = NearestUsableStairs(pawn, target, checkReachability: true);
            stairsMemo[key] = new StairsMemoEntry { tick = now, stairs = found };
            return found;
        }

        /// <summary>Nearest stairwell on the pawn's map whose counterpart sits on the
        /// target map. Reachability checks are optional because they are too heavy
        /// for per-frame gizmo building.</summary>
        public static Building_ABStairs NearestUsableStairs(Pawn pawn, Map target, bool checkReachability)
        {
            List<Building_ABStairs> stairs = pawn.Map.Levels()?.Stairs;
            if (stairs == null)
            {
                return null;
            }
            Building_ABStairs best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < stairs.Count; i++)
            {
                Building_ABStairs s = stairs[i];
                if (s == null || !s.Spawned)
                {
                    continue;
                }
                if (s.Ext != null && s.Ext.utilityOnly)
                {
                    // Vertical conduits and pipes carry networks, never pawns.
                    continue;
                }
                Building_ABStairs cp = s.CounterpartTowards(target);
                if (cp == null)
                {
                    continue;
                }
                if (s.EndForbiddenFor(pawn) || cp.EndForbiddenFor(pawn))
                {
                    continue; // door parity: forbidden ends seal the passage
                }
                float d = (s.Position - pawn.Position).LengthHorizontalSquared;
                if (d >= bestDist)
                {
                    continue;
                }
                if (checkReachability && !pawn.CanReach(s, PathEndMode.Touch, Danger.Deadly))
                {
                    continue;
                }
                best = s;
                bestDist = d;
            }
            return best;
        }

        /// <summary>Runs the vanilla work scan as if the pawn stood at the stairwell
        /// exit on the target map. Position and map index are swapped through the
        /// private fields (a technique MultiFloors also uses, implemented independently here) and restored in a
        /// finally block no matter what the scan does. Any job the scan produces is
        /// discarded; the real job gets picked normally after the transfer.</summary>
        private static bool WorkTargetAt(JobGiver_Work giver, Pawn pawn, Map target, IntVec3 entryCell, out IntVec3 workDest, out int rank)
        {
            workDest = IntVec3.Invalid;
            rank = int.MaxValue;
            if (!ABVirtualPosition.TrySwap(pawn, target, entryCell, out ABVirtualPosition.Token token))
            {
                return false;
            }
            bool found = false;
            VirtualScanActive = true;
            try
            {
                ThinkResult result = giver.TryIssueJobPackage(pawn, default(JobIssueParams));
                found = result.Job != null;
                if (found)
                {
                    workDest = StairRouter.DestHint(result.Job, target);
                    rank = RankOf(pawn, result.Job.workGiverDef);
                }
            }
            finally
            {
                ABVirtualPosition.Restore(pawn, token);
                VirtualScanActive = false;
            }
            return found;
        }

        /// <summary>Rank of a work giver in the pawn's ordered giver list
        /// (lower = higher priority). -1 for a null giver (non-scan job, giver
        /// unknown) which we treat as top priority; int.MaxValue when the giver
        /// is somehow absent from the list.</summary>
        private static int RankOf(Pawn pawn, WorkGiverDef def)
        {
            if (def == null)
            {
                return -1;
            }
            List<WorkGiver> order = pawn.workSettings?.WorkGiversInOrderNormal;
            if (order == null)
            {
                return int.MaxValue;
            }
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i].def == def)
                {
                    return i;
                }
            }
            return int.MaxValue;
        }
    }

    /// <summary>
    /// Temporarily relocates a pawn (private position and map index fields) so
    /// vanilla map-scoped queries run as if the pawn stood on another level.
    /// Callers must Restore in a finally block. Shared by cross-level work and
    /// hauling; a technique MultiFloors also uses, implemented independently here.
    /// </summary>
    internal static class ABVirtualPosition
    {
        private static readonly AccessTools.FieldRef<Thing, sbyte> MapIndexRef =
            AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");

        private static readonly AccessTools.FieldRef<Thing, IntVec3> PositionRef =
            AccessTools.FieldRefAccess<Thing, IntVec3>("positionInt");

        public struct Token
        {
            internal sbyte mapIndex;
            internal IntVec3 pos;
        }

        public static bool TrySwap(Pawn pawn, Map target, IntVec3 cell, out Token token)
        {
            token = default(Token);
            sbyte idx = (sbyte)Find.Maps.IndexOf(target);
            if (idx < 0)
            {
                return false;
            }
            token.mapIndex = MapIndexRef(pawn);
            token.pos = PositionRef(pawn);
            MapIndexRef(pawn) = idx;
            PositionRef(pawn) = cell;
            return true;
        }

        public static void Restore(Pawn pawn, Token token)
        {
            MapIndexRef(pawn) = token.mapIndex;
            PositionRef(pawn) = token.pos;
        }

        /// <summary>Runs a scan with the pawn virtually placed at a cell on the
        /// target map, restoring no matter what the scan does. Cold paths only:
        /// the lambda closure allocates, so the hottest storage scan
        /// (CrossLevelHaul.Check) keeps its hand-written swap.</summary>
        public static bool WithPawnAt(Pawn pawn, Map target, IntVec3 cell, Func<bool> scan)
        {
            if (!TrySwap(pawn, target, cell, out Token token))
            {
                return false;
            }
            try
            {
                return scan();
            }
            finally
            {
                Restore(pawn, token);
            }
        }

        /// <summary>Position-only swap for non-pawn things. Vanilla storage queries
        /// measure reachability and distance from the ITEM's position, which lives
        /// on the source map; re-seating it at the stairwell exit for the duration
        /// of the query makes both meaningful on the destination map.</summary>
        public static IntVec3 SwapPositionOnly(Thing thing, IntVec3 cell)
        {
            IntVec3 old = PositionRef(thing);
            PositionRef(thing) = cell;
            return old;
        }

        public static void RestorePositionOnly(Thing thing, IntVec3 oldPos)
        {
            PositionRef(thing) = oldPos;
        }
    }
}
