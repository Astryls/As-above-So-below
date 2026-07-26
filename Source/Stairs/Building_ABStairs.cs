using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
// Narrow alias (no namespace-wide UnityEngine import to avoid Verse type
// collisions) so the cs-assets manifest scanner can see the ContentFinder
// literal below - it matches ContentFinder<Texture2D>, not the fully-qualified
// form.
using Texture2D = UnityEngine.Texture2D;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A stairwell. When built by the player it generates (or reuses) the level
    /// map in its direction and spawns a linked counterpart stairwell at the same
    /// coordinates there. Auto-spawned counterparts skip generation because their
    /// link is set before spawning. Destroying either end removes both.
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_ABStairs : Building
    {
        /// <summary>Base climb duration in ticks before the per-type factor,
        /// quality scaling, and the settings slider. Single source of truth for
        /// every driver that walks a pawn through a link.</summary>
        public const int BaseClimbTicks = 90;

        protected Building_ABStairs counterpart;

        /// <summary>Second link, used only by elevators (primary = up, second =
        /// down). Plain stairs never set it.</summary>
        protected Building_ABStairs counterpartSecond;

        public Building_ABStairs Counterpart => FilterLink(ref counterpart);

        public Building_ABStairs SecondCounterpart => FilterLink(ref counterpartSecond);

        public bool HasAnyLink => Counterpart != null || SecondCounterpart != null;

        private static Building_ABStairs FilterLink(ref Building_ABStairs link)
        {
            if (link == null)
            {
                return null;
            }
            if (link.Destroyed)
            {
                // Terminal state: drop the reference so it cannot leak or mislead UI.
                link = null;
                return null;
            }
            if (!link.Spawned)
            {
                // Transient (mid-spawn or mid-load): keep the link, just report unavailable.
                return null;
            }
            return link;
        }

        /// <summary>The linked end sitting on the given map, if any. The single
        /// call every consumer should use: it works identically for plain stairs
        /// (one link) and elevators (up to two).</summary>
        public Building_ABStairs CounterpartTowards(Map target)
        {
            Building_ABStairs cp = Counterpart;
            if (cp != null && cp.Map == target)
            {
                return cp;
            }
            cp = SecondCounterpart;
            if (cp != null && cp.Map == target)
            {
                return cp;
            }
            return null;
        }

        /// <summary>Store a link in the slot for the given direction. Plain stairs
        /// have one slot; elevators split by sign (primary = up, second = down).</summary>
        protected virtual void SetLink(int delta, Building_ABStairs other)
        {
            counterpart = other;
        }

        protected virtual Building_ABStairs GetLink(int delta)
        {
            return Counterpart;
        }

        /// <summary>Whether spawning should immediately establish the def-direction
        /// link. Elevators extend their shaft through gizmos instead.</summary>
        public virtual bool AutoLinkOnSpawn => true;

        private ABStairsExtension extCached;
        private bool extResolved;

        /// <summary>Def extension, resolved once. Sentinel flag rather than a
        /// null check: the extension can legitimately be absent, and
        /// GetModExtension walks the extension list on every call.</summary>
        public ABStairsExtension Ext
        {
            get
            {
                if (!extResolved)
                {
                    extResolved = true;
                    extCached = def.GetModExtension<ABStairsExtension>();
                }
                return extCached;
            }
        }

        public int DeltaLevel => Ext?.deltaLevel ?? 0;

        private CompForbiddable forbiddableInt;

        private bool forbiddableResolved;

        /// <summary>Vanilla forbid comp; null on defs without it (utility links).</summary>
        public CompForbiddable Forbiddable
        {
            get
            {
                if (!forbiddableResolved)
                {
                    forbiddableInt = GetComp<CompForbiddable>();
                    forbiddableResolved = true;
                }
                return forbiddableInt;
            }
        }

        /// <summary>Raw comp state of THIS end - the pawnless colony-flow check
        /// (demand anchors, planning). Hostile flows never consult it.</summary>
        public bool EndForbidden
        {
            get
            {
                CompForbiddable f = Forbiddable;
                return f != null && f.Forbidden;
            }
        }

        /// <summary>Door-passage semantics for THIS end (2026-07-24), mirroring
        /// vanilla IsForbiddenToPass exactly: comp forbid only (never the
        /// pawn's allowed area - stairs stay traversable as route segments),
        /// player-faction scoped, drafted pawns respect it, mental states and
        /// hostiles ignore it.</summary>
        public bool EndForbiddenFor(Pawn p)
        {
            return p != null && EndForbidden && p.Faction == Faction.OfPlayer
                && ForbidUtility.CaresAboutForbidden(p, cellTarget: false, bypassDraftedCheck: true);
        }

        /// <summary>A passage is forbidden when EITHER end is: forbidding the
        /// top of a staircase seals it from below too, like a forbidden door
        /// anywhere along a corridor.</summary>
        public bool PassageForbiddenFor(Pawn p, Map target)
        {
            if (EndForbiddenFor(p))
            {
                return true;
            }
            Building_ABStairs cp = target != null ? CounterpartTowards(target) : Counterpart;
            return cp != null && cp.EndForbiddenFor(p);
        }

        /// <summary>Pawnless variant for colony planning flows.</summary>
        public bool PassageForbiddenForColony(Map target)
        {
            if (EndForbidden)
            {
                return true;
            }
            Building_ABStairs cp = target != null ? CounterpartTowards(target) : Counterpart;
            return cp != null && cp.EndForbidden;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.Levels()?.RegisterStairs(this);
            if (!respawningAfterLoad && counterpart == null && AutoLinkOnSpawn)
            {
                // Defer to end of frame: map generation must not run inside a spawn call.
                LongEventHandler.ExecuteWhenFinished(TryEstablishLink);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // Uninstall (minify) severs the pair and collapses the far side
            // without refund: a shaft cannot keep standing without its head, and
            // leaving it standing would mint free counterpart buildings on every
            // reinstall cycle. The Destroy path and map-removal severing null the
            // link BEFORE despawning, so cp is only non-null for minify-style
            // despawns. Reinstalling elsewhere spawns a fresh counterpart there,
            // exactly like the initial build.
            Building_ABStairs cp = counterpart;
            Building_ABStairs cp2 = counterpartSecond;
            SeverLink();
            Map m = Map;
            base.DeSpawn(mode);
            m?.Levels()?.DeregisterStairs(this);
            if (cp != null && !cp.Destroyed && cp.Spawned)
            {
                cp.Destroy(DestroyMode.Vanish);
            }
            if (cp2 != null && !cp2.Destroyed && cp2.Spawned)
            {
                cp2.Destroy(DestroyMode.Vanish);
            }
        }

        /// <summary>Climb duration for this stairs type: base 90 ticks scaled by
        /// the def's climbFactor, by quality when present (legendary climbs a
        /// third faster than awful), and by the settings slider.</summary>
        public int ClimbTicksFor(Pawn p)
        {
            float f = Ext?.climbFactor ?? 1f;
            if (this.TryGetQuality(out QualityCategory qc))
            {
                f *= UnityEngine.Mathf.Lerp(1.15f, 0.75f, (int)qc / 6f);
            }
            float setting = ABMod.Settings?.climbTimeMultiplier ?? 1f;
            return UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(90f * f * setting));
        }

        private void TryEstablishLink()
        {
            EstablishLink(DeltaLevel);
        }

        /// <summary>Resolve or generate the level in the given direction and spawn
        /// a linked counterpart there. Shared by stairs auto-linking on build and
        /// elevator shaft extension gizmos.</summary>
        protected void EstablishLink(int delta)
        {
            if (Destroyed || !Spawned || delta == 0 || GetLink(delta) != null || !ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            // Never grow a column from a link mounted on gravship substructure:
            // the level maps cannot fly with the ship. PlaceWorker refuses this
            // at build time; this is the backstop for any path that reaches here.
            if (ABGravship.OnSubstructure(this))
            {
                ABLog.Dev("Refused stair link on gravship substructure at " + Position + ".");
                return;
            }
            try
            {
                int target = Map.Level() + delta;
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
                SpawnCounterpartOn(targetMap, delta);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "stair link creation");
            }
        }

        private void SpawnCounterpartOn(Map targetMap, int delta)
        {
            ThingDef cpDef = Ext?.counterpartDef;
            if (cpDef == null)
            {
                Log.Error(ABLog.Tag + " Missing counterpartDef on " + def.defName);
                return;
            }
            IntVec3 pos = Position;
            CellRect footprint = GenAdj.OccupiedRect(pos, Rotation, def.Size);
            PrepareLanding(targetMap, footprint);
            Building blocker = null;
            foreach (IntVec3 c in footprint.ClipInsideMap(targetMap))
            {
                Building b = c.GetEdifice(targetMap);
                if (b != null && b.def.passability == Traversability.Impassable
                    && !(b is Building_ABStairs))
                {
                    blocker = b;
                    break;
                }
            }
            if (blocker != null)
            {
                // Placement-time validation normally prevents this; something was
                // built on the matching spot in the meantime. Stay unlinked instead
                // of deleting the player's building.
                Messages.Message("AB_LinkBlocked".Translate(), new TargetInfo(Position, Map), MessageTypeDefOf.RejectInput);
                return;
            }
            Building_ABStairs cp = (Building_ABStairs)ThingMaker.MakeThing(cpDef, Stuff);
            cp.SetFaction(Faction.OfPlayer);
            cp.SetLink(-delta, this);
            SetLink(delta, cp);
            GenSpawn.Spawn(cp, pos, targetMap, Rotation, WipeMode.Vanish);
            ABLog.Dev("Linked stairs " + ThingID + " (level " + Map.Level() + ") with " + cp.ThingID + " (level " + targetMap.Level() + ").");
        }

        /// <summary>Clears the landing (footprint plus a one-cell rim) on the
        /// destination level: mineable rock is mined out (basement fill or sky
        /// mountains alike), open air becomes rooftop on the sky level, fog
        /// lifted.</summary>
        private static void PrepareLanding(Map targetMap, CellRect footprint)
        {
            int lvl = targetMap.Level();
            foreach (IntVec3 c in footprint.ExpandedBy(1).ClipInsideMap(targetMap))
            {
                if (lvl != 0)
                {
                    List<Thing> things = c.GetThingList(targetMap).ToList();
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        if (t.def.mineable && t.def.destroyable)
                        {
                            t.Destroy(DestroyMode.Vanish);
                        }
                    }
                }
                if (lvl > 0 && targetMap.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                {
                    targetMap.terrainGrid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                }
                targetMap.fogGrid.Unfog(c);
            }
        }

        /// <summary>Breaks every pair link from this side without destroying
        /// anything. Used when a level map is removed so surviving counterparts
        /// read unlinked.</summary>
        internal void SeverLink()
        {
            Sever(ref counterpart);
            Sever(ref counterpartSecond);
        }

        private void Sever(ref Building_ABStairs link)
        {
            Building_ABStairs cp = link;
            link = null;
            if (cp != null && !cp.Destroyed)
            {
                cp.Unlink(this);
            }
        }

        internal void Unlink(Building_ABStairs other)
        {
            if (counterpart == other)
            {
                counterpart = null;
            }
            if (counterpartSecond == other)
            {
                counterpartSecond = null;
            }
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Building_ABStairs cp = counterpart;
            Building_ABStairs cp2 = counterpartSecond;
            counterpart = null;
            counterpartSecond = null;
            base.Destroy(mode);
            if (cp != null && !cp.Destroyed)
            {
                cp.Unlink(this);
                cp.Destroy(DestroyMode.Vanish);
            }
            if (cp2 != null && !cp2.Destroyed)
            {
                cp2.Unlink(this);
                cp2.Destroy(DestroyMode.Vanish);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref counterpart, "AB_counterpart");
            Scribe_References.Look(ref counterpartSecond, "AB_counterpartSecond");
        }

        public override string GetInspectString()
        {
            string baseStr = base.GetInspectString();
            string line = LinkLine();
            return baseStr.NullOrEmpty() ? line : baseStr + "\n" + line;
        }

        protected virtual string LinkLine()
        {
            if (Counterpart != null)
            {
                return (DeltaLevel > 0 ? "AB_LinkedAbove" : "AB_LinkedBelow").Translate();
            }
            return "AB_NotLinkedLine".Translate();
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

        private static UnityEngine.Texture removeIcon;

        private static UnityEngine.Texture RemoveIcon
        {
            get
            {
                if (removeIcon == null)
                {
                    removeIcon = DesignatorUtility.FindAllowedDesignator<Designator_Deconstruct>()?.icon
                        ?? BaseContent.BadTex;
                }
                return removeIcon;
            }
        }

        private static UnityEngine.Texture removeLevelIcon;

        /// <summary>Icon for the remove-level command; falls back to the
        /// deconstruct icon if the custom texture is missing.</summary>
        private static UnityEngine.Texture RemoveLevelIcon
        {
            get
            {
                if (removeLevelIcon == null)
                {
                    removeLevelIcon = ContentFinder<Texture2D>.Get("UI/AB_RemoveLevel", false)
                        ?? RemoveIcon;
                }
                return removeLevelIcon;
            }
        }

        protected virtual List<Gizmo> BuildGizmos()
        {
            List<Gizmo> list = new List<Gizmo>();
            // Links are immortal (no hit points), so removal always routes
            // through deconstruction; surface it right on the building with the
            // vanilla deconstruct icon.
            if (Faction == Faction.OfPlayer)
            {
                bool queued = Map.designationManager.DesignationOn(this, DesignationDefOf.Deconstruct) != null;
                Command_Action remove = new Command_Action
                {
                    defaultLabel = "AB_RemoveLink".Translate(),
                    defaultDesc = "AB_RemoveLinkDesc".Translate(),
                    icon = RemoveIcon,
                    action = delegate
                    {
                        Map.designationManager.AddDesignation(new Designation(this, DesignationDefOf.Deconstruct));
                    }
                };
                if (queued)
                {
                    remove.Disable("AB_RemoveQueued".Translate());
                }
                list.Add(remove);
            }
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
            // Safe level deletion: remove the whole level this stair opens onto
            // (never the ground level 0). Opens a confirmation with an evacuate
            // toggle; see Dialog_ABDeleteLevel / ABLevelDeletion.
            if (Faction == Faction.OfPlayer)
            {
                AddRemoveLevelGizmo(list, Counterpart);
                AddRemoveLevelGizmo(list, SecondCounterpart);
            }
            return list;
        }

        private void AddRemoveLevelGizmo(List<Gizmo> list, Building_ABStairs cp)
        {
            if (cp == null || cp.Map == null || cp.Map.Disposed)
            {
                return;
            }
            int lvl = cp.Map.Level();
            if (lvl == 0)
            {
                // Never offer to delete the ground/home level.
                return;
            }
            bool above = lvl > Map.Level();
            Map target = cp.Map;
            list.Add(new Command_Action
            {
                defaultLabel = (above ? "AB_RemoveLevelAbove" : "AB_RemoveLevelBelow").Translate(),
                defaultDesc = "AB_RemoveLevelDesc".Translate(),
                icon = RemoveLevelIcon,
                action = delegate
                {
                    Find.WindowStack.Add(new Dialog_ABDeleteLevel(target));
                }
            });
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

        /// <summary>Float-menu eligibility: humanlike colonists as before, plus
        /// player-owned tool-user machines (controlled Biotech mechs, Misc.
        /// Robots) so a stranded machine can always be ordered home manually.</summary>
        protected static bool CanBeOrderedToUse(Pawn selPawn)
        {
            if (selPawn.RaceProps.Humanlike)
            {
                return true;
            }
            return selPawn.Faction == Faction.OfPlayer && selPawn.RaceProps.ToolUser;
        }

        protected virtual List<FloatMenuOption> BuildUseOptions(Pawn selPawn)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();
            if (!CanBeOrderedToUse(selPawn))
            {
                return list;
            }
            string label = (DeltaLevel > 0 ? "AB_GoUp" : "AB_GoDown").Translate();
            Building_ABStairs cp = Counterpart;
            if (cp == null)
            {
                list.Add(new FloatMenuOption(label + " (" + "AB_NotLinkedShort".Translate() + ")", null));
            }
            else if (EndForbiddenFor(selPawn) || cp.EndForbiddenFor(selPawn))
            {
                list.Add(new FloatMenuOption(label + " (" + "ForbiddenLower".Translate() + ")", null));
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
                    job.targetC = cp;
                    selPawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
                }));
            }
            return list;
        }
    }
}
