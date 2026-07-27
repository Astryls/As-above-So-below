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
    // Partial of ABDevTools (threats diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
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

    }
}
