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

        /// <summary>Lift the 200x200 colony cap. See ABMapSizeLimit for why the cap exists:
        /// 1.6's path grid is an IJobParallelFor over EVERY cell, and a banded map is three
        /// bands plus gutters tall, so width is paid three times over.</summary>
        public bool unclampMapSize;

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

        // ---- basement generation -------------------------------------------

        /// <summary>Ore lumps per 10k basement cells.</summary>
        public float basementOreDensity = 6f;

        // --------------------------------------------------------------------

        private static readonly Color WarnRed = new Color(1f, 0.25f, 0.25f);
        private static readonly Color NoteDim = new Color(1f, 1f, 1f, 0.62f);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref unclampMapSize, "unclampMapSize", false);
            Scribe_Values.Look(ref naturalPeaks, "naturalPeaks", true);
            Scribe_Values.Look(ref skyBiomeInherit, "skyBiomeInherit", true);
            Scribe_Values.Look(ref peakSoilFraction, "peakSoilFraction", 0.15f);
            Scribe_Values.Look(ref peakMeadowCutoff, "peakMeadowCutoff", 0.60f);
            Scribe_Values.Look(ref peakMeadowScale, "peakMeadowScale", 0.024f);
            Scribe_Values.Look(ref peakTerraceMax, "peakTerraceMax", 4);
            Scribe_Values.Look(ref basementOreDensity, "basementOreDensity", 6f);
        }

        /// <summary>The map-size cap banner. Kept at the very top rather than filed under a
        /// heading, because it changes how every new colony generates and is the single
        /// setting most likely to be blamed for bad performance if a player flips it and
        /// then forgets they did.</summary>
        private void DrawMapSizeCap(Listing_Standard list)
        {
            bool before = unclampMapSize;
            list.CheckboxLabeled("AB_UnclampMapSize".Translate(ABMapSizeLimit.Cap),
                ref unclampMapSize, "AB_UnclampMapSizeTip".Translate(ABMapSizeLimit.Cap));

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

            list.GapLine();
            Text.Font = GameFont.Medium;
            list.Label("AB_BasementHeading".Translate());
            Text.Font = GameFont.Small;

            list.Label("AB_BasementOreDensity".Translate(basementOreDensity.ToString("0.#")));
            basementOreDensity = list.Slider(basementOreDensity, 0f, 12f);

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
