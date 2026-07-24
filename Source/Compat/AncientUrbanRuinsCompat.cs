using System;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Ancient urban ruins (XMB.AncientUrbanrUins.MO).
    ///
    /// AUR's explorable maps are pocket maps (MapParent_Custom : PocketMapParent,
    /// biome AM_UndergroundSpace) reached through its OWN MapEntrance/MapExit
    /// portal system - not the player's home column. Many are flagged
    /// isTemporary and auto-remove when their source map goes
    /// (MapParent_Custom.ShouldRemoveMapNow). Stacking our sky/basement levels
    /// on a temporary exploration submap is off-theme and (before this gate)
    /// could leave a dangling child pocket map. So BY DEFAULT we exclude AUR
    /// submaps from z-level eligibility: the stair PlaceWorker rejects them and
    /// LevelMapGen refuses to generate. A settings toggle
    /// (allowLevelsOnUrbanRuins, default off) lets power users build vertical
    /// bases inside urban ruins anyway.
    ///
    /// Removal safety for the opt-in case is already covered by vanilla: our
    /// AB_Sky / AB_Basement generators set
    /// pocketMapProperties.destroyOnParentMapAbandoned=true, and
    /// Game.DeinitAndRemoveMap destroys every child pocket map whose
    /// sourceMap == the removed map with that flag - so when AUR tears down the
    /// submap our levels go with it, no orphan hook needed.
    ///
    /// Reflection only, no foreign types in signatures; fails OPEN - any map we
    /// cannot positively identify as an AUR submap is treated as eligible.
    /// </summary>
    public static class AncientUrbanRuinsCompat
    {
        public const string PackageId = "XMB.AncientUrbanrUins.MO";

        private const string SubmapParentTypeName = "AncientMarket_Libraray.MapParent_Custom";

        private static bool? active;
        private static bool typeResolved;
        private static Type submapParentType;

        /// <summary>True when Ancient urban ruins is loaded (postfix-insensitive,
        /// so a local copy of the workshop mod still counts).</summary>
        public static bool Active => active ?? (active = ABDetect.Active(PackageId)).Value;

        private static Type SubmapParentType
        {
            get
            {
                if (!typeResolved)
                {
                    typeResolved = true;
                    try
                    {
                        submapParentType = AccessTools.TypeByName(SubmapParentTypeName);
                    }
                    catch (Exception e)
                    {
                        submapParentType = null;
                        ABLog.Dev("AUR submap type resolve failed (ignored): " + e.Message);
                    }
                }
                return submapParentType;
            }
        }

        /// <summary>True when the map is one of AUR's explorable submaps
        /// (its parent world object is a MapParent_Custom).</summary>
        public static bool IsUrbanRuinsMap(Map map)
        {
            if (!Active || map == null)
            {
                return false;
            }
            Type t = SubmapParentType;
            if (t == null)
            {
                return false;
            }
            try
            {
                MapParent parent = map.Parent;
                return parent != null && t.IsInstanceOfType(parent);
            }
            catch (Exception e)
            {
                ABLog.Dev("AUR submap identity check failed (ignored): " + e.Message);
                return false;
            }
        }

        /// <summary>True when z-level generation must be refused on this map:
        /// AUR active, the map is an AUR submap, and the player has not opted
        /// in via settings. Fails open - any uncertainty allows levels.</summary>
        public static bool BlocksLevels(Map map)
        {
            if (!Active)
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            if (settings != null && settings.allowLevelsOnUrbanRuins)
            {
                return false;
            }
            return IsUrbanRuinsMap(map);
        }
    }
}
