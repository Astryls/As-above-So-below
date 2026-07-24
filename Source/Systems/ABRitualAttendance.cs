using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Rituals across levels (the attendance model). Vanilla rituals are map-scoped
    /// at two points and both are handled here:
    ///
    ///  1. CANDIDATES: the begin-gizmo gating (RitualBehaviorWorker.CanStartRitualNow)
    ///     and the begin dialog's pool (Dialog_BeginRitual.CreateRitualRoleAssignments)
    ///     both read MapPawns.FreeColonistsAndPrisonersSpawned of the ritual map only -
    ///     a role changer in the basement makes the ritual read "you need a role
    ///     changer". While either method runs (scoped flag, same pattern as the float
    ///     -menu redirect), the getter's postfix returns a MERGED COPY that appends
    ///     colonists and prisoners from the column's linked levels who have a usable
    ///     stair route to the ritual map. Vanilla's cached list is never mutated.
    ///
    ///  2. ATTENDANCE: vanilla TryExecuteOn creates the ritual lord immediately, and
    ///     lords are map-scoped - an off-map participant would corrupt it. A prefix
    ///     intercepts the start when any assigned participant is off-map: everyone
    ///     off-map rides the stairs (AB_UseStairs), a message announces the gathering,
    ///     and the fully VANILLA start re-runs once all participants stand on the
    ///     ritual map (reentrancy flag). Stages, roles, spectators, and outcomes stay
    ///     untouched vanilla because the lord only ever starts with everyone present.
    ///     Pending gathers time out (participant died, stairs destroyed) with a
    ///     cancel message; obligation consumption on a timed-out gather is accepted
    ///     and documented (obligations regenerate).
    ///
    /// Setting crossLevelRituals (default ON); ABGuard.Movement (it is pawn routing);
    /// zero idle cost (single static count read per tick); state cleared on load.
    /// </summary>
    internal static class ABRitualAttendance
    {
        /// <summary>Scoped: true only while a ritual candidate-list builder runs.</summary>
        [ThreadStatic]
        private static bool candidateScope;

        /// <summary>Reentrancy: true while the pending machinery re-runs the vanilla
        /// start; the TryExecuteOn prefix passes it straight through.</summary>
        internal static bool Executing;

        internal static bool Enabled
        {
            get
            {
                ABSettings s = ABMod.Settings;
                return ABGuard.On(ABGuard.Movement) && s != null && s.crossLevelRituals;
            }
        }

        internal static void EnterScope()
        {
            candidateScope = true;
        }

        internal static void ExitScope()
        {
            candidateScope = false;
        }

        /// <summary>Reentrancy guard for the merge: reading a LINKED map's pool from
        /// inside AppendFrom fires this same postfix again (the scope flag is still
        /// set), which merges from ITS links, which reads the first map again -
        /// unbounded recursion and an instant no-dialog stack-overflow crash (run-12
        /// BUG1: clicking a ritual spot). While merging, inner reads return the raw
        /// vanilla lists.</summary>
        [ThreadStatic]
        private static bool merging;

        /// <summary>Merged candidate pool for the ritual map: vanilla's list plus
        /// column-mates with a usable stair route. Returns null when no merge applies
        /// (common case: flag off, no linked levels).</summary>
        internal static List<Pawn> TryMergeCandidates(Map map, List<Pawn> vanilla)
        {
            if (merging || !candidateScope || !Enabled || map == null || vanilla == null)
            {
                return null;
            }
            LevelComp comp = map.Levels();
            if (comp == null)
            {
                return null;
            }
            merging = true;
            try
            {
                List<Pawn> merged = null;
                AppendFrom(comp.upperMap, map, vanilla, ref merged);
                AppendFrom(comp.lowerMap, map, vanilla, ref merged);
                return merged;
            }
            finally
            {
                merging = false;
            }
        }

        /// <summary>Merged colony-animal pool for animal ritual roles (sacrifice
        /// etc., parity pass 2026-07-24): vanilla's SpawnedColonyAnimals plus
        /// linked levels' colony animals with a usable stair route. Same
        /// copy-on-merge + reentrancy rules as the colonist merge.</summary>
        internal static List<Pawn> TryMergeAnimalCandidates(Map map, List<Pawn> vanilla)
        {
            if (merging || !candidateScope || !Enabled || map == null || vanilla == null)
            {
                return null;
            }
            LevelComp comp = map.Levels();
            if (comp == null)
            {
                return null;
            }
            merging = true;
            try
            {
                List<Pawn> merged = null;
                AppendAnimalsFrom(comp.upperMap, map, vanilla, ref merged);
                AppendAnimalsFrom(comp.lowerMap, map, vanilla, ref merged);
                return merged;
            }
            finally
            {
                merging = false;
            }
        }

        private static void AppendAnimalsFrom(Map other, Map ritualMap, List<Pawn> vanilla, ref List<Pawn> merged)
        {
            if (other == null || other.Disposed || other == ritualMap)
            {
                return;
            }
            List<Pawn> pool = other.mapPawns.SpawnedColonyAnimals;
            for (int i = 0; i < pool.Count; i++)
            {
                Pawn p = pool[i];
                if (p == null || p.Dead || p.Downed || !p.Spawned || p.MentalStateDef != null)
                {
                    continue;
                }
                if (CrossLevelWork.NearestUsableStairsCached(p, ritualMap)?.CounterpartTowards(ritualMap) == null)
                {
                    continue;
                }
                if (merged == null)
                {
                    merged = new List<Pawn>(vanilla);
                }
                if (!merged.Contains(p))
                {
                    merged.Add(p);
                }
            }
        }

        private static void AppendFrom(Map other, Map ritualMap, List<Pawn> vanilla, ref List<Pawn> merged)
        {
            if (other == null || other.Disposed || other == ritualMap)
            {
                return;
            }
            List<Pawn> pool = other.mapPawns.FreeColonistsAndPrisonersSpawned;
            for (int i = 0; i < pool.Count; i++)
            {
                Pawn p = pool[i];
                if (p == null || p.Dead || p.Downed || !p.Spawned)
                {
                    continue;
                }
                if (p.IsPrisoner && (p.guest == null || !p.guest.PrisonerIsSecure))
                {
                    // Parity pass 2026-07-24: SECURE prisoners now cross for
                    // rituals (vanilla walks them to same-map rituals via the
                    // ritual duty; the gather machinery walks them over the
                    // stairs the same way). Unsecured prisoners would bolt.
                    continue;
                }
                if (CrossLevelWork.NearestUsableStairsCached(p, ritualMap)?.CounterpartTowards(ritualMap) == null)
                {
                    continue;
                }
                if (merged == null)
                {
                    merged = new List<Pawn>(vanilla);
                }
                if (!merged.Contains(p))
                {
                    merged.Add(p);
                }
            }
        }

        // --- pending gather machinery -----------------------------------------

        private sealed class Pending
        {
            public RitualBehaviorWorker behavior;
            public TargetInfo target;
            public Pawn organizer;
            public Precept_Ritual ritual;
            public RitualObligation obligation;
            public RitualRoleAssignments assignments;
            public bool playerForced;
            public int timeoutAt;
        }

        private static readonly List<Pending> pending = new List<Pending>();

        private const int GatherTimeoutTicks = 18000;

        internal static bool AnyPending => pending.Count > 0;

        internal static void ClearAll()
        {
            pending.Clear();
        }

        /// <summary>Begin a cross-level gather: route every off-map participant and
        /// queue the vanilla start for when they all arrive.</summary>
        internal static bool BeginGather(RitualBehaviorWorker behavior, TargetInfo target, Pawn organizer,
            Precept_Ritual ritual, RitualObligation obligation, RitualRoleAssignments assignments, bool playerForced)
        {
            try
            {
                Map ritualMap = target.Map;
                if (ritualMap == null || assignments == null)
                {
                    return false;
                }
                int routed = 0;
                List<Pawn> parts = assignments.Participants;
                for (int i = 0; i < parts.Count; i++)
                {
                    Pawn p = parts[i];
                    if (p == null || p.Dead || !p.Spawned || p.MapHeld == ritualMap)
                    {
                        continue;
                    }
                    // Attendees file toward the stairwell nearest the ritual
                    // spot, not nearest themselves.
                    if (!StairRouter.TryBestToward(p, ritualMap, target.Cell,
                        out Building_ABStairs entry, out Building_ABStairs exit))
                    {
                        entry = CrossLevelWork.NearestUsableStairsCached(p, ritualMap);
                        exit = entry?.CounterpartTowards(ritualMap);
                    }
                    if (exit == null)
                    {
                        Messages.Message("AB_RitualNoRoute".Translate(p.LabelShort), p,
                            MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                    Job job = CrossLevelWork.MakeStairsJob(entry, exit);
                    p.jobs?.StartJob(job, JobCondition.InterruptForced);
                    routed++;
                }
                if (routed == 0)
                {
                    return false; // everyone is already here; run vanilla
                }
                if (pending.Count > 8)
                {
                    pending.RemoveAt(0);
                }
                pending.Add(new Pending
                {
                    behavior = behavior,
                    target = target,
                    organizer = organizer,
                    ritual = ritual,
                    obligation = obligation,
                    assignments = assignments,
                    playerForced = playerForced,
                    timeoutAt = Find.TickManager.TicksGame + GatherTimeoutTicks
                });
                Messages.Message("AB_RitualGathering".Translate(routed), target, MessageTypeDefOf.NeutralEvent, historical: false);
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "ritual gather begin");
                return false;
            }
        }

        /// <summary>Called every tick from the game comp; first line is a count read.
        /// A pending ritual starts the moment every living participant stands on the
        /// ritual map; it cancels on timeout or when the target dies.</summary>
        internal static void Tick()
        {
            if (pending.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending pd = pending[i];
                Map ritualMap = pd.target.Map;
                if (ritualMap == null || ritualMap.Disposed || pd.behavior == null)
                {
                    pending.RemoveAt(i);
                    continue;
                }
                if (now > pd.timeoutAt)
                {
                    pending.RemoveAt(i);
                    Messages.Message("AB_RitualGatherTimeout".Translate(pd.ritual?.LabelCap ?? "ritual".TranslateSimple()),
                        pd.target, MessageTypeDefOf.NegativeEvent, historical: false);
                    continue;
                }
                bool allPresent = true;
                List<Pawn> parts = pd.assignments.Participants;
                for (int j = 0; j < parts.Count; j++)
                {
                    Pawn p = parts[j];
                    if (p == null || p.Dead)
                    {
                        continue; // vanilla copes with dead entries at start
                    }
                    if (p.MapHeld != ritualMap)
                    {
                        allPresent = false;
                        break;
                    }
                }
                if (!allPresent)
                {
                    continue;
                }
                pending.RemoveAt(i);
                try
                {
                    Executing = true;
                    pd.behavior.TryExecuteOn(pd.target, pd.organizer, pd.ritual, pd.obligation,
                        pd.assignments, pd.playerForced);
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.Movement, e, "ritual gather start");
                }
                finally
                {
                    Executing = false;
                }
            }
        }
    }

    /// <summary>Scope the candidate merge around the begin-gizmo gating.</summary>
    [HarmonyPatch(typeof(RitualBehaviorWorker), nameof(RitualBehaviorWorker.CanStartRitualNow))]
    internal static class Patch_Ritual_CanStartNow_Scope
    {
        private static void Prefix()
        {
            ABRitualAttendance.EnterScope();
        }

        private static void Finalizer()
        {
            ABRitualAttendance.ExitScope();
        }
    }

    /// <summary>Scope the candidate merge around the begin dialog's pool builder.</summary>
    [HarmonyPatch(typeof(Dialog_BeginRitual), nameof(Dialog_BeginRitual.CreateRitualRoleAssignments))]
    internal static class Patch_Ritual_CreateAssignments_Scope
    {
        private static void Prefix()
        {
            ABRitualAttendance.EnterScope();
        }

        private static void Finalizer()
        {
            ABRitualAttendance.ExitScope();
        }
    }

    /// <summary>The merge itself: inside a ritual candidate scope, the spawned
    /// colonists+prisoners list of the ritual map gains the column-mates who can
    /// actually take the stairs there. Copy-on-merge; vanilla's cache is never
    /// touched, and outside the scope this is a single bool read.</summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.FreeColonistsAndPrisonersSpawned), MethodType.Getter)]
    internal static class Patch_MapPawns_RitualCandidates
    {
        private static void Postfix(Map ___map, ref List<Pawn> __result)
        {
            try
            {
                List<Pawn> merged = ABRitualAttendance.TryMergeCandidates(___map, __result);
                if (merged != null)
                {
                    __result = merged;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "ritual candidate merge");
            }
        }
    }

    /// <summary>The animal-role merge: inside a ritual candidate scope, the
    /// spawned colony animals of the ritual map gain linked levels' animals
    /// with a stair route (sacrifice roles etc.). Copy-on-merge, same
    /// guards as the colonist merge.</summary>
    [HarmonyPatch(typeof(MapPawns), nameof(MapPawns.SpawnedColonyAnimals), MethodType.Getter)]
    internal static class Patch_MapPawns_RitualAnimalCandidates
    {
        private static void Postfix(Map ___map, ref List<Pawn> __result)
        {
            try
            {
                List<Pawn> merged = ABRitualAttendance.TryMergeAnimalCandidates(___map, __result);
                if (merged != null)
                {
                    __result = merged;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "ritual animal candidate merge");
            }
        }
    }

    /// <summary>Reachability parity for ritual gating: the base GetBlockingIssues
    /// checks mustBeAbleToReachTarget roles with pawn.CanReach, which is same-map
    /// only - an assigned role holder on a linked level always read as "must be able
    /// to reach ritual target" (run-13 BUG2). The base loop is replaced verbatim
    /// with one whose reach test accepts a usable stair route for off-map pawns.
    /// Subclass overrides that add EXTRA issues call base.GetBlockingIssues and get
    /// this version for the base part.</summary>
    [HarmonyPatch(typeof(RitualObligationTargetFilter), nameof(RitualObligationTargetFilter.GetBlockingIssues))]
    internal static class Patch_Ritual_BlockingIssues_Reach
    {
        private static bool Prefix(RitualObligationTargetFilter __instance, TargetInfo target,
            RitualRoleAssignments assignments, ref IEnumerable<string> __result)
        {
            if (!ABRitualAttendance.Enabled)
            {
                return true;
            }
            __result = Issues(target, assignments);
            return false;
        }

        private static IEnumerable<string> Issues(TargetInfo target, RitualRoleAssignments assignments)
        {
            foreach (RitualRole role in assignments.AllRolesForReading)
            {
                if (!role.mustBeAbleToReachTarget)
                {
                    continue;
                }
                Pawn pawn = assignments.FirstAssignedPawn(role);
                if (pawn == null)
                {
                    continue;
                }
                bool reachable;
                if (pawn.MapHeld == target.Map)
                {
                    reachable = pawn.CanReach((LocalTargetInfo)target, PathEndMode.Touch, pawn.NormalMaxDanger());
                }
                else
                {
                    // Off-map candidate: reachable when a usable stair route leads
                    // to the ritual map (the gather machinery walks them over).
                    reachable = target.Map != null
                        && CrossLevelWork.NearestUsableStairsCached(pawn, target.Map)
                            ?.CounterpartTowards(target.Map) != null;
                }
                if (!reachable)
                {
                    yield return "RitualTargetUnreachable".Translate(role.LabelCap);
                }
            }
        }
    }

    /// <summary>Intercept the vanilla start when participants are off-map: gather
    /// first, then run the untouched vanilla start on arrival.</summary>
    [HarmonyPatch(typeof(RitualBehaviorWorker), nameof(RitualBehaviorWorker.TryExecuteOn))]
    internal static class Patch_Ritual_TryExecuteOn_Gather
    {
        private static bool Prefix(RitualBehaviorWorker __instance, TargetInfo target, Pawn organizer,
            Precept_Ritual ritual, RitualObligation obligation, RitualRoleAssignments assignments,
            bool playerForced)
        {
            // NOTE: TryExecuteOn is VOID. The first build declared `ref bool __result`,
            // which made Harmony REJECT the patch at boot (HarmonyBoot skipped the
            // class with only a startup warning) - the intercept never existed and
            // rituals started immediately with off-map participants (run-14 bug).
            if (ABRitualAttendance.Executing || !ABRitualAttendance.Enabled)
            {
                return true;
            }
            try
            {
                Map ritualMap = target.Map;
                if (ritualMap == null || assignments == null)
                {
                    return true;
                }
                bool anyOffMap = false;
                List<Pawn> parts = assignments.Participants;
                for (int i = 0; i < parts.Count; i++)
                {
                    Pawn p = parts[i];
                    if (p != null && !p.Dead && p.Spawned && p.MapHeld != ritualMap)
                    {
                        anyOffMap = true;
                        break;
                    }
                }
                if (!anyOffMap)
                {
                    return true;
                }
                if (ABRitualAttendance.BeginGather(__instance, target, organizer, ritual,
                        obligation, assignments, playerForced))
                {
                    // The dialog closes and the gather is under way; the vanilla
                    // start re-runs from Tick() when everyone has arrived.
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "ritual gather intercept");
                return true;
            }
        }
    }
}
