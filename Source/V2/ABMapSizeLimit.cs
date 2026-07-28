using System;
using System.Collections.Generic;
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
        public const int Cap = 200;

        public static bool Active => ABV2.Enabled && !(ABMod.Settings?.unclampMapSize ?? false);

        /// <summary>Labels of the size options that are locked, rebuilt each time the
        /// chooser opens. Matching on the exact rendered label rather than parsing keeps
        /// this robust against localisation.</summary>
        private static readonly HashSet<string> lockedLabels = new HashSet<string>();

        private static bool inChooser;

        public static void BeginChooser()
        {
            lockedLabels.Clear();
            inChooser = true;
            if (!Active)
            {
                return;
            }
            foreach (int size in Dialog_AdvancedGameConfig.MapSizes)
            {
                if (size > Cap)
                {
                    lockedLabels.Add("MapSizeDesc".Translate(size, size * size));
                }
            }
            // Test sizes too, when the player has them enabled.
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
        }

        public static bool IsLocked(string label)
        {
            return inChooser && Active && label != null && lockedLabels.Contains(label);
        }

        /// <summary>Clamp a requested colony map size. Applied at generation so nothing can
        /// bypass the chooser.</summary>
        public static int Clamp(int size)
        {
            return Active ? Mathf.Min(size, Cap) : size;
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
                if (ABMapSizeLimit.Active && Find.GameInitData != null
                    && Find.GameInitData.mapSize > ABMapSizeLimit.Cap)
                {
                    Find.GameInitData.mapSize = ABMapSizeLimit.Cap;
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
                if (!ABMapSizeLimit.IsLocked(label))
                {
                    return;
                }
                label = "AB_MapSizeLocked".Translate(label);
                tooltip = "AB_MapSizeLockedTip".Translate(ABMapSizeLimit.Cap);
                disabled = true;
            }
            catch
            {
            }
        }
    }
}
