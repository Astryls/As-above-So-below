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
    // Partial of ABDevTools (levels diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
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

    }
}
