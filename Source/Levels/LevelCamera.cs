using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Level view switching that keeps the camera position and zoom, so moving
    /// between levels feels like looking through the floor instead of
    /// teleporting. Reads the driver's precise root position (T10 #5): the
    /// public MapPosition truncates to the cell, which made every switch snap
    /// to cell centers and read as a small jolt while panning. Every switch runs
    /// through ABArchitectPreserve so an open build tool survives the map change.
    /// </summary>
    public static class LevelCamera
    {
        private static readonly AccessTools.FieldRef<CameraDriver, Vector3> RootPosRef =
            AccessTools.FieldRefAccess<CameraDriver, Vector3>("rootPos");

        public static void JumpPreservingView(Map target)
        {
            if (target == null || target.Disposed)
            {
                return;
            }
            CameraDriver driver = Find.CameraDriver;
            Vector3 root = RootPosRef(driver);
            float zoom = driver.ZoomRootSize;
            if (target.rememberedCameraPos != null)
            {
                target.rememberedCameraPos.rootPos = root;
                target.rememberedCameraPos.rootSize = zoom;
            }
            IntVec3 cell = root.ToIntVec3();
            cell.x = Mathf.Clamp(cell.x, 0, target.Size.x - 1);
            cell.z = Mathf.Clamp(cell.z, 0, target.Size.z - 1);
            ABArchitectPreserve.Around(delegate
            {
                CameraJumper.TryJump(cell, target);
                driver.SetRootPosAndSize(root, zoom);
            });
        }

        /// <summary>Switch the view to the pawn's current level, centered on the
        /// pawn at the current zoom, and keep it selected. Used when a selected
        /// colonist takes the stairs so the camera rides along (toggleable).</summary>
        public static void FollowPawn(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Map.Disposed)
            {
                return;
            }
            CameraDriver driver = Find.CameraDriver;
            float zoom = driver.ZoomRootSize;
            Map target = pawn.Map;
            Vector3 root = new Vector3(pawn.DrawPos.x, 0f, pawn.DrawPos.z);
            if (target.rememberedCameraPos != null)
            {
                target.rememberedCameraPos.rootPos = root;
                target.rememberedCameraPos.rootSize = zoom;
            }
            ABArchitectPreserve.Around(delegate
            {
                CameraJumper.TryJump(pawn.Position, target);
                driver.SetRootPosAndSize(root, zoom);
                Find.Selector.ClearSelection();
                Find.Selector.Select(pawn);
            });
        }
    }

    /// <summary>
    /// Keeps the Architect menu on the same category with the same build tool
    /// active across a level switch. The vanilla map-switch handler
    /// (MapInterface.Notify_SwitchedMap) deselects the active designator and
    /// rebuilds the Architect window from scratch, dropping you back to the top
    /// category list. Here we snapshot the open category and the selected
    /// designator before the switch and restore both after, so flipping levels
    /// does not interrupt a wall-laying session. Fails open: reflection trouble
    /// or a closed window just means the switch behaves as it did before.
    /// </summary>
    internal static class ABArchitectPreserve
    {
        private static readonly AccessTools.FieldRef<MainTabWindow_Architect, List<ArchitectCategoryTab>> PanelsRef =
            AccessTools.FieldRefAccess<MainTabWindow_Architect, List<ArchitectCategoryTab>>("desPanelsCached");

        public static void Around(Action jump)
        {
            bool architectOpen = false;
            DesignationCategoryDef cat = null;
            Designator des = null;
            try
            {
                architectOpen = Find.MainTabsRoot.OpenTab == MainButtonDefOf.Architect;
                if (architectOpen && MainButtonDefOf.Architect.TabWindow is MainTabWindow_Architect w)
                {
                    cat = w.selectedDesPanel?.def;
                }
                des = Find.DesignatorManager?.SelectedDesignator;
            }
            catch
            {
                // Best-effort snapshot; a failure here just skips restoration.
            }

            jump();

            try
            {
                if (!architectOpen || Find.MainTabsRoot.OpenTab != MainButtonDefOf.Architect
                    || !(MainButtonDefOf.Architect.TabWindow is MainTabWindow_Architect w2))
                {
                    return;
                }
                if (cat != null)
                {
                    List<ArchitectCategoryTab> panels = PanelsRef(w2);
                    if (panels != null)
                    {
                        for (int i = 0; i < panels.Count; i++)
                        {
                            if (panels[i]?.def == cat)
                            {
                                w2.selectedDesPanel = panels[i];
                                break;
                            }
                        }
                    }
                }
                if (des != null)
                {
                    Find.DesignatorManager.Select(des);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "architect state preserve");
            }
        }
    }
}
