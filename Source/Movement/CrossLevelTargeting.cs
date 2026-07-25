using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Makes the drafted-pawn attack targeters cross-level aware. Vanilla binds H to the
    /// melee attack targeter and B to the ranged attack targeter (Command_Target /
    /// weapon-verb Command_VerbTarget); both resolve their target against Find.CurrentMap
    /// only, so you cannot click an enemy on the level below (seen through open air) or on
    /// the level above.
    ///
    /// We prefix Targeter.ProcessInputEvents: on a left click while an ATTACK targeter is
    /// active, we resolve the target including below-visible things (shifted hit-test) and
    /// current-map things, and if the target sits on a different level than the commanded
    /// pawn - or is a below thing vanilla can't even see from up here - we take over: route
    /// each pawn to the target's level via the stairs and engage same-level on arrival
    /// (ranged pawns move into firing range and shoot, melee pawns charge). Intent is kept:
    /// a weapon-verb source (B) forces ranged, the melee gizmo action (H) forces melee.
    /// Pure same-level attacks on the viewed level fall through to vanilla untouched.
    /// </summary>
    internal static class CrossLevelTargeting
    {
        private static readonly AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>> ActionRef =
            AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>>("action");

        private static readonly AccessTools.FieldRef<Targeter, TargetingParameters> TargetParamsRef =
            AccessTools.FieldRefAccess<Targeter, TargetingParameters>("targetParams");

        private static readonly AccessTools.FieldRef<Targeter, Func<LocalTargetInfo, bool>> TargetValidatorRef =
            AccessTools.FieldRefAccess<Targeter, Func<LocalTargetInfo, bool>>("targetValidator");

        private static readonly List<Pawn> tmpPawns = new List<Pawn>();

        internal static bool TryHandle(Targeter targeter)
        {
            ITargetingSource source = targeter.targetingSource;
            Action<LocalTargetInfo> action = ActionRef(targeter);

            // Dispatcher: every targeting source gets a cross-level story.
            //  - turret arc verbs (mortars/artillery/ICBM-class) -> direct bombardment order
            //  - pawn WEAPON verbs (the equipped gun, B) -> the verified fire/route path
            //  - any other pawn-cast source (psycasts, VPE, modded targeters) -> generic
            //    route-then-OrderForceTarget (the source runs its own vanilla cast job
            //    once the pawn stands on the target's level)
            //  - the melee/squad action path -> the verified melee route
            AttackMode mode;
            tmpPawns.Clear();
            if (source != null)
            {
                if (source.Caster is Building_Turret turret)
                {
                    return TryHandleTurret(turret);
                }
                Pawn caster = source.CasterPawn;
                if (caster == null)
                {
                    return false;
                }
                // The equipped weapon's own direct-fire verb is the classic B attack:
                // keep it on the purpose-built fire/route path. Anything else (ability
                // verbs - including ability-shoot hybrids - and modded sources) goes
                // through the generic force-target route. Shared classifier so the
                // hover cursor and the click can never disagree.
                if (!CrossLevelCombat.IsEquippedGunVerb(source, caster, out _))
                {
                    return TryHandleGenericSource(source, caster);
                }
                mode = AttackMode.ForceRanged;
                tmpPawns.Add(caster);
            }
            else if (action != null)
            {
                CollectSelectedDraftedPawns(tmpPawns);
                if (tmpPawns.Count == 0)
                {
                    // No drafted attackers => this is a config/tool targeter (connect
                    // fixture to bed, pick a cell, modded selectors), NOT the melee squad
                    // path. Widen it to the level below with the targeter's own params
                    // instead of letting it die on a vanilla same-map resolve.
                    return TryHandleActionTargeter(targeter);
                }
                mode = AttackMode.ForceMelee;
            }
            else
            {
                return false;
            }
            if (tmpPawns.Count == 0)
            {
                return false;
            }

            Map cur = Find.CurrentMap;
            if (cur == null)
            {
                return false;
            }
            // Vanilla targeter semantics: the source's own targetParams decide what is
            // clickable - which INCLUDES friendly pawns (deliberate friendly fire is a
            // vanilla capability; filtering to hostiles was the round-4 "cannot select
            // colonists" parity bug).
            TargetingParameters tp = (source as Verb)?.targetParams ?? CrossLevelCombatUI.AttackAnyParams;
            Thing target = ResolveAttackTarget(cur, tmpPawns[0], tp, source);
            if (target == null)
            {
                return false;
            }
            Map targetMap = target.MapHeld;
            if (targetMap == null)
            {
                return false;
            }

            // Only take over when a level is actually crossed: the target lives off the
            // viewed level, or a commanded pawn is on a different level than the target.
            bool anyCross = targetMap != cur;
            for (int i = 0; i < tmpPawns.Count && !anyCross; i++)
            {
                if (tmpPawns[i].Map != targetMap)
                {
                    anyCross = true;
                }
            }
            if (!anyCross)
            {
                return false;
            }

            for (int i = 0; i < tmpPawns.Count; i++)
            {
                EngageCrossLevel(tmpPawns[i], target, mode);
            }
            targeter.StopTargeting();
            return true;
        }

        /// <summary>Turret with a projectile verb: clicking across the gap orders a
        /// cross-level attack. Arc verbs (mortars) accept cells or things; direct verbs
        /// (autocannons and friends) need a thing with a real gap line. Both directions:
        /// a sky turret targeting the surface through the hole, and a below-selected
        /// surface turret targeting the sky. Same-map clicks fall through untouched.</summary>
        private static bool TryHandleTurret(Building_Turret turret)
        {
            Verb_LaunchProjectile verb = CrossLevelTurret.LauncherVerb(turret);
            if (verb == null)
            {
                return false;
            }
            if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower) || sky != Find.CurrentMap)
            {
                return false;
            }
            Vector3 mouse = UI.MouseMapPosition();
            IntVec3 skyCell = mouse.ToIntVec3();
            if (!skyCell.InBounds(sky))
            {
                return false;
            }
            bool overOpenAir = sky.terrainGrid.TerrainAt(skyCell) == ABDefOf.AB_OpenAir;
            if (turret.Map == sky && overOpenAir)
            {
                // Sky turret firing at the surface through the hole. Prefer a thing
                // under the shifted cursor; arc verbs fall back to the bare cell.
                List<Thing> below = BelowSelection.SelectablesUnderMouse(sky, lower, mouse);
                LocalTargetInfo target;
                if (below.Count > 0)
                {
                    target = below[0];
                }
                else if (CrossLevelTurret.IsArc(verb))
                {
                    IntVec3 cell = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
                    if (!cell.InBounds(lower))
                    {
                        return false;
                    }
                    target = cell;
                }
                else
                {
                    return false;
                }
                return CrossLevelTurret.TryOrder(turret, target, lower);
            }
            if (turret.Map == lower && !overOpenAir)
            {
                // Below-selected surface turret firing at the sky plane. Prefer a
                // thing under the mouse on the sky map; arc verbs accept the cell.
                foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(TargetingParameters.ForAttackAny(), thingsOnly: true))
                {
                    if (lt.Thing != null)
                    {
                        return CrossLevelTurret.TryOrder(turret, lt.Thing, sky);
                    }
                }
                if (CrossLevelTurret.IsArc(verb))
                {
                    return CrossLevelTurret.TryOrder(turret, skyCell, sky);
                }
                return false;
            }
            return false;
        }

        /// <summary>Any other pawn-cast targeting source (vanilla psycasts, VPE, modded):
        /// a click on a below thing the source's own targetParams accept either casts
        /// directly (caster already on that level) or routes the pawn through the stairs
        /// and calls the source's OrderForceTarget on arrival - the source then runs its
        /// completely vanilla cast job on the right map. No per-effect auditing, no
        /// foreign types.</summary>
        private static bool TryHandleGenericSource(ITargetingSource source, Pawn caster)
        {
            if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower) || sky != Find.CurrentMap)
            {
                return false;
            }
            // A same-map target under the mouse stays vanilla's business.
            foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(source.targetParams, thingsOnly: true))
            {
                if (lt.Thing != null)
                {
                    return false;
                }
            }
            List<Thing> below = BelowSelection.SelectablesUnderMouse(sky, lower, UI.MouseMapPosition());
            Thing pick = null;
            for (int i = 0; i < below.Count; i++)
            {
                if (source.targetParams != null
                    && source.targetParams.CanTarget(new TargetInfo(below[i]), source))
                {
                    pick = below[i];
                    break;
                }
            }
            if (pick == null)
            {
                return TryHandleGenericCell(source, caster, sky, lower);
            }
            Map targetMap = pick.MapHeld;
            if (caster.MapHeld == targetMap)
            {
                // Below-selected caster acting on its own level: hand the target
                // straight to the source (vanilla could not resolve the click at all).
                source.OrderForceTarget(new LocalTargetInfo(pick));
                return true;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(caster, targetMap);
            if (entry == null || entry.CounterpartTowards(targetMap) == null)
            {
                Messages.Message("AB_NoStairsToLevel".Translate("AB_LevelBelow".Translate()),
                    caster, MessageTypeDefOf.RejectInput, historical: false);
                return true;
            }
            Thing target = pick;
            ITargetingSource src = source;
            CrossLevelOrders.RouteThenRun(caster, targetMap, entry,
                delegate { src.OrderForceTarget(new LocalTargetInfo(target)); });
            return true;
        }

        /// <summary>Cell-targeted casts across the gap (parity item 2026-07-24):
        /// Skip destinations, AoE ground casts - any source whose own
        /// targetParams accept a bare location. Same route-then-cast contract
        /// as things: the caster rides the stairs and the source runs its
        /// fully vanilla cast job on arrival. Direct no-travel cell casting
        /// stays excluded (ability verbs are same-map by construction);
        /// mortar-class arc verbs keep their own direct-bombardment path.</summary>
        private static bool TryHandleGenericCell(ITargetingSource source, Pawn caster, Map sky, Map lower)
        {
            Vector3 mouse = UI.MouseMapPosition();
            IntVec3 skyCell = mouse.ToIntVec3();
            if (!skyCell.InBounds(sky)
                || sky.terrainGrid.TerrainAt(skyCell) != ABDefOf.AB_OpenAir)
            {
                return false; // only a click through open air aims below
            }
            IntVec3 cell = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
            if (!cell.InBounds(lower) || cell.Fogged(lower))
            {
                return false;
            }
            if (source.targetParams == null
                || !source.targetParams.CanTarget(new TargetInfo(cell, lower), source))
            {
                return false;
            }
            if (caster.MapHeld == lower)
            {
                source.OrderForceTarget(new LocalTargetInfo(cell));
                return true;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(caster, lower);
            if (entry == null || entry.CounterpartTowards(lower) == null)
            {
                Messages.Message("AB_NoStairsToLevel".Translate("AB_LevelBelow".Translate()),
                    caster, MessageTypeDefOf.RejectInput, historical: false);
                return true;
            }
            IntVec3 cellCopy = cell;
            ITargetingSource src = source;
            CrossLevelOrders.RouteThenRun(caster, lower, entry,
                delegate { src.OrderForceTarget(new LocalTargetInfo(cellCopy)); });
            return true;
        }

        /// <summary>Action-only (non-attack) targeters - a building's own targeting
        /// gizmo (DBH "connect fixture to bed"), a cell picker, any modded
        /// BeginTargeting(params, action) with no drafted attackers behind it. Vanilla
        /// resolves the click against Find.CurrentMap only, so a below thing seen through
        /// open air is unclickable. We resolve the below target with the targeter's OWN
        /// params + validator and fire its action on it directly (no pawn routing - these
        /// are instantaneous config actions). CurrentMap is pointed at the target's level
        /// for the call so any map-scoped logic inside a foreign action resolves right;
        /// the target itself already carries its map. A matching same-map thing under the
        /// cursor stays vanilla's business.</summary>
        private static bool TryHandleActionTargeter(Targeter targeter)
        {
            if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower) || sky != Find.CurrentMap)
            {
                return false;
            }
            TargetingParameters tp = TargetParamsRef(targeter);
            Action<LocalTargetInfo> action = ActionRef(targeter);
            if (tp == null || action == null)
            {
                return false;
            }
            Func<LocalTargetInfo, bool> validator = TargetValidatorRef(targeter);
            // A real same-map thing under the mouse that the targeter accepts: vanilla's.
            foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(tp, thingsOnly: true))
            {
                if (lt.Thing != null)
                {
                    return false;
                }
            }
            LocalTargetInfo target = ResolveBelowActionTarget(sky, lower, tp, validator);
            if (!target.IsValid)
            {
                return false;
            }
            Map targetMap = target.Thing?.MapHeld ?? lower;
            ABCurrentMapSwap.Token token = default;
            bool swapped = targetMap != Find.CurrentMap && ABCurrentMapSwap.Swap(targetMap, out token);
            try
            {
                action(target);
            }
            finally
            {
                if (swapped)
                {
                    ABCurrentMapSwap.Restore(token);
                }
            }
            targeter.StopTargeting();
            return true;
        }

        /// <summary>The below thing (preferred) or below cell under the cursor that the
        /// action targeter's params + validator accept, mirroring the source-cast
        /// resolution: a visible below thing first, then a bare open-air cell for
        /// location-capable targeters.</summary>
        private static LocalTargetInfo ResolveBelowActionTarget(Map sky, Map lower,
            TargetingParameters tp, Func<LocalTargetInfo, bool> validator)
        {
            List<Thing> below = BelowSelection.SelectablesUnderMouse(sky, lower, UI.MouseMapPosition());
            for (int i = 0; i < below.Count; i++)
            {
                LocalTargetInfo t = new LocalTargetInfo(below[i]);
                if (tp.CanTarget(new TargetInfo(below[i]), null) && (validator == null || validator(t)))
                {
                    return t;
                }
            }
            if (!tp.canTargetLocations)
            {
                return LocalTargetInfo.Invalid;
            }
            Vector3 mouse = UI.MouseMapPosition();
            IntVec3 skyCell = mouse.ToIntVec3();
            if (!skyCell.InBounds(sky) || sky.terrainGrid.TerrainAt(skyCell) != ABDefOf.AB_OpenAir)
            {
                return LocalTargetInfo.Invalid;
            }
            IntVec3 cell = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
            if (!cell.InBounds(lower) || cell.Fogged(lower))
            {
                return LocalTargetInfo.Invalid;
            }
            LocalTargetInfo ct = new LocalTargetInfo(cell);
            if (tp.CanTarget(new TargetInfo(cell, lower), null) && (validator == null || validator(ct)))
            {
                return ct;
            }
            return LocalTargetInfo.Invalid;
        }

        private static void CollectSelectedDraftedPawns(List<Pawn> into)
        {
            List<object> sel = Find.Selector.SelectedObjects;
            for (int i = 0; i < sel.Count; i++)
            {
                if (sel[i] is Pawn p && p.IsColonistPlayerControlled && p.Drafted && !p.Downed && p.Spawned)
                {
                    into.Add(p);
                }
            }
        }

        /// <summary>The targetable thing under the cursor per the SOURCE's own accept
        /// rules (friendlies included, exactly like vanilla), preferring a below-visible
        /// one (seen through open air from the sky), then a thing on the viewed map.</summary>
        private static Thing ResolveAttackTarget(Map cur, Pawn sample, TargetingParameters tp, ITargetingSource source)
        {
            if (BelowSelection.TryGetBelowView(out Map sky, out Map lower) && sky == cur)
            {
                List<Thing> below = BelowSelection.SelectablesUnderMouse(sky, lower, UI.MouseMapPosition());
                for (int i = 0; i < below.Count; i++)
                {
                    Thing t = below[i];
                    if (t != sample && tp != null && tp.CanTarget(new TargetInfo(t), source))
                    {
                        return t;
                    }
                }
            }
            foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(tp, thingsOnly: true))
            {
                if (lt.Thing != null && lt.Thing != sample)
                {
                    return lt.Thing;
                }
            }
            return null;
        }

        private static void EngageCrossLevel(Pawn pawn, Thing target, AttackMode mode)
        {
            if (pawn == null || target == null)
            {
                return;
            }
            Map targetMap = target.MapHeld;
            if (targetMap == null)
            {
                return;
            }
            if (pawn.Map == targetMap)
            {
                CrossLevelOrders.IssueEngageJob(pawn, target, mode);
                return;
            }
            // Model B first: fire across the gap (ranged intent only). Falls through to
            // routing when there is no line of fire, no ranged weapon, or melee is forced.
            if (mode != AttackMode.ForceMelee && CrossLevelCombat.TryStartCrossGapAttack(pawn, target))
            {
                return;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(pawn, targetMap);
            if (entry == null || entry.CounterpartTowards(targetMap) == null)
            {
                string dir = targetMap.Level() > pawn.Map.Level() ? "AB_LevelAbove".Translate() : "AB_LevelBelow".Translate();
                Messages.Message("AB_NoStairsToLevel".Translate(dir), pawn, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            CrossLevelOrders.RouteThenRun(pawn, targetMap, entry,
                delegate { CrossLevelOrders.IssueEngageJob(pawn, target, mode); });
        }
    }

    /// <summary>
    /// On a left click during an attack targeter, hand a cross-level target to the routing
    /// engine instead of the vanilla same-map attack. Same-level attacks pass through.
    /// </summary>
    [HarmonyPatch(typeof(Targeter), nameof(Targeter.ProcessInputEvents))]
    internal static class Patch_Targeter_ProcessInputEvents
    {
        private static bool Prefix(Targeter __instance)
        {
            // Cheapest, most selective gate first: this prefix runs for every input
            // event while a targeter is up (mouse moves included).
            if (Event.current.type != EventType.MouseDown || Event.current.button != 0 || !__instance.IsTargeting)
            {
                return true;
            }
            if (!ABGuard.On(ABGuard.Movement))
            {
                return true;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return true;
            }
            try
            {
                if (CrossLevelTargeting.TryHandle(__instance))
                {
                    Event.current.Use();
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level targeting");
                return true;
            }
        }
    }
}
