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
    public static partial class ABDevTools
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
