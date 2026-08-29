using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Dev tooling for §84, cross-level AI jumping.
    ///
    /// ⚠ THE PROBLEM THESE SOLVE IS THAT THE FEATURE'S SUCCESS AND ITS FAILURE LOOK
    /// IDENTICAL FROM THE OUTSIDE: a raider that cannot reach your base stands around, and a
    /// raider whose leap logic declined also stands around. Worse, the feature is deliberately
    /// hard to trigger - it needs a hostile with a jump, a goal on another band, and no
    /// walking route - so a test run that produces nothing proves nothing at all (rule 17).
    ///
    /// ⚠ USE THEM IN THIS ORDER:
    ///   1. `AB2: spawn jump raider` / `AB2: spawn sanguophage raider` to MANUFACTURE the
    ///      situation, instead of hoping a raid brings the right pawn.
    ///   2. `AB2: why won't this pawn leap` on that pawn. It runs the REAL decision with
    ///      tracing on and names the clause that declined (rule 31) - including the two that
    ///      are supposed to decline, so "conservative gate: it can walk there" reads as the
    ///      feature working rather than as a failure.
    ///   3. `AB2: force leap now` only after the probe says WOULD LEAP, to watch the jump
    ///      itself. It bypasses the cooldown, nothing else.
    ///
    /// ⚠ THE PROBE DOES NOT DISTURB THE PAWN. Tracing suppresses the cooldown write and the
    /// counters are restored afterwards, so probing a pawn repeatedly cannot gag its real
    /// behaviour or leave `leaps=1` behind for a leap that never happened.
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: why won't this pawn leap",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2LeapProbe()
        {
            Pawn pawn = SelectedLeapPawn();
            if (pawn == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("V2 leap probe: " + pawn.LabelShortCap + " ("
                + (pawn.Faction != null ? pawn.Faction.Name : "no faction") + ", "
                + (pawn.HostileTo(Faction.OfPlayer) ? "HOSTILE" : "not hostile")
                + " to the player)");
            Map map = pawn.Map;
            ABBandMap bands = ABBands.CompOf(map);
            sb.AppendLine("  at " + pawn.Position + " band "
                + (bands != null && bands.Banded ? bands.BandOf(pawn.Position).ToString() : "n/a")
                + ", drafted=" + pawn.Drafted + ", downed=" + pawn.Downed
                + ", curJob=" + (pawn.CurJob != null ? pawn.CurJob.def.defName : "none")
                + ", duty=" + (pawn.mindState?.duty != null
                    ? pawn.mindState.duty.def.defName : "none"));
            sb.Append(ABBandLeapAI.Explain(pawn));
            Log.Message(sb.ToString());
            Messages.Message("AB2: leap probe written to log.", MessageTypeDefOf.TaskCompletion,
                historical: false);
        }

        [DebugAction("As above", "AB2: force leap now",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ForceLeap()
        {
            Pawn pawn = SelectedLeapPawn();
            if (pawn == null)
            {
                return;
            }
            // Only the cooldown is bypassed. Every other clause still has to pass, or this
            // would be a tool that proves the jump animation works and nothing else.
            ABBandLeapAI.ClearCooldown(pawn);
            Job job = ABBandLeapAI.TryGiveLeapJob(pawn);
            if (job == null)
            {
                Messages.Message(
                    "AB2: the leap decision declined - run `AB2: why won't this pawn leap`.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            pawn.jobs.StartJob(job, JobCondition.InterruptForced);
            Messages.Message("AB2: leaping to " + job.targetA.Cell + ".",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }

        [DebugAction("As above", "AB2: spawn jump raider", actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2SpawnJumpRaider()
        {
            SpawnLeaper(jumpPack: true);
        }

        [DebugAction("As above", "AB2: spawn sanguophage raider",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2SpawnSanguophageRaider()
        {
            SpawnLeaper(jumpPack: false);
        }

        /// <summary>
        /// A hostile pawn that can jump, standing where you clicked, under a real assault
        /// Lord.
        ///
        /// ⚠ THE LORD IS THE POINT, NOT DECORATION. `JobGiver_ABBandLeap` hangs off
        /// `Humanlike_PostDuty`, which a pawn only reaches after a Lord duty produced no job.
        /// A factionless or lordless pawn never walks that branch of the tree, so it would
        /// never leap however perfect the geometry - and the test would report a bug that
        /// does not exist.
        /// </summary>
        private static void SpawnLeaper(bool jumpPack)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map) || !cell.Standable(map))
            {
                Messages.Message("AB2: click a standable cell.", MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }
            Faction faction = Find.FactionManager.RandomEnemyFaction(allowHidden: false,
                allowDefeated: false, allowNonHumanlike: false);
            if (faction == null)
            {
                Messages.Message("AB2: no enemy faction on this world.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            XenotypeDef xeno = jumpPack
                ? null
                : DefDatabase<XenotypeDef>.GetNamedSilentFail("Sanguophage");
            if (!jumpPack && xeno == null)
            {
                Messages.Message("AB2: Sanguophage xenotype not found (Biotech not active?).",
                    MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                PawnKindDefOf.Pirate, faction, PawnGenerationContext.NonPlayer, null,
                forceGenerateNewPawn: true, mustBeCapableOfViolence: true,
                forcedXenotype: xeno));
            GenSpawn.Spawn(pawn, cell, map);

            if (jumpPack)
            {
                ThingDef packDef = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_PackJump");
                if (packDef == null)
                {
                    Messages.Message("AB2: jump pack not found (Royalty not active?).",
                        MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                Apparel pack = (Apparel)ThingMaker.MakeThing(packDef);
                pawn.apparel.Wear(pack, dropReplacedApparel: false);
            }

            LordMaker.MakeNewLord(faction,
                new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: false),
                map, new List<Pawn> { pawn });

            ABBandMap bands = ABBands.CompOf(map);
            Messages.Message("AB2: spawned " + pawn.LabelShortCap + " ("
                + (jumpPack ? "jump pack" : "sanguophage") + ", " + faction.Name + ") on band "
                + (bands != null && bands.Banded ? bands.BandOf(cell).ToString() : "n/a") + ".",
                MessageTypeDefOf.TaskCompletion, historical: false);
        }

        private static Pawn SelectedLeapPawn()
        {
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null || !pawn.Spawned)
            {
                Messages.Message("AB2: select exactly one spawned pawn first.",
                    MessageTypeDefOf.RejectInput, historical: false);
                return null;
            }
            return pawn;
        }
    }
}
