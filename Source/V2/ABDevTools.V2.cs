using System;
using System.Text;
using LudeonTK;
using RimWorld;
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
                    + " gutter=" + ABBandMap.Gutter + " slot=" + b.Slot + " surfaceBand=" + b.surfaceBand);
                for (int i = 0; i < b.bandCount; i++)
                {
                    sb.AppendLine("  band " + i + " (level " + (i - b.surfaceBand) + ") rect=" + b.RectOfBand(i)
                        + " open=" + b.IsOpen(i));
                }
                sb.AppendLine("current view band: " + ABBandView.CurrentBand(map)
                    + " (level " + ABBandView.CurrentLevel(map) + ")");
                sb.AppendLine("wormhole pairs: " + ABWormhole.PairCount(map));
            }
            Log.Warning(ABLog.Tag + " V2 band info:\n" + sb);
            Messages.Message("AB2: band info written to log.", MessageTypeDefOf.TaskCompletion, false);
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
