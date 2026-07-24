using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// An elevator car (T10 #2). Unlike stairs it does not auto-link on build:
    /// the player extends the shaft up or down through gizmos, generating the
    /// level if needed and spawning a linked car there. A ground car can hold
    /// BOTH links (primary = up, second = down), making it the middle of a
    /// chain: riding from the sky to the basement transfers to the ground car
    /// and immediately continues down in the same ride (the transfer queues the
    /// onward hop). Carries a power bridge like stairs; climbs much faster.
    /// </summary>
    public class Building_ABElevator : Building_ABStairs
    {
        public override bool AutoLinkOnSpawn => false;

        protected override void SetLink(int delta, Building_ABStairs other)
        {
            if (delta >= 0)
            {
                counterpart = other;
            }
            else
            {
                counterpartSecond = other;
            }
        }

        protected override Building_ABStairs GetLink(int delta)
        {
            return delta >= 0 ? Counterpart : SecondCounterpart;
        }

        protected override string LinkLine()
        {
            string up = Counterpart != null ? "AB_LinkedAbove".Translate() : (string)null;
            string down = SecondCounterpart != null ? "AB_LinkedBelow".Translate() : (string)null;
            if (up == null && down == null)
            {
                return "AB_NotLinkedLine".Translate();
            }
            if (up != null && down != null)
            {
                return up + " " + down;
            }
            return up ?? down;
        }

        protected override List<Gizmo> BuildGizmos()
        {
            List<Gizmo> list = new List<Gizmo>();
            AddDirection(list, 1);
            AddDirection(list, -1);
            return list;
        }

        private void AddDirection(List<Gizmo> list, int delta)
        {
            Building_ABStairs link = GetLink(delta);
            bool up = delta > 0;
            if (link != null)
            {
                Building_ABStairs target = link;
                list.Add(new Command_Action
                {
                    defaultLabel = (up ? "AB_ViewAbove" : "AB_ViewBelow").Translate(),
                    defaultDesc = "AB_ViewOtherLevelDesc".Translate(),
                    icon = def.uiIcon,
                    action = delegate
                    {
                        LevelCamera.JumpPreservingView(target.Map);
                    }
                });
                return;
            }
            Command_Action extend = new Command_Action
            {
                defaultLabel = (up ? "AB_ExtendUp" : "AB_ExtendDown").Translate(),
                defaultDesc = (up ? "AB_ExtendUpDesc" : "AB_ExtendDownDesc").Translate(),
                icon = def.uiIcon
            };
            int targetLevel = Map.Level() + delta;
            if (targetLevel < -1 || targetLevel > 1)
            {
                extend.Disable("AB_LevelCap".Translate());
            }
            else
            {
                int d = delta;
                extend.action = delegate
                {
                    // Level generation must not run inside the UI event.
                    LongEventHandler.ExecuteWhenFinished(delegate
                    {
                        EstablishLink(d);
                    });
                };
            }
            list.Add(extend);
        }

        protected override List<FloatMenuOption> BuildUseOptions(Pawn selPawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            if (!CanBeOrderedToUse(selPawn))
            {
                return list;
            }
            bool reachable = selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly);
            AddRide(list, selPawn, Counterpart, "AB_GoUp".Translate(), reachable);
            AddRide(list, selPawn, SecondCounterpart, "AB_GoDown".Translate(), reachable);
            // Two-hop rides through a middle car that links onward.
            AddTwoHop(list, selPawn, Counterpart, 1, reachable);
            AddTwoHop(list, selPawn, SecondCounterpart, -1, reachable);
            if (list.Count == 0)
            {
                list.Add(new FloatMenuOption("AB_GoUp".Translate() + " (" + "AB_NotLinkedShort".Translate() + ")", null));
            }
            return list;
        }

        private void AddTwoHop(List<FloatMenuOption> list, Pawn selPawn, Building_ABStairs mid, int delta, bool reachable)
        {
            if (!(mid is Building_ABElevator midCar))
            {
                return;
            }
            Building_ABStairs far = midCar.GetLink(delta);
            if (far == null)
            {
                return;
            }
            string label = (delta > 0 ? "AB_RideToSky" : "AB_RideToBasement").Translate();
            // Door parity: a forbidden middle car seals the through ride.
            if (midCar.EndForbiddenFor(selPawn))
            {
                list.Add(new FloatMenuOption(label + " (" + "ForbiddenLower".Translate() + ")", null));
                return;
            }
            AddRide(list, selPawn, far, label, reachable);
        }

        private void AddRide(List<FloatMenuOption> list, Pawn selPawn, Building_ABStairs destination, string label, bool reachable)
        {
            if (destination == null)
            {
                return;
            }
            if (EndForbiddenFor(selPawn) || destination.EndForbiddenFor(selPawn))
            {
                list.Add(new FloatMenuOption(label + " (" + "ForbiddenLower".Translate() + ")", null));
                return;
            }
            if (!reachable)
            {
                list.Add(new FloatMenuOption(label + " (" + "AB_NoPath".Translate() + ")", null));
                return;
            }
            list.Add(new FloatMenuOption(label, delegate
            {
                Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, this);
                job.targetC = destination;
                selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }));
        }
    }
}
