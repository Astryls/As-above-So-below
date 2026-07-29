using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 map-size cap.
    ///
    /// A banded map is THREE playable bands stacked in one Map, and RimWorld 1.6's
    /// pathfinding grid job is an IJobParallelFor over every cell of the map. A 200-wide
    /// colony therefore costs ~120k cells; 325 costs ~317k, on a hot per-request path. This
    /// is the one place V2 is measurably worse than V1, where only maps with pathing pawns
    /// paid at all.
    ///
    /// So colony maps are capped at 200x200 by default. The cap is enforced in TWO places
    /// deliberately: at the map-size chooser (so the player is told, not silently
    /// overridden) and again at generation (so nothing - a scenario, another mod, a loaded
    /// config - can slip past it).
    /// </summary>
    public static class ABMapSizeLimit
    {
        /// <summary>
        /// The offered sizes, and why these three specifically.
        ///
        /// Slot is `ceil((bandHeight + MinGutter) / 64) * 64`, so cost does NOT rise
        /// smoothly with size - it steps. A size just past a 64 boundary pays a whole extra
        /// 64 rows of dead gutter PER BAND for nothing. These are the three sizes that sit
        /// immediately under a boundary, where the gutter collapses to its 2-row minimum:
        ///
        ///   126 -> slot 128, gutter 2   ->  48,384 cells  (1.6% wasted)
        ///   190 -> slot 192, gutter 2   -> 109,440 cells  (1.0% wasted)
        ///   254 -> slot 256, gutter 2   -> 195,072 cells  (0.8% wasted)
        ///
        /// The old 200x200 cap was one of the WORST points on that curve: slot 256, gutter
        /// 56, 153,600 cells of which 33,600 are empty seam - 21.9% wasted. Dropping the cap
        /// to 190 gives up 9.7% of playable area and saves 28.8% of the cells. Intermediate
        /// sizes are never worth offering: 150x150 costs 86,400 for 67,500 playable, while
        /// 190x190 costs 26% more and yields 60% more playable area.
        ///
        /// This matters because 1.6's PathGridJob is an IJobParallelFor over EVERY cell of
        /// the map, so the stacked total - not the per-level size - is what the pathfinder
        /// pays on a hot per-request path.
        /// </summary>
        public static readonly int[] Sizes = { 126, 190, 254 };

        /// <summary>Largest size allowed while the cap is on. Must be one of Sizes.</summary>
        public const int Cap = 190;

        public static bool Active => ABV2.Enabled && !(ABMod.Settings?.unclampMapSize ?? false);

        /// <summary>Total cells RimWorld actually allocates and paths over for a banded
        /// colony of this per-level size.</summary>
        public static int StackedCells(int size)
        {
            return size * ABV2.BandCount * ABBandMap.SlotFor(size);
        }

        /// <summary>Labels of the size options that are locked, rebuilt each time the
        /// chooser opens. Matching on the exact rendered label rather than parsing keeps
        /// this robust against localisation.</summary>
        private static readonly HashSet<string> lockedLabels = new HashSet<string>();

        /// <summary>Vanilla label -> our relabelled version, carrying the stacked cost.</summary>
        private static readonly Dictionary<string, string> relabel = new Dictionary<string, string>();

        private static bool inChooser;

        /// <summary>Vanilla's own array, restored when the dialog closes.</summary>
        private static int[] savedSizes;

        private static readonly FieldInfo MapSizesField =
            AccessTools.Field(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.MapSizes));

        public static void BeginChooser()
        {
            lockedLabels.Clear();
            relabel.Clear();
            inChooser = true;
            if (!Active)
            {
                return;
            }

            // Swap vanilla's size list for ours FOR THE DURATION OF THE DIALOG ONLY.
            // MapSizes is `static readonly int[]`, so the reference is settable by
            // reflection; scoping the swap to the dialog means anything else that reads it
            // (other mods, scenario code) still sees vanilla's list.
            try
            {
                if (MapSizesField != null && savedSizes == null)
                {
                    savedSizes = (int[])MapSizesField.GetValue(null);
                    MapSizesField.SetValue(null, (int[])Sizes.Clone());
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: could not replace the map size list, "
                    + "falling back to locking oversized options: " + e.Message, 762195911);
            }

            foreach (int size in Sizes)
            {
                string vanilla = "MapSizeDesc".Translate(size, size * size);
                relabel[vanilla] = "AB_MapSizeBanded".Translate(size,
                    (size * size).ToString("N0"), StackedCells(size).ToString("N0"));
                if (size > Cap)
                {
                    lockedLabels.Add(vanilla);
                }
            }
            // Test sizes are appended by the dialog itself and bypass our list entirely.
            int[] test = { 350, 400 };
            for (int i = 0; i < test.Length; i++)
            {
                lockedLabels.Add("MapSizeDesc".Translate(test[i], test[i] * test[i]));
            }
        }

        public static void EndChooser()
        {
            inChooser = false;
            lockedLabels.Clear();
            relabel.Clear();
            try
            {
                if (savedSizes != null && MapSizesField != null)
                {
                    MapSizesField.SetValue(null, savedSizes);
                }
            }
            catch
            {
            }
            finally
            {
                savedSizes = null;
            }
        }

        /// <summary>The relabelled text for a size option, or null if it is not one of
        /// ours.</summary>
        public static string RelabelFor(string label)
        {
            if (!inChooser || !Active || label == null)
            {
                return null;
            }
            return relabel.TryGetValue(label, out string s) ? s : null;
        }

        public static bool IsLocked(string label)
        {
            return inChooser && Active && label != null && lockedLabels.Contains(label);
        }

        /// <summary>Clamp a requested colony map size. Applied at generation so nothing can
        /// bypass the chooser.</summary>
        public static int Clamp(int size)
        {
            if (!Active)
            {
                return size;
            }
            // Snap to the largest OFFERED size that fits. Plain Min(size, Cap) would happily
            // return something like 175 - a size that is inside the cap but sits badly on
            // the 64-row slot boundary, paying a full extra band of gutter for nothing.
            int best = Sizes[0];
            for (int i = 0; i < Sizes.Length; i++)
            {
                if (Sizes[i] <= Cap && Sizes[i] <= size && Sizes[i] > best)
                {
                    best = Sizes[i];
                }
            }
            return Mathf.Min(best, Cap);
        }
    }

    /// <summary>
    /// The size chooser. A prefix/postfix pair brackets the dialog so the RadioButton patch
    /// below only applies inside it, and the selection is snapped back afterwards in case
    /// anything set it out of range.
    /// </summary>
    [HarmonyPatch(typeof(Dialog_AdvancedGameConfig), nameof(Dialog_AdvancedGameConfig.DoWindowContents))]
    public static class Patch_AdvancedGameConfig_ABMapSizeLock
    {
        private static void Prefix()
        {
            ABMapSizeLimit.BeginChooser();
        }

        private static void Postfix()
        {
            try
            {
                // Snap whatever is selected onto an offered size. Vanilla's default is 250,
                // which is no longer in the list, so without this the dialog can close with
                // a size that has no radio button and never went through Clamp.
                if (ABMapSizeLimit.Active && Find.GameInitData != null
                    && Array.IndexOf(ABMapSizeLimit.Sizes, Find.GameInitData.mapSize) < 0)
                {
                    Find.GameInitData.mapSize = ABMapSizeLimit.Clamp(Find.GameInitData.mapSize);
                }
            }
            catch
            {
            }
            finally
            {
                ABMapSizeLimit.EndChooser();
            }
        }
    }

    /// <summary>
    /// Renders the oversized options as locked. Vanilla's RadioButton already has a
    /// `disabled` mode with the right greyed styling, so this just switches it on and
    /// explains why - no custom widget, and the option cannot be picked.
    /// </summary>
    [HarmonyPatch(typeof(Listing_Standard), nameof(Listing_Standard.RadioButton),
        new Type[] { typeof(string), typeof(bool), typeof(float), typeof(float),
            typeof(string), typeof(float?), typeof(bool) })]
    public static class Patch_ListingStandard_ABLockMapSizes
    {
        private static void Prefix(ref string label, ref string tooltip, ref bool disabled)
        {
            try
            {
                string relabelled = ABMapSizeLimit.RelabelFor(label);
                bool locked = ABMapSizeLimit.IsLocked(label);
                if (relabelled == null && !locked)
                {
                    return;
                }
                if (locked)
                {
                    label = "AB_MapSizeLocked".Translate(relabelled ?? label);
                    tooltip = "AB_MapSizeLockedTip".Translate(ABMapSizeLimit.Cap);
                    disabled = true;
                    return;
                }
                // One of ours, and allowed: show the stacked cost, because that - not the
                // per-level size - is what the pathfinder actually pays.
                label = relabelled;
                tooltip = "AB_MapSizeBandedTip".Translate(ABV2.BandCount);
            }
            catch
            {
            }
        }
    }
}
