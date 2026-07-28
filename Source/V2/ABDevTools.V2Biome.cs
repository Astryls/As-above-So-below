using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Diagnostics for per-band biome and vegetation.
    ///
    /// The discriminating question this answers is deliberately narrow: does
    /// <c>map.BiomeAt(cell)</c> - the call vanilla's own plant and animal spawners make -
    /// actually return the band biome? Everything downstream (which plants regrow, at what
    /// density, which animals wander in) follows from that one answer, so it is reported
    /// as a live round-trip through the real vanilla call rather than by asking
    /// ABBandEnv.BiomeOf what it thinks. If the patch ever fails to apply, the two columns
    /// disagree and the report says so outright instead of leaving it to be inferred.
    /// </summary>
    public static class ABDevToolsV2Biome
    {
        [DebugAction("As above", "AB2: biome report", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2BiomeReport()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("map biome (world tile): " + (map.Biome?.defName ?? "null"));
            sb.AppendLine("Biomes! Caverns loaded: " + BiomesCavernsCompat.Active
                + "  |  setting: " + (ABMod.Settings?.basementBiomeChoice ?? "?"));

            ABBandMap b = ABBands.CompOf(map);
            if (b == null || !b.Banded)
            {
                sb.AppendLine("NOT BANDED - nothing per-band to report.");
                Log.Warning(ABLog.Tag + " V2 biome report:\n" + sb);
                return;
            }
            sb.AppendLine("scribed basementBiome: " + (b.basementBiome?.defName ?? "null (plain solid rock)"));
            sb.AppendLine();

            bool anyMismatch = false;
            for (int band = 0; band < b.bandCount; band++)
            {
                CellRect rect = b.RectOfBand(band);
                IntVec3 probe = rect.CenterCell;

                // The round trip that matters: vanilla's own accessor.
                BiomeDef viaVanilla = map.BiomeAt(probe);
                BiomeDef viaOurs = ABBandEnv.BiomeOf(map, probe);
                bool match = viaVanilla == viaOurs;
                anyMismatch |= !match;

                // Census: what actually lives on this band right now.
                int plants = 0;
                int animals = 0;
                float fertSum = 0f;
                int sampled = 0;
                foreach (IntVec3 c in rect)
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    if (c.GetPlant(map) != null)
                    {
                        plants++;
                    }
                    // Sample fertility on a coarse lattice; a full sweep of a 200x256 band
                    // per press is wasteful and adds nothing.
                    if ((c.x % 8 == 0) && (c.z % 8 == 0))
                    {
                        fertSum += map.fertilityGrid.FertilityAt(c);
                        sampled++;
                    }
                }
                List<Pawn> pawns = map.mapPawns.AllPawnsSpawned as List<Pawn>
                    ?? new List<Pawn>(map.mapPawns.AllPawnsSpawned);
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p?.RaceProps != null && p.RaceProps.Animal && rect.Contains(p.Position))
                    {
                        animals++;
                    }
                }

                sb.AppendLine("band " + band + " (level " + (band - b.surfaceBand) + ") " + rect);
                sb.AppendLine("   BiomeAt()  = " + (viaVanilla?.defName ?? "null")
                    + (match ? "   [patch live]" : "   <-- MISMATCH, patch NOT applied"));
                sb.AppendLine("   BiomeOf()  = " + (viaOurs?.defName ?? "null")
                    + "   plantDensity=" + (viaOurs?.plantDensity.ToString("0.00") ?? "-"));
                sb.AppendLine("   plants=" + plants + "  animals=" + animals
                    + "  meanFertility=" + (sampled > 0 ? (fertSum / sampled).ToString("0.000") : "-"));
            }

            if (anyMismatch)
            {
                sb.AppendLine();
                sb.AppendLine("AT LEAST ONE BAND MISMATCHED. BiomeAt is the call vanilla's plant and");
                sb.AppendLine("animal spawners use, so a mismatch means regrowth is still running on the");
                sb.AppendLine("surface biome. Check that Patch_MixedBiome_ABBandBiomeAt applied at boot.");
            }

            // One self-contained message: separate Log calls from one helper share a stack
            // signature and get folded together by the error monitor.
            Log.Warning(ABLog.Tag + " V2 biome report:\n" + sb);
            Messages.Message("AB2: biome report written to log.", MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
