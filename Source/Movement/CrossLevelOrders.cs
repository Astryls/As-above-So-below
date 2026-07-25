using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

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

        /// <summary>Player-commandable pawn for cross-level RMB orders:
        /// colonists as before, plus overseen colony mechs (parity fix
        /// 2026-07-24 - the mechanitor command-range patch already extends the
        /// command cylinder through the column, but this gate still refused
        /// mechs the order menu, so drafted mechs could not be sent across
        /// levels at all). Command range stays enforced downstream: the
        /// vanilla goto/attack providers re-check it during the virtual
        /// rebuild, and the formation drag filters cells per pawn.</summary>
        internal static bool PlayerCommandable(Pawn p)
        {
            return p != null && p.Spawned && p.Map != null
                && (p.IsColonistPlayerControlled || p.IsColonyMechPlayerControlled);
        }

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
            if (!PlayerCommandable(p))
            {
                return false;
            }
            targetMap = ResolveTargetMap(cur, clickPos, out Map below);
            // Any pawn in this COLUMN qualifies - not just the viewed/below
            // pair. A sky pawn selected while viewing the surface (colonist
            // bar keeps selections across level switches) previously fell
            // through to vanilla, which rejects off-map pawns outright and
            // showed NOTHING (user reports 2026-07-24: "cross level right
            // click construction does not function"). Routing handles any
            // hop count via the chain.
            if (p.Map != cur && p.Map != below && !p.Map.SameColumn(cur))
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

        /// <summary>Multi-pawn variant of ShouldRedirect: two or more player-commandable
        /// pawns (colonists and overseen mechs) selected, all within this column's
        /// viewed/below pair, and either the click aims at the other level or at least
        /// one pawn stands on the other level. Selections containing anything else
        /// (animals, guests) fall through to vanilla.</summary>
        internal static bool ShouldRedirectMulti(List<Pawn> selectedPawns, Vector3 clickPos,
            out Map cur, out Map targetMap, out List<Pawn> pawns)
        {
            cur = Find.CurrentMap;
            targetMap = cur;
            pawns = null;
            if (cur == null || selectedPawns == null || selectedPawns.Count < 2)
            {
                return false;
            }
            targetMap = ResolveTargetMap(cur, clickPos, out Map below);
            bool anyCross = false;
            List<Pawn> list = null;
            for (int i = 0; i < selectedPawns.Count; i++)
            {
                Pawn p = selectedPawns[i];
                if (!PlayerCommandable(p))
                {
                    return false;
                }
                if (p.Map != cur && p.Map != below)
                {
                    return false;
                }
                (list ?? (list = new List<Pawn>())).Add(p);
                if (p.Map != targetMap)
                {
                    anyCross = true;
                }
            }
            if (!anyCross && targetMap == cur)
            {
                return false; // pure same-level order on the viewed level: vanilla.
            }
            pawns = list;
            return true;
        }

        /// <summary>Dispatch a multi-pawn cross-level order (drafted pawns only, like
        /// vanilla's multiselect goto). Attack click -> every drafted pawn engages
        /// (cross-gap fire when it can, stairs route otherwise). Move click -> pawns
        /// already on a below target level get the press-preview-release ghost drag;
        /// otherwise immediate vanilla-style gotos with formation spread, cross pawns
        /// routed through the stairs and replaying the goto on arrival. Returns true
        /// when the click was consumed.</summary>
        internal static bool HandleMultiOrder(List<Pawn> pawns, Vector3 clickPos, Map cur, Map targetMap)
        {
            List<Pawn> drafted = null;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (pawns[i].Drafted)
                {
                    (drafted ?? (drafted = new List<Pawn>())).Add(pawns[i]);
                }
            }
            if (drafted == null)
            {
                return false; // undrafted multi-selection: vanilla's (mostly empty) menu.
            }

            // Attack click: everyone engages the clicked target.
            Thing attackTarget = FindAttackTargetAt(targetMap, clickPos, drafted[0]);
            if (attackTarget != null)
            {
                for (int i = 0; i < drafted.Count; i++)
                {
                    Pawn p = drafted[i];
                    if (p.Map == targetMap)
                    {
                        IssueEngageJob(p, attackTarget, AttackMode.Auto);
                    }
                    else if (!CrossLevelCombat.TryStartCrossGapAttack(p, attackTarget))
                    {
                        if (!StairRouter.TryBestToward(p, targetMap, attackTarget.PositionHeld,
                            out Building_ABStairs entry, out Building_ABStairs _))
                        {
                            entry = CrossLevelWork.NearestUsableStairsCached(p, targetMap);
                        }
                        RouteThenEngage(p, targetMap, entry, attackTarget);
                    }
                }
                SoundDefOf.ColonistOrdered.PlayOneShotOnCamera();
                return true;
            }

            // Pure move: the vanilla press-preview-release formation drag for every
            // mix (all on the below level, all on the viewed level, or spanning
            // both). Preview + spacing mirror MultiPawnGotoController exactly;
            // cross pawns ride the stairs to their assigned cell on release.
            Vector3 destPos = targetMap != cur && cur.Levels()?.lowerMap == targetMap
                ? LevelRenderer.ScreenToBelowPos(clickPos)
                : clickPos;
            IntVec3 destCenter = destPos.ToIntVec3();
            if (!destCenter.InBounds(targetMap))
            {
                return false;
            }
            ABBelowGotoDrag.Start(drafted, targetMap, destCenter);
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

            // The click IS the destination: inverse-map it when it aims through
            // open air or glass at the level below (same transform item
            // selection uses). Identity today; funneled for a future transform.
            Vector3 destPos = crossMap && cur.Levels()?.lowerMap == targetMap
                ? LevelRenderer.ScreenToBelowPos(clickPos)
                : clickPos;
            IntVec3 destCell = destPos.ToIntVec3();
            Building_ABStairs entry = null;
            ABVirtualPosition.Token posToken = default;
            bool swappedPos = false;
            if (!pawnOnTarget)
            {
                bool adjacent = Math.Abs(targetMap.Level() - pawn.Map.Level()) == 1;
                IntVec3 virtualCell;
                if (adjacent)
                {
                    entry = CrossLevelWork.NearestUsableStairsCached(pawn, targetMap);
                    Building_ABStairs exit = entry?.CounterpartTowards(targetMap);
                    if (entry == null || exit == null)
                    {
                        context = new FloatMenuContext(single, clickPos, cur);
                        return NoStairsOptions(targetMap, cur);
                    }
                    // Prefer the stairwell landing nearest the destination.
                    if (destCell.InBounds(targetMap))
                    {
                        StairRouter.Reroute(pawn, targetMap, destCell, ref entry, ref exit);
                    }
                    virtualCell = exit.Position;
                }
                else
                {
                    // TWO HOPS (basement pawn ordered onto the sky or the
                    // reverse): generate the options from the clicked cell
                    // itself - close enough for job resolution, and the chain
                    // re-validates everything hop by hop with the pawn really
                    // there. A missing FIRST hop shows the no-stairs row;
                    // later hops resolve live at travel time.
                    if (!TryNextHop(pawn, targetMap, destCell, out _, out _, out _))
                    {
                        context = new FloatMenuContext(single, clickPos, cur);
                        return NoStairsOptions(targetMap, cur);
                    }
                    if (!destCell.InBounds(targetMap))
                    {
                        context = new FloatMenuContext(single, clickPos, cur);
                        return new List<FloatMenuOption>();
                    }
                    virtualCell = destCell;
                }
                if (!ABVirtualPosition.TrySwap(pawn, targetMap, virtualCell, out posToken))
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
                // The below-transformed position, so providers compute the
                // ClickedCell on the swapped map exactly where the player aimed.
                options = FloatMenuMakerMap.GetOptions(single, crossMap ? destPos : clickPos, out context);
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
                WrapOptions(options, pawn, targetMap, destCell, destPos);
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
            // entry is only a hint from callers now: the chain re-resolves
            // stairs live per hop and handles pawns any number of hops away
            // (a basement pawn ordered to attack something on the sky).
            RouteChainThenRun(pawn, targetMap, target.PositionHeld,
                delegate { IssueEngageJob(pawn, target, AttackMode.Auto); });
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

        /// <summary>Right-clicking a stairwell with a colonist selected offers
        /// "Go up/down via X" directly - no view switching needed. Single
        /// selection, pawn on the same map as the stairs, counterpart usable;
        /// forbidden stairs are skipped exactly like a held door.</summary>
        internal static void AddStairsTravelOption(List<Pawn> selectedPawns, Vector3 clickPos,
            List<FloatMenuOption> options)
        {
            if (selectedPawns == null || selectedPawns.Count != 1)
            {
                return;
            }
            Pawn pawn = selectedPawns[0];
            Map cur = Find.CurrentMap;
            if (!PlayerCommandable(pawn) || cur == null || pawn.Map != cur)
            {
                return;
            }
            IntVec3 c = clickPos.ToIntVec3();
            if (!c.InBounds(cur))
            {
                return;
            }
            Building_ABStairs stairs = c.GetEdifice(cur) as Building_ABStairs;
            if (stairs == null)
            {
                List<Thing> things = c.GetThingList(cur);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Building_ABStairs s)
                    {
                        stairs = s;
                        break;
                    }
                }
            }
            if (stairs == null || !stairs.Spawned || stairs.Map != pawn.Map)
            {
                return;
            }
            Building_ABStairs exit = stairs.Counterpart;
            Map dest = exit?.Map;
            if (exit == null || !exit.Spawned || dest == null || dest.Disposed || dest == pawn.Map)
            {
                return;
            }
            if (stairs.EndForbiddenFor(pawn) || exit.EndForbiddenFor(pawn))
            {
                return; // door parity: a forbidden link offers nothing.
            }
            string label = (dest.Level() > pawn.Map.Level()
                ? "AB_GoUpVia".Translate(stairs.LabelShort)
                : "AB_GoDownVia".Translate(stairs.LabelShort)).CapitalizeFirst();
            // Duplicate guard: revalidation regenerates the list each frame.
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i] != null && options[i].Label == label)
                {
                    return;
                }
            }
            Building_ABStairs entry = stairs;
            Pawn p = pawn;
            options.Add(new FloatMenuOption(label, delegate
            {
                Job job = CrossLevelWork.MakeStairsJob(entry, entry.Counterpart);
                if (job != null)
                {
                    job.playerForced = true;
                    p.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
                }
            }, MenuOptionPriority.High)
            {
                iconThing = stairs
            });
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
            IntVec3 destHint, Vector3 destPos)
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
                string label = opt.Label;
                opt.action = delegate
                {
                    RouteChainThenRun(pawn, targetMap, destHint,
                        delegate { ReplayFresh(pawn, targetMap, destPos, label, original); });
                };
            }
        }

        /// <summary>Arrival execution for a wrapped option: regenerate the REAL
        /// float menu with the pawn genuinely standing on the target level and
        /// invoke the fresh option matching the clicked label.
        ///
        /// This replaces replaying the menu-time action (root cause of the
        /// "cross level right click construction does not function" reports):
        /// vanilla's work options capture a Job OBJECT built during our virtual
        /// scan, and by arrival that object is minutes stale - Job instances
        /// are single-use, its chosen material stack may be hauled or reserved
        /// away, and closure state points at menu-time context. Regenerating
        /// runs every provider and every RMB-modifying mod's patches again in a
        /// fully real context (fresh jobs, fresh reservations, fresh counts),
        /// so whatever a mod would offer a pawn standing there is exactly what
        /// executes - 1:1 by construction, the same label-matching idiom
        /// vanilla's own FloatMenuMap revalidation uses. Labels with live
        /// numbers ("deliver 75 steel") match digit-insensitively; if the
        /// option no longer exists at all, the captured menu-time action runs
        /// as a last resort (better a stale attempt than a silent no-op).</summary>
        internal static void ReplayFresh(Pawn pawn, Map targetMap, Vector3 destPos, string label,
            Action original)
        {
            try
            {
                if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Map != targetMap)
                {
                    return;
                }
                List<FloatMenuOption> fresh = null;
                ABCurrentMapSwap.Token mapToken = default;
                // The player may still be VIEWING another level; the generator
                // builds against Find.CurrentMap, so point it at the pawn's
                // real map for the regeneration. Position needs no swap - the
                // pawn is genuinely here now.
                bool swapped = Find.CurrentMap != targetMap && ABCurrentMapSwap.Swap(targetMap, out mapToken);
                Redirecting = true;
                try
                {
                    fresh = FloatMenuMakerMap.GetOptions(new List<Pawn> { pawn }, destPos, out _);
                }
                finally
                {
                    Redirecting = false;
                    if (swapped)
                    {
                        ABCurrentMapSwap.Restore(mapToken);
                    }
                }
                FloatMenuOption match = FindByLabel(fresh, label);
                if (match != null)
                {
                    match.action();
                    return;
                }
                original?.Invoke();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level order replay");
            }
        }

        private static FloatMenuOption FindByLabel(List<FloatMenuOption> options, string label)
        {
            if (options == null || label == null)
            {
                return null;
            }
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption o = options[i];
                if (o != null && !o.Disabled && o.action != null && o.Label == label)
                {
                    return o;
                }
            }
            string target = StripDigits(label);
            for (int i = 0; i < options.Count; i++)
            {
                FloatMenuOption o = options[i];
                if (o != null && !o.Disabled && o.action != null && StripDigits(o.Label) == target)
                {
                    return o;
                }
            }
            return null;
        }

        /// <summary>Digit-insensitive form for labels carrying live counts
        /// ("Prioritize delivering 75 steel" vs 60 after a partial haul).</summary>
        private static string StripDigits(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return s;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsDigit(s[i]))
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

        /// <summary>Next stairs hop from the pawn's CURRENT map strictly toward
        /// targetMap. destHint reroutes the final hop toward the destination
        /// cell so the pawn lands near its order, exactly like the single-hop
        /// path always did.</summary>
        internal static bool TryNextHop(Pawn pawn, Map targetMap, IntVec3 destHint,
            out Building_ABStairs entry, out Building_ABStairs exit, out Map next)
        {
            entry = null;
            exit = null;
            next = null;
            LevelComp comp = pawn.Map.Levels();
            if (comp == null)
            {
                return false;
            }
            int dir = Math.Sign(targetMap.Level() - pawn.Map.Level());
            next = dir > 0 ? comp.upperMap : dir < 0 ? comp.lowerMap : null;
            if (next == null || next.Disposed)
            {
                return false;
            }
            entry = CrossLevelWork.NearestUsableStairsCached(pawn, next);
            exit = entry?.CounterpartTowards(next);
            if (entry == null || exit == null)
            {
                entry = null;
                exit = null;
                return false;
            }
            if (next == targetMap && destHint.IsValid && destHint.InBounds(next))
            {
                StairRouter.Reroute(pawn, next, destHint, ref entry, ref exit);
            }
            return true;
        }

        /// <summary>Routes the pawn hop by hop to the target level and runs the
        /// order on final arrival. Each hop re-resolves its stairs LIVE at
        /// travel time - fresher than a menu-open-time pick (stairs forbidden
        /// or destroyed between click and arrival re-route instead of dead-
        /// ending) - re-arming the pending order per hop; ABPendingOrders'
        /// idle retry self-heals every leg. Recursion is bounded by the
        /// three-level cap.</summary>
        internal static void RouteChainThenRun(Pawn pawn, Map targetMap, IntVec3 destHint, Action original)
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
                if (!TryNextHop(pawn, targetMap, destHint, out Building_ABStairs entry,
                        out Building_ABStairs exit, out Map next))
                {
                    Messages.Message("AB_NoStairsToLevel".Translate(
                        targetMap.Level() > pawn.Map.Level()
                            ? "AB_LevelAbove".Translate()
                            : "AB_LevelBelow".Translate()),
                        pawn, MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                Job job = JobMaker.MakeJob(ABDefOf.AB_UseStairs, entry);
                job.targetC = exit;
                job.playerForced = true;
                ABPendingOrders.Set(pawn, next,
                    delegate { RouteChainThenRun(pawn, targetMap, destHint, original); });
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level order chain");
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
            // See-below right-click parity (user report "can't right click the well"):
            // when looking down through the live below view, snap the click onto a
            // visible below BUILDING/item/pawn under or beside the cursor so its cell
            // drives the whole cross-level menu. The target level was keyed off the
            // raw clicked cell's open-air terrain ALONE, which missed a building whose
            // cell the cursor rounded just shy of - the render draws the building where
            // it is, but the hit-test looked one cell over. Only refines when the cheap
            // test would NOT already resolve below, so an open-air click pays nothing.
            if (selectedPawns != null && selectedPawns.Count == 1 && Find.CurrentMap != null
                && CrossLevelOrders.ResolveTargetMap(Find.CurrentMap, clickPos, out _) == Find.CurrentMap
                && BelowSelection.TryGetLiveBelowView(out Map skyV, out Map lowerV)
                && BelowSelection.TryBelowRightClickCell(skyV, lowerV, clickPos, out IntVec3 belowCell))
            {
                clickPos = belowCell.ToVector3Shifted();
            }
            if (!CrossLevelOrders.ShouldRedirect(selectedPawns, clickPos, out Map cur, out Map targetMap, out Pawn pawn))
            {
                if (Prefs.DevMode && selectedPawns != null && selectedPawns.Count == 1)
                {
                    Pawn sp = selectedPawns[0];
                    Map below = cur?.Levels()?.lowerMap;
                    IntVec3 rc = clickPos.ToIntVec3();
                    string terr = (cur != null && rc.InBounds(cur)) ? cur.terrainGrid.TerrainAt(rc)?.defName : "oob";
                    string belowBld = "n/a";
                    if (below != null && rc.InBounds(below))
                    {
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        List<Thing> bthings = below.thingGrid.ThingsListAtFast(rc);
                        for (int i = 0; i < bthings.Count; i++)
                        {
                            if (bthings[i].def.category == ThingCategory.Building || bthings[i].def.category == ThingCategory.Item)
                            {
                                sb.Append(bthings[i].LabelShort).Append(",");
                            }
                        }
                        belowBld = sb.Length == 0 ? "(none)" : sb.ToString();
                    }
                    Log.Message("[AB RMB diag] NO redirect: sel=" + sp?.LabelShort
                        + " selMapL=" + (sp?.Map != null ? sp.Map.Level().ToString() : "?")
                        + " curL=" + (cur != null ? cur.Level().ToString() : "?")
                        + " targetL=" + (targetMap != null ? targetMap.Level().ToString() : "?")
                        + " cell=" + rc + " skyTerr=" + terr + " belowBld=[" + belowBld + "]"
                        + " hasBelow=" + (below != null));
                }
                // Multi-pawn cross-level orders: group move/attack across the column.
                if (CrossLevelOrders.ShouldRedirectMulti(selectedPawns, clickPos,
                        out Map mCur, out Map mTarget, out List<Pawn> mPawns))
                {
                    try
                    {
                        if (CrossLevelOrders.HandleMultiOrder(mPawns, clickPos, mCur, mTarget))
                        {
                            // A single-pawn context so the Selector takes no further
                            // action (its multiselect-goto fallback re-filters by
                            // CurrentMap and would double-handle same-level pawns).
                            context = new FloatMenuContext(new List<Pawn> { mPawns[0] }, clickPos, mCur);
                            __result = new List<FloatMenuOption>();
                            return false;
                        }
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Movement, e, "multi cross level order");
                    }
                }
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
                    ABBelowGotoDrag.Start(new List<Pawn> { pawn }, targetMap,
                        LevelRenderer.ScreenToBelowPos(clickPos).ToIntVec3());
                    context = new FloatMenuContext(new List<Pawn> { pawn }, clickPos, cur);
                    __result = new List<FloatMenuOption>();
                    return false;
                }
                __result = CrossLevelOrders.BuildOptions(pawn, clickPos, cur, targetMap, out context);
                if (Prefs.DevMode)
                {
                    string labels = __result == null ? "null"
                        : string.Join(" | ", __result.ConvertAll(o => (o?.Label ?? "?") + (o != null && o.Disabled ? "(disabled)" : "")).ToArray());
                    Log.Message("[AB RMB diag] curL=" + cur.Level() + " targetL=" + targetMap.Level()
                        + " pawn=" + pawn.LabelShort + " onTarget=" + (pawn.Map == targetMap)
                        + " opts=[" + labels + "]");
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "cross level float menu");
                return true;
            }
        }

        /// <summary>Same-level clicks fall through to vanilla (prefix returns
        /// true) - this postfix then appends the stairs travel option when the
        /// click landed on a stairwell: the natural "send them through" order
        /// that testers reach for FIRST, before discovering view switching
        /// (live report 2026-07-24). Skips our own nested regeneration calls.</summary>
        private static void Postfix(List<Pawn> selectedPawns, Vector3 clickPos,
            ref List<FloatMenuOption> __result)
        {
            if (CrossLevelOrders.Redirecting || __result == null
                || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                CrossLevelOrders.AddStairsTravelOption(selectedPawns, clickPos, __result);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "stairs travel option");
            }
        }
    }
}
