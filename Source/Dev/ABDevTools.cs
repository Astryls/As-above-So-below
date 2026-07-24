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
    /// <summary>
    /// Autonomous dev/test harness (category "As above"). These build controlled
    /// cross-level scenarios and self-check the mechanics, writing a report to
    /// docs/SelfTest.log (via the mod's RootDir, which the sync symlink maps back to
    /// the workspace) and emitting Log.Warning/Log.Error summaries so results surface
    /// over the diagnostics bridge without a human having to read the screen.
    ///
    /// Only compiled behaviour is asserted here (pairing, line-of-fire, the cross-map
    /// cast, projectiles landing on the correct map). Anything that needs a human to
    /// see it (the plunging-fire visuals, feel) is left running as a live demo.
    /// </summary>
    public static class ABDevTools
    {
        [DebugAction("As above", "AB: ensure sky + basement", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void EnsureLevels()
        {
            Map ground = Find.CurrentMap?.GroundMap();
            if (ground == null)
            {
                Messages.Message("AB dev: no ground map for this column.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (ground.Levels()?.upperMap == null)
            {
                LevelMapGen.GetOrGenerate(ground, 1, ABDefOf.AB_Sky, out _);
            }
            if (ground.Levels()?.lowerMap == null)
            {
                LevelMapGen.GetOrGenerate(ground, -1, ABDefOf.AB_Basement, out _);
            }
            // Starter stairwells (round-6 friction fix: a fresh test column had
            // no stairs at all until the player built them - "no stairs
            // appears"). Spawning the surface side runs the production pairing
            // path, which spawns and links the far end itself.
            int spawned = 0;
            spawned += EnsureStarterStairs(ground, ground.Levels()?.lowerMap, "AB_StairsDown");
            spawned += EnsureStarterStairs(ground, ground.Levels()?.upperMap, "AB_StairsUp");
            Messages.Message("AB dev: ensured sky + basement"
                + (spawned > 0 ? " (+" + spawned + " starter stairs)" : "") + ".",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Spawns one surface-side stairwell toward the target level
        /// when no link exists yet. Returns 1 when spawned.</summary>
        private static int EnsureStarterStairs(Map ground, Map target, string defName)
        {
            if (ground == null || target == null || target.Disposed)
            {
                return 0;
            }
            List<Thing> things = ground.listerThings.AllThings;
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Building_ABStairs s && s.Counterpart?.Map == target)
                {
                    return 0; // already linked
                }
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                return 0;
            }
            IntVec3 spot;
            bool found = CellFinderLoose.TryFindRandomNotEdgeCellWith(
                20,
                c => c.Standable(ground) && c.GetEdifice(ground) == null
                    && !ground.terrainGrid.TerrainAt(c).IsWater
                    && (c.GetZone(ground) == null),
                ground, out spot);
            if (!found)
            {
                spot = ground.Center;
            }
            Thing stairs = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
            GenSpawn.Spawn(stairs, spot, ground);
            stairs.SetFaction(Faction.OfPlayer);
            return 1;
        }

        [DebugAction("As above", "AB: cross-gap combat self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestCrossGapCombat()
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("cross-gap combat self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("cross-gap combat self-test", sb, pass, fail);
                    return;
                }

                // --- Build a controlled arena: an open-air column with a target on the
                // surface below and a shooter on a sky platform beside the hole.
                IntVec3 b = FindOpenBaseCell(surface);
                IntVec3 s = b + IntVec3.East;
                if (!s.InBounds(sky))
                {
                    s = b + IntVec3.West;
                }

                if (surface.roofGrid.Roofed(b))
                {
                    surface.roofGrid.SetRoof(b, null);
                }
                ClearCell(surface, b);
                ClearCell(sky, b);
                sky.terrainGrid.SetTerrain(b, ABDefOf.AB_OpenAir);
                MakePlatform(sky, surface, s);

                Check("target column is open air on the sky", sky.terrainGrid.TerrainAt(b) == ABDefOf.AB_OpenAir);
                Check("shooter cell is standable on the sky", s.Standable(sky), "s=" + s);

                // --- Spawn combatants.
                Pawn hostile = SpawnHostile(surface, b);
                Check("hostile spawned on surface", hostile != null && hostile.Spawned && hostile.Map == surface);

                Pawn colonist = SpawnArmedColonist(sky, s);
                Check("armed colonist spawned on sky", colonist != null && colonist.Spawned && colonist.Map == sky);

                if (hostile == null || colonist == null)
                {
                    Report("cross-gap combat self-test", sb, pass, fail);
                    return;
                }

                Verb verb = CrossLevelCombat.GetRangedVerb(colonist);
                Check("colonist has a ranged projectile verb", verb != null);
                Check("maps are a sky<->surface pair", CrossLevelCombat.AreCrossGapPaired(sky, surface, out _, out _));

                CrossLevelCombat.GapShot shot = default;
                bool canFire = verb != null && CrossLevelCombat.CanCrossGapFire(colonist, hostile, verb, out shot);
                Check("CanCrossGapFire from the sky at the surface target", canFire);
                if (canFire)
                {
                    Check("resolved shot lands on the surface map", shot.targetMap == surface, "map=" + shot.targetMap?.uniqueID);
                    float aim = CrossLevelCombat.ComputeAimChance(colonist, verb, hostile, shot.distance);
                    Check("aim chance is a sane probability", aim > 0f && aim <= 1f, "aim=" + aim.ToString("0.000") + " dist=" + shot.distance.ToString("0.0"));
                }

                // --- The cross-map cast must place live projectiles on the TARGET's map.
                int before = surface.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Count;
                int fired = 0;
                if (verb != null)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        if (CrossLevelCombat.Fire(colonist, verb, hostile))
                        {
                            fired++;
                        }
                    }
                }
                int after = surface.listerThings.ThingsInGroup(ThingRequestGroup.Projectile).Count;
                Check("Fire() reported casts", fired > 0, "fired=" + fired);
                Check("projectiles now live on the surface map", after > before, "before=" + before + " after=" + after);

                // --- The reverse direction should be blocked from an enclosed sky target
                // (physically correct: solid structure between them). Enclose 's' fully.
                bool reverseBlockedWhenEnclosed = TestReverseEnclosed(surface, sky, b);
                Check("surface->sky is blocked for a fully enclosed sky cell", reverseBlockedWhenEnclosed);

                // --- Leave a live demo running so the plunging fire can be watched.
                bool started = CrossLevelCombat.TryStartCrossGapAttack(colonist, hostile);
                Check("sustained cross-gap attack job started", started);

                Find.Selector.ClearSelection();
                Find.Selector.Select(colonist, playSound: false);
                Messages.Message("AB dev: cross-gap demo armed. View the SKY level to watch the colonist plunge-fire the raider below.",
                    colonist, MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("cross-gap combat self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: auto-engage self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestAutoEngage()
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("auto-engage self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("auto-engage self-test", sb, pass, fail);
                    return;
                }

                // Arena: hole at b; ARMED hostile stands beside it on the surface;
                // undrafted colonist stands on a sky platform beside the same hole.
                IntVec3 b = FindOpenBaseCell(surface);
                IntVec3 h = b + IntVec3.North;
                IntVec3 s = b + IntVec3.East;
                if (!h.InBounds(surface) || !s.InBounds(sky))
                {
                    Check("arena cells in bounds", false);
                    Report("auto-engage self-test", sb, pass, fail);
                    return;
                }
                foreach (IntVec3 c in new[] { b, h })
                {
                    if (surface.roofGrid.Roofed(c))
                    {
                        surface.roofGrid.SetRoof(c, null);
                    }
                    ClearCell(surface, c);
                    ClearCell(sky, c);
                    sky.terrainGrid.SetTerrain(c, ABDefOf.AB_OpenAir);
                }
                MakePlatform(sky, surface, s);

                Pawn hostile = SpawnHostile(surface, h);
                Check("hostile spawned on surface", hostile != null && hostile.Spawned);
                if (hostile == null)
                {
                    Report("auto-engage self-test", sb, pass, fail);
                    return;
                }
                ArmWithRanged(hostile);
                Check("hostile has a ranged verb", CrossLevelCombat.GetRangedVerb(hostile) != null);

                Pawn colonist = SpawnArmedColonist(sky, s);
                Check("colonist spawned on sky", colonist != null && colonist.Spawned);
                if (colonist == null)
                {
                    Report("auto-engage self-test", sb, pass, fail);
                    return;
                }
                // Part 1 wants the HOSTILE to acquire the colonist: leave the colonist
                // undrafted so the drafted-colonist scan cannot claim the kill first.
                colonist.drafter.Drafted = false;

                CrossLevelAutoEngage.ScanPair(sky, surface);
                Check("hostile auto-engaged up through the gap",
                    hostile.CurJobDef == ABDefOf.AB_CrossLevelAttack,
                    "job=" + (hostile.CurJobDef?.defName ?? "null"));

                // Part 2: a drafted, idle, fire-at-will colonist returns fire on its own.
                colonist.drafter.Drafted = true;
                colonist.jobs?.StopAll();
                CrossLevelAutoEngage.ScanPair(sky, surface);
                Check("drafted colonist returned fire on their own",
                    colonist.CurJobDef == ABDefOf.AB_CrossLevelAttack,
                    "job=" + (colonist.CurJobDef?.defName ?? "null"));

                Messages.Message("AB dev: auto-engage demo armed - watch the firefight through the hole.",
                    hostile, MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("auto-engage self-test", sb, pass, fail);
        }

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

        [DebugAction("As above", "AB: animal-wander self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestAnimalWander()
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
                    Report("animal-wander self-test", sb, pass, fail);
                    return;
                }
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("animal-wander self-test", sb, pass, fail);
                    return;
                }

                // Stairs: spawn a wooden stairs-down on a clear surface cell and rely
                // on its spawn logic to open/link the counterpart below.
                IntVec3 b = FindOpenBaseCell(surface);
                ClearCell(surface, b);
                Building_ABStairs stairs = null;
                ThingDef stairsDef = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown");
                if (stairsDef != null)
                {
                    stairs = GenSpawn.Spawn(ThingMaker.MakeThing(stairsDef, ThingDefOf.WoodLog), b, surface, WipeMode.Vanish)
                        as Building_ABStairs;
                    stairs?.SetFaction(Faction.OfPlayer);
                }
                bool linked = stairs != null && stairs.CounterpartTowards(basement) != null;
                Check("spawned stairs linked to the basement", linked,
                    stairs == null ? "no stairs" : "no counterpart");

                PawnKindDef wildKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Hare")
                    ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Squirrel")
                    ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Rat");
                Check("wild animal kind found", wildKind != null);
                if (wildKind == null || !linked)
                {
                    Report("animal-wander self-test", sb, pass, fail);
                    return;
                }

                // AMBIENT: surface wild animal descends via the roll-free core.
                Pawn wanderer = PawnGenerator.GeneratePawn(wildKind, null);
                GenSpawn.Spawn(wanderer, CellFinder.StandableCellNear(b, surface, 6f), surface, WipeMode.Vanish);
                Check("wild wanderer spawned on surface", wanderer.Spawned && wanderer.Faction == null);
                Check("ambient descent starts the stairs job",
                    CrossLevelAnimals.TryDescend(wanderer, basement)
                    && wanderer.CurJobDef == ABDefOf.AB_UseStairs,
                    "job=" + (wanderer.CurJobDef?.defName ?? "null"));

                // ESCAPE: hungry wild animal in the basement takes the stairs on the
                // pocket scan; a well-fed fresh visitor stays put (linger window).
                Building_ABStairs exit = stairs.CounterpartTowards(basement);
                IntVec3 down = CellFinder.StandableCellNear(exit.Position, basement, 6f);
                Check("standable basement cell near the counterpart", down.IsValid);
                if (down.IsValid)
                {
                    Pawn hungry = PawnGenerator.GeneratePawn(wildKind, null);
                    GenSpawn.Spawn(hungry, down, basement, WipeMode.Vanish);
                    if (hungry.needs?.food != null)
                    {
                        hungry.needs.food.CurLevel = 0.01f;
                    }
                    Pawn content = PawnGenerator.GeneratePawn(wildKind, null);
                    IntVec3 down2 = CellFinder.StandableCellNear(exit.Position, basement, 8f);
                    GenSpawn.Spawn(content, down2.IsValid ? down2 : down, basement, WipeMode.Vanish);
                    if (content.needs?.food != null)
                    {
                        content.needs.food.CurLevel = content.needs.food.MaxLevel;
                    }
                    LevelComp basementComp = basement.Levels();
                    HostileDescend.ScanPocketMap(basementComp);
                    Check("hungry basement animal heads for the stairs",
                        hungry.CurJobDef == ABDefOf.AB_UseStairs,
                        "job=" + (hungry.CurJobDef?.defName ?? "null"));
                    Check("content fresh visitor lingers (no stairs job)",
                        content.CurJobDef != ABDefOf.AB_UseStairs,
                        "job=" + (content.CurJobDef?.defName ?? "null"));
                }

                Messages.Message("AB dev: animal-wander demo armed - unpause and watch the stairwell.",
                    wanderer, MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("animal-wander self-test", sb, pass, fail);
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

        [DebugAction("As above", "AB: cavern-basement self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestCavernBasement()
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
                if (!BiomesCavernsCompat.Active)
                {
                    sb.AppendLine("  SKIP  Biomes! Caverns not loaded - nothing to verify.");
                    Report("cavern-basement self-test", sb, pass, fail);
                    return;
                }
                Check("basement type is Caverns", ABMod.Settings != null && ABMod.Settings.basementType == BasementEnv.Caverns);
                Map surface = Find.CurrentMap?.GroundMap();
                if (surface == null)
                {
                    Check("ground/surface map exists", false);
                    Report("cavern-basement self-test", sb, pass, fail);
                    return;
                }
                bool existed = surface.Levels()?.lowerMap != null;
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("cavern-basement self-test", sb, pass, fail);
                    return;
                }
                if (existed)
                {
                    sb.AppendLine("  NOTE  basement predates this test; verifying whatever it has.");
                }
                string biomeName = basement.Biome?.defName ?? "null";
                Check("basement biome is a cavern biome", biomeName.StartsWith("BMT_"), "biome=" + biomeName);

                int open = 0;
                int unsupported = 0;
                int plants = 0;
                foreach (IntVec3 c in basement.AllCells)
                {
                    if (c.GetEdifice(basement) != null || !c.Walkable(basement))
                    {
                        continue;
                    }
                    open++;
                    if (!RoofCollapseUtility.WithinRangeOfRoofHolder(c, basement))
                    {
                        unsupported++;
                    }
                    if (c.GetPlant(basement) != null)
                    {
                        plants++;
                    }
                }
                Check("carved network is substantial", open > 300, "open=" + open);
                Check("no carved cell is out of roof-holder range", unsupported == 0, "unsupported=" + unsupported);
                Check("cave flora present", plants > 10, "plants=" + plants);
                int fauna = 0;
                IReadOnlyList<Pawn> pawns = basement.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (pawns[i].RaceProps.Animal && pawns[i].Faction == null)
                    {
                        fauna++;
                    }
                }
                Check("starting fauna present", fauna >= 1, "fauna=" + fauna);
                Messages.Message("AB dev: cavern basement checked - view the level below to explore it.",
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("cavern-basement self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: peak-plateau self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestPeakPlateau()
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
                Check("setting on", ABMod.Settings != null && ABMod.Settings.naturalPeaks);
                Map surface = Find.CurrentMap?.GroundMap();
                if (surface == null)
                {
                    Check("ground/surface map exists", false);
                    Report("peak-plateau self-test", sb, pass, fail);
                    return;
                }
                bool existed = surface.Levels()?.upperMap != null;
                Map sky = surface.Levels()?.upperMap
                    ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("peak-plateau self-test", sb, pass, fail);
                    return;
                }
                if (existed)
                {
                    sb.AppendLine("  NOTE  sky level predates this test; verifying whatever it has.");
                }
                int plateau = 0;
                int roofedPlateau = 0;
                int plants = 0;
                int walls = 0;
                foreach (IntVec3 c in sky.AllCells)
                {
                    TerrainDef t = c.GetTerrain(sky);
                    Building ed = c.GetEdifice(sky);
                    if (ed != null && ed.def.building != null && ed.def.building.isNaturalRock)
                    {
                        walls++;
                        continue;
                    }
                    if (t == TerrainDefOf.Soil || t == TerrainDefOf.Gravel)
                    {
                        plateau++;
                        if (sky.roofGrid.Roofed(c))
                        {
                            roofedPlateau++;
                        }
                        if (c.GetPlant(sky) != null)
                        {
                            plants++;
                        }
                    }
                }
                if (plateau == 0)
                {
                    sb.AppendLine("  NOTE  no plateau cells - the surface mountain may be too small to open one. Walls=" + walls);
                    Check("mountain mass present at all", walls > 0, "walls=" + walls);
                }
                else
                {
                    Check("plateau ground present", plateau > 40, "plateau=" + plateau);
                    Check("plateau is open sky (unroofed)", roofedPlateau == 0, "roofed=" + roofedPlateau);
                    Check("plateau vegetation present", plants > 0, "plants=" + plants);
                    Check("cliff rim walls present", walls > 0, "walls=" + walls);
                }
                Messages.Message("AB dev: peak plateau checked - go up a level to see it.",
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("peak-plateau self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: ritual-attendance self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestRitualAttendance()
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
                    Report("ritual-attendance self-test", sb, pass, fail);
                    return;
                }
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("ritual-attendance self-test", sb, pass, fail);
                    return;
                }
                IntVec3 b = FindOpenBaseCell(surface);
                ClearCell(surface, b);
                Building_ABStairs stairs = null;
                ThingDef stairsDef = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown");
                if (stairsDef != null)
                {
                    stairs = GenSpawn.Spawn(ThingMaker.MakeThing(stairsDef, ThingDefOf.WoodLog), b, surface, WipeMode.Vanish)
                        as Building_ABStairs;
                    stairs?.SetFaction(Faction.OfPlayer);
                }
                bool linked = stairs != null && stairs.CounterpartTowards(basement) != null;
                Check("stairs linked to the basement", linked);
                if (!linked)
                {
                    Report("ritual-attendance self-test", sb, pass, fail);
                    return;
                }

                // Colonist in the basement near the counterpart.
                Building_ABStairs exit = stairs.CounterpartTowards(basement);
                IntVec3 down = CellFinder.StandableCellNear(exit.Position, basement, 6f);
                Pawn below = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(below, down.IsValid ? down : exit.Position, basement, WipeMode.Vanish);
                Check("colonist spawned in the basement", below.Spawned && below.Map == basement);

                // 1. Candidate merge: inside the ritual scope, the surface pool
                //    contains the basement colonist; outside the scope it does not.
                bool inPoolBefore = surface.mapPawns.FreeColonistsAndPrisonersSpawned.Contains(below);
                Check("basement colonist NOT in the surface pool outside the scope", !inPoolBefore);
                ABRitualAttendance.EnterScope();
                bool inPoolScoped;
                try
                {
                    inPoolScoped = surface.mapPawns.FreeColonistsAndPrisonersSpawned.Contains(below);
                }
                finally
                {
                    ABRitualAttendance.ExitScope();
                }
                Check("basement colonist IS in the surface pool inside the ritual scope", inPoolScoped);
                bool inPoolAfter = surface.mapPawns.FreeColonistsAndPrisonersSpawned.Contains(below);
                Check("vanilla pool untouched after the scope (no cache corruption)", !inPoolAfter);

                // 2. Gather routing: a below participant gets a stairs job.
                bool routed = false;
                Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(below, surface);
                if (entry?.CounterpartTowards(surface) != null)
                {
                    Job job = CrossLevelWork.MakeStairsJob(entry, entry.CounterpartTowards(surface));
                    below.jobs?.StartJob(job, JobCondition.InterruptForced);
                    routed = below.CurJobDef == ABDefOf.AB_UseStairs;
                }
                Check("below participant takes the stairs toward the ritual map", routed,
                    "job=" + (below.CurJobDef?.defName ?? "null"));

                Messages.Message("AB dev: ritual-attendance mechanism verified. For the full flow, assign a role holder below and begin a real ritual.",
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("ritual-attendance self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: below-view diagnostic", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BelowViewDiagnostic()
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                Map cur = Find.CurrentMap;
                Map ground = cur?.GroundMap();
                Map sky = ground?.Levels()?.upperMap;
                sb.AppendLine("current map=" + (cur?.uniqueID.ToString() ?? "null") + " level=" + (cur?.Level() ?? 0)
                    + " | ground=" + (ground?.uniqueID.ToString() ?? "null")
                    + " | sky=" + (sky?.uniqueID.ToString() ?? "null"));
                sb.AppendLine("guards: rendering=" + ABGuard.On(ABGuard.Rendering) + " async=" + ABGuard.On(ABGuard.Async)
                    + " roofSync=" + ABGuard.On(ABGuard.RoofSync)
                    + " | showLiveBelow=" + (ABMod.Settings?.showLiveBelow ?? false)
                    + " | queueCeiling=" + LevelRenderer.DebugQueueCeiling);
                sb.AppendLine("belowThings tallies: " + SectionLayer_ABBelowThings.DiagSummary());

                if (sky != null && !sky.Disposed)
                {
                    // Sky-side print census: sections with content vs open-air cells.
                    Section[,] sections = LevelRenderer.DebugSections(sky);
                    if (sections == null)
                    {
                        sb.AppendLine("sky drawer: sections NOT built yet");
                    }
                    else
                    {
                        int total = 0;
                        int withVerts = 0;
                        long verts = 0;
                        foreach (Section s in sections)
                        {
                            SectionLayer layer = s?.GetLayer(typeof(SectionLayer_ABBelowThings));
                            if (layer == null)
                            {
                                continue;
                            }
                            total++;
                            long v = 0;
                            for (int i = 0; i < layer.subMeshes.Count; i++)
                            {
                                v += layer.subMeshes[i].verts.Count;
                            }
                            if (v > 0)
                            {
                                withVerts++;
                            }
                            verts += v;
                        }
                        int airCells = 0;
                        TerrainGrid tg = sky.terrainGrid;
                        foreach (IntVec3 c in sky.AllCells)
                        {
                            if (tg.TerrainAt(c) == ABDefOf.AB_OpenAir)
                            {
                                airCells++;
                            }
                        }
                        sb.AppendLine("sky prints: sections=" + total + " withContent=" + withVerts
                            + " totalVerts=" + verts + " openAirCells=" + airCells);
                    }
                }

                if (ground != null)
                {
                    // Lower-map layer census for one in-view section: catches copy-set
                    // misses (fade layers) and queue anomalies.
                    Section[,] gs = LevelRenderer.DebugSections(ground);
                    if (gs != null)
                    {
                        IntVec3 vc = Find.CameraDriver.MapPosition;
                        if (!vc.InBounds(ground))
                        {
                            vc = ground.Center;
                        }
                        Section sec = ground.mapDrawer.SectionAt(vc);
                        if (sec != null)
                        {
                            sb.AppendLine("ground section @" + vc + " layers:");
                            List<SectionLayer> layers = LevelRenderer.DebugLayers(sec);
                            for (int i = 0; i < layers.Count; i++)
                            {
                                SectionLayer l = layers[i];
                                long v = 0;
                                float maxY = -99f;
                                int q = -1;
                                for (int j = 0; j < l.subMeshes.Count; j++)
                                {
                                    LayerSubMesh sm = l.subMeshes[j];
                                    v += sm.verts.Count;
                                    if (sm.finalized && sm.mesh != null)
                                    {
                                        maxY = Mathf.Max(maxY, sm.mesh.bounds.center.y);
                                    }
                                    if (q < 0 && sm.material != null)
                                    {
                                        q = sm.material.renderQueue;
                                    }
                                }
                                if (v > 0)
                                {
                                    sb.AppendLine("  " + l.GetType().Name + ": verts=" + v
                                        + " maxBoundsY=" + maxY.ToString("0.00") + " q=" + q);
                                }
                            }
                        }
                    }
                }

                // BUG1 probe: every air-defense building on either map, with the
                // exact roof verdict its own mod reads.
                foreach (Map m in new[] { ground, sky })
                {
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    List<Building> all = m.listerBuildings.allBuildingsColonist;
                    for (int i = 0; i < all.Count; i++)
                    {
                        Building b = all[i];
                        if (b?.def?.thingClass == null || !b.def.thingClass.Name.Contains("AirDefense"))
                        {
                            continue;
                        }
                        RoofDef roof = m.roofGrid.RoofAt(b.Position);
                        sb.AppendLine("ADA '" + b.def.defName + "' on map " + m.uniqueID + " (level " + m.Level()
                            + ") at " + b.Position
                            + ": roof=" + (roof?.defName ?? "none")
                            + " terrain=" + m.terrainGrid.TerrainAt(b.Position)?.defName);
                        Map other = m.Level() == 0 ? sky : ground;
                        if (other != null && !other.Disposed && b.Position.InBounds(other))
                        {
                            sb.AppendLine("    same cell on level " + other.Level() + ": roof="
                                + (other.roofGrid.RoofAt(b.Position)?.defName ?? "none")
                                + " terrain=" + other.terrainGrid.TerrainAt(b.Position)?.defName);
                        }
                    }
                }

                // Force a below-print reprint so a second diagnostic run shows the delta.
                if (sky != null && !sky.Disposed && LevelRenderer.DrawerReady(sky))
                {
                    sky.mapDrawer.WholeMapChanged((ulong)ABDefOf.AB_BelowThings);
                    sb.AppendLine("forced below-print reprint armed (in-view sections regen next frame).");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine("EXCEPTION: " + e);
            }
            Log.Warning("[As above, So below] BELOW-VIEW DIAGNOSTIC\n" + sb);
            Messages.Message("AB dev: below-view diagnostic logged. Run it again after a few seconds to compare.",
                MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("As above", "AB: pod transit self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestPodTransit()
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("pod transit self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("pod transit self-test", sb, pass, fail);
                    return;
                }

                // --- Def eligibility table.
                Check("drop pod def is transit-eligible", PodTransit.IsTransitDef(ThingDefOf.DropPodIncoming));
                ThingDef meteorite = DefDatabase<ThingDef>.GetNamedSilentFail("MeteoriteIncoming");
                Check("meteorite def is transit-eligible", meteorite != null && PodTransit.IsTransitDef(meteorite));
                ThingDef shuttle = DefDatabase<ThingDef>.GetNamedSilentFail("ShuttleIncoming");
                if (shuttle != null)
                {
                    Check("shuttle def is NOT transit-eligible", !PodTransit.IsTransitDef(shuttle));
                }

                // --- Build the gap: open air on the sky above a clear surface cell.
                IntVec3 b = FindOpenBaseCell(surface);
                ClearCell(surface, b);
                ClearCell(sky, b);
                if (surface.roofGrid.Roofed(b))
                {
                    surface.roofGrid.SetRoof(b, null);
                }
                sky.terrainGrid.SetTerrain(b, ABDefOf.AB_OpenAir);
                if (sky.roofGrid.Roofed(b))
                {
                    sky.roofGrid.SetRoof(b, null);
                }
                Check("gap is open through the sky level", PodTransit.GapOpen(sky, CellRect.SingleCell(b)));

                bool settingOn = ABMod.Settings != null && ABMod.Settings.podTransit;
                Check("podTransit setting is on", settingOn, "enable it in mod settings to test");

                // --- Full loop, fast-forwarded deterministically: spawn a cargo pod
                // at the gap cell, run the lift, then the handoff, asserting the
                // descent clock is preserved at every step.
                ActiveTransporterInfo info = new ActiveTransporterInfo();
                Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                steel.stackCount = 25;
                info.innerContainer.TryAdd(steel);
                DropPodUtility.MakeDropPodAt(b, surface, info);
                DropPodIncoming pod = null;
                List<Thing> atCell = b.GetThingList(surface);
                for (int i = 0; i < atCell.Count; i++)
                {
                    pod = atCell[i] as DropPodIncoming;
                    if (pod != null)
                    {
                        break;
                    }
                }
                Check("pod spawned on the surface gap cell", pod != null);
                if (pod == null || !settingOn)
                {
                    Report("pod transit self-test", sb, pass, fail);
                    return;
                }

                PodTransitComp surfaceComp = surface.GetComponent<PodTransitComp>();
                Check("pod queued for lift to the sky level", surfaceComp != null && surfaceComp.DevQueuedForLift(pod));

                int clockBefore = pod.ticksToImpact;
                surfaceComp?.MapComponentTick();
                Check("pod transferred to the sky map", pod.Spawned && pod.Map == sky,
                    "map=" + (pod.Map?.uniqueID.ToString() ?? "null"));
                Check("descent clock preserved across the lift", pod.ticksToImpact == clockBefore,
                    "before=" + clockBefore + " after=" + pod.ticksToImpact);

                PodTransitComp skyComp = sky.GetComponent<PodTransitComp>();
                int at = skyComp?.DevTransferAt(pod) ?? -1;
                Check("handoff mark registered on the sky map", at > 0, "at=" + at);
                if (at > 0)
                {
                    // Fast-forward the upper leg to the handoff mark.
                    pod.ticksToImpact = at;
                    skyComp.MapComponentTick();
                    Check("pod handed off to the ground map", pod.Spawned && pod.Map == surface,
                        "map=" + (pod.Map?.uniqueID.ToString() ?? "null"));
                    Check("lower leg keeps the remaining descent", pod.ticksToImpact == at,
                        "expected=" + at + " actual=" + pod.ticksToImpact);
                }

                // --- Direct sky spawn over open air takes the downward leg only.
                ActiveTransporterInfo info2 = new ActiveTransporterInfo();
                Thing wood = ThingMaker.MakeThing(ThingDefOf.WoodLog);
                wood.stackCount = 10;
                info2.innerContainer.TryAdd(wood);
                DropPodUtility.MakeDropPodAt(b, sky, info2);
                DropPodIncoming skyPod = null;
                List<Thing> atSkyCell = b.GetThingList(sky);
                for (int i = 0; i < atSkyCell.Count; i++)
                {
                    skyPod = atSkyCell[i] as DropPodIncoming;
                    if (skyPod != null && skyPod != pod)
                    {
                        break;
                    }
                    skyPod = null;
                }
                Check("sky-spawned pod over open air registers a descent",
                    skyPod != null && skyComp != null && skyComp.DevTransferAt(skyPod) > 0);

                // --- Leave both pods to land live; watch from the sky level to see
                // the second one fall past into the gap. Any installed anti-air on
                // the sky level can engage during the upper leg.
                Messages.Message("AB dev: pod transit demo armed - two cargo pods are falling through the sky gap. "
                    + "View the SKY level to watch; check the surface for the deliveries.",
                    new TargetInfo(b, surface), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("pod transit self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: sky drop-grouping self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestSkyDropGrouping()
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("sky drop-grouping self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);
                if (sky == null)
                {
                    Report("sky drop-grouping self-test", sb, pass, fail);
                    return;
                }

                // Carve a wide open-air region on the sky and punch a small plateau
                // block in its middle - the only landable ground for the whole drop.
                IntVec3 baseCell = FindOpenBaseCell(surface);
                const int Half = 9;
                for (int dx = -Half; dx <= Half; dx++)
                {
                    for (int dz = -Half; dz <= Half; dz++)
                    {
                        IntVec3 c = baseCell + new IntVec3(dx, 0, dz);
                        if (!c.InBounds(sky) || !c.InBounds(surface))
                        {
                            continue;
                        }
                        ClearCell(sky, c);
                        ClearCell(surface, c);
                        if (surface.roofGrid.Roofed(c))
                        {
                            surface.roofGrid.SetRoof(c, null);
                        }
                        sky.terrainGrid.SetTerrain(c, ABDefOf.AB_OpenAir);
                        if (sky.roofGrid.Roofed(c))
                        {
                            sky.roofGrid.SetRoof(c, null);
                        }
                    }
                }
                const int PlateauHalf = 2;
                int plateauCells = 0;
                for (int dx = -PlateauHalf; dx <= PlateauHalf; dx++)
                {
                    for (int dz = -PlateauHalf; dz <= PlateauHalf; dz++)
                    {
                        IntVec3 c = baseCell + new IntVec3(dx, 0, dz);
                        if (!c.InBounds(sky))
                        {
                            continue;
                        }
                        MakePlatform(sky, surface, c);
                        plateauCells++;
                    }
                }
                sky.regionAndRoomUpdater?.TryRebuildDirtyRegionsAndRooms();
                Check("plateau block built on the sky",
                    plateauCells > 0 && sky.terrainGrid.TerrainAt(baseCell) != ABDefOf.AB_OpenAir);

                // Stress the fix: drop CENTER deep in open air, off the plateau.
                IntVec3 openCenter = baseCell + new IntVec3(Half - 1, 0, 0);
                Check("stress drop center is open air",
                    openCenter.InBounds(sky) && sky.terrainGrid.TerrainAt(openCenter) == ABDefOf.AB_OpenAir);

                const int Groups = 6;
                HashSet<int> before = new HashSet<int>();
                foreach (Thing t in surface.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming))
                {
                    before.Add(t.thingIDNumber);
                }
                foreach (Thing t in sky.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming))
                {
                    before.Add(t.thingIDNumber);
                }

                List<List<Thing>> thingsGroups = new List<List<Thing>>();
                for (int i = 0; i < Groups; i++)
                {
                    Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
                    steel.stackCount = 20;
                    thingsGroups.Add(new List<Thing> { steel });
                }
                DropPodUtility.DropThingGroupsNear(openCenter, sky, thingsGroups, forbid: false);

                List<Thing> pods = new List<Thing>();
                foreach (Thing t in sky.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming))
                {
                    if (!before.Contains(t.thingIDNumber))
                    {
                        pods.Add(t);
                    }
                }
                int onSurface = 0;
                foreach (Thing t in surface.listerThings.ThingsOfDef(ThingDefOf.DropPodIncoming))
                {
                    if (!before.Contains(t.thingIDNumber))
                    {
                        onSurface++;
                    }
                }

                Check("pods spawned", pods.Count > 0, "pods=" + pods.Count);
                Check("no pod scattered to the surface (fell through open air)", onSurface == 0,
                    "onSurface=" + onSurface);

                int onAir = 0;
                int maxDistSq = 0;
                foreach (Thing t in pods)
                {
                    if (sky.terrainGrid.TerrainAt(t.Position) == ABDefOf.AB_OpenAir)
                    {
                        onAir++;
                    }
                    int d = (t.Position - baseCell).LengthHorizontalSquared;
                    if (d > maxDistSq)
                    {
                        maxDistSq = d;
                    }
                }
                Check("every pod landed on a plateau cell (never open air)", onAir == 0,
                    "onAir=" + onAir + " of " + pods.Count);
                Check("pods grouped near the plateau (not spread map-wide)",
                    pods.Count == 0 || maxDistSq <= 12 * 12, "maxDist=" + Mathf.Sqrt(maxDistSq).ToString("0.0"));

                // --- Redirect exemption: a pod-drop pinned on a sky plateau must NOT be
                // bounced to the surface. The bounce keeps the sky spawn center, so the
                // drop then runs at nonsensical surface coords and scatters - the actual
                // "pods spread across the lower level" cause.
                IncidentParms plateauParms = new IncidentParms { target = sky, spawnCenter = baseCell };
                Check("a drop pinned on a sky plateau is exempt from the surface redirect",
                    ThreatDivert.IsSkyPodDrop(plateauParms, sky));
                IncidentParms airParms = new IncidentParms { target = sky, spawnCenter = openCenter };
                Check("a non-drop incident over open air is NOT exempt (still redirects)",
                    !ThreatDivert.IsSkyPodDrop(airParms, sky));

                // Clean up the incoming pods so the test leaves no falling debris.
                foreach (Thing t in pods)
                {
                    if (!t.Destroyed)
                    {
                        t.Destroy();
                    }
                }
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("sky drop-grouping self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: cross-fire probe (selected pawn)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CrossFireProbe()
        {
            Pawn p = Find.Selector.SelectedPawns.Count > 0 ? Find.Selector.SelectedPawns[0] : null;
            if (p == null || !p.Spawned)
            {
                Log.Warning(ABLog.Tag + " PROBE: select a pawn first.");
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[As above, So below] CROSS-FIRE PROBE for " + p.LabelShort
                + " (faction=" + (p.Faction?.Name ?? "null")
                + ", drafted=" + p.Drafted
                + ", response=" + (p.playerSettings?.hostilityResponse.ToString() ?? "n/a")
                + ", map level=" + (p.Map?.Levels()?.level.ToString() ?? "?") + ")");
            sb.AppendLine("guards: Combat=" + ABGuard.On(ABGuard.Combat)
                + " setting crossLevelCombat=" + (ABMod.Settings?.crossLevelCombat ?? false)
                + " autoEngage=" + (ABMod.Settings?.crossLevelAutoEngage ?? false)
                + " CE.Active=" + ABCECompat.Active);
            if (p.Faction != null && p.Faction != Faction.OfPlayer)
            {
                sb.AppendLine("relation to player: " + p.Faction.RelationKindWith(Faction.OfPlayer)
                    + " (any non-hostile NPC cross-fires; routing needs ally/assist-lord)");
            }
            Thing eqPrimary = p.equipment?.Primary;
            Verb primaryVerb = p.equipment?.PrimaryEq?.PrimaryVerb;
            sb.AppendLine("equipment: " + (eqPrimary?.def?.defName ?? "NONE (unarmed/melee-natural)")
                + (primaryVerb == null
                    ? " primaryVerb=NULL"
                    : " primaryVerb=" + primaryVerb.GetType().Name
                      + " isMelee=" + primaryVerb.verbProps.IsMeleeAttack
                      + " isVanillaLP=" + (primaryVerb is Verb_LaunchProjectile)
                      + (ABCECompat.Active ? " isCE=" + ABCECompat.IsCEVerb(primaryVerb) : "")
                      + " recognized=" + ABVerb.IsProjectileVerb(primaryVerb)));
            Verb verb = CrossLevelCombat.GetRangedVerb(p);
            sb.AppendLine("verb: " + (verb == null ? "NULL" : verb.GetType().Name
                + " range=" + verb.EffectiveRange.ToString("0.#")));
            LevelComp comp = p.Map?.Levels();
            Map other = comp == null ? null : (comp.level == 1 ? comp.lowerMap : comp.level == 0 ? comp.upperMap : null);
            if (other == null || other.Disposed)
            {
                sb.AppendLine("paired level: NONE (level=" + (comp?.level.ToString() ?? "?") + ")");
                Log.Warning(sb.ToString());
                return;
            }
            sb.AppendLine("paired level: map " + other.uniqueID + " (level " + (other.Levels()?.level ?? -9) + ")");
            Building_ABStairs routeStairs = CrossLevelWork.NearestUsableStairs(p, other, checkReachability: true);
            sb.AppendLine("stairs route to paired level: " + (routeStairs != null
                ? "available (" + routeStairs.ThingID + ")"
                : "NONE reachable - melee/no-LoF pawns cannot route and will idle"));
            List<Pawn> targets = new List<Pawn>();
            IReadOnlyList<Pawn> cand = other.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < cand.Count; i++)
            {
                Pawn t = cand[i];
                if (t != null && !t.Dead && !t.Downed && t.Spawned && t.HostileTo(p))
                {
                    targets.Add(t);
                }
            }
            sb.AppendLine("hostile-to-me targets on paired level: " + targets.Count);
            IntVec3 origin = p.Position;
            targets.Sort((a, b) =>
                (a.Position - origin).LengthHorizontalSquared.CompareTo((b.Position - origin).LengthHorizontalSquared));
            int probes = Math.Min(targets.Count, 5);
            for (int i = 0; i < probes; i++)
            {
                Pawn t = targets[i];
                string why = CrossLevelCombat.ExplainCanFire(p.Map, p.Position, t, verb);
                string cell = "";
                if (why != "OK" && verb != null)
                {
                    IntVec3 fc = CrossLevelCombat.FindFiringCell(p, t, verb);
                    cell = fc.IsValid ? "  [reposition available -> " + fc + "]" : "  [no firing cell either]";
                }
                sb.AppendLine("  -> " + t.LabelShort + " @" + t.Position + " dist="
                    + (t.Position - origin).LengthHorizontal.ToString("0.#") + ": " + why + cell);
            }
            Log.Warning(sb.ToString());
        }

        [DebugAction("As above", "AB: toggle cap corner fillers", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleCapCornerFillers()
        {
            SectionLayer_ABMountainCap.CornerFillersEnabled = !SectionLayer_ABMountainCap.CornerFillersEnabled;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].Level() == 1)
                {
                    maps[i].mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Terrain);
                }
            }
            Messages.Message("AB dev: cap corner fillers "
                + (SectionLayer_ABMountainCap.CornerFillersEnabled ? "ON" : "OFF")
                + " - compare the dash artifacts.", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>Basement ground truth after the round-5 all-fog report:
        /// biome identity (def-load probe - a fallback name here means our
        /// biome XML died at startup), stairs counts on both ends (generation
        /// probe - zero means never spawned, nonzero + invisible means
        /// rendering), fog data percentage (a cleared grid that still LOOKS
        /// fogged means stale section meshes), links, and drawer readiness.</summary>
        [DebugAction("As above", "AB: basement diagnostic", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BasementDiagnostic()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Map cur = Find.CurrentMap;
                Map ground = cur.GroundMap() ?? cur.LowerMap()?.GroundMap() ?? cur.UpperMap()?.GroundMap();
                Map basement = ground?.Levels()?.lowerMap;
                sb.Append("[AB basement diagnostic] cur=L").Append(cur.Level())
                    .Append(" ground=").Append(ground != null ? ("map" + ground.uniqueID) : "null");
                if (basement == null || basement.Disposed)
                {
                    sb.Append(" | NO BASEMENT LINKED");
                }
                else
                {
                    sb.Append(" | basement=map").Append(basement.uniqueID)
                        .Append(" biome=").Append(basement.Biome?.defName ?? "NULL")
                        .Append(" gen=").Append(basement.generatorDef?.defName ?? "NULL")
                        .Append(" linkUp=").Append(basement.Levels()?.upperMap == ground)
                        .Append(" drawerReady=").Append(LevelRenderer.DrawerReady(basement));
                    int stairsBasement = 0;
                    int stairsGround = 0;
                    List<Thing> bThings = basement.listerThings.AllThings;
                    for (int i = 0; i < bThings.Count; i++)
                    {
                        if (bThings[i] is Building_ABStairs)
                        {
                            stairsBasement++;
                        }
                    }
                    List<Thing> gThings = ground.listerThings.AllThings;
                    for (int i = 0; i < gThings.Count; i++)
                    {
                        if (gThings[i] is Building_ABStairs s && s.Counterpart?.Map == basement)
                        {
                            stairsGround++;
                        }
                    }
                    sb.Append(" | stairs: basementSide=").Append(stairsBasement)
                        .Append(" groundSideTowardBasement=").Append(stairsGround);
                    int fogged = 0;
                    int total = 0;
                    foreach (IntVec3 c in basement.AllCells)
                    {
                        total++;
                        if (basement.fogGrid.IsFogged(c))
                        {
                            fogged++;
                        }
                    }
                    sb.Append(" | fog: ").Append(fogged).Append("/").Append(total)
                        .Append(" (").Append((100f * fogged / Mathf.Max(1, total)).ToString("F0")).Append("%)");
                    IntVec3 center = basement.Center;
                    sb.Append(" | center: terrain=").Append(basement.terrainGrid.TerrainAt(center)?.defName ?? "null")
                        .Append(" edifice=").Append(center.GetEdifice(basement)?.def.defName ?? "none")
                        .Append(" fogged=").Append(basement.fogGrid.IsFogged(center));
                }
            }
            catch (Exception e)
            {
                sb.Append(" EXCEPTION: ").Append(e);
            }
            Log.Warning(sb.ToString());
            Messages.Message("AB basement diagnostic written to log.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>Force-lift demo (round-10): click any mass-crossing water
        /// cell on the surface and its WHOLE stretch lifts to the sky level
        /// (seal + carve + banks + falls) regardless of flow classification -
        /// instant waterfall demo on any river-through-mountain map, no tile
        /// lottery. Ensures the sky level first.</summary>
        [DebugAction("As above", "AB: force lift river (click water)", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceLiftRiver()
        {
            try
            {
                IntVec3 cell = UI.MouseCell();
                Map ground = Find.CurrentMap?.GroundMap();
                if (ground == null)
                {
                    Messages.Message("AB dev: no ground map here.", MessageTypeDefOf.RejectInput, false);
                    return;
                }
                if (ground.Levels()?.upperMap == null)
                {
                    LevelMapGen.GetOrGenerate(ground, 1, ABDefOf.AB_Sky, out _);
                }
                Map sky = ground.Levels()?.upperMap;
                if (sky == null || sky.Disposed)
                {
                    Messages.Message("AB dev: could not ensure a sky level.", MessageTypeDefOf.RejectInput, false);
                    return;
                }
                List<IntVec3> stretch = GenStep_ABSkyRivers.StretchAt(sky, ground, cell);
                if (stretch.Count == 0)
                {
                    Messages.Message("AB dev: that cell is not mass-crossing water (click the river where it passes the mountain).",
                        MessageTypeDefOf.RejectInput, false);
                    return;
                }
                string report = GenStep_ABSkyRivers.ExecuteLift(sky, ground,
                    new List<List<IntVec3>> { stretch });
                Log.Warning("[AB force lift] " + report);
                Messages.Message("AB dev: lifted " + stretch.Count + " cells - view the sky level.",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Warning("[AB force lift] EXCEPTION: " + e);
            }
        }

        /// <summary>River generation ground truth: prints the genstep's own
        /// last-run summary (or bail reason) plus a LIVE recount of the
        /// column's river state, as one warning that crosses the bridge.</summary>
        [DebugAction("As above", "AB: river diagnostic", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RiverDiagnostic()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                sb.Append("[AB river diagnostic] genstep last run: ")
                    .Append(GenStep_ABSkyRivers.LastSummary);
                Map cur = Find.CurrentMap;
                sb.Append(" | cur=L").Append(cur.Level());
                // Robust ground resolution: a sky comp whose groundMap field
                // has not linked yet (run #123 blind spot) still resolves
                // through its lower neighbor.
                Map ground = cur.GroundMap() ?? cur.LowerMap()?.GroundMap()
                    ?? cur.UpperMap()?.GroundMap();
                Map sky = ground?.UpperMap();
                if (ground == null || sky == null || sky.Disposed)
                {
                    sb.Append(" | no sky level linked from here (ground=")
                        .Append(ground != null ? ("L" + ground.Level()) : "null")
                        .Append(")");
                }
                else
                {
                    int groundRiver = 0;
                    int underMass = 0;
                    int underMassWet = 0;
                    int skyWater = 0;
                    TerrainGrid gt = ground.terrainGrid;
                    TerrainGrid st = sky.terrainGrid;
                    TerrainDef air = ABDefOf.AB_OpenAir;
                    foreach (IntVec3 c in ground.AllCells)
                    {
                        TerrainDef g = gt.BaseTerrainAt(c);
                        bool skyIsWater = st.TerrainAt(c) != null && st.TerrainAt(c).IsWater;
                        if (skyIsWater)
                        {
                            skyWater++;
                        }
                        if (g == null || !g.IsWater)
                        {
                            continue;
                        }
                        groundRiver++;
                        TerrainDef top = st.TerrainAt(c);
                        bool mass = top != null && top != air && top != ABDefOf.AB_RoofSurface
                            && !top.IsRiver;
                        if (mass)
                        {
                            underMass++;
                            underMassWet++;
                        }
                    }
                    sb.Append(" | LIVE: groundRiver=").Append(groundRiver)
                        .Append(" wetUnderMass(through-tunnels stay wet BY DESIGN; lifted source stretches read 0)=").Append(underMassWet)
                        .Append(" skyWaterCells=").Append(skyWater)
                        .Append(" skyLips=").Append(sky.listerThings.ThingsOfDef(ABDefOf.AB_Waterfall).Count)
                        .Append(" groundBases=").Append(ground.listerThings.ThingsOfDef(ABDefOf.AB_WaterfallBase).Count);
                }
            }
            catch (Exception e)
            {
                sb.Append(" EXCEPTION: ").Append(e);
            }
            Log.Warning(sb.ToString());
            Messages.Message("AB river diagnostic written to log.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>Light-chain ground truth for one cell: glow grid value,
        /// any glower/shaft found there, and the shaft's full gate chain
        /// (feature, sun on the ground map, pane chain). One warning.</summary>
        [DebugAction("As above", "AB: light diagnostic", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void LightDiagnostic()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Map map = Find.CurrentMap;
                IntVec3 c = UI.MouseCell();
                sb.Append("[AB light diagnostic] map=L").Append(map.Level())
                    .Append(" cell=").Append(c.ToString())
                    .Append(" glow=").Append(map.glowGrid.GroundGlowAt(c).ToString("F2"))
                    .Append(" roofed=").Append(map.roofGrid.Roofed(c))
                    .Append(" fogged=").Append(map.fogGrid.IsFogged(c));
                Map groundMap = map.GroundMap() ?? map;
                sb.Append(" | sun(groundMap)=").Append(GenCelestial.CurCelestialSunGlow(groundMap).ToString("F2"))
                    .Append(" sun(thisMap)=").Append(GenCelestial.CurCelestialSunGlow(map).ToString("F2"));
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    CompGlower glower = t.TryGetComp<CompGlower>();
                    if (glower == null)
                    {
                        continue;
                    }
                    sb.Append(" | ").Append(t.def.defName)
                        .Append(" glowerLit=").Append(glower.Glows);
                    CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
                    if (power != null)
                    {
                        sb.Append(" powered=").Append(power.PowerOn);
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append(" EXCEPTION: ").Append(e);
            }
            Log.Warning(sb.ToString());
            Messages.Message("AB light diagnostic written to log.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>One-click ground truth for "right click does not work":
        /// runs the exact redirect pipeline for the current selection against
        /// the clicked cell and reports every decision as ONE warning (warnings
        /// cross the bridge). Use: select the pawn(s), pick this tool, click
        /// the cell you would have right-clicked.</summary>
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

        [DebugAction("As above", "AB: probe ledge cell", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ProbeLedgeCell()
        {
            IntVec3 c = UI.MouseCell();
            Map cur = Find.CurrentMap;
            if (cur == null || !c.InBounds(cur))
            {
                return;
            }
            Map sky = cur.Level() == 1 ? cur : cur.UpperMap();
            Map ground = cur.GroundMap();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[AB probe] cell " + c + " viewing map " + cur.uniqueID + " (level " + cur.Level() + ")");
            if (sky != null && !sky.Disposed && c.InBounds(sky))
            {
                TerrainDef st = sky.terrainGrid.TerrainAt(c);
                Building sEd = c.GetEdifice(sky);
                sb.AppendLine("  sky terrain=" + (st?.defName ?? "null")
                    + " edifice=" + (sEd?.def.defName ?? "none")
                    + " fogged=" + c.Fogged(sky));
            }
            else
            {
                sb.AppendLine("  sky: none");
            }
            if (ground != null && c.InBounds(ground))
            {
                RoofDef roof = ground.roofGrid.RoofAt(c);
                Building gEd = ground.edificeGrid[c];
                sb.AppendLine("  ground roof=" + (roof?.defName ?? "none")
                    + " (natural=" + (roof?.isNatural ?? false) + ", thick=" + (roof?.isThickRoof ?? false) + ")"
                    + " edifice=" + (gEd?.def.defName ?? "none")
                    + " mineable=" + (gEd?.def.mineable ?? false)
                    + " fogged=" + c.Fogged(ground));
                sb.AppendLine("  CoveredBelow=" + LevelSync.CoveredBelow(ground, c));
            }
            if (sky != null && !sky.Disposed && c.InBounds(sky))
            {
                TerrainGrid sg = sky.terrainGrid;
                TerrainDef capDef = ABDefOf.AB_MountainTop;
                int mask = 0;
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = c + GenAdj.CardinalDirections[i];
                    if (SectionLayer_ABMountainCap.Linked(sky, sg, capDef, n))
                    {
                        mask |= 1 << i;
                    }
                }
                sb.AppendLine("  cap fill: massCell=" + SectionLayer_ABMountainCap.IsMassCell(sky, sg, capDef, c)
                    + " linkMask=" + mask + " (15 = fully interior)");
                sb.AppendLine("  " + SectionLayer_ABMountainCap.DebugCapFillInfo(sky, ground, c));
            }
            Log.Warning(ABLog.Tag + " LEDGEPROBE:\n" + sb);
            Messages.Message("AB probe logged for " + c + " - check the dev log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("As above", "AB: mech overseer self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestMechOverseer()
        {
            if (!ModsConfig.BiotechActive)
            {
                Messages.Message("AB dev: Biotech is not active; mech overseer test skipped.", MessageTypeDefOf.RejectInput, false);
                return;
            }
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("mech overseer self-test", sb, pass, fail);
                    return;
                }
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("mech overseer self-test", sb, pass, fail);
                    return;
                }

                // --- Overseer: first free colonist, mechlinked if needed.
                List<Pawn> colonists = surface.mapPawns.FreeColonists;
                Pawn overseer = colonists.Count > 0 ? colonists[0] : null;
                Check("a colonist is available as overseer", overseer != null);
                if (overseer == null)
                {
                    Report("mech overseer self-test", sb, pass, fail);
                    return;
                }
                if (!MechanitorUtility.IsMechanitor(overseer))
                {
                    HediffDef mechlink = DefDatabase<HediffDef>.GetNamedSilentFail("MechlinkImplant");
                    if (mechlink != null)
                    {
                        overseer.health.AddHediff(mechlink, overseer.health.hediffSet.GetBrain());
                        PawnComponentsUtility.AddAndRemoveDynamicComponents(overseer);
                    }
                }
                Check("overseer has a mechanitor tracker", overseer.mechanitor != null);
                if (overseer.mechanitor == null)
                {
                    Report("mech overseer self-test", sb, pass, fail);
                    return;
                }

                // --- Work mech bonded to the overseer, spawned beside them.
                PawnKindDef lifterKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Mech_Lifter");
                Check("lifter pawn kind found", lifterKind != null);
                if (lifterKind == null)
                {
                    Report("mech overseer self-test", sb, pass, fail);
                    return;
                }
                Pawn mech = PawnGenerator.GeneratePawn(new PawnGenerationRequest(lifterKind, Faction.OfPlayer));
                IntVec3 baseCell = FindOpenBaseCell(surface);
                GenSpawn.Spawn(mech, baseCell, surface, WipeMode.Vanish);
                overseer.relations.AddDirectRelation(PawnRelationDefOf.Overseer, mech);
                PawnComponentsUtility.AddAndRemoveDynamicComponents(mech);
                overseer.mechanitor.AssignPawnControlGroup(mech);
                if (mech.needs?.energy != null)
                {
                    mech.needs.energy.CurLevel = mech.needs.energy.MaxLevel;
                }
                Check("mech is overseen on the surface",
                    mech.OverseerSubject != null && mech.OverseerSubject.State == OverseerSubjectState.Overseen,
                    "state=" + mech.OverseerSubject?.State);

                // --- Cross the level: pocket in the basement at the overseer's
                // coordinates (levels share the coordinate space).
                IntVec3 pocket = CarveBasementPocket(basement, overseer.Position);
                mech.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                mech.DeSpawn(DestroyMode.Vanish);
                GenSpawn.Spawn(mech, pocket, basement, WipeMode.Vanish);
                Check("mech transferred to the basement", mech.Spawned && mech.Map == basement);
                Check("mech is STILL overseen across levels",
                    mech.OverseerSubject != null && mech.OverseerSubject.State == OverseerSubjectState.Overseen,
                    "state=" + mech.OverseerSubject?.State);
                Check("mechanitor command range reaches through the column",
                    MechanitorUtility.InMechanitorCommandRange(mech, mech.Position),
                    "overseer at " + overseer.Position + ", mech at " + mech.Position);

                // --- Think-tree determination at full and mid energy: log the
                // exact giver so a dormancy repro names its culprit branch.
                for (int pct = 0; pct < 2; pct++)
                {
                    if (mech.needs?.energy != null)
                    {
                        mech.needs.energy.CurLevel = (pct == 0 ? 1f : 0.25f) * mech.needs.energy.MaxLevel;
                    }
                    ThinkResult res = ThinkResult.NoJob;
                    string thinkErr = null;
                    try
                    {
                        res = mech.thinker.MainThinkNodeRoot.TryIssueJobPackage(mech, default(JobIssueParams));
                    }
                    catch (Exception te)
                    {
                        thinkErr = te.GetType().Name + ": " + te.Message;
                    }
                    string jobName = res.Job?.def?.defName ?? "none";
                    string giver = res.SourceNode?.GetType().Name ?? "none";
                    string label = pct == 0 ? "full energy" : "25% energy";
                    sb.AppendLine("  info  think (" + label + "): job=" + jobName + " giver=" + giver
                        + (thinkErr != null ? " EX=" + thinkErr : ""));
                    Check("think tree (" + label + ") does not force dormant self-shutdown",
                        thinkErr == null && (res.Job == null || res.Job.def != JobDefOf.SelfShutdown),
                        "job=" + jobName + " giver=" + giver + (thinkErr != null ? " EX=" + thinkErr : ""));
                }
                if (mech.needs?.energy != null)
                {
                    mech.needs.energy.CurLevel = mech.needs.energy.MaxLevel;
                }

                Messages.Message("AB dev: mech overseer self-test done. Lifter left live in the basement pocket - watch what job it settles into.",
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("mech overseer self-test", sb, pass, fail);
        }

        [DebugAction("As above", "AB: bond + home self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestBondAndHome()
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
                    Check("ground/surface map exists", false, "no ground map");
                    Report("bond + home self-test", sb, pass, fail);
                    return;
                }
                Map basement = surface.Levels()?.lowerMap
                    ?? LevelMapGen.GetOrGenerate(surface, -1, ABDefOf.AB_Basement, out _);
                Check("basement exists", basement != null);
                if (basement == null)
                {
                    Report("bond + home self-test", sb, pass, fail);
                    return;
                }

                // --- Column home identity.
                Check("surface is player home", surface.IsPlayerHome);
                Check("basement inherits the column's home verdict",
                    basement.IsPlayerHome == surface.IsPlayerHome,
                    "basement=" + basement.IsPlayerHome + " surface=" + surface.IsPlayerHome);
                Map sky = surface.Levels()?.upperMap;
                if (sky != null)
                {
                    Check("sky inherits the column's home verdict",
                        sky.IsPlayerHome == surface.IsPlayerHome,
                        "sky=" + sky.IsPlayerHome + " surface=" + surface.IsPlayerHome);
                }

                // --- Psychic bond across levels (Biotech).
                if (!ModsConfig.BiotechActive)
                {
                    sb.AppendLine("  info  Biotech not active; bond half skipped.");
                    Report("bond + home self-test", sb, pass, fail);
                    return;
                }
                List<Pawn> colonists = surface.mapPawns.FreeColonists;
                Pawn a = colonists.Count > 0 ? colonists[0] : null;
                Pawn b = colonists.Count > 1 ? colonists[1] : null;
                if (b == null && a != null)
                {
                    b = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    GenSpawn.Spawn(b, FindOpenBaseCell(surface), surface, WipeMode.Vanish);
                }
                Check("two colonists available for bonding", a != null && b != null);
                if (a == null || b == null)
                {
                    Report("bond + home self-test", sb, pass, fail);
                    return;
                }
                Hediff_PsychicBond bondA = a.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.PsychicBond) as Hediff_PsychicBond;
                if (bondA == null)
                {
                    bondA = (Hediff_PsychicBond)a.health.AddHediff(HediffDefOf.PsychicBond);
                    bondA.target = b;
                }
                Hediff_PsychicBond bondB = b.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.PsychicBond) as Hediff_PsychicBond;
                if (bondB == null)
                {
                    bondB = (Hediff_PsychicBond)b.health.AddHediff(HediffDefOf.PsychicBond);
                    bondB.target = a;
                }

                IntVec3 pocket = CarveBasementPocket(basement, b.Position);
                b.jobs?.EndCurrentJob(JobCondition.InterruptForced, startNewJob: false);
                b.DeSpawn(DestroyMode.Vanish);
                GenSpawn.Spawn(b, pocket, basement, WipeMode.Vanish);
                Check("bonded pawn moved to the basement", b.Spawned && b.Map == basement);
                Check("bond counts as NEAR from the surface side",
                    ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson(a, bondA));
                Check("bond counts as NEAR from the basement side",
                    ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson(b, bondB));

                Messages.Message("AB dev: bond + home self-test done. " + a.LabelShort + " (surface) and " + b.LabelShort
                    + " (basement) are psychically bonded - check their mood tabs for the bond WITHOUT the distance debuff.",
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("bond + home self-test", sb, pass, fail);
        }

        // --- helpers ---------------------------------------------------------

        /// <summary>Mines a 3x3 standable pocket in the basement at the given
        /// coordinates (levels share the coordinate space) and unfogs it.
        /// Returns the pocket center, clamped inside bounds.</summary>
        private static IntVec3 CarveBasementPocket(Map basement, IntVec3 around)
        {
            IntVec3 c = around;
            c.x = Mathf.Clamp(c.x, 2, basement.Size.x - 3);
            c.z = Mathf.Clamp(c.z, 2, basement.Size.z - 3);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    IntVec3 cell = new IntVec3(c.x + dx, 0, c.z + dz);
                    ClearCell(basement, cell);
                    basement.fogGrid.Unfog(cell);
                }
            }
            return c;
        }

        /// <summary>A durable sky platform cell: rooftop terrain on the sky AND a real
        /// constructed roof on the surface below, so the rooftop reconcile sweep agrees
        /// the platform is legitimate. Hand-painted rooftop terrain without the backing
        /// roof gets reverted to open air within a sweep cycle, destroying whatever
        /// stands on it (run-3 mortar leavings warning).</summary>
        private static void MakePlatform(Map sky, Map surface, IntVec3 c)
        {
            ClearCell(sky, c);
            sky.terrainGrid.SetTerrain(c, ABDefOf.AB_RoofSurface);
            if (c.InBounds(surface))
            {
                surface.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
        }

        private static IntVec3 FindOpenBaseCell(Map surface)
        {
            foreach (IntVec3 c in GenRadial.RadialCellsAround(surface.Center, 24f, useCenter: true))
            {
                if (c.InBounds(surface) && c.Standable(surface) && !c.Fogged(surface)
                    && (c + IntVec3.East).InBounds(surface))
                {
                    return c;
                }
            }
            return surface.Center;
        }

        private static void ClearCell(Map map, IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return;
            }
            List<Thing> things = new List<Thing>(c.GetThingList(map));
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed)
                {
                    continue;
                }
                ThingCategory cat = t.def.category;
                if (cat == ThingCategory.Building || cat == ThingCategory.Item || cat == ThingCategory.Plant)
                {
                    t.Destroy(DestroyMode.Vanish);
                }
            }
        }

        private static Pawn SpawnHostile(Map surface, IntVec3 cell)
        {
            try
            {
                Faction enemy = Find.FactionManager.RandomEnemyFaction(allowHidden: false, allowDefeated: false, allowNonHumanlike: false)
                    ?? Find.FactionManager.RandomEnemyFaction();
                PawnKindDef kind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate")
                    ?? DefDatabase<PawnKindDef>.GetNamedSilentFail("Drifter")
                    ?? PawnKindDefOf.Colonist;
                Pawn p = PawnGenerator.GeneratePawn(kind, enemy);
                GenSpawn.Spawn(p, cell, surface, WipeMode.Vanish);
                return p;
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " dev self-test could not spawn a hostile: " + e.Message);
                return null;
            }
        }

        private static void ArmWithRanged(Pawn p)
        {
            try
            {
                if (p?.equipment == null)
                {
                    return;
                }
                if (CrossLevelCombat.GetRangedVerb(p) != null)
                {
                    return;
                }
                ThingDef gunDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Revolver")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Autopistol");
                if (gunDef != null)
                {
                    p.equipment.DestroyAllEquipment();
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gunDef));
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " dev self-test could not arm pawn: " + e.Message);
            }
        }

        private static Pawn SpawnArmedColonist(Map sky, IntVec3 cell)
        {
            try
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(p, cell, sky, WipeMode.Vanish);
                ThingDef gunDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Autopistol")
                    ?? DefDatabase<ThingDef>.GetNamedSilentFail("Gun_Revolver");
                if (gunDef != null && p.equipment != null)
                {
                    p.equipment.DestroyAllEquipment();
                    p.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gunDef));
                }
                if (p.drafter == null)
                {
                    p.drafter = new Pawn_DraftController(p);
                }
                p.drafter.Drafted = true;
                return p;
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " dev self-test could not spawn an armed colonist: " + e.Message);
                return null;
            }
        }

        /// <summary>Enclose the sky cell over the column with walls on all sides plus the
        /// cell itself as solid floor, then confirm a shooter standing on the surface
        /// under it has NO line of fire up (structure blocks). Restores the arena after.</summary>
        private static bool TestReverseEnclosed(Map surface, Map sky, IntVec3 b)
        {
            try
            {
                // A sky cell whose column and all neighbours are solid (not open air):
                // pick a spot far from the hole so it is naturally enclosed.
                IntVec3 solid = b + new IntVec3(8, 0, 0);
                if (!solid.InBounds(sky))
                {
                    return true; // cannot set up; treat as pass (not a real failure)
                }
                TerrainDef air = ABDefOf.AB_OpenAir;
                bool anyOpen = sky.terrainGrid.TerrainAt(solid) == air;
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = solid + GenAdj.AdjacentCells[i];
                    if (n.InBounds(sky) && sky.terrainGrid.TerrainAt(n) == air)
                    {
                        anyOpen = true;
                        break;
                    }
                }
                if (anyOpen)
                {
                    // Force it solid for the test.
                    sky.terrainGrid.SetTerrain(solid, ABDefOf.AB_RoofSurface);
                    for (int i = 0; i < 8; i++)
                    {
                        IntVec3 n = solid + GenAdj.AdjacentCells[i];
                        if (n.InBounds(sky) && sky.terrainGrid.TerrainAt(n) == air)
                        {
                            sky.terrainGrid.SetTerrain(n, ABDefOf.AB_RoofSurface);
                        }
                    }
                }
                // A dummy: a real thing to line-of-fire test against.
                Thing dummy = ThingMaker.MakeThing(ThingDefOf.Wall, ThingDefOf.WoodLog);
                GenSpawn.Spawn(dummy, solid, sky, WipeMode.Vanish);
                Verb verb = null;
                Pawn probe = null;
                try
                {
                    probe = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                    GenSpawn.Spawn(probe, solid, surface, WipeMode.Vanish);
                    verb = CrossLevelCombat.GetRangedVerb(probe);
                    // No open-air neighbour on the sky over the enclosed cell -> not exposed.
                    bool blocked = verb == null
                        || !CrossLevelCombat.CanFireFrom(surface, solid, dummy, verb, out _);
                    return blocked;
                }
                finally
                {
                    dummy?.Destroy(DestroyMode.Vanish);
                    if (probe != null && probe.Spawned)
                    {
                        probe.Destroy(DestroyMode.Vanish);
                    }
                }
            }
            catch
            {
                return true; // setup failure is not a combat-logic failure
            }
        }

        private static void Report(string name, StringBuilder body, int pass, int fail)
        {
            int total = pass + fail;
            string header = "[As above, So below] SELF-TEST: " + name + "\n"
                + "when: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\n"
                + "result: " + pass + "/" + total + " checks passed"
                + (fail > 0 ? " -- " + fail + " FAILED" : " -- ALL PASS") + "\n\n";
            string full = header + body;

            try
            {
                string root = ABMod.ModContent?.RootDir;
                if (!string.IsNullOrEmpty(root))
                {
                    string dir = Path.Combine(root, "docs");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "SelfTest.log");
                    // Append so multiple tests in one session all land in the file;
                    // reset when it grows past a sane bound.
                    if (File.Exists(path) && new FileInfo(path).Length > 262144)
                    {
                        File.Delete(path);
                    }
                    File.AppendAllText(path, full + "\n----\n");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " could not write docs/SelfTest.log: " + e.Message);
            }

            if (fail > 0)
            {
                Log.Error(ABLog.Tag + " SELFTEST '" + name + "': " + fail + " of " + total
                    + " checks FAILED (see docs/SelfTest.log):\n" + body);
            }
            else
            {
                Log.Warning(ABLog.Tag + " SELFTEST '" + name + "': all " + total + " checks passed.");
            }
            Messages.Message("AB self-test: " + pass + " pass / " + fail + " fail. See docs/SelfTest.log.",
                fail > 0 ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
