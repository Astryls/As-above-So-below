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
    // Partial of ABDevTools (combat diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
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

        [DebugAction("As above", "AB: CAI fog self-test", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SelfTestCombatAIFog()
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

            void Note(string line)
            {
                sb.AppendLine("  INFO  " + line);
            }

            try
            {
                if (!ABCombatAICompat.Active)
                {
                    sb.AppendLine("  SKIP  CAI 5000 (Krkr.rule56) not loaded - nothing to verify.");
                    Report("CAI fog self-test", sb, pass, fail);
                    return;
                }
                Note(ABCombatAICompat.StatusLine());

                // Reflection surface must fully resolve or the bridge is inert.
                Check("CAI fog API resolved (MapComponent_FogGrid + RevealSpot + settings)", ABCombatAICompat.Ready);
                if (!ABCombatAICompat.Ready)
                {
                    Report("CAI fog self-test", sb, pass, fail);
                    return;
                }

                // Fog Of War is opt-in; report but do not fail on it being off.
                Note("CAI Fog Of War is currently " + (ABCombatAICompat.FogEnabled ? "ON" : "off (opt-in in CAI mod settings)")
                    + "; cross-level vision only runs while it is ON.");

                Map surface = Find.CurrentMap?.GroundMap();
                if (surface == null)
                {
                    Check("ground/surface map exists", false, "no ground map");
                    Report("CAI fog self-test", sb, pass, fail);
                    return;
                }
                Map sky = surface.Levels()?.upperMap ?? LevelMapGen.GetOrGenerate(surface, 1, ABDefOf.AB_Sky, out _);
                Check("sky level exists", sky != null);

                // CAI adds its fog component to every map, ours included.
                Check("surface carries CAI fog component", ABCombatAICompat.HasFogComp(surface));
                if (sky != null)
                {
                    Check("sky carries CAI fog component", ABCombatAICompat.HasFogComp(sky));
                }

                // Exercise the reveal seam end to end: queue a reveal on the
                // surface at an open base cell. Succeeds (queues without error)
                // regardless of whether FoW is currently on.
                IntVec3 b = FindOpenBaseCell(surface);
                bool revealed = ABCombatAICompat.RevealOnMap(surface, b, 18f, 90);
                Check("RevealSpot invoked on the surface fog grid at " + b, revealed);

                if (sky != null)
                {
                    bool revealedUp = ABCombatAICompat.RevealOnMap(sky, b, 18f, 90);
                    Check("RevealSpot invoked on the sky fog grid at " + b, revealedUp);
                }

                // Phase 2 - option B: the below view honors CAI fog. In CAI's
                // default mode this is free (vanilla fog carries CAI fog); only
                // overlay mode (UseVanillaUnexplored off) needs our explicit check.
                Note("Below-view fog: option B is automatic in CAI default mode; overlay-mode explicit check "
                    + (ABCombatAICompat.OverlayFogMode ? "ACTIVE" : "not needed right now") + ".");
                Func<IntVec3, bool> caiFog = ABCombatAICompat.GetOverlayFogChecker(surface);
                Check("overlay fog checker matches mode (non-null iff overlay mode)",
                    (caiFog != null) == ABCombatAICompat.OverlayFogMode);
                if (caiFog != null)
                {
                    bool threw = false;
                    try { caiFog(b); }
                    catch { threw = true; }
                    Check("overlay fog checker evaluates a cell without throwing", !threw);
                }

                Note("Phase 1+2 seams verified. Turn on CAI Fog Of War, view the sky over a hole, "
                    + "and (once the vision pass is wired in Phase 3) the surface below should stay lit under a colonist.");
            }
            catch (Exception e)
            {
                fail++;
                sb.AppendLine("  EXCEPTION during self-test:\n" + e);
            }

            Report("CAI fog self-test", sb, pass, fail);
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

    }
}
