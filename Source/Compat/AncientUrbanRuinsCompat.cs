using System;
using System.Collections.Generic;
using System.Reflection;
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

        // ------------------------------------------------------------------
        // Basement environment: stamp an AUR prefab facility into a basement
        // (BasementEnv.UrbanRuins). Reflection into AUR's own generator, so a
        // version change over there degrades to plain solid rock, never a crash.
        // ------------------------------------------------------------------

        /// <summary>Underground-appropriate facility layouts, picked at random.
        /// Surface layouts (streets, parking, supermarkets) are deliberately
        /// excluded. Non-existent names are skipped at pick time.</summary>
        private static readonly string[] UndergroundFacilities =
        {
            "AM_UndergroundFacilityA", "AM_UndergroundFacilityB",
            "AM_UndergroundFacilityC", "AM_UndergroundFacilityD",
            "AM_Bunker_A", "AM_Bunker_B", "AM_Bunker_C", "AM_Bunker_D", "AM_Bunker_E",
            "AM_Bunker_F", "AM_Bunker_G", "AM_Bunker_H", "AM_Bunker_I",
            "AM_Shelter_A", "AM_Shelter_B", "AM_Shelter_C", "AM_Shelter_D",
            "AM_Subway_A", "AM_Subway_B", "AM_ReserveBunker"
        };

        private static bool genResolved;
        private static Type dataDefType;
        private static Type entranceType;
        private static Type exitType;
        private static FieldInfo sizeField;
        private static MethodInfo mPretreat;
        private static MethodInfo mSetRoofAndTerrain;
        private static MethodInfo mSpawnThings;
        private static MethodInfo mSpawnPawns;

        /// <summary>Lazily resolve AUR's generator surface. Returns true only
        /// when every piece needed to stamp a facility is present.</summary>
        private static bool ResolveGen()
        {
            if (genResolved)
            {
                return dataDefType != null && mPretreat != null && mSetRoofAndTerrain != null
                    && mSpawnThings != null && sizeField != null;
            }
            genResolved = true;
            try
            {
                Type util = AccessTools.TypeByName("AncientMarket_Libraray.MapGeneratingUtility");
                dataDefType = AccessTools.TypeByName("AncientMarket_Libraray.CustomMapDataDef");
                entranceType = AccessTools.TypeByName("AncientMarket_Libraray.MapEntrance");
                exitType = AccessTools.TypeByName("AncientMarket_Libraray.MapExit");
                if (util != null)
                {
                    mPretreat = AccessTools.Method(util, "Pretreat");
                    mSetRoofAndTerrain = AccessTools.Method(util, "SetRoofAndTerrain");
                    mSpawnThings = AccessTools.Method(util, "SpawnThings");
                    mSpawnPawns = AccessTools.Method(util, "SpawnPawns");
                }
                if (dataDefType != null)
                {
                    sizeField = AccessTools.Field(dataDefType, "size");
                }
            }
            catch (Exception e)
            {
                ABLog.Dev("AUR generator resolve failed (ignored): " + e.Message);
            }
            return dataDefType != null && mPretreat != null && mSetRoofAndTerrain != null
                && mSpawnThings != null && sizeField != null;
        }

        /// <summary>Stamp a random underground facility centered on the map,
        /// mirroring AUR's own SpawnCustomMap piecewise so occupants can be
        /// skipped, then strip the functional entrance/exit portals so the only
        /// way in or out is the player's stairs. Returns false (=> keep the
        /// solid-rock basement) on any failure.</summary>
        public static bool TryStampFacility(Map map, bool includeOccupants)
        {
            if (!Active || map == null || !ResolveGen())
            {
                return false;
            }
            try
            {
                object def = PickFacilityDef();
                if (def == null)
                {
                    return false;
                }
                IntVec3 size = (IntVec3)sizeField.GetValue(def);
                IntVec3 center = map.Center - new IntVec3(size.x / 2, 0, size.z / 2);
                object[] core = { map, def, center };

                InvokeSafe(mPretreat, core, "Pretreat");
                InvokeSafe(mSetRoofAndTerrain, new object[] { map, def, center, false }, "SetRoofAndTerrain");
                InvokeSafe(mSpawnThings, core, "SpawnThings");
                if (includeOccupants && mSpawnPawns != null)
                {
                    InvokeSafe(mSpawnPawns, core, "SpawnPawns");
                }
                StripPortals(map);
                ABLog.Dev("Stamped AUR facility " + ((Def)def).defName + " at " + center
                    + " (occupants " + includeOccupants + ").");
                return true;
            }
            catch (Exception e)
            {
                ABLog.Dev("AUR facility stamp failed (basement stays solid rock): " + e.Message);
                return false;
            }
        }

        private static object PickFacilityDef()
        {
            List<object> pool = new List<object>();
            for (int i = 0; i < UndergroundFacilities.Length; i++)
            {
                Def d = GenDefDatabase.GetDef(dataDefType, UndergroundFacilities[i], false);
                if (d != null)
                {
                    pool.Add(d);
                }
            }
            return pool.Count == 0 ? null : pool[Rand.Range(0, pool.Count)];
        }

        private static void InvokeSafe(MethodInfo m, object[] args, string label)
        {
            if (m == null)
            {
                return;
            }
            try
            {
                m.Invoke(null, args);
            }
            catch (Exception e)
            {
                // Mirror AUR's own per-step resilience: one bad phase must not
                // abort the rest of the stamp.
                ABLog.Dev("AUR facility " + label + " phase failed (ignored): "
                    + (e.InnerException ?? e).Message);
            }
        }

        /// <summary>Destroy AUR's functional MapEntrance/MapExit portals so the
        /// stamped facility has no exit of its own - reached only by our stairs.
        /// Decorative blocked stairs/elevators (plain buildings) are left as
        /// flavor.</summary>
        private static void StripPortals(Map map)
        {
            if (entranceType == null && exitType == null)
            {
                return;
            }
            List<Thing> doomed = new List<Thing>();
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if ((entranceType != null && entranceType.IsInstanceOfType(t))
                    || (exitType != null && exitType.IsInstanceOfType(t)))
                {
                    doomed.Add(t);
                }
            }
            for (int i = 0; i < doomed.Count; i++)
            {
                if (!doomed[i].Destroyed)
                {
                    doomed[i].Destroy(DestroyMode.Vanish);
                }
            }
            if (doomed.Count > 0)
            {
                ABLog.Dev("Stripped " + doomed.Count + " AUR entrance/exit portals from basement facility.");
            }
        }
    }
}
