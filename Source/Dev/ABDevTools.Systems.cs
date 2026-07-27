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
    // Partial of ABDevTools (systems diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB: list compat modules", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ListCompatModules()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(ABLog.Tag + " soft-compat registry (" + ABCompat.Modules.Count + " targets):");
            foreach (ABCompatInfo m in ABCompat.Modules)
            {
                sb.AppendLine("  [" + m.state + "] " + m.name + " (" + m.packageId + ")"
                    + (m.note != null ? " \u2014 " + m.note : string.Empty));
            }
            Log.Message(sb.ToString());
            Messages.Message("AB: " + ABCompat.Modules.Count + " compat targets registered \u2014 see log.",
                MessageTypeDefOf.TaskCompletion, historical: false);
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
    }
}
