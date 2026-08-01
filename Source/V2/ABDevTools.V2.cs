using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>V2 dev tools: inspect the band layout, force bands open, and place
    /// stairwells without waiting for a construction job.</summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: band info", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2BandInfo()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            ABBandMap b = ABBands.CompOf(map);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("map size: " + map.Size);
            if (b == null || !b.Banded)
            {
                sb.AppendLine("NOT BANDED (ordinary vanilla map).");
                sb.AppendLine("V2 enabled: " + ABV2.Enabled + " - banding applies to newly generated player colony maps.");
            }
            else
            {
                sb.AppendLine("bandCount=" + b.bandCount + " bandHeight=" + b.bandHeight
                    + " gutter=" + b.GutterRows + " slot=" + b.Slot
                    + " (aligned to " + ABBandMap.SlotAlignment + ")"
                    + " surfaceBand=" + b.surfaceBand);
                for (int i = 0; i < b.bandCount; i++)
                {
                    sb.AppendLine("  band " + i + " (level " + (i - b.surfaceBand) + ") rect=" + b.RectOfBand(i)
                        + " open=" + b.IsOpen(i));
                }
                sb.AppendLine("current view band: " + ABBandView.CurrentBand(map)
                    + " (level " + ABBandView.CurrentLevel(map) + ")");
                sb.Append(ABWormhole.DebugDump(map));
            }
            Log.Warning(ABLog.Tag + " V2 band info:\n" + sb);
            Messages.Message("AB2: band info written to log.", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Per-pawn verdict from ONE below-pawn draw pass: drawn, or skipped and
        /// why. Run it from the band ABOVE the pawn you cannot see - if the pawn is listed
        /// DRAW then the masking is fine and the problem is draw order or altitude; if it is
        /// listed SKIP the reason names the filter that rejected it.</summary>
        [DebugAction("As above", "AB2: below pawn report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2BelowPawnReport()
        {
            ABBelowDynamicDraw.ReportNextPass = true;
            Messages.Message("AB2: below pawn report armed for the next frame - see log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>Point-in-time view of every in-flight transit. Use when pawns or animals
        /// look stuck at a stairwell: an ageing record whose distToNear is not shrinking is a
        /// stuck pawn, a young record with a large distance is one still walking.</summary>
        [DebugAction("As above", "AB2: transit health", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2TransitHealth()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            Log.Warning(ABLog.Tag + " V2 transit health:\n" + ABWormholePather.HealthReport(map));
            Messages.Message("AB2: transit health written to log.", MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: toggle transit logging", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleTransitLog()
        {
            ABV2Debug.LogTransit = !ABV2Debug.LogTransit;
            Messages.Message("AB2: transit logging " + (ABV2Debug.LogTransit ? "ON" : "OFF") + ".",
                MessageTypeDefOf.TaskCompletion, false);
            Log.Warning(ABLog.Tag + " transit logging " + (ABV2Debug.LogTransit ? "ON" : "OFF"));
        }

        [DebugAction("As above", "AB2: toggle combat logging", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleCombatLog()
        {
            ABV2Debug.LogCombat = !ABV2Debug.LogCombat;
            Messages.Message("AB2: combat logging " + (ABV2Debug.LogCombat ? "ON" : "OFF") + ".",
                MessageTypeDefOf.TaskCompletion, false);
            Log.Warning(ABLog.Tag + " combat logging " + (ABV2Debug.LogCombat ? "ON" : "OFF"));
        }

        /// <summary>A/B for the retracting rock field. OFF = the historical full-cell opaque
        /// quad, which squares off the mass silhouette past its own black outline once per
        /// level; ON = the fill eroded to the tile's own link rule so it hides under the art.
        /// Flips all three emitters at once (cap in-band, cross-level, below-terrain base) -
        /// they are one look and splitting the switch would invite half-fixing it.</summary>
        [DebugAction("As above", "AB2: toggle mass field fade", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleMassFieldFade()
        {
            SectionLayer_ABMountainCap.MassFieldFadeEnabled =
                !SectionLayer_ABMountainCap.MassFieldFadeEnabled;
            Messages.Message(
                "AB2: mass field fade "
                    + (SectionLayer_ABMountainCap.MassFieldFadeEnabled ? "ON (retracts under the outline)" : "OFF (full-cell square)"),
                MessageTypeDefOf.TaskCompletion, false);
            V2ApplyBisect();
        }

        [DebugAction("As above", "AB2: bisect - toggle below terrain", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowTerrain()
        {
            ABV2Debug.DrawBelowTerrain = !ABV2Debug.DrawBelowTerrain;
            V2ApplyBisect();
        }

        [DebugAction("As above", "AB2: bisect - toggle below things", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowThings()
        {
            ABV2Debug.DrawBelowThings = !ABV2Debug.DrawBelowThings;
            V2ApplyBisect();
        }

        [DebugAction("As above", "AB2: bisect - toggle below air mask", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowAirMask()
        {
            ABV2Debug.DrawBelowAirMask = !ABV2Debug.DrawBelowAirMask;
            V2ApplyBisect();
        }

        [DebugAction("As above", "AB2: bisect - toggle below lighting", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowLighting()
        {
            ABV2Debug.DrawBelowLighting = !ABV2Debug.DrawBelowLighting;
            V2ApplyBisect();
        }

        [DebugAction("As above", "AB2: bisect - toggle below water depth", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowWater()
        {
            ABV2Debug.DrawBelowWater = !ABV2Debug.DrawBelowWater;
            V2ApplyBisect();
        }

        /// <summary>Everything the water systems know, per band. Run it BEFORE theorising
        /// about water: the per-band census separates "the river never generated" from "the
        /// river generated three levels up and was carved away", and the flow/global readout
        /// separates a missing depth pass from a stretched shader UV.</summary>
        [DebugAction("As above", "AB2: water report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2WaterReport()
        {
            Log.Warning(ABLog.Tag + " V2 water report:\n" + ABWaterBand.Report(Find.CurrentMap));
            Messages.Message("AB2: water report written to log.", MessageTypeDefOf.TaskCompletion, false);
        }

        private static void V2ApplyBisect()
        {
            Map map = Find.CurrentMap;
            map?.mapDrawer?.RegenerateEverythingNow();
            Messages.Message("AB2 bisect: " + ABV2Debug.StateSummary(),
                MessageTypeDefOf.TaskCompletion, false);
            Log.Warning(ABLog.Tag + " AB2 bisect: " + ABV2Debug.StateSummary());
        }

        [DebugAction("As above", "AB2: below layer report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2BelowLayerReport()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("cell " + c + " band=" + ABBands.BandOf(map, c)
                + " terrain=" + map.terrainGrid.TerrainAt(c).defName);
            sb.AppendLine("DebugViewSettings.drawShadows=" + DebugViewSettings.drawShadows);

            Section target = null;
            foreach (Section sec in AllSectionsOf(map))
            {
                if (sec.CellRect.Contains(c))
                {
                    target = sec;
                    break;
                }
            }
            if (target == null)
            {
                sb.AppendLine("no section found for that cell");
                Log.Warning(ABLog.Tag + " V2 below layer report:\n" + sb);
                return;
            }
            sb.AppendLine("section botLeft=" + target.botLeft);
            foreach (SectionLayer layer in SectionLayersOf(target))
            {
                string name = layer.GetType().Name;
                // "SectionLayer_AB", NOT "SectionLayer_ABBelow".
                //
                // The narrower prefix silently excluded SectionLayer_ABMountainCap from
                // every report this tool has ever produced - and the cap is one of the
                // layers that draws INTO the below view (§6a), so the instrument was blind
                // in exactly the place the below view is most likely to be wrong. A report
                // that omits a layer reads identically to a report proving that layer
                // emitted nothing, which is worse than no report at all: it invites the
                // conclusion that the cap is innocent.
                //
                // Found while diagnosing MO's golem formations rendering as four Better
                // Mountains rock sprites from above; the first report came back with no cap
                // line and nearly closed that hypothesis.
                bool ours = name.StartsWith("SectionLayer_AB");
                bool shadowy = name.Contains("Shadow");
                if (!ours && !shadowy)
                {
                    continue;
                }
                sb.AppendLine(name + "  visible=" + layer.Visible
                    + "  subMeshes=" + layer.subMeshes.Count);
                for (int i = 0; i < layer.subMeshes.Count; i++)
                {
                    LayerSubMesh sm = layer.subMeshes[i];
                    string mat = sm.material != null ? sm.material.name : "null";
                    sb.AppendLine("    [" + i + "] verts=" + sm.verts.Count
                        + " tris=" + sm.tris.Count
                        + " finalized=" + sm.finalized
                        + " disabled=" + sm.disabled
                        + " queue=" + (sm.material != null ? sm.material.renderQueue.ToString() : "?")
                        + " mat=" + mat);
                }
            }
            Log.Warning(ABLog.Tag + " V2 below layer report:\n" + sb);
            Messages.Message("AB2: below layer report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: lighting report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2LightingReport()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("banded: " + ABBands.Banded(map));
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                Log.Warning(ABLog.Tag + " V2 lighting report:\n" + sb);
                return;
            }
            ABBandMap b = ABBands.CompOf(map);
            sb.AppendLine("cell " + c + " band=" + ABBands.BandOf(map, c)
                + " level=" + ABBands.LevelOf(map, c)
                + " terrain=" + map.terrainGrid.TerrainAt(c).defName);
            sb.AppendLine("  glow here: " + map.glowGrid.VisualGlowAt(c));
            if (b != null && b.Banded)
            {
                IntVec3 below = new IntVec3(c.x, 0, c.z - b.Slot);
                if (below.InBounds(map))
                {
                    sb.AppendLine("  below " + below + " terrain="
                        + map.terrainGrid.TerrainAt(below).defName
                        + " fogged=" + map.fogGrid.IsFogged(below));
                    sb.AppendLine("  glow below: " + map.glowGrid.VisualGlowAt(below));
                }
            }
            // The decisive number: if vanilla's overlay is still Visible on a banded map,
            // below content is being darkened twice.
            int vanillaVisible = 0;
            int oursVisible = 0;
            foreach (Section sec in AllSectionsOf(map))
            {
                foreach (SectionLayer layer in SectionLayersOf(sec))
                {
                    if (layer is SectionLayer_LightingOverlay && layer.Visible)
                    {
                        vanillaVisible++;
                    }
                    else if (layer is SectionLayer_ABBelowLighting && layer.Visible)
                    {
                        oursVisible++;
                    }
                }
            }
            sb.AppendLine("vanilla lighting layers still visible: " + vanillaVisible
                + "  (MUST be 0 on a banded map)");
            sb.AppendLine("AB below-lighting layers visible: " + oursVisible);
            Log.Warning(ABLog.Tag + " V2 lighting report:\n" + sb);
            Messages.Message("AB2: lighting report written to log.", MessageTypeDefOf.TaskCompletion, false);
        }

        private static IEnumerable<Section> AllSectionsOf(Map map)
        {
            Section[,] arr = (Section[,])AccessTools.Field(typeof(MapDrawer), "sections").GetValue(map.mapDrawer);
            if (arr == null)
            {
                yield break;
            }
            foreach (Section s in arr)
            {
                if (s != null)
                {
                    yield return s;
                }
            }
        }

        private static List<SectionLayer> SectionLayersOf(Section sec)
        {
            return (List<SectionLayer>)AccessTools.Field(typeof(Section), "layers").GetValue(sec);
        }

        /// <summary>Opens the camera-bounds calibration readout. The window UNLOCKS the
        /// band clamp while it is open, so the camera can be pushed past the currently
        /// baked limits to find better ones; the numbers it reports get baked into
        /// ABCameraBounds.For(level).</summary>
        /// <summary>Bisect for the drafted group-move bug: three drafted pawns ordered to
        /// one cell, only one moves. Flip these ONE AT A TIME and re-test - whichever
        /// restores group movement owns the bug.</summary>
        [DebugAction("As above", "AB2: bisect - toggle click-through", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleClickThrough()
        {
            ABBelowClickThrough.Enabled = !ABBelowClickThrough.Enabled;
            Messages.Message("AB2: below click-through / select-through "
                + (ABBelowClickThrough.Enabled ? "ON" : "OFF"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: bisect - toggle below multi-select", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleBelowMultiSelect()
        {
            Patch_ThingSelectionUtility_ABBelow.Enabled = !Patch_ThingSelectionUtility_ABBelow.Enabled;
            Messages.Message("AB2: below multi-select "
                + (Patch_ThingSelectionUtility_ABBelow.Enabled ? "ON" : "OFF"),
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: camera calibration", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2CameraCalibration()
        {
            Find.WindowStack.Add(new Dialog_ABCameraCalibration());
        }

        [DebugAction("As above", "AB2: open all bands", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2OpenAllBands()
        {
            Map map = Find.CurrentMap;
            ABBandMap b = ABBands.CompOf(map);
            if (b == null || !b.Banded)
            {
                Messages.Message("AB2: this map is not banded.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            for (int i = 0; i < b.bandCount; i++)
            {
                b.Open(i);
                foreach (IntVec3 c in b.RectOfBand(i))
                {
                    if (c.InBounds(map))
                    {
                        map.fogGrid.Unfog(c);
                    }
                }
            }
            Messages.Message("AB2: all " + b.bandCount + " bands opened and unfogged.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: place stairs down here", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2PlaceStairsDown()
        {
            V2PlaceStairs("AB2_StairsDown");
        }

        [DebugAction("As above", "AB2: place stairs up here", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2PlaceStairsUp()
        {
            V2PlaceStairs("AB2_StairsUp");
        }

        private static void V2PlaceStairs(string defName)
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            IntVec3 cell = UI.MouseCell();
            if (!cell.InBounds(map))
            {
                return;
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                Messages.Message("AB2: " + defName + " def missing.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            try
            {
                ClearCell(map, cell);
                Thing t = ThingMaker.MakeThing(def, GenStuff.DefaultStuffFor(def));
                Thing spawned = GenSpawn.Spawn(t, cell, map, WipeMode.Vanish);
                spawned.SetFaction(Faction.OfPlayer);
                Messages.Message("AB2: placed " + def.label + " at " + cell + ".",
                    MessageTypeDefOf.TaskCompletion, false);
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " V2: stairs placement failed: " + e);
            }
        }
    }
}
