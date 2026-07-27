using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    // Partial of ABDevTools (movement diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB: targeting-hub self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestTargetingHub()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0;
            int fail = 0;

            void Check(string name, bool cond, string detail = "")
            {
                if (cond)
                {
                    pass++;
                    sb.AppendLine("  PASS  " + name);
                }
                else
                {
                    fail++;
                    sb.AppendLine("  FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "   [" + detail + "]"));
                }
            }

            try
            {
                Map surface = Find.CurrentMap?.GroundMap();
                if (surface == null)
                {
                    Check("ground/surface map exists", false);
                    Report("targeting-hub self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("targeting-hub self-test", sb, pass, fail);
                    return;
                }

                // --- Mortar arc geometry + bombardment order. Mortar placed on the
                // sky; target = a far surface cell under open air (minRange ~30).
                IntVec3 b = FindOpenBaseCell(surface);
                IntVec3 m = b + IntVec3.East;
                MakePlatform(sky, surface, m);
                ThingDef mortarDef = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_Mortar");
                Check("vanilla mortar def found", mortarDef != null);
                Building_Turret mortar = null;
                if (mortarDef != null)
                {
                    mortar = (Building_Turret)GenSpawn.Spawn(
                        ThingMaker.MakeThing(mortarDef, ThingDefOf.Steel), m, sky, WipeMode.Vanish);
                    mortar.SetFaction(Faction.OfPlayer);
                }
                Verb_LaunchProjectile arcVerb = CrossLevelTurret.LauncherVerb(mortar);
                Check("mortar verb is an arc (flyOverhead) verb",
                    arcVerb != null && CrossLevelTurret.IsArc(arcVerb));

                // Single nearest-first enumeration; the first open-air column at or
                // beyond the mortar's min range (~30) wins. 54 stays inside GenRadial's
                // max pattern radius.
                IntVec3 far = IntVec3.Invalid;
                if (arcVerb != null)
                {
                    foreach (IntVec3 c in GenRadial.RadialCellsAround(m, 54f, useCenter: false))
                    {
                        if ((c - m).LengthHorizontalSquared < 32f * 32f)
                        {
                            continue;
                        }
                        if (c.InBounds(sky) && c.InBounds(surface)
                            && sky.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                        {
                            far = c;
                            break;
                        }
                    }
                }
                Check("found a far open-air target column", far.IsValid);
                if (arcVerb != null && far.IsValid)
                {
                    Check("sky mortar can arc-fire at the surface cell",
                        CrossLevelCombat.CanArcFireAt(sky, m, far, surface, arcVerb, out _));
                    IntVec3 near = b;
                    Check("arc fire respects min range (near cell rejected)",
                        !CrossLevelCombat.CanArcFireAt(sky, m, near, surface, arcVerb, out _));
                    Check("bombardment order stored",
                        CrossLevelTurret.TryOrder(mortar, far, surface)
                        && CrossLevelTurret.HasOrder(mortar, out _, out _));
                    // Left in place: man the mortar and it bombards the marked cell.
                }

                // --- DIRECT-fire turret: mini-turret at the hole's edge auto-acquires
                // the hostile below and, driven tick-accurately, puts real projectiles
                // on the surface map.
                IntVec3 t = b + IntVec3.North;
                MakePlatform(sky, surface, t);
                if (surface.roofGrid.Roofed(b))
                {
                    surface.roofGrid.SetRoof(b, null);
                }
                ClearCell(surface, b);
                ClearCell(sky, b);
                sky.terrainGrid.SetTerrain(b, ABDefOf.AB_OpenAir);
                ThingDef miniDef = DefDatabase<ThingDef>.GetNamedSilentFail("Turret_MiniTurret");
                Check("mini-turret def found", miniDef != null);
                Pawn victim = SpawnHostile(surface, b);
                Check("hostile under the hole", victim != null && victim.Spawned);
                if (miniDef != null && victim != null)
                {
                    Building_Turret mini = (Building_Turret)GenSpawn.Spawn(
                        ThingMaker.MakeThing(miniDef, ThingDefOf.Steel), t, sky, WipeMode.Vanish);
                    mini.SetFaction(Faction.OfPlayer);
                    CompPowerTrader miniPower = mini.TryGetComp<CompPowerTrader>();
                    if (miniPower != null)
                    {
                        miniPower.PowerOn = true; // dev arena: no grid up here
                    }
                    Verb_LaunchProjectile miniVerb = CrossLevelTurret.LauncherVerb(mini);
                    Check("mini-turret has a direct projectile verb",
                        miniVerb != null && !CrossLevelTurret.IsArc(miniVerb));
                    if (miniVerb != null)
                    {
                        Check("direct turret has a gap line to the hostile",
                            CrossLevelTurret.TurretCanFire(mini, victim, miniVerb, out _));
                        CrossLevelTurret.AcquireAuto(sky, surface);
                        Check("direct turret auto-acquired the hostile below",
                            CrossLevelTurret.HasOrder(mini, out _, out _));
                        int beforeShots = surface.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Count;
                        // The game is paused during a dev action, so TicksGame stands
                        // still; drive the state machine with simulated ticks instead.
                        int baseNow = Find.TickManager.TicksGame;
                        for (int i = 0; i < 300; i++)
                        {
                            CrossLevelTurret.TickPair(sky, surface, baseNow + i);
                        }
                        int afterShots = surface.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Count;
                        Check("tick driver put turret projectiles on the surface",
                            afterShots > beforeShots, "before=" + beforeShots + " after=" + afterShots);
                    }
                }

                // --- Generic-source plumbing sanity: targetParams thing-filtering.
                TargetingParameters pawnParams = TargetingParameters.ForAttackAny();
                Pawn anyPawn = surface.mapPawns.AllPawnsSpawned.Count > 0 ? surface.mapPawns.AllPawnsSpawned[0] : null;
                if (anyPawn != null)
                {
                    Check("targetParams.CanTarget accepts a cross-map pawn TargetInfo",
                        pawnParams.CanTarget(new TargetInfo(anyPawn)));
                }
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("targeting-hub self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: stair-routing self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestStairRouting()
        {
            StringBuilder sb = new StringBuilder();
            int pass = 0;
            int fail = 0;

            void Check(string name, bool cond, string detail = "")
            {
                if (cond)
                {
                    pass++;
                    sb.AppendLine("  PASS  " + name);
                }
                else
                {
                    fail++;
                    sb.AppendLine("  FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "   [" + detail + "]"));
                }
            }

            try
            {
                Map surface = Find.CurrentMap?.GroundMap();
                if (surface == null)
                {
                    Check("ground/surface map exists", false);
                    Report("stair-routing self-test", sb, pass, fail);
                    return;
                }
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("stair-routing self-test", sb, pass, fail);
                    return;
                }

                // Two stairwells ~40 cells apart on one east-west line.
                ThingDef stairsDef = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown");
                Check("stairs def exists", stairsDef != null);
                if (stairsDef == null)
                {
                    Report("stair-routing self-test", sb, pass, fail);
                    return;
                }
                IntVec3 cellA = FindOpenBaseCell(surface);
                IntVec3 cellB = new IntVec3(Mathf.Clamp(cellA.x + 40, 6, surface.Size.x - 6), 0, cellA.z);
                ClearCell(surface, cellA);
                ClearCell(surface, cellB);
                Building_ABStairs stairsA = GenSpawn.Spawn(ThingMaker.MakeThing(stairsDef, ThingDefOf.WoodLog),
                    cellA, surface, WipeMode.Vanish) as Building_ABStairs;
                stairsA?.SetFaction(Faction.OfPlayer);
                Building_ABStairs stairsB = GenSpawn.Spawn(ThingMaker.MakeThing(stairsDef, ThingDefOf.WoodLog),
                    cellB, surface, WipeMode.Vanish) as Building_ABStairs;
                stairsB?.SetFaction(Faction.OfPlayer);
                Building_ABStairs exitA = stairsA?.CounterpartTowards(basement);
                Building_ABStairs exitB = stairsB?.CounterpartTowards(basement);
                Check("both stairwells linked to the basement", exitA != null && exitB != null,
                    (exitA == null ? "A unlinked " : "") + (exitB == null ? "B unlinked" : ""));
                if (exitA == null || exitB == null)
                {
                    Report("stair-routing self-test", sb, pass, fail);
                    return;
                }

                // Colonist parked nearer A (40% of the way toward B); the errand
                // destination sits at B's basement landing. Whole-trip cost says
                // B; the legacy nearest-to-pawn pick says A.
                IntVec3 mid = new IntVec3((cellA.x * 3 + cellB.x * 2) / 5, 0, cellA.z);
                IntVec3 spawn = CellFinder.StandableCellNear(mid, surface, 6f);
                Pawn walker = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(walker, spawn.IsValid ? spawn : mid, surface, WipeMode.Vanish);
                Check("walker spawned nearer stairwell A", walker.Spawned
                    && (walker.Position - cellA).LengthHorizontalSquared
                        < (walker.Position - cellB).LengthHorizontalSquared);
                IntVec3 dest = CellFinder.StandableCellNear(exitB.Position, basement, 4f);
                Check("destination cell near B's landing", dest.IsValid);

                Building_ABStairs legacy = CrossLevelWork.NearestUsableStairs(walker, basement, checkReachability: true);
                Check("legacy pick is the pawn-nearest stairwell (A)", legacy == stairsA,
                    "picked " + (legacy?.ThingID ?? "null"));

                bool best = StairRouter.TryBestToward(walker, basement, dest,
                    out Building_ABStairs bestStairs, out Building_ABStairs bestExit);
                Check("router picks the destination-side stairwell (B)",
                    best && bestStairs == stairsB && bestExit == exitB,
                    "picked " + (bestStairs?.ThingID ?? "null"));

                Building_ABStairs reStairs = legacy;
                Building_ABStairs reExit = legacy?.CounterpartTowards(basement);
                StairRouter.Reroute(walker, basement, dest, ref reStairs, ref reExit);
                Check("reroute upgrades a legacy pick to B", reStairs == stairsB && reExit == exitB);

                bool job = CrossLevelWork.TryStairsJobToward(walker, basement, dest, out Job ride);
                Check("dest-aware stairs job targets B", job && ride?.targetA.Thing == stairsB);
                if (job)
                {
                    walker.jobs?.StartJob(ride, JobCondition.InterruptForced);
                    Check("walker takes the stairs job", walker.CurJobDef == ABDefOf.AB_UseStairs,
                        "job=" + (walker.CurJobDef?.defName ?? "null"));
                }

                Messages.Message("AB dev: stair-routing demo armed - unpause and watch the walker head for the far stairwell.",
                    walker, MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("stair-routing self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: RMB diagnostic", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RmbDiagnostic()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Vector3 clickPos = UI.MouseMapPosition();
                List<Pawn> sel = Find.Selector.SelectedPawns;
                Map cur = Find.CurrentMap;
                sb.Append("[AB RMB diagnostic] cur=L").Append(cur.Level())
                    .Append(" cell=").Append(clickPos.ToIntVec3().ToString())
                    .Append(" selected=").Append(sel.Count);
                for (int i = 0; i < sel.Count; i++)
                {
                    Pawn p = sel[i];
                    sb.Append(" | ").Append(p.LabelShort)
                        .Append(" L").Append(p.Map?.Level() ?? -99)
                        .Append(p.Drafted ? " drafted" : " undrafted")
                        .Append(p.IsColonistPlayerControlled ? "" : " NOT-player-controlled");
                }
                sb.Append(" | guardMovement=").Append(ABGuard.On(ABGuard.Movement))
                    .Append(" setting=").Append(ABMod.Settings?.crossLevelOrders ?? false);
                Map target = CrossLevelOrders.ResolveTargetMap(cur, clickPos, out Map below);
                sb.Append(" | targetMap=L").Append(target.Level())
                    .Append(" below=").Append(below != null ? ("L" + below.Level()) : "none");
                bool single = CrossLevelOrders.ShouldRedirect(sel, clickPos, out Map c1, out Map t1, out Pawn one);
                sb.Append(" | ShouldRedirect=").Append(single);
                if (single)
                {
                    List<FloatMenuOption> opts = CrossLevelOrders.BuildOptions(one, clickPos, c1, t1, out _);
                    sb.Append(" options=").Append(opts.Count);
                    int shown = 0;
                    for (int i = 0; i < opts.Count && shown < 10; i++, shown++)
                    {
                        sb.Append(" [").Append(opts[i].Disabled ? "X " : "").Append(opts[i].Label).Append("]");
                    }
                }
                else
                {
                    sb.Append(" (falls through to vanilla: same-level click or ineligible selection)");
                }
            }
            catch (Exception e)
            {
                sb.Append(" EXCEPTION: ").Append(e);
            }
            Log.Warning(sb.ToString());
            Messages.Message("AB RMB diagnostic written to log.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

    }
}
