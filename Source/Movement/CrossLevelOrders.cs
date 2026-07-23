using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Right-click ordering for a pawn selected in place across levels. From the sky
    /// view you can select a pawn on the surface below (BelowSelection) or a pawn on
    /// the sky itself (vanilla). This routes their orders to the level you are pointing
    /// at, using one simple rule:
    ///
    ///   Right-click a SOLID sky cell  -> the order acts on the sky level.
    ///   Right-click through OPEN AIR   -> the order acts on the surface below (the
    ///                                     level you can see through the hole).
    ///
    /// So a surface pawn right-clicked on the rooftop walks to the stairs and rides UP;
    /// a sky pawn right-clicked through a hole walks to the stairs and rides DOWN; and a
    /// surface pawn right-clicked through a hole just acts on the surface where it already
    /// stands. "Interacting with things on the lower level" and "sending pawns back down"
    /// both fall out of the same rule.
    ///
    /// FloatMenuMakerMap.GetOptions builds everything against Find.CurrentMap and refuses
    /// a pawn whose Map != CurrentMap (ShouldGenerateFloatMenuForPawn). So we (a) virtually
    /// place the pawn at the correct stairwell exit on the target level when it must travel
    /// there (ABVirtualPosition), and (b) temporarily point Find.CurrentMap at the target
    /// level when the target isn't the viewed level (ABCurrentMapSwap - the currentMapIndex
    /// field, no camera move, restored immediately). The vanilla generator then produces
    /// correct options for the target level; we wrap each action to route the pawn through
    /// the stairs first (ABPendingOrders replays the order on arrival) unless the pawn is
    /// already on the target level.
    ///
    /// Interception is uniform: EVERY GetOptions call for a redirectable selection is
    /// handled the same way, keyed only off the click position's level, so FloatMenuMap's
    /// per-frame revalidation re-calls regenerate matching options (label match) and menus
    /// never grey out. Single selected pawn only. Fails open to vanilla.
    /// </summary>
    internal static class CrossLevelOrders
    {
        /// <summary>Reentrancy guard: our inner GetOptions re-call (pawn/current-map
        /// swapped) must run the vanilla generator, not recurse.</summary>
        internal static bool Redirecting;

        /// <summary>The level a click is aimed at: the level below when the cursor is
        /// over open air on the viewed level, otherwise the viewed level itself.</summary>
        internal static Map ResolveTargetMap(Map cur, Vector3 clickPos, out Map below)
        {
            below = cur.Levels()?.lowerMap;
            if (below == null || below.Disposed)
            {
                return cur;
            }
            IntVec3 c = clickPos.ToIntVec3();
            if (!c.InBounds(cur))
            {
                return cur;
            }
            return cur.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir ? below : cur;
        }

        /// <summary>True when a single player pawn is selected and the (pawn level,
        /// click-target level) pairing needs cross-level handling. Pure same-level
        /// clicks (pawn on the viewed level, target the viewed level) fall through to
        /// vanilla.</summary>
        internal static bool ShouldRedirect(List<Pawn> selectedPawns, Vector3 clickPos,
            out Map cur, out Map targetMap, out Pawn pawn)
        {
            cur = Find.CurrentMap;
            targetMap = cur;
            pawn = null;
            if (cur == null || selectedPawns == null || selectedPawns.Count != 1)
            {
                return false;
            }
            Pawn p = selectedPawns[0];
            if (p == null || !p.Spawned || !p.IsColonistPlayerControlled || p.Map == null)
            {
                return false;
            }
            targetMap = ResolveTargetMap(cur, clickPos, out Map below);
            // Pawn and target must both be within this column's viewed/below pair.
            if (p.Map != cur && p.Map != below)
            {
                return false;
            }
            if (p.Map == cur && targetMap == cur)
            {
                // Pure same-level order on the viewed level: vanilla handles it.
                return false;
            }
            pawn = p;
            return true;
        }

        /// <summary>Builds the target-level options for the selected pawn, routing it
        /// through the stairs when it isn't already on the target level.</summary>
        internal static List<FloatMenuOption> BuildOptions(Pawn pawn, Vector3 clickPos,
            Map cur, Map targetMap, out FloatMenuContext context)
        {
            List<Pawn> single = new List<Pawn> { pawn };
            bool crossMap = targetMap != cur;
            bool pawnOnTarget = pawn.Map == targetMap;

            // Cross-gap combat takes priority over routing and does NOT need stairs:
            // if a drafted pawn's click lands on an attackable thing on the paired
            // level, offer a single attack order that fires across the gap when it
            // can (Model B) and only falls back to routing down the stairs (Model A).
            // This runs BEFORE the stairs requirement so you can shoot an enemy on the
            // level below with no stairs built at all.
            if (!pawnOnTarget && pawn.Drafted)
            {
                Thing gapTarget = FindAttackTargetAt(targetMap, clickPos, pawn);
                if (gapTarget != null)
                {
                    context = new FloatMenuContext(single, clickPos, cur);
                    return MakeAttackOptions(pawn, targetMap, gapTarget);
                }
            }

            Building_ABStairs entry = null;
            ABVirtualPosition.Token posToken = default;
            bool swappedPos = false;
            if (!pawnOnTarget)
            {
                entry = CrossLevelWork.NearestUsableStairsCached(pawn, targetMap);
                Building_ABStairs exit = entry?.CounterpartTowards(targetMap);
                if (entry == null || exit == null)
                {
                    context = new FloatMenuContext(single, clickPos, cur);
                    return NoStairsOptions(targetMap, cur);
                }
                // The click IS the destination: prefer the stairwell landing
                // nearest it. Inverse-map the click when it aims through open
                // air at the level below (same transform item selection uses).
                Vector3 destPos = crossMap && cur.Levels()?.lowerMap == targetMap
                    ? LevelRenderer.ScreenToBelowPos(clickPos)
                    : clickPos;
                IntVec3 destCell = destPos.ToIntVec3();
                if (destCell.InBounds(targetMap))
                {
                    StairRouter.Reroute(pawn, targetMap, destCell, ref entry, ref exit);
                }
                if (!ABVirtualPosition.TrySwap(pawn, targetMap, exit.Position, out posToken))
                {
                    context = new FloatMenuContext(single, clickPos, cur);
                    return new List<FloatMenuOption>();
                }
                swappedPos = true;
            }

            ABCurrentMapSwap.Token mapToken = default;
            bool swappedMap = false;
            if (crossMap)
            {
                swappedMap = ABCurrentMapSwap.Swap(targetMap, out mapToken);
            }

            List<FloatMenuOption> options;
            Redirecting = true;
            try
            {
                options = FloatMenuMakerMap.GetOptions(single, clickPos, out context);
            }
            finally
            {
                Redirecting = false;
                if (swappedMap)
                {
                    ABCurrentMapSwap.Restore(mapToken);
                }
                if (swappedPos)
                {
                    ABVirtualPosition.Restore(pawn, posToken);
                }
            }

            if (!pawnOnTarget)
            {
                // Cross-level attack: if the click hits an attackable target, route the
                // pawn over and engage properly (advance into range / charge to melee).
                // Vanilla's own attack options are evaluated at the far stairwell exit,
                // where a ranged pawn is out of LOS/range (AttackStatic never advances)
                // and even an enabled melee would fire from the wrong spot - so we own
                // the combat case with a purpose-built order.
                Thing attackTarget = pawn.Drafted ? FindAttackTarget(context, pawn) : null;
                if (attackTarget != null)
                {
                    return MakeAttackOptions(pawn, targetMap, attackTarget);
                }
                WrapOptions(options, pawn, targetMap, entry);
                // Forced construction with materials in hand: when the clicked
                // blueprint/frame lacks a material the pawn's own level can
                // supply, add the carry-and-build order (the wrapped vanilla
                // option is dead on arrival - no materials on the target level).
                ABConstructSupply.AddOption(options, pawn, targetMap, cur, clickPos);
            }
            return options;
        }

        /// <summary>The attackable thing under the click on the target level, if any:
        /// a hostile, or a wild/manhunter animal (huntable). Mirrors
        /// FloatMenuOptionProvider_DraftedAttack.CanTarget.</summary>
        private static Thing FindAttackTarget(FloatMenuContext context, Pawn pawn)
        {
            if (context?.ClickedPawns != null)
            {
                for (int i = 0; i < context.ClickedPawns.Count; i++)
                {
                    if (IsAttackTarget(context.ClickedPawns[i], pawn))
                    {
                        return context.ClickedPawns[i];
                    }
                }
            }
            if (context?.ClickedThings != null)
            {
                for (int i = 0; i < context.ClickedThings.Count; i++)
                {
                    if (IsAttackTarget(context.ClickedThings[i], pawn))
                    {
                        return context.ClickedThings[i];
                    }
                }
            }
            return null;
        }

        internal static bool IsAttackTarget(Thing t, Pawn pawn)
        {
            if (t == null || t == pawn || !t.Spawned || !t.def.destroyable)
            {
                return false;
            }
            if (t.def.noRightClickDraftAttack && t.HostileTo(Faction.OfPlayer))
            {
                return false;
            }
            if (t.HostileTo(Faction.OfPlayer))
            {
                return true;
            }
            return t is Pawn p && p.NonHumanlikeOrWildMan();
        }

        private static List<FloatMenuOption> MakeAttackOptions(Pawn pawn, Map targetMap, Thing target)
        {
            FloatMenuOption opt = new FloatMenuOption("Attack".Translate(target.Label, target),
                delegate
                {
                    // Model B first: stand and fire across the open-air gap when there
                    // is a clear line and the target is in range. Model A (route down
                    // the stairs and engage same-map) is the guaranteed fallback - the
                    // stairs are resolved lazily here so the attack option can exist
                    // even when no stairs are built (fire-only case).
                    if (!CrossLevelCombat.TryStartCrossGapAttack(pawn, target))
                    {
                        // Chase through the stairwell nearest the target, not
                        // nearest the pawn.
                        if (!StairRouter.TryBestToward(pawn, targetMap, target.PositionHeld,
                            out Building_ABStairs entry, out Building_ABStairs _))
                        {
                            entry = CrossLevelWork.NearestUsableStairsCached(pawn, targetMap);
                        }
                        RouteThenEngage(pawn, targetMap, entry, target);
                    }
                },
                MenuOptionPriority.AttackEnemy)
            {
                autoTakeable = target.HostileTo(Faction.OfPlayer),
                autoTakeablePriority = 40f
            };
            return new List<FloatMenuOption> { opt };
        }

        /// <summary>The attackable thing at (or beside) a click position on the target
        /// level - a direct cell probe used by the early cross-gap-attack path, which
        /// runs before the map-swap machinery that populates FloatMenuContext.</summary>
        internal static Thing FindAttackTargetAt(Map map, Vector3 clickPos, Pawn pawn)
        {
            if (map == null)
            {
                return null;
            }
            // Height-language rework: the below view renders plumb, so this
            // mapping is identity today. Funneled through ScreenToBelowPos
            // anyway so a future transform has one inversion point, exactly
            // like item selection.
            Map cur = Find.CurrentMap;
            if (cur != null && cur != map && cur.Levels()?.lowerMap == map)
            {
                clickPos = LevelRenderer.ScreenToBelowPos(clickPos);
            }
            IntVec3 c = clickPos.ToIntVec3();
            if (!c.InBounds(map))
            {
                return null;
            }
            Thing hit = AttackTargetInCell(map, c, pawn);
            if (hit != null)
            {
                return hit;
            }
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map))
                {
                    Thing t = AttackTargetInCell(map, n, pawn);
                    if (t != null)
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        private static Thing AttackTargetInCell(Map map, IntVec3 c, Pawn pawn)
        {
            List<Thing> things = c.GetThingList(map);
            // Prefer a pawn (the usual target) over other attackable things.
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn && IsAttackTarget(things[i], pawn))
                {
                    return things[i];
                }
            }
            for (int i = 0; i < things.Count; i++)
            {
                if (IsAttackTarget(things[i], pawn))
                {
                    return things[i];
                }
            }
            return null;
        }

        /// <summary>Route the pawn to the target level, then engage the target on arrival
        /// (via ABPendingOrders replay).</summary>
        private static void RouteThenEngage(Pawn pawn, Map targetMap, Building_ABStairs entry, Thing target)
        {
            RouteThenRun(pawn, targetMap, entry, delegate { IssueEngageJob(pawn, target, AttackMode.Auto); });
        }

        /// <summary>Same-level combat once the pawn has arrived on the target level: a
        /// ranged pawn moves to a firing position then attacks; anyone else charges to
        /// melee. Never fires across levels - the pawn is on the target's map here.
        /// mode forces melee (H) / ranged (B) intent from the drafted attack targeter;
        /// Auto picks from the pawn's best attack verb.</summary>
        internal static void IssueEngageJob(Pawn pawn, Thing target, AttackMode mode)
        {
            try
            {
                if (pawn == null || target == null || !pawn.Spawned || pawn.Dead
                    || target.Destroyed || !target.Spawned || target.Map != pawn.Map)
                {
                    return;
                }
                Verb verb = null;
                if (mode != AttackMode.ForceMelee)
                {
                    Verb v = mode == AttackMode.ForceRanged
                        ? pawn.equipment?.PrimaryEq?.PrimaryVerb
                        : pawn.TryGetAttackVerb(target, !pawn.IsColonist);
                    if (v != null && !v.verbProps.IsMeleeAttack)
                    {
                        verb = v;
                    }
                }
                if (verb != null)
                {
                    CastPositionRequest req = new CastPositionRequest
                    {
                        caster = pawn,
                        target = target,
                        verb = verb,
                        wantCoverFromTarget = true,
                        maxRangeFromTarget = Mathf.Max(verb.EffectiveRange * 0.95f, 1.42f)
                    };
                    if (CastPositionFinder.TryFindCastPosition(req, out IntVec3 dest)
                        && dest.IsValid && dest != pawn.Position)
                    {
                        Job go = JobMaker.MakeJob(JobDefOf.Goto, dest);
                        go.playerForced = true;
                        pawn.jobs.TryTakeOrderedJob(go, JobTag.Misc);
                        Job shoot = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                        shoot.playerForced = true;
                        pawn.jobs.jobQueue.EnqueueLast(shoot, JobTag.Misc);
                        return;
                    }
                    Job shootHere = JobMaker.MakeJob(JobDefOf.AttackStatic, target);
                    shootHere.playerForced = true;
                    pawn.jobs.TryTakeOrderedJob(shootHere, JobTag.Misc);
                    return;
                }
                Job melee = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
                if (target is Pawn tp)
                {
                    melee.killIncappedTarget = tp.Downed;
                }
                melee.playerForced = true;
                pawn.jobs.TryTakeOrderedJob(melee, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level engage");
            }
        }

        private static List<FloatMenuOption> NoStairsOptions(Map targetMap, Map cur)
        {
            string dir = (targetMap.Level() > cur.Level()) ? "AB_LevelAbove".Translate() : "AB_LevelBelow".Translate();
            return new List<FloatMenuOption>
            {
                new FloatMenuOption("AB_NoStairsToLevel".Translate(dir), null)
            };
        }

        private static void WrapOptions(List<FloatMenuOption> options, Pawn pawn, Map targetMap,
            Building_ABStairs entry)
        {
            if (options == null)
            {
                return;
            }
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption opt = options[i];
                // Disabled == action null (a shown "can't do X" reason): leave it so the
                // player still sees why.
                if (opt == null || opt.Disabled)
                {
                    continue;
                }
                Action original = opt.action;
                opt.action = delegate { RouteThenRun(pawn, targetMap, entry, original); };
            }
        }

        /// <summary>Sends the pawn through the stairs toward the target level and replays
        /// the original order on arrival. Direction-agnostic (CounterpartTowards picks the
        /// correct end). Mirrors ABReverseCompat.RouteThenRun.</summary>
        internal static void RouteThenRun(Pawn pawn, Map targetMap, Building_ABStairs entry, Action original)
        {
            try
            {
                if (pawn == null || original == null || !pawn.Spawned || pawn.Dead)
                {
                    return;
                }
                if (pawn.Map == targetMap)
                {
                    original();
                    return;
                }
                if (entry == null || !entry.Spawned)
                {
                    return;
                }
                Building_ABStairs exit = entry.CounterpartTowards(targetMap);
                if (exit == null)
                {
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, entry);
                job.targetC = exit;
                job.playerForced = true;
                ABPendingOrders.Set(pawn, targetMap, original);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level order routing");
            }
        }
    }

    /// <summary>Which attack a cross-level engage should use: Auto = pick from the
    /// pawn's best verb; ForceMelee = the H melee targeter; ForceRanged = the B ranged
    /// targeter.</summary>
    internal enum AttackMode
    {
        Auto,
        ForceMelee,
        ForceRanged
    }

    /// <summary>
    /// Temporarily points Find.CurrentMap at another map (the currentMapIndex backing
    /// field, set directly so there is no camera jump or sound) so a map-scoped vanilla
    /// query can run as if that map were current. Synchronous, main-thread only; callers
    /// MUST Restore in a finally.
    /// </summary>
    internal static class ABCurrentMapSwap
    {
        public struct Token
        {
            internal sbyte index;
        }

        public static bool Swap(Map target, out Token token)
        {
            token = default;
            Game game = Current.Game;
            if (game == null || target == null)
            {
                return false;
            }
            int idx = Find.Maps.IndexOf(target);
            if (idx < 0)
            {
                return false;
            }
            token.index = game.currentMapIndex;
            game.currentMapIndex = (sbyte)idx;
            return true;
        }

        public static void Restore(Token token)
        {
            Game game = Current.Game;
            if (game != null)
            {
                game.currentMapIndex = token.index;
            }
        }
    }

    /// <summary>
    /// Redirects the right-click float menu for a cross-level selection so its orders
    /// target the level being pointed at (up the stairs, down the stairs, or on the
    /// surface directly). Same-level clicks on the viewed level fall through to vanilla.
    /// </summary>
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    internal static class Patch_FloatMenuMakerMap_GetOptions_CrossLevel
    {
        private static bool Prefix(List<Pawn> selectedPawns, Vector3 clickPos,
            ref FloatMenuContext context, ref List<FloatMenuOption> __result)
        {
            if (CrossLevelOrders.Redirecting || !ABGuard.On(ABGuard.Movement))
            {
                return true;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return true;
            }
            if (!CrossLevelOrders.ShouldRedirect(selectedPawns, clickPos, out Map cur, out Map targetMap, out Pawn pawn))
            {
                return true;
            }
            try
            {
                // Pure MOVE click for a drafted below pawn on its own level: instead
                // of the instant order, run the vanilla-style press-preview-release
                // interaction (ghost while the right button is held, goto on release).
                // Attack clicks and cross-map orders keep the immediate path.
                if (pawn.Drafted && pawn.Map == targetMap && targetMap != cur
                    && CrossLevelOrders.FindAttackTargetAt(targetMap, clickPos, pawn) == null)
                {
                    ABBelowGotoDrag.Start(pawn);
                    context = new FloatMenuContext(new List<Pawn> { pawn }, clickPos, cur);
                    __result = new List<FloatMenuOption>();
                    return false;
                }
                __result = CrossLevelOrders.BuildOptions(pawn, clickPos, cur, targetMap, out context);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level float menu");
                return true;
            }
        }
    }
}
