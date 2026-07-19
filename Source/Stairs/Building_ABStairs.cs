using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A stairwell. When built by the player it generates (or reuses) the level
    /// map in its direction and spawns a linked counterpart stairwell at the same
    /// coordinates there. Auto-spawned counterparts skip generation because their
    /// link is set before spawning. Destroying either end removes both.
    /// </summary>
    public class Building_ABStairs : Building
    {
        private Building_ABStairs counterpart;

        public Building_ABStairs Counterpart
        {
            get
            {
                if (counterpart == null)
                {
                    return null;
                }
                if (counterpart.Destroyed)
                {
                    // Terminal state: drop the reference so it cannot leak or mislead UI.
                    counterpart = null;
                    return null;
                }
                if (!counterpart.Spawned)
                {
                    // Transient (mid-spawn or mid-load): keep the link, just report unavailable.
                    return null;
                }
                return counterpart;
            }
        }

        public ABStairsExtension Ext => def.GetModExtension<ABStairsExtension>();

        public int DeltaLevel => Ext?.deltaLevel ?? 0;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!respawningAfterLoad && counterpart == null)
            {
                // Defer to end of frame: map generation must not run inside a spawn call.
                LongEventHandler.ExecuteWhenFinished(TryEstablishLink);
            }
        }

        private void TryEstablishLink()
        {
            if (Destroyed || !Spawned || counterpart != null || !ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            try
            {
                int target = Map.Level() + DeltaLevel;
                Map targetMap = null;
                if (target == 0)
                {
                    targetMap = Map.GroundMap();
                }
                else if (target >= -1 && target <= 1)
                {
                    MapGeneratorDef gen = target == -1 ? ABDefOf.AB_Basement : ABDefOf.AB_Sky;
                    bool generated;
                    targetMap = LevelMapGen.GetOrGenerate(Map, target, gen, out generated);
                    if (generated && targetMap != null)
                    {
                        string key = target == -1 ? "AB_BasementCreated" : "AB_SkyCreated";
                        Messages.Message(key.Translate(), new TargetInfo(Position, Map), MessageTypeDefOf.PositiveEvent);
                    }
                }
                if (targetMap == null || targetMap == Map)
                {
                    Log.Warning(ABLog.Tag + " Stairs at " + Position + " could not resolve a target level map.");
                    return;
                }
                SpawnCounterpartOn(targetMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "stair link creation");
            }
        }

        private void SpawnCounterpartOn(Map targetMap)
        {
            ThingDef cpDef = Ext?.counterpartDef;
            if (cpDef == null)
            {
                Log.Error(ABLog.Tag + " Missing counterpartDef on " + def.defName);
                return;
            }
            IntVec3 pos = Position;
            PrepareLanding(targetMap, pos);
            Building_ABStairs cp = (Building_ABStairs)ThingMaker.MakeThing(cpDef, Stuff);
            cp.SetFaction(Faction.OfPlayer);
            cp.counterpart = this;
            counterpart = cp;
            GenSpawn.Spawn(cp, pos, targetMap, Rotation, WipeMode.Vanish);
            ABLog.Dev("Linked stairs " + ThingID + " (level " + Map.Level() + ") with " + cp.ThingID + " (level " + targetMap.Level() + ").");
        }

        /// <summary>Clears a 3x3 landing: rocks mined out in the basement, air becomes rooftop on the sky level, fog lifted.</summary>
        private static void PrepareLanding(Map targetMap, IntVec3 center)
        {
            int lvl = targetMap.Level();
            foreach (IntVec3 c in CellRect.CenteredOn(center, 1).ClipInsideMap(targetMap))
            {
                if (lvl < 0)
                {
                    List<Thing> things = c.GetThingList(targetMap).ToList();
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (t.def.building != null && t.def.building.isNaturalRock && t.def.destroyable)
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                }
                else if (lvl > 0 && targetMap.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                {
                    targetMap.terrainGrid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
                targetMap.fogGrid.Unfog(c);
            }
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Building_ABStairs cp = counterpart;
            counterpart = null;
            base.Destroy(mode);
            if (cp != null && !cp.Destroyed)
            {
                cp.counterpart = null;
                cp.Destroy(DestroyMode.Vanish);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref counterpart, "AB_counterpart");
        }

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string line;
            if (Counterpart != null)
            {
                line = (DeltaLevel > 0 ? "AB_LinkedAbove" : "AB_LinkedBelow").Translate();
            }
            else
            {
                line = "AB_NotLinkedLine".Translate();
            }
            return baseStr.NullOrEmpty() ? line : baseStr + "\n" + line;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            List<Gizmo> extras = null;
            try
            {
                extras = BuildGizmos();
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Stairs gizmos failed: " + e, 762195844);
            }
            if (extras == null)
            {
                yield break;
            }
            for (int i = 0; i < extras.Count; i++)
            {
                yield return extras[i];
            }
        }

        private List<Gizmo> BuildGizmos()
        {
            List<Gizmo> list = new List<Gizmo>();
            Building_ABStairs cp = Counterpart;
            if (cp != null)
            {
                list.Add(new Command_Action
                {
                    defaultLabel = (DeltaLevel > 0 ? "AB_ViewAbove" : "AB_ViewBelow").Translate(),
                    defaultDesc = "AB_ViewOtherLevelDesc".Translate(),
                    icon = def.uiIcon,
                    action = delegate
                    {
                        LevelCamera.JumpPreservingView(cp.Map);
                    }
                });
            }
            return list;
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(selPawn))
            {
                yield return o;
            }
            List<FloatMenuOption> extras = null;
            try
            {
                extras = BuildUseOptions(selPawn);
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Stairs float menu failed: " + e, 762195843);
            }
            if (extras == null)
            {
                yield break;
            }
            for (int i = 0; i < extras.Count; i++)
            {
                yield return extras[i];
            }
        }

        private List<FloatMenuOption> BuildUseOptions(Pawn selPawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            if (!selPawn.RaceProps.Humanlike)
            {
                return list;
            }
            string label = (DeltaLevel > 0 ? "AB_GoUp" : "AB_GoDown").Translate();
            if (Counterpart == null)
            {
                list.Add(new FloatMenuOption(label + " (" + "AB_NotLinkedShort".Translate() + ")", null));
            }
            else if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly))
            {
                list.Add(new FloatMenuOption(label + " (" + "AB_NoPath".Translate() + ")", null));
            }
            else
            {
                list.Add(new FloatMenuOption(label, delegate
                {
                    Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, this);
                    selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }));
            }
            return list;
        }
    }
}
