using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Utility;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// AI JUMPING BETWEEN LEVELS.
    ///
    /// ⚠⚠ FIRST, A CORRECTION THAT COST A WRONG ANSWER: **VANILLA DOES HAVE AI JUMP
    /// BEHAVIOUR.** An earlier pass grepped the decompiled assembly for
    /// `Verb_Jump|JumpUtility|CastJump|JumpRange`, found no JobGiver, and concluded there was
    /// no AI path for jumping at all. All three of vanilla's AI jump givers reach jumping
    /// through an <c>AbilityDef</c> FIELD instead, so not one of them matched the pattern:
    ///   * <c>JobGiver_AIJumpEscapeEnemies</c> - Core's `Abilities_Escape` tree, for anyone
    ///     with `Longjump` (the `LongjumpLegs` gene, i.e. every sanguophage): panic-jump away
    ///     when damaged past 25%, harmed within 120 ticks, under close combat pressure;
    ///   * <c>JobGiver_AIJumpToJobTarget</c> and <c>JobGiver_AIJumpToJobRescueTarget</c> -
    ///     Biotech's mech subtree, for `LongjumpMechLauncher`: jump to beat a fire, jump to
    ///     rescue a downed pawn.
    /// **RULE 18, EXACTLY: a clean grep is not an exoneration.** What survives of the old
    /// answer is narrower and still true - JUMP PACKS have no AI path (all three givers are
    /// ability-driven) and no PawnKindDef equips one.
    ///
    /// So this file is three things, not one:
    ///   A. REPAIR the two vanilla givers, which are band-broken in the mod's most familiar
    ///      shape (a raw distance test standing in front of a verb that already knows better).
    ///   B. ADD the missing behaviour: reach a goal on another level by jumping when walking
    ///      cannot get there.
    ///   C. CLAMP the escape jump to its own band, by the user's call.
    ///
    /// ⚠ THE DESIGN IS CONSERVATIVE BY INSTRUCTION, AND THE THINK TREE ENFORCES IT FOR FREE.
    /// The new giver hangs off <c>Humanlike_PostDuty</c>, vanilla's own modder insertion hook,
    /// which a pawn only reaches once its Lord duty produced NO job. "The AI wanted to get
    /// somewhere and could not" is therefore a structural fact at that point in the tree
    /// rather than a heuristic we have to invent - and it means an ordinary raid, where the
    /// duty works fine, never runs a single line of this.
    ///
    /// ⚠ AND A JUMP MAY NOT STRAND A PAWN (user's call). The landing cell must have a route
    /// to the goal, checked with <c>map.reachability</c> FROM THE LANDING CELL - not from the
    /// pawn, which is the whole point. That one test is both "conservative" and "no stranding"
    /// at once: a jump is always a shortcut into somewhere useful, never a one-way trip into a
    /// sealed room where the stuck watchdog would start firing.
    /// </summary>
    public static class ABBandLeapAI
    {
        /// <summary>How many landing candidates get the EXPENSIVE tests (shaft solve +
        /// reachability). The cheap filters run over the whole disc; only the best few by
        /// distance-to-goal are solved, so cost is bounded regardless of jump range.</summary>
        private const int MaxSolveCandidates = 40;

        /// <summary>Ticks before a pawn that found nothing may scan again. The scan is behind
        /// a duty that already failed, so it repeats every think until something changes;
        /// without this a stranded raider re-scans forever.</summary>
        private const int IdleCooldownTicks = 300;

        /// <summary>Ticks before a pawn that DID jump may jump again. Long enough that a pawn
        /// which lands still unable to reach anything does not chain-jump across the
        /// stack.</summary>
        private const int LeapCooldownTicks = 600;

        private static readonly ABPawnCooldown Cooldown = new ABPawnCooldown();

        // Observe-only counters for `AB2: combat report` (§36).
        public static int scans;

        public static int leaps;

        public static int noLanding;

        public static int escapesClamped;

        public static int vanillaGiverRepairs;

        private static readonly IntVec3[] BestCells = new IntVec3[MaxSolveCandidates];

        private static readonly float[] BestScores = new float[MaxSolveCandidates];

        /// <summary>
        /// When non-null, every decision point appends the value that caused it, and the
        /// pawn's real cooldown is left alone.
        ///
        /// ⚠ THE SAME CODE PATH, INSTRUMENTED - not a second explainer, by the §14 rule and
        /// exactly as ABShaft.Explain does it. A parallel "why not" routine agrees with your
        /// belief about the decision rather than with the decision.
        ///
        /// ⚠ IT DOUBLES AS THE "AM I PROBING" FLAG. `Charge` below writes no cooldown while
        /// tracing, because a diagnostic that gags the thing it is diagnosing for the next
        /// 300 ticks is worse than no diagnostic at all.
        /// </summary>
        [ThreadStatic]
        private static StringBuilder trace;

        private static void Trace(string line)
        {
            trace?.AppendLine("    " + line);
        }

        private static void Charge(Pawn pawn, int untilTick)
        {
            if (trace == null)
            {
                Cooldown.ChargeUntil(pawn, untilTick);
            }
        }

        /// <summary>Clear a pawn's leap cooldown, for `AB2: force leap now`.</summary>
        public static void ClearCooldown(Pawn pawn)
        {
            Cooldown.ChargeUntil(pawn, 0);
        }

        /// <summary>
        /// Runs the REAL decision with tracing on and the cooldown bypassed, then puts every
        /// counter back: a probe must not leave `leaps=1` behind for a leap that never
        /// happened (§36 lets us restore them - nothing may read them to decide anything).
        /// </summary>
        public static string Explain(Pawn pawn)
        {
            StringBuilder sb = new StringBuilder();
            int sScans = scans;
            int sLeaps = leaps;
            int sNoLanding = noLanding;
            trace = sb;
            try
            {
                Job job = TryGiveLeapJob(pawn, ignoreCooldown: true);
                sb.AppendLine("    => " + (job != null
                    ? "WOULD LEAP to " + job.targetA.Cell + " (job " + job.def.defName + ")"
                    : "NO LEAP"));
            }
            catch (Exception e)
            {
                sb.AppendLine("    => THREW: " + e.Message);
            }
            finally
            {
                trace = null;
                scans = sScans;
                leaps = sLeaps;
                noLanding = sNoLanding;
            }
            return sb.ToString();
        }

        public static void ResetCounters()
        {
            scans = 0;
            leaps = 0;
            noLanding = 0;
            escapesClamped = 0;
            vanillaGiverRepairs = 0;
        }

        public static string CounterReport()
        {
            return "leapAI: scans=" + scans + " leaps=" + leaps + " noLanding=" + noLanding
                + " escapesClamped=" + escapesClamped + " giverRepairs=" + vanillaGiverRepairs;
        }

        /// <summary>The whole decision, from the think node.</summary>
        public static Job TryGiveLeapJob(Pawn pawn, bool ignoreCooldown = false)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Downed || !ABGuard.On(ABGuard.Movement))
                {
                    Trace("declined: null, unspawned, downed, or the movement guard is OFF");
                    return null;
                }
                // ⚠ NEVER FOR A DRAFTED PAWN. A drafted pawn is doing exactly what the player
                // said; deciding on its behalf to spend a jump-pack charge is the one thing
                // that would make this feature feel like a bug. Undrafted colonists are
                // allowed (the user asked for "anyone with a jump pack or jump ability"), and
                // in practice almost never qualify - a WorkGiver never hands out a goal it has
                // not already proved reachable.
                if (pawn.Drafted)
                {
                    Trace("declined: DRAFTED - a drafted pawn does what the player said");
                    return null;
                }
                Map map = pawn.Map;
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    Trace("declined: map is not banded");
                    return null;
                }
                int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
                if (!ignoreCooldown && !Cooldown.Ready(pawn, now))
                {
                    Trace("declined: on leap cooldown (idle " + IdleCooldownTicks
                        + " / post-leap " + LeapCooldownTicks + " ticks)");
                    return null;
                }
                if (!TryFindJumpSource(pawn, out Ability ability, out Verb verb))
                {
                    Trace("declined: no usable jump - no ready Verb_CastAbilityJump ability "
                        + "and no worn jump pack with a charge");
                    // No cooldown charge: having no jump pack is not a failed scan, and
                    // charging here would just fill the dictionary with every pawn on the map.
                    return null;
                }
                scans++;
                Trace("jump source: " + (ability != null
                    ? "ability " + ability.def.defName
                    : "apparel " + verb.EquipmentSource.ToStringSafe())
                    + ", range " + verb.EffectiveRange.ToString("0.0"));
                if (!TryFindGoal(pawn, bands, out LocalTargetInfo goal) || !goal.IsValid)
                {
                    Trace("declined: NO GOAL - no enemy target, no duty focus, no job target, "
                        + "and not hostile to the player (so no colony fallback)");
                    Charge(pawn, now + IdleCooldownTicks);
                    return null;
                }
                int goalBand = bands.BandOf(goal.Cell);
                Trace("goal " + goal.ToStringSafe() + " at " + goal.Cell + " band " + goalBand
                    + "; pawn band " + bands.BandOf(pawn.Position));
                if (goalBand == bands.BandOf(pawn.Position))
                {
                    Trace("declined: goal is on the pawn's OWN band - cross-band only, so "
                        + "flat-map behaviour is untouched by construction");
                    // Same level: vanilla's own behaviour owns this, and adding to it would be
                    // a balance change on flat maps too. Cross-band only, by construction.
                    Charge(pawn, now + IdleCooldownTicks);
                    return null;
                }
                // THE CONSERVATIVE GATE. If it can walk there, it walks; this never makes an
                // existing raid faster or an existing route redundant.
                if (pawn.CanReach(goal, PathEndMode.Touch, Danger.Deadly))
                {
                    Trace("declined: the pawn can WALK to the goal (stairs exist) - "
                        + "conservative gate, walking always wins");
                    Charge(pawn, now + IdleCooldownTicks);
                    return null;
                }
                if (!TryFindLanding(pawn, bands, verb, goal, goalBand, out IntVec3 landing))
                {
                    noLanding++;
                    Charge(pawn, now + IdleCooldownTicks);
                    return null;
                }
                Charge(pawn, now + LeapCooldownTicks);
                leaps++;
                ABV2Debug.Combat("AI leap " + pawn.LabelShortCap + " band "
                    + bands.BandOf(pawn.Position) + " -> " + goalBand + " landing " + landing
                    + " for goal " + goal.ToStringSafe());
                return MakeLeapJob(ability, verb, landing);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 AI band leap");
            }
            return null;
        }

        /// <summary>
        /// The pawn's usable jump, if any.
        ///
        /// ⚠ ABILITY BEFORE APPAREL, DELIBERATELY. An ability recharges on a cooldown; a jump
        /// pack burns one of five charges that the AI can never replace, because reloading
        /// needs chemfuel and a hauling job it will not do. Spending the renewable resource
        /// first is the difference between a raider who keeps its options and one that is out
        /// of fuel by the second wall.
        /// </summary>
        public static bool TryFindJumpSource(Pawn pawn, out Ability ability, out Verb verb)
        {
            ability = null;
            verb = null;
            Pawn_AbilityTracker abilities = pawn.abilities;
            if (abilities != null)
            {
                List<Ability> list = abilities.abilities;
                for (int i = 0; i < list.Count; i++)
                {
                    Ability a = list[i];
                    if (a != null && a.verb is Verb_CastAbilityJump && a.CanCast)
                    {
                        ability = a;
                        verb = a.verb;
                        return true;
                    }
                }
            }
            if (pawn.apparel != null)
            {
                foreach (Verb v in pawn.apparel.AllApparelVerbs)
                {
                    if (!(v is Verb_Jump) || !v.Available())
                    {
                        continue;
                    }
                    // The same charge test Verb_Jump.ValidateTarget runs, so the AI cannot
                    // start a jump the verb would refuse.
                    if (v.EquipmentSource != null
                        && !ReloadableUtility.CanUseConsideringQueuedJobs(pawn, v.EquipmentSource,
                            showMessage: false))
                    {
                        continue;
                    }
                    verb = v;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// What this pawn is trying to get to, in descending order of how sure we are.
        ///
        /// ⚠ THE LAST SOURCE EXISTS BECAUSE THE FIRST THREE ARE ALL EMPTY IN THE HEADLINE
        /// CASE. An assaulting raider that cannot reach anything has no enemy target (target
        /// acquisition already failed), no duty focus (AssaultColony states none) and no job
        /// (every giver returned null - which is why we are being asked at all). Without a
        /// fallback the feature would be inert in exactly the situation it was built for, so
        /// a pawn hostile to the player falls back to "the colony", measured BAND-LOCALLY
        /// (§1: a raw DistanceTo across the stack is meaningless).
        /// </summary>
        public static bool TryFindGoal(Pawn pawn, ABBandMap bands, out LocalTargetInfo goal)
        {
            goal = LocalTargetInfo.Invalid;
            Map map = pawn.Map;
            Thing enemy = pawn.mindState?.enemyTarget;
            if (enemy != null && enemy.Spawned && enemy.Map == map)
            {
                goal = enemy;
                return true;
            }
            PawnDuty duty = pawn.mindState?.duty;
            if (duty != null && duty.focus.IsValid && duty.focus.Cell.InBounds(map))
            {
                goal = duty.focus;
                return true;
            }
            Job job = pawn.CurJob;
            if (job != null && job.targetA.IsValid && job.targetA.Cell.InBounds(map))
            {
                goal = job.targetA;
                return true;
            }
            if (Faction.OfPlayer == null || !pawn.HostileTo(Faction.OfPlayer))
            {
                return false;
            }
            int band = bands.BandOf(pawn.Position);
            float best = float.MaxValue;
            Thing bestThing = null;
            foreach (Pawn colonist in map.mapPawns.FreeColonistsSpawned)
            {
                Score(colonist, pawn, bands, band, ref best, ref bestThing);
            }
            List<Building> buildings = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < buildings.Count; i++)
            {
                Score(buildings[i], pawn, bands, band, ref best, ref bestThing);
            }
            if (bestThing == null)
            {
                return false;
            }
            goal = bestThing;
            return true;
        }

        private static void Score(Thing t, Pawn pawn, ABBandMap bands, int band, ref float best,
            ref Thing bestThing)
        {
            if (t == null || !t.Spawned)
            {
                return;
            }
            float d = (bands.Translate(t.Position, band) - pawn.Position).LengthHorizontalSquared;
            if (d < best)
            {
                best = d;
                bestThing = t;
            }
        }

        /// <summary>
        /// Where to land: the cell nearest the goal that this pawn can legally jump to AND
        /// from which the goal is genuinely reachable on foot.
        ///
        /// ⚠ THE REACHABILITY TEST IS FROM THE LANDING CELL, NOT FROM THE PAWN. `pawn.CanReach`
        /// answers a question about where the pawn is standing NOW, which we have already
        /// established is useless - it is why we are jumping. `map.reachability.CanReach(cell,
        /// ...)` is the one that says whether the jump accomplishes anything, and it is the
        /// clause that makes stranding impossible.
        ///
        /// Cheap filters over the whole disc, expensive ones over the best few: the shaft
        /// solve and the reachability query are the only costly tests and they run at most
        /// MaxSolveCandidates times per scan, per pawn, per cooldown.
        /// </summary>
        public static bool TryFindLanding(Pawn pawn, ABBandMap bands, Verb verb,
            LocalTargetInfo goal, int goalBand, out IntVec3 landing)
        {
            landing = IntVec3.Invalid;
            Map map = pawn.Map;
            float range = verb.EffectiveRange;
            if (range <= 0f || range > GenRadial.MaxRadialPatternRadius)
            {
                Trace("declined: jump range " + range.ToString("0.0") + " is unusable");
                return false;
            }
            // ⚠⚠ THE DISC IS THE HORIZONTAL BUDGET, NOT THE RAW RANGE, AND GETTING THIS WRONG
            // MADE THE WHOLE FEATURE INERT (run #383). ABShaft charges VerticalCostPerLevel
            // for each band crossed, so a cell at the rim of a full-range disc computes as
            // `range + 3*levels` and is always refused. That alone would only have wasted a
            // few solves - but the candidates are ranked by DISTANCE TO GOAL, and when the
            // goal is far away the nearest cells to it are exactly the rim ones. The ranking
            // therefore hand-picked the twenty-four cells guaranteed to fail, every time, and
            // the trace read "24 candidates out of jump reach" with 1307 cells available.
            //
            // ⚠ THE LESSON: WHEN YOU RANK CANDIDATES BY ONE COST AND FILTER THEM BY ANOTHER,
            // THE RANKING WILL FIND THE FILTER'S BLIND SPOT. Spend the vertical cost up front
            // and the disc only contains cells the range rule can accept.
            int levels = Mathf.Abs(goalBand - bands.BandOf(pawn.Position));
            float budget = range - ABShaft.VerticalCostPerLevel * levels;
            if (budget <= 0f)
            {
                Trace("declined: jump range " + range.ToString("0.0") + " is entirely consumed "
                    + "by the vertical cost of " + levels + " level(s) ("
                    + (ABShaft.VerticalCostPerLevel * levels).ToString("0.0") + ")");
                return false;
            }
            IntVec3 anchor = bands.Translate(pawn.Position, goalBand);
            IntVec3 goalCell = goal.Cell;
            int filled = 0;
            int cheapPass = 0;
            int noSolution = 0;
            int noRoute = 0;
            int count = Mathf.Min(GenRadial.NumCellsInRadius(budget),
                GenRadial.RadialPattern.Length);
            for (int i = 0; i < count; i++)
            {
                IntVec3 c = anchor + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || bands.BandOf(c) != goalBand || bands.InGutter(c))
                {
                    continue;
                }
                // Vanilla's own landing legality (rule 36) - refuses open air, fog, closed
                // doors and anything this pawn cannot stand on.
                if (!JumpUtility.ValidJumpTarget(pawn, map, c) || c.IsForbidden(pawn))
                {
                    continue;
                }
                cheapPass++;
                float score = (c - goalCell).LengthHorizontalSquared;
                filled = Insert(c, score, filled);
            }
            Trace("landing search: horizontal budget " + budget.ToString("0.0") + " (range "
                + range.ToString("0.0") + " less " + levels + " level(s) of vertical cost); "
                + cheapPass + " cells passed the cheap filters (band, gutter, ValidJumpTarget, "
                + "forbidden); solving the best " + filled + " by distance to goal");
            for (int k = 0; k < filled; k++)
            {
                IntVec3 c = BestCells[k];
                // Band-aware, and it is the SAME call the player's targeter makes - it lands
                // in Patch_JumpUtility_ABCrossBandRange and then in ABShaft. Free of side
                // effects: the jump verbs override CanHitTargetFrom to bypass the shoot-line
                // path entirely, so nothing is parked for Projectile.Launch (rule 52).
                if (!verb.CanHitTargetFrom(pawn.Position, c))
                {
                    noSolution++;
                    continue;
                }
                if (!map.reachability.CanReach(c, goal, PathEndMode.Touch,
                        TraverseParms.For(pawn, Danger.Deadly)))
                {
                    noRoute++;
                    continue;
                }
                landing = c;
                Trace("landing " + c + " accepted after " + noSolution
                    + " out of jump reach and " + noRoute + " with no route onward");
                return true;
            }
            // Now that the disc is budgeted, a CanHitTargetFrom failure can no longer be a
            // range verdict - it means the shaft solver found no opening with sight lines at
            // both ends. Naming it precisely is the difference between "tune the numbers" and
            // "there is no hole in that floor".
            Trace("declined: NO LANDING - " + noSolution + " candidates with NO OPENING in "
                + "reach (no open column, or no sight line from the pawn to it, or the landing "
                + "cell is more than " + ABShaft.MaxDriftPerLevel + " cells per level from the "
                + "opening's mouth), " + noRoute + " jumpable but with NO ROUTE from the "
                + "landing cell to the goal (that clause is what stops a jump stranding a pawn)");
            return false;
        }

        /// <summary>Bounded best-of-K by score, ascending. Avoids sorting (and allocating for)
        /// a two-thousand-cell candidate list to use twenty-four of them.</summary>
        private static int Insert(IntVec3 c, float score, int filled)
        {
            if (filled == MaxSolveCandidates && score >= BestScores[filled - 1])
            {
                return filled;
            }
            int at = filled < MaxSolveCandidates ? filled : MaxSolveCandidates - 1;
            while (at > 0 && BestScores[at - 1] > score)
            {
                BestScores[at] = BestScores[at - 1];
                BestCells[at] = BestCells[at - 1];
                at--;
            }
            BestScores[at] = score;
            BestCells[at] = c;
            return Mathf.Min(filled + 1, MaxSolveCandidates);
        }

        /// <summary>Mirrors what each caster's own order path would build - `Ability.GetJob`
        /// for an ability (which also sets `job.ability`, without which the ability verb has
        /// no idea which ability triggered it), and `JumpUtility.OrderJump`'s job for a pack,
        /// minus the player-order plumbing a JobGiver must not use.</summary>
        private static Job MakeLeapJob(Ability ability, Verb verb, IntVec3 landing)
        {
            if (ability != null)
            {
                return ability.GetJob(landing, landing);
            }
            Job job = JobMaker.MakeJob(JobDefOf.CastJump, landing);
            job.verbToUse = verb;
            return job;
        }

        /// <summary>
        /// The jump-aware replacement for `RCellFinder.TryFindGoodAdjacentSpotToTouch`, which
        /// vanilla's mech giver uses to pick the cell it will land on.
        ///
        /// ⚠ VANILLA'S VERSION ASKS `toucher.CanReach(item, ...)` - A WALKING QUESTION,
        /// STANDING IN FRONT OF A JUMP (rule 5, §48's shape again). Repairing the giver's
        /// distance test alone would have moved the failure here and looked like the fix had
        /// not worked, because the mech would clear the range check and then find no adjacent
        /// spot it could WALK to on a level it cannot walk to at all.
        /// </summary>
        public static bool TryFindJumpSpotToTouch(Pawn pawn, Verb verb, Thing touchee,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;
            Map map = pawn.Map;
            int best = int.MaxValue;
            ABBandMap bands = ABBands.CompOf(map);
            int band = bands != null && bands.Banded ? bands.BandOf(pawn.Position) : 0;
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(touchee))
            {
                if (!c.InBounds(map) || !JumpUtility.ValidJumpTarget(pawn, map, c)
                    || c.IsForbidden(pawn))
                {
                    continue;
                }
                if (!ReachabilityImmediate.CanReachImmediate(c, touchee, map, PathEndMode.Touch,
                        pawn))
                {
                    continue;
                }
                if (!verb.CanHitTargetFrom(pawn.Position, c))
                {
                    continue;
                }
                // Band-local, so "nearest" means nearest horizontally rather than nearest
                // through the stack.
                IntVec3 here = bands != null && bands.Banded ? bands.Translate(c, band) : c;
                int d = (here - pawn.Position).LengthHorizontalSquared;
                if (d < best)
                {
                    best = d;
                    result = c;
                }
            }
            return result.IsValid;
        }
    }

    /// <summary>The node itself. Hung off `Humanlike_PostDuty` by AB_ThinkTrees.xml, so it is
    /// asked only when the duty tree produced nothing.</summary>
    public class JobGiver_ABBandLeap : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            return ABBandLeapAI.TryGiveLeapJob(pawn);
        }
    }

    /// <summary>
    /// PART A - REPAIRING VANILLA'S OWN MECH JUMP GIVER.
    ///
    /// <c>JobGiver_AIJumpToJobTarget.TryGiveJob</c> gates on
    /// <c>pawn.Position.DistanceTo(result)</c> against the verb's range plus a flat
    /// <c>GenSight.LineOfSight</c>, and it does that BEFORE it ever asks
    /// <c>ValidateTarget</c> - which has been band-aware since §82. So a mech could not jump
    /// to a fire or a downed colonist one level away, not because the jump was illegal but
    /// because a hand-rolled range check in front of the verb said a Slot was too far. Sixth
    /// instance of the mod's most common defect.
    ///
    /// Reimplemented in a prefix rather than postfixed, for the same reason as the turret
    /// forced-target patch: the two guards being replaced sit AHEAD of the part worth keeping.
    /// Same-band cases return true and never enter this code.
    ///
    /// ⚠ THIS GIVER RETURNS null AND STARTS ITS OWN JOB. It calls
    /// <c>pawn.jobs.StartJob(..., resumeCurJobAfterwards: true)</c> so the pawn resumes what
    /// it was doing after landing, and hands the think tree null regardless. Preserved
    /// exactly - returning the job instead would drop the resume and silently change mech
    /// behaviour on flat maps.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIJumpToJobTarget), "TryGiveJob")]
    public static class Patch_JobGiverAIJumpToJobTarget_ABCrossBand
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(JobGiver_AIJumpToJobTarget), "TryGiveJob") != null;
        }

        private static bool Prefix(JobGiver_AIJumpToJobTarget __instance, Pawn pawn,
            ref Job __result)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || !ABGuard.On(ABGuard.Movement))
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(pawn.Map);
                if (bands == null || !bands.Banded || __instance.ability == null)
                {
                    return true;
                }
                Ability ability = pawn.abilities?.GetAbility(__instance.ability);
                if (ability == null || !ability.CanCast || ability.verb == null)
                {
                    return true;
                }
                Job curJob = pawn.CurJob;
                if (curJob == null || curJob.def == __instance.ability.jobDef)
                {
                    return true;
                }
                LocalTargetInfo target = curJob.GetTarget(__instance.targetIndex);
                if (!target.IsValid || !target.Cell.InBounds(pawn.Map))
                {
                    return true;
                }
                if (bands.BandOf(target.Cell) == bands.BandOf(pawn.Position))
                {
                    return true; // one level: vanilla is correct as written
                }
                if (!__instance.CanJumpToTarget(pawn, target))
                {
                    return true; // vanilla will re-run its own refusal and return null
                }
                // The two replaced guards, band-aware, in one call.
                if (!ability.verb.CanHitTargetFrom(pawn.Position, target))
                {
                    __result = null;
                    return false;
                }
                IntVec3 result = target.Cell;
                if (target.HasThing && !ABBandLeapAI.TryFindJumpSpotToTouch(pawn, ability.verb,
                        target.Thing, out result))
                {
                    __result = null;
                    return false;
                }
                if (ability.verb.ValidateTarget(target, showMessages: false))
                {
                    ABBandLeapAI.vanillaGiverRepairs++;
                    Job job = ability.GetJob(result, result);
                    pawn.jobs.StartJob(job, JobCondition.Ongoing, null,
                        resumeCurJobAfterwards: true);
                    FleckMaker.Static(result, pawn.Map, FleckDefOf.FeedbackGoto);
                    ABV2Debug.Combat("mech giver repaired: " + pawn.LabelShortCap
                        + " jumping to " + result + " for " + target.ToStringSafe());
                }
                __result = null;
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "V2 cross-band mech jump giver");
            }
            return true;
        }
    }

    /// <summary>
    /// PART C - THE PANIC JUMP STAYS ON ITS OWN LEVEL (user's call).
    ///
    /// <c>JobGiver_AIJumpEscapeEnemies</c> picks its destination with
    /// <c>CellFinderLoose.GetFallbackDest</c>, a raw-distance search whose only per-cell
    /// filter is <c>verb.ValidateTarget</c> - and since §82 that validator ACCEPTS a cell one
    /// level down. So near a band seam a cornered sanguophage could panic-jump through the
    /// floor into whatever is below, which is a stealth breach mechanic arriving by accident
    /// through a reflex behaviour. A deliberate assault jump is the new giver's job and has to
    /// pass the reachability test; this one does not, so it is kept where it started.
    ///
    /// A postfix that nulls the job is the whole fix: the pawn simply does not panic-jump this
    /// tick and falls through to the rest of its tree, which is what happens on a flat map
    /// when no fallback cell is found.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_AIJumpEscapeEnemies), "TryGiveJob")]
    public static class Patch_JobGiverAIJumpEscape_ABClampToBand
    {
        private static bool Prepare()
        {
            return AccessTools.Method(typeof(JobGiver_AIJumpEscapeEnemies), "TryGiveJob") != null;
        }

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (__result == null || pawn == null || !pawn.Spawned)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(pawn.Map);
                if (bands == null || !bands.Banded || !__result.targetA.IsValid)
                {
                    return;
                }
                if (bands.BandOf(__result.targetA.Cell) == bands.BandOf(pawn.Position))
                {
                    return;
                }
                ABBandLeapAI.escapesClamped++;
                ABV2Debug.Combat("escape jump clamped: " + pawn.LabelShortCap
                    + " would have panic-jumped to band "
                    + bands.BandOf(__result.targetA.Cell));
                __result = null;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: escape jump clamp threw: " + e.Message,
                    762195936);
            }
        }
    }
}
