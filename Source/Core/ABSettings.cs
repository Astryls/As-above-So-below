using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Mod settings.
    ///
    /// REWRITTEN FOR V2. The V1 version carried 79 fields and a seven-tab window covering
    /// pocket-level generation, vertical links, per-level biomes, incident policy and a
    /// dozen compat bridges - all of which described a model V2 does not have. Rather than
    /// prune it field by field, this is the settings surface V2 actually reads, which the
    /// compiler can confirm: nine fields, every one of them consumed by a live V2 call site.
    ///
    /// Dropped settings simply stop being scribed. Old saves and old config files keep their
    /// unknown keys in Config/Mod_*.xml and RimWorld ignores them, so downgrading is lossy
    /// but nothing errors on load.
    /// </summary>
    public class ABSettings : ModSettings
    {
        // ---- diagnostics ---------------------------------------------------

        /// <summary>Chatty logging through ABLog.Dev. Off by default.</summary>
        public bool verboseLogging;

        // ---- performance ---------------------------------------------------

        /// <summary>Lift the total-cell budget on colony maps. See ABMapSizeLimit for why
        /// the budget exists: 1.6's path grid is an IJobParallelFor over EVERY cell, and a
        /// banded map is every level plus gutters tall, so the stacked total is what the
        /// pathfinder pays.</summary>
        public bool unclampMapSize;

        // ---- level plan ----------------------------------------------------
        //
        // Chosen per colony on the advanced-config screen; stored here so the choice
        // persists as the default for the next colony. The GENERATED layout is scribed on
        // ABBandMap, so an existing save never depends on these.

        /// <summary>Levels above the surface (0-3).</summary>
        public int upperLevels = 1;

        /// <summary>Levels below the surface (0-3).</summary>
        public int lowerLevels = 1;

        // ---- camera ---------------------------------------------------------
        //
        // Nothing here any more, deliberately. `freeCameraPan` was a binary between two
        // unsatisfying extremes that asked the player to reason about rendering internals.
        // How the view behaves at the edge of a level is an authored, per-level decision:
        // see ABCameraBounds for the baked table and the in-game calibration tool that
        // produced its numbers.

        // ---- sky band generation -------------------------------------------

        /// <summary>Meadow-Perlin peaks (varied ledges and plateaus) rather than a plain
        /// projection of the solid mass below.</summary>
        public bool naturalPeaks = true;

        /// <summary>Sky band inherits the surface tile's biome for plants and temperature.</summary>
        public bool skyBiomeInherit = true;

        /// <summary>Share of peak surface that is soil rather than bare rock.</summary>
        public float peakSoilFraction = 0.15f;

        /// <summary>Rock-vs-meadow noise threshold.</summary>
        public float peakMeadowCutoff = 0.60f;

        /// <summary>Meadow noise frequency, i.e. feature size.</summary>
        public float peakMeadowScale = 0.024f;

        /// <summary>Deepest walkable edge band, in cells.</summary>
        public int peakTerraceMax = 4;

        /// <summary>Multiplier on the plateau's starting vegetation. 0 leaves the summit
        /// bare (the pre-2026-07-28 behaviour, which was not a choice so much as a
        /// missing feature).</summary>
        public float skyVegetationDensity = 1f;

        // ---- basement generation -------------------------------------------

        /// <summary>Ore lumps per 10k basement cells.</summary>
        public float basementOreDensity = 6f;

        /// <summary>Which Biomes! Caverns biome the basement is carved as: a defName,
        /// "Random" for a weighted pick, or "None" for plain solid rock. Ignored entirely
        /// when Biomes! Caverns is not loaded.</summary>
        public string basementBiomeChoice = BiomesCavernsCompat.RandomChoice;

        /// <summary>How much of the basement the tunnel network opens up.</summary>
        public float cavernOpenness = 0.3f;

        /// <summary>Chance per worm step of widening into a chamber.</summary>
        public float cavernChamberFreq = 0.02f;

        /// <summary>How many passes of Biomes! Caverns' stalagmite scatterer to run.</summary>
        public float cavernFormations = 1f;

        // --------------------------------------------------------------------

        /// <summary>Display label for the current basement-biome choice, resolving a stored
        /// defName to its in-game label so the button never shows a raw defName.</summary>
        private string LabelForBiomeChoice()
        {
            if (basementBiomeChoice == BiomesCavernsCompat.NoneChoice)
            {
                return "AB_BasementBiomeNone".Translate();
            }
            if (string.IsNullOrEmpty(basementBiomeChoice)
                || basementBiomeChoice == BiomesCavernsCompat.RandomChoice)
            {
                return "AB_BasementBiomeRandom".Translate();
            }
            BiomeDef def = DefDatabase<BiomeDef>.GetNamedSilentFail(basementBiomeChoice);
            return def != null ? def.LabelCap.ToString() : basementBiomeChoice;
        }

        private static readonly Color WarnRed = new Color(1f, 0.25f, 0.25f);
        private static readonly Color NoteDim = new Color(1f, 1f, 1f, 0.62f);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref unclampMapSize, "unclampMapSize", false);
            Scribe_Values.Look(ref upperLevels, "upperLevels", 1);
            Scribe_Values.Look(ref lowerLevels, "lowerLevels", 1);
            Scribe_Values.Look(ref naturalPeaks, "naturalPeaks", true);
            Scribe_Values.Look(ref skyBiomeInherit, "skyBiomeInherit", true);
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakMeadowCutoff, "peakMeadowCutoff", 0.60f);
            Scribe_Values.Look(ref peakMeadowScale, "peakMeadowScale", 0.024f);
            Scribe_Values.Look(ref peakTerraceMax, "peakTerraceMax", 4);
            Scribe_Values.Look(ref skyVegetationDensity, "skyVegetationDensity", 1f);
            Scribe_Values.Look(ref basementOreDensity, "basementOreDensity", 6f);
            Scribe_Values.Look(ref basementBiomeChoice, "basementBiomeChoice",
                BiomesCavernsCompat.RandomChoice);
            Scribe_Values.Look(ref cavernOpenness, "cavernOpenness", 0.3f);
            Scribe_Values.Look(ref cavernChamberFreq, "cavernChamberFreq", 0.02f);
            Scribe_Values.Look(ref cavernFormations, "cavernFormations", 1f);
        }

        /// <summary>The map-size cap banner. Kept at the very top rather than filed under a
        /// heading, because it changes how every new colony generates and is the single
        /// setting most likely to be blamed for bad performance if a player flips it and
        /// then forgets they did.</summary>
        private void DrawMapSizeCap(Listing_Standard list)
        {
            bool before = unclampMapSize;
            list.CheckboxLabeled(
                "AB_UnclampMapSize".Translate(ABMapSizeLimit.CellBudget.ToString("N0")),
                ref unclampMapSize,
                "AB_UnclampMapSizeTip".Translate(ABMapSizeLimit.CellBudget.ToString("N0")));

            if (unclampMapSize)
            {
                Color old = GUI.color;
                GUI.color = WarnRed;
                list.Label("AB_UnclampMapSizeWarning".Translate());
                GUI.color = old;
            }
            if (before != unclampMapSize)
            {
                Write();
            }

            // The authoritative place to choose levels is the advanced-config screen while
            // starting a colony; this row is the persisted default that screen opens with,
            // and the only way to see it outside world creation.
            list.Label("AB_LevelsDefault".Translate(upperLevels, lowerLevels,
                ABMapSizeLimit.BandCount));
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            DrawMapSizeCap(list);
            list.GapLine();

            Text.Font = GameFont.Medium;
            list.Label("AB_SkyHeading".Translate());
            Text.Font = GameFont.Small;

            list.CheckboxLabeled("AB_NaturalPeaks".Translate(), ref naturalPeaks,
                "AB_NaturalPeaksTip".Translate());
            list.CheckboxLabeled("AB_SkyBiomeInherit".Translate(), ref skyBiomeInherit,
                "AB_SkyBiomeInheritTip".Translate());

            if (naturalPeaks)
            {
                list.Label("AB_PeakSoilFraction".Translate(peakSoilFraction.ToStringPercent()));
                peakSoilFraction = list.Slider(peakSoilFraction, 0f, 0.5f);

                list.Label("AB_PeakMeadowCutoff".Translate(peakMeadowCutoff.ToString("0.00")));
                peakMeadowCutoff = list.Slider(peakMeadowCutoff, 0.2f, 0.9f);

                list.Label("AB_PeakMeadowScale".Translate(peakMeadowScale.ToString("0.000")));
                peakMeadowScale = list.Slider(peakMeadowScale, 0.005f, 0.08f);

                list.Label("AB_PeakTerraceMax".Translate(peakTerraceMax));
                peakTerraceMax = Mathf.RoundToInt(list.Slider(peakTerraceMax, 1f, 12f));
            }

            list.Label("AB_SkyVegetationDensity".Translate(skyVegetationDensity.ToString("0.0")));
            skyVegetationDensity = list.Slider(skyVegetationDensity, 0f, 2f);

            list.GapLine();
            Text.Font = GameFont.Medium;
            list.Label("AB_BasementHeading".Translate());
            Text.Font = GameFont.Small;

            list.Label("AB_BasementOreDensity".Translate(basementOreDensity.ToString("0.#")));
            basementOreDensity = list.Slider(basementOreDensity, 0f, 12f);

            // Cavern options only exist when Biomes! Caverns is actually loaded - showing
            // dead sliders would imply the mod does something it cannot.
            if (BiomesCavernsCompat.Active)
            {
                list.Gap(6f);
                if (list.ButtonText("AB_BasementBiome".Translate(LabelForBiomeChoice())))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>
                    {
                        new FloatMenuOption("AB_BasementBiomeRandom".Translate(),
                            () => basementBiomeChoice = BiomesCavernsCompat.RandomChoice),
                        new FloatMenuOption("AB_BasementBiomeNone".Translate(),
                            () => basementBiomeChoice = BiomesCavernsCompat.NoneChoice)
                    };
                    foreach (BiomeDef b in BiomesCavernsCompat.CavernBiomes())
                    {
                        BiomeDef local = b;
                        opts.Add(new FloatMenuOption(local.LabelCap,
                            () => basementBiomeChoice = local.defName));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }

                if (basementBiomeChoice != BiomesCavernsCompat.NoneChoice)
                {
                    list.Label("AB_CavernOpenness".Translate(cavernOpenness.ToString("0.00")));
                    cavernOpenness = list.Slider(cavernOpenness, 0.1f, 0.6f);

                    list.Label("AB_CavernChamberFreq".Translate(cavernChamberFreq.ToString("0.000")));
                    cavernChamberFreq = list.Slider(cavernChamberFreq, 0.01f, 0.05f);

                    list.Label("AB_CavernFormations".Translate(cavernFormations.ToString("0.0")));
                    cavernFormations = list.Slider(cavernFormations, 0f, 2f);
                }
            }

            list.GapLine();
            list.CheckboxLabeled("AB_VerboseLogging".Translate(), ref verboseLogging,
                "AB_VerboseLoggingTip".Translate());

            Color oldColor = GUI.color;
            GUI.color = NoteDim;
            list.Label("AB_SettingsGenerationNote".Translate());
            GUI.color = oldColor;

            list.End();
        }
    }
}
