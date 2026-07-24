using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// ONE-STEP reinstall across levels (parity pass 2026-07-24). Vanilla's
    /// reinstall is a single HaulToContainer job (walk to the built building,
    /// uninstall-minify, carry to the install blueprint) - strictly same-map,
    /// so a blueprint placed on another level never progressed: the deliver
    /// giver's CanReach fails cross-map and nobody ever uninstalled the source
    /// building. The old flow required the player to designate the uninstall
    /// by hand (the documented two-step).
    ///
    /// The cross-map chain is driven by ONE event hook, everything downstream
    /// is machinery that already exists:
    ///
    ///   1. Blueprint_Install.SpawnSetup postfix - a reinstall blueprint whose
    ///      source building stands on a DIFFERENT level of the same column
    ///      gets an Uninstall designation dropped on that building.
    ///   2. Vanilla's WorkGiver_Uninstall executes it locally; our designation
    ///      detector (constructionDesigs includes Uninstall) migrates a
    ///      constructor to that level when none lives there.
    ///   3. MinifyUtility re-points the blueprint at the resulting mini -
    ///      InstallBlueprintUtility.ExistingBlueprintFor already walks ALL
    ///      maps, so the cross-map link is native vanilla.
    ///   4. The construction-supply ferry (FindInstallMini) carries the loose
    ///      mini through the stairs; the target level's vanilla install giver
    ///      hauls it into the blueprint and builds.
    ///
    /// Cancelling the blueprint removes the designation again (only when no
    /// other install blueprint still references the building). Same-level
    /// reinstalls are untouched - pure vanilla single-job flow.
    /// Kill switch: logistics; setting: supplyConstruction.
    /// </summary>
    internal static class ReinstallAcrossLevels
    {
        /// <summary>Both maps alive and members of the same column (share a
        /// ground map), but not the same map.</summary>
        internal static bool CrossLevelPair(Map a, Map b)
        {
            if (a == null || b == null || a == b || a.Disposed || b.Disposed)
            {
                return false;
            }
            LevelComp ca = a.Levels();
            LevelComp cb = b.Levels();
            if (ca == null || cb == null)
            {
                return false;
            }
            Map groundA = ca.level == 0 ? a : ca.groundMap;
            Map groundB = cb.level == 0 ? b : cb.groundMap;
            return groundA != null && groundA == groundB;
        }

        private static bool Enabled()
        {
            if (!ABGuard.On(ABGuard.Logistics))
            {
                return false;
            }
            ABSettings settings = ABMod.Settings;
            return settings != null && settings.crossLevelSupply && settings.supplyConstruction;
        }

        /// <summary>A reinstall blueprint just spawned: when its source building
        /// stands on another level of this column, designate the uninstall so
        /// the vanilla flow (uninstall -> mini -> ferry -> install) runs with
        /// no player micromanagement.</summary>
        internal static void OnInstallBlueprintSpawned(Blueprint_Install install)
        {
            if (!Enabled() || install == null || !install.Spawned
                || install.Faction != Faction.OfPlayer)
            {
                return;
            }
            Thing source = install.MiniToInstallOrBuildingToReinstall;
            if (!(source is Building b) || !b.Spawned
                || !CrossLevelPair(b.Map, install.Map))
            {
                return;
            }
            DesignationManager dm = b.Map.designationManager;
            if (dm.DesignationOn(b, DesignationDefOf.Uninstall) == null)
            {
                dm.AddDesignation(new Designation(b, DesignationDefOf.Uninstall));
            }
        }

        /// <summary>A reinstall blueprint despawned (cancelled, replaced, or
        /// completed): drop the uninstall designation we added, unless another
        /// install blueprint still wants the building. On the completed path
        /// the source is a (destroyed) mini, not a spawned building, so this
        /// no-ops naturally.</summary>
        internal static void OnInstallBlueprintDespawned(Thing source)
        {
            if (!(source is Building b) || !b.Spawned || b.Map == null)
            {
                return;
            }
            if (InstallBlueprintUtility.ExistingBlueprintFor(b) != null)
            {
                return; // another blueprint (re-placed elsewhere) still needs it
            }
            Designation des = b.Map.designationManager.DesignationOn(b, DesignationDefOf.Uninstall);
            if (des != null)
            {
                b.Map.designationManager.RemoveDesignation(des);
            }
        }
    }

    [HarmonyPatch(typeof(Blueprint_Install), nameof(Blueprint_Install.SpawnSetup))]
    internal static class Patch_BlueprintInstall_SpawnSetup_CrossLevel
    {
        private static void Postfix(Blueprint_Install __instance, bool respawningAfterLoad)
        {
            if (respawningAfterLoad)
            {
                return; // the designation was scribed with the save
            }
            try
            {
                ReinstallAcrossLevels.OnInstallBlueprintSpawned(__instance);
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level reinstall spawn");
            }
        }
    }

    [HarmonyPatch(typeof(Blueprint_Install), "DeSpawn")]
    internal static class Patch_BlueprintInstall_DeSpawn_CrossLevel
    {
        /// <summary>Snapshot the source before base.DeSpawn tears state down.</summary>
        private static void Prefix(Blueprint_Install __instance, out Thing __state)
        {
            __state = null;
            try
            {
                if (__instance.Spawned)
                {
                    __state = __instance.MiniToInstallOrBuildingToReinstall;
                }
            }
            catch
            {
                // MiniToInstallOrBuildingToReinstall throws when both refs are
                // gone (destroyed mini on the completed path): nothing to clean.
            }
        }

        private static void Postfix(Thing __state)
        {
            if (__state == null)
            {
                return;
            }
            try
            {
                ReinstallAcrossLevels.OnInstallBlueprintDespawned(__state);
            }
            catch (System.Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "cross level reinstall despawn");
            }
        }
    }
}
