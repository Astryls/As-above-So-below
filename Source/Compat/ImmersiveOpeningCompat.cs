using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Immersive Opening (ferny.ImmersiveOpening): aim its opening
    /// cinematic at a LEVEL instead of at the middle of the map.
    ///
    /// WHAT IT DOES. Immersive Opening prefixes ScenPart_GameStartDialog.PostGameStart and,
    /// instead of the scenario text box, opens a fullscreen window that pans the camera
    /// around the map while showing the scenario text one sentence at a time. Everything it
    /// aims at hangs off ONE cached cell, taken in PreOpen:
    ///     mapCenter = Find.CurrentMap.Center;
    /// and each sentence then pans between two cells picked as
    ///     mapCenter + new IntVec3(Rand.Range(-30, 30), 0, Rand.Range(-30, 30))
    /// clamped with ClampInsideMap, at rootSize 15 (CameraDriver.MinAltitude). On close it
    /// parks the camera back on that same cached cell at rootSize 24.
    ///
    /// ⚠ WHY THAT CANNOT WORK ON A BANDED MAP. `map.Center` is the centre of the whole
    /// STACK, not of a level. On a 190x768 four-band map it is z=384 - which is a gutter
    /// row on a band seam, and belongs to whichever band happens to sit in the middle of
    /// the column rather than to the one the colony landed on. So out of the box the
    /// cinematic tours a sky band or a sealed basement, the +-30 z jitter walks it through
    /// the impassable seam rows, and `ClampInsideMap` cannot save it because it clamps to
    /// the whole map (rule 24 - the TARGET has to be clamped too, not just the map). On top
    /// of that our own two camera patches then fight it: the view-rect clip draws only the
    /// viewed band, so every out-of-band frame is curtain, and the Priority.Last clamp in
    /// Patch_CameraDriver_ABClampToBand drags rootPos.z back inside the band every frame,
    /// so the pan sticks against the band edge instead of moving.
    ///
    /// ⚠ THE FIX IS AT SELECTION, NOT IN THE CLAMP (rule 1). Rather than standing our clamp
    /// down for the cinematic - which would just let it wander into the gutter - we
    /// overwrite the cached `mapCenter` with a cell that makes every position Immersive
    /// Opening can subsequently derive band-legal BY CONSTRUCTION. That needs an inset of
    ///     30 (their jitter) + 15 (half the viewport at their rootSize) = 45 rows
    /// from both z edges of the band, and then their own arithmetic can never produce a
    /// cell, or a visible frame, outside the level. Our clamp becomes a no-op rather than
    /// an opponent, and none of their code has to be transpiled.
    ///
    /// ⚠ FOG DECIDES WHICH BANDS ARE WORTH TOURING. ABBandedGeneration refogs every band
    /// BELOW the surface wholesale (basements are solid rock, revealed by mining, like a
    /// vanilla mountain) and deliberately leaves the sky bands unfogged. A tour of a sealed
    /// basement is therefore a black screen, and ABBandView.SetBand refuses it anyway
    /// (looking down needs stairs). The stack tour walks only bands that are actually
    /// viewable - it asks IsOpen rather than assuming, so a basement that IS open gets
    /// toured - and always ends on the colony's own level.
    ///
    /// Reflection-only, same rules as every bridge in this folder: no foreign type appears
    /// in any signature (Harmony hands us `object __instance`), the shape is resolved once
    /// and the bridge is inert forever if any member is missing.
    ///
    /// Member shape as of Immersive Opening 1.6 (verified against their shipped Source/ AND
    /// against the built DLL - rule 17):
    ///   ImmersiveOpening.Window_ImmersiveOpening  - public class, extends Verse.Window
    ///     .mapCenter           - private instance FIELD, IntVec3
    ///     .currentSentenceIndex- private instance FIELD, int (starts at -1, incremented at
    ///                            the TOP of NextSentence)
    ///     .sentences           - private instance FIELD, List&lt;string&gt;
    ///     .PreOpen()           - public override, void, no args
    ///     .NextSentence()      - private instance METHOD, void, no args
    ///     .PostClose()         - public override, void, no args
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ImmersiveOpeningCompat
    {
        /// <summary>Their per-sentence jitter: `Rand.Range(-30, 30)` on both axes.</summary>
        private const int JitterCells = 30;

        /// <summary>Half the viewport height at their pan zoom - they pan at rootSize 15
        /// (CameraDriver.MinAltitude), and rootSize IS the half-height in cells. Without
        /// this term the CENTRE would stay in the band while the top or bottom of the
        /// screen still showed the curtain.</summary>
        private const int HalfViewCells = 15;

        /// <summary>Rows of a band that are unusable as an anchor, per edge.</summary>
        private const int ZInset = JitterCells + HalfViewCells;

        private static bool resolved;

        private static bool present;

        private static FieldInfo mapCenterField;

        private static FieldInfo sentenceIndexField;

        private static FieldInfo sentencesField;

        /// <summary>Bands to visit, in order, for the current cinematic. Built once in
        /// PreOpen: rebuilding it per sentence would let a band opened mid-cinematic (it
        /// cannot happen today, but nothing enforces that) renumber the tour halfway
        /// through and skip or repeat a level.</summary>
        private static List<int> tour;

        /// <summary>The colony cell the cinematic was aimed at, cached so the final
        /// "click to start" rest position and the hand-over agree.</summary>
        private static IntVec3 colonyAnchor = IntVec3.Invalid;

        /// <summary>True while one of their windows is open. Everything here is keyed off
        /// this so the NextSentence prefix costs one boolean test on any other path.</summary>
        private static bool cinematicActive;

        internal static bool Present
        {
            get
            {
                if (!resolved)
                {
                    Resolve();
                }
                return present;
            }
        }

        static ImmersiveOpeningCompat()
        {
            Resolve();
        }

        private static void Resolve()
        {
            resolved = true;
            try
            {
                Type window = AccessTools.TypeByName("ImmersiveOpening.Window_ImmersiveOpening");
                if (window == null)
                {
                    return; // Immersive Opening is not loaded. Nothing to do, no warning.
                }
                present = true;

                mapCenterField = AccessTools.Field(window, "mapCenter");
                sentenceIndexField = AccessTools.Field(window, "currentSentenceIndex");
                sentencesField = AccessTools.Field(window, "sentences");
                MethodInfo preOpen = AccessTools.Method(window, "PreOpen");
                MethodInfo nextSentence = AccessTools.Method(window, "NextSentence");
                MethodInfo postClose = AccessTools.Method(window, "PostClose");

                if (mapCenterField == null || mapCenterField.FieldType != typeof(IntVec3)
                    || preOpen == null || postClose == null)
                {
                    // ⚠ DEGRADED, NOT BROKEN. Without the anchor rewrite our band clamp is
                    // still the backstop, so the camera cannot actually leave the level -
                    // the player gets a cinematic that sticks against the band edge rather
                    // than a black screen. Say so plainly instead of failing silently.
                    Log.WarningOnce(ABLog.Tag + " Immersive Opening is loaded but its window"
                        + " shape has changed (mapCenter/PreOpen/PostClose not found); the"
                        + " opening cinematic will not be aimed at your colony's level.",
                        0x2B10F0);
                    return;
                }

                HarmonyBoot.Harmony.Patch(preOpen,
                    postfix: new HarmonyMethod(typeof(ImmersiveOpeningCompat),
                        nameof(PreOpenPostfix)));
                HarmonyBoot.Harmony.Patch(postClose,
                    postfix: new HarmonyMethod(typeof(ImmersiveOpeningCompat),
                        nameof(PostClosePostfix)));

                // The tour half is optional: without it the cinematic is simply locked to
                // the colony's level, which is the default setting anyway (rule 78 - one
                // try around N lookups would make two features one feature).
                if (nextSentence != null && sentenceIndexField != null && sentencesField != null)
                {
                    HarmonyBoot.Harmony.Patch(nextSentence,
                        prefix: new HarmonyMethod(typeof(ImmersiveOpeningCompat),
                            nameof(NextSentencePrefix)));
                }
                else
                {
                    Log.WarningOnce(ABLog.Tag + " Immersive Opening is loaded but"
                        + " NextSentence/currentSentenceIndex/sentences were not found; the"
                        + " opening will stay on the colony's level and the stack tour"
                        + " setting will do nothing.", 0x2B10F1);
                }
                ABLog.Dev("Immersive Opening level aiming INSTALLED.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Immersive Opening compat failed to install: " + e);
            }
        }

        // ---- the two anchors -------------------------------------------------

        /// <summary>
        /// A cell in <paramref name="band"/> that is safe to hand Immersive Opening as its
        /// `mapCenter`: inset far enough from both band edges that their jitter plus their
        /// viewport still lands inside the level, and preferring somewhere unfogged so the
        /// shot is not a wall of black.
        ///
        /// <paramref name="seed"/> is translated into the band rather than re-derived, so
        /// the tour rises and falls through the SAME column - which is the one thing that
        /// makes a stack read as one place rather than as several maps.
        /// </summary>
        private static bool TryBandAnchor(Map map, ABBandMap bands, int band, IntVec3 seed,
            out IntVec3 anchor)
        {
            anchor = IntVec3.Invalid;
            if (map == null || bands == null || !bands.BandExists(band))
            {
                return false;
            }
            CellRect rect = bands.RectOfBand(band);
            int lo = rect.minZ + ZInset;
            int hi = rect.maxZ - ZInset;
            if (lo > hi)
            {
                // Band shorter than two insets: there is no safe row, so centre it and let
                // our own clamp absorb the overhang. Better a slightly stuck pan than an
                // anchor outside the level.
                lo = hi = rect.CenterCell.z;
            }
            IntVec3 c = bands.Translate(seed, band);
            anchor = new IntVec3(Mathf.Clamp(c.x, 0, map.Size.x - 1), 0,
                Mathf.Clamp(c.z, lo, hi));
            if (!map.fogGrid.IsFogged(anchor))
            {
                return true;
            }
            // The column above a colony can run straight into the inside of a mountain.
            // Slide to the nearest visible cell that is still a legal anchor before giving
            // up; the shot is cosmetic, so a short search and no retry is the right budget.
            foreach (IntVec3 r in GenRadial.RadialCellsAround(anchor, 24f, useCenter: false))
            {
                if (r.InBounds(map) && r.z >= lo && r.z <= hi && !map.fogGrid.IsFogged(r))
                {
                    anchor = new IntVec3(r.x, 0, r.z);
                    break;
                }
            }
            return true;
        }

        /// <summary>Bands worth showing, top first, ALWAYS ending on the colony's level so
        /// the cinematic hands the player over where they will actually be playing.</summary>
        private static List<int> BuildTour(Map map, ABBandMap bands)
        {
            var list = new List<int>();
            for (int b = bands.bandCount - 1; b >= 0; b--)
            {
                if (!bands.BandExists(b))
                {
                    continue;
                }
                // A sealed basement is refogged wholesale at generation and SetBand refuses
                // it outright, so touring one would be a black screen AND a rejected band
                // change. Ask IsOpen rather than assuming none are.
                if (b < bands.surfaceBand && !bands.IsOpen(b))
                {
                    continue;
                }
                list.Add(b);
            }
            if (list.Count == 0 || list[list.Count - 1] != bands.surfaceBand)
            {
                list.Add(bands.surfaceBand);
            }
            return list;
        }

        // ---- patches ---------------------------------------------------------

        private static void PreOpenPostfix(object __instance)
        {
            try
            {
                tour = null;
                colonyAnchor = IntVec3.Invalid;
                cinematicActive = false;

                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return; // ordinary map: their map.Center is already the right answer
                }
                if (!ABBandView.TryColonyAnchor(map, out IntVec3 seed))
                {
                    seed = bands.RectOfBand(bands.surfaceBand).CenterCell;
                }
                int surface = bands.BandOf(seed);
                if (!bands.BandExists(surface))
                {
                    surface = bands.surfaceBand;
                }
                if (!TryBandAnchor(map, bands, surface, seed, out colonyAnchor))
                {
                    return;
                }

                cinematicActive = true;
                tour = BuildTour(map, bands);
                mapCenterField.SetValue(__instance, colonyAnchor);
                // Pin the viewed band NOW: their window is on screen before the first
                // sentence fires, and a viewBand pointing anywhere else would show the
                // curtain behind their "click to start" screen.
                ABBandView.SetBand(map, surface, preserveXZ: false);
                ABLog.Dev("Immersive Opening: cinematic aimed at " + colonyAnchor
                    + " (band " + surface + " of " + bands.bandCount + ", map.Center was "
                    + map.Center + "), tour=" + (tour == null ? "none" : string.Join(",", tour))
                    + ", stackTour=" + (ABMod.Settings != null && ABMod.Settings.ioStackTour));
            }
            catch (Exception e)
            {
                cinematicActive = false;
                Log.Warning(ABLog.Tag + " Immersive Opening: could not aim the cinematic: " + e);
            }
        }

        /// <summary>
        /// Retarget the cached anchor for the sentence that is ABOUT to be shown.
        ///
        /// ⚠ A PREFIX, DELIBERATELY. NextSentence increments currentSentenceIndex as its
        /// first statement and then immediately derives, clamps and issues the pan from
        /// `mapCenter`. A postfix would be reading a pan that has already been handed to
        /// the CameraPanner - too late to influence it without re-issuing the whole move
        /// (rule 25). So the upcoming index is `currentSentenceIndex + 1`, and writing
        /// mapCenter here means their own untouched arithmetic produces our cells.
        /// </summary>
        private static void NextSentencePrefix(object __instance)
        {
            try
            {
                if (!cinematicActive || tour == null || tour.Count == 0)
                {
                    return;
                }
                Map map = Find.CurrentMap;
                ABBandMap bands = ABBands.CompOf(map);
                if (map == null || bands == null || !bands.Banded)
                {
                    return;
                }

                int band = tour[tour.Count - 1]; // the colony's level
                IntVec3 anchor = colonyAnchor;

                ABSettings set = ABMod.Settings;
                if (set != null && set.ioStackTour && tour.Count > 1
                    && sentencesField.GetValue(__instance) is List<string> sentences
                    && sentences.Count > 1
                    && sentenceIndexField.GetValue(__instance) is int index)
                {
                    int next = index + 1;
                    if (next < sentences.Count)
                    {
                        // Spread the sentences over the tour, top band first. Integer
                        // division guarantees the last sentence lands on the last entry,
                        // which BuildTour pins to the colony's level.
                        int ti = Mathf.Clamp(next * tour.Count / sentences.Count,
                            0, tour.Count - 1);
                        band = tour[ti];
                    }
                }

                if (band != tour[tour.Count - 1]
                    && !TryBandAnchor(map, bands, band, colonyAnchor, out anchor))
                {
                    band = tour[tour.Count - 1];
                    anchor = colonyAnchor;
                }
                if (!anchor.IsValid)
                {
                    return;
                }

                if (band != ABBandView.CurrentBand(map)
                    && !ABBandView.SetBand(map, band, preserveXZ: false))
                {
                    // Refused (a basement that closed under us): fall back rather than pan
                    // to a band the clip will not draw.
                    band = tour[tour.Count - 1];
                    anchor = colonyAnchor;
                    ABBandView.SetBand(map, band, preserveXZ: false);
                }
                mapCenterField.SetValue(__instance, anchor);
            }
            catch (Exception e)
            {
                cinematicActive = false;
                Log.Warning(ABLog.Tag + " Immersive Opening: sentence retarget failed,"
                    + " staying on the current level: " + e);
            }
        }

        /// <summary>
        /// Hand the player over on the colony, not on the inset anchor.
        ///
        /// Their PostClose parks the camera on the cached cell at rootSize 24. That cell is
        /// now ours and is on the right level, so nothing is broken - but it can sit up to
        /// 45 rows off the colonists because of the inset, and it never touches
        /// `rememberedCameraPos`, so the first save would remember the wrong spot. Running
        /// the ordinary new-game landing afterwards fixes both, and also rescues the case
        /// where the player pressed Escape mid-tour on a sky band.
        /// </summary>
        private static void PostClosePostfix()
        {
            try
            {
                if (!cinematicActive)
                {
                    return;
                }
                cinematicActive = false;
                tour = null;
                ABBandView.LandOnColony(Find.CurrentMap);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Immersive Opening: hand-over to the colony"
                    + " failed: " + e);
            }
        }
    }
}
