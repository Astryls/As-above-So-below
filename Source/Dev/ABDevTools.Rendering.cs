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
    // Partial of ABDevTools (rendering diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
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

    }
}
