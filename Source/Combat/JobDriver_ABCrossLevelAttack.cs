using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A drafted pawn stands at a firing cell on its own level and shoots across the
    /// vertical gap at a target on the paired level (Model B), driving warm-up, bursts
    /// and cooldown from the weapon's own verb props. Each shot goes through
    /// CrossLevelCombat.Fire, which spawns a real projectile on the target's map.
    /// The job ends the moment the line of fire is lost (target moved out of range /
    /// behind cover / to another map), the target dies, or combat is disabled - at
    /// which point the caller's routing fallback or the player can take over.
    /// </summary>
    public class JobDriver_ABCrossLevelAttack : JobDriver
    {
        private Thing target;
        private bool warmedUp;
        private int warmupTicksLeft = -1;
        private int burstShotsLeft;
        private int ticksToNextShot;
        private int cooldownTicksLeft;

        /// <summary>Read by the selection-overlay UI (target line + aim pie).</summary>
        internal Thing Target => target;

        internal bool Warming => !warmedUp && warmupTicksLeft > 0;

        internal int WarmupTicksLeft => warmupTicksLeft;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        public override string GetReport()
        {
            if (target != null && !target.Destroyed)
            {
                return "AB_ReportAttackingAcross".Translate(target.LabelShort);
            }
            return base.GetReport();
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();
            // Pull the cross-map target from the handoff before any fail check runs.
            if (target == null && CrossLevelCombat.PendingTargets.TryGetValue(pawn.thingIDNumber, out Thing t))
            {
                target = t;
            }
            CrossLevelCombat.PendingTargets.Remove(pawn.thingIDNumber);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref target, "AB_target");
            Scribe_Values.Look(ref warmedUp, "AB_warmedUp", false);
            Scribe_Values.Look(ref warmupTicksLeft, "AB_warmupLeft", -1);
            Scribe_Values.Look(ref burstShotsLeft, "AB_burstLeft", 0);
            Scribe_Values.Look(ref ticksToNextShot, "AB_toNextShot", 0);
            Scribe_Values.Look(ref cooldownTicksLeft, "AB_cooldownLeft", 0);
        }

        /// <summary>GetRangedVerb walks the equipment and verb lists; with several AI
        /// shooters that is a per-tick cost worth caching. Re-resolved on the same
        /// 15-tick cadence as the line-of-fire revalidation, and dropped immediately
        /// when the equipment it came from is gone.</summary>
        private Verb_LaunchProjectile cachedVerb;

        private int verbStaleAt;

        private Verb_LaunchProjectile ResolvedVerb()
        {
            int now = Find.TickManager.TicksGame;
            if (cachedVerb != null && now < verbStaleAt)
            {
                Thing eq = cachedVerb.EquipmentSource;
                if (eq == null || !eq.Destroyed)
                {
                    return cachedVerb;
                }
            }
            cachedVerb = CrossLevelCombat.GetRangedVerb(pawn);
            verbStaleAt = now + LofCheckInterval;
            return cachedVerb;
        }

        private bool Valid()
        {
            if (!CrossLevelCombat.Enabled)
            {
                return false;
            }
            if (target == null || target.Destroyed || !target.Spawned)
            {
                return false;
            }
            if (pawn.Downed || pawn.Dead)
            {
                return false;
            }
            // Player pawns need the draft to keep shooting (undrafting cancels the
            // order, like any drafted attack); AI shooters have no draft at all.
            if (pawn.IsColonistPlayerControlled && !pawn.Drafted)
            {
                return false;
            }
            return ResolvedVerb() != null;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Engagement-line registry: MakeNewToils runs on fresh starts AND on load
            // resume, so loaded mid-fight shooters re-register too.
            CrossLevelCombatUI.ActiveShooters.Add(pawn);
            AddFinishAction(delegate { CrossLevelCombatUI.ActiveShooters.Remove(pawn); });
            this.FailOn(() => !Valid());
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);

            Toil fire = ToilMaker.MakeToil("AB_CrossFire");
            fire.defaultCompleteMode = ToilCompleteMode.Never;
            fire.handlingFacing = true;
            fire.tickAction = FireTick;
            yield return fire;
        }

        /// <summary>Ticks between line-of-fire revalidations. Each actual shot is
        /// fully validated inside CrossLevelCombat.Fire anyway; this only bounds how
        /// long a pawn can visibly aim at a target that stepped under a roof, so the
        /// per-tick GenSight raycast is not paid every tick by every firing pawn.</summary>
        private const int LofCheckInterval = 15;

        private int lofCheckIn;

        private void FireTick()
        {
            Verb_LaunchProjectile verb = ResolvedVerb();
            if (verb == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            if (--lofCheckIn <= 0)
            {
                if (!CrossLevelCombat.CanFireFrom(pawn.Map, pawn.Position, target, verb, out _))
                {
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }
                lofCheckIn = LofCheckInterval;
            }
            pawn.rotationTracker.FaceCell(target.Position);

            if (cooldownTicksLeft > 0)
            {
                cooldownTicksLeft--;
                return;
            }
            if (!warmedUp)
            {
                if (warmupTicksLeft < 0)
                {
                    // Vanilla warmup: verb warmup seconds scaled by the pawn's aiming
                    // delay stat (careful shooter, trigger-happy).
                    float seconds = verb.verbProps.warmupTime;
                    try
                    {
                        seconds *= pawn.GetStatValue(StatDefOf.AimingDelayFactor);
                    }
                    catch
                    {
                        // stat missing on exotic pawns: unscaled warmup
                    }
                    warmupTicksLeft = SecondsToTicks(seconds);
                }
                if (warmupTicksLeft > 0)
                {
                    warmupTicksLeft--;
                    return;
                }
                warmedUp = true;
                // Aim complete: the once-per-burst vanilla side effects (combat log +
                // Shooting XP), mirroring Verb_Shoot.WarmupComplete.
                ABShotEffects.OnBurstWarmupComplete(pawn, verb, target, verb.Projectile);
            }
            if (burstShotsLeft <= 0)
            {
                burstShotsLeft = Mathf.Max(1, verb.verbProps.burstShotCount);
                ticksToNextShot = 0;
            }
            if (ticksToNextShot > 0)
            {
                ticksToNextShot--;
                return;
            }

            if (!CrossLevelCombat.Fire(pawn, verb, target))
            {
                // The shot-time validation failed (target moved under a roof, map
                // gone, combat disabled): stop aiming at nothing.
                EndJobWith(JobCondition.Incompletable);
                return;
            }
            burstShotsLeft--;
            if (burstShotsLeft > 0)
            {
                ticksToNextShot = Mathf.Max(0, verb.verbProps.ticksBetweenBurstShots);
            }
            else
            {
                // Vanilla cycle: aim -> burst -> cooldown -> re-aim. Reset the warmup
                // so the next burst re-aims like a real stance cycle would.
                cooldownTicksLeft = CooldownTicks(verb, pawn);
                warmedUp = false;
                warmupTicksLeft = -1;
            }
        }

        private static int CooldownTicks(Verb verb, Pawn pawn)
        {
            try
            {
                // The vanilla path: handles equipment stats, tools, and modded verbs.
                return Mathf.Max(1, SecondsToTicks(verb.verbProps.AdjustedCooldown(verb, pawn)));
            }
            catch
            {
                return Mathf.Max(1, SecondsToTicks(verb.verbProps.defaultCooldownTime));
            }
        }

        private static int SecondsToTicks(float seconds) => Mathf.Max(0, Mathf.RoundToInt(seconds * 60f));
    }
}
