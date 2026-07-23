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
        /// <summary>Camera level lock (End by default): while on, AUTOMATIC
        /// level switches are suppressed - the camera riding along when a
        /// selected pawn takes the stairs, AND the view being yanked to another
        /// level when you select/jump to a pawn there (colonist bar, alerts,
        /// letters; user report 2026-07-23). The pawn still selects, the view
        /// stays put. Manual switches (level widget, view up/down hotkeys,
        /// gizmos) always work: explicit intent wins. Session state, not scribed.</summary>
        public static bool LevelLocked;

        /// <summary>Set true around our own explicit level switches so the
        /// lock's map-switch suppression lets them through. JumpPreservingView
        /// is the single choke for every manual switch (widget/hotkey/gizmo).</summary>
        public static bool ManualSwitch;

        /// <summary>True when a pending Game.CurrentMap change should be blocked
        /// by the level lock: locked, not one of our manual switches, in-game,
        /// and the target is a DIFFERENT level of the SAME column (jumping to
        /// another colony's map is not a "level change" and always works).
        /// Fails open - any doubt allows the switch.</summary>
        public static bool ShouldSuppressJump(Map target)
        {
            try
            {
                if (!LevelLocked || ManualSwitch || target == null
                    || Current.ProgramState != ProgramState.Playing)
                {
                    return false;
                }
                Map cur = Find.CurrentMap;
                if (cur == null || target == cur)
                {
                    return false;
                }
                Map g1 = target.GroundMap();
                Map g2 = cur.GroundMap();
                return g1 != null && g1 == g2;
            }
            catch
            {
                return false;
            }
        }

        public static void ToggleLevelLock()
        {
            LevelLocked = !LevelLocked;
            Messages.Message((LevelLocked ? "AB_CameraLockOn" : "AB_CameraLockOff").Translate(),
                MessageTypeDefOf.SilentInput, historical: false);
        }

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
            // Vanilla's MapInterface.Notify_SwitchedMap clears the selection on every map
            // switch. For the one-colony feel, keep whatever was selected across a level
            // change so looking at another level does not drop your colonist. Restored
            // directly (not via Select, which would jump the camera back to the pawn's map).
            List<object> savedSelection = new List<object>(Find.Selector.SelectedObjects);
            // Manual, explicit switch: bypass the lock's suppression.
            ManualSwitch = true;
            try
            {
                ABArchitectPreserve.Around(delegate
                {
                    CameraJumper.TryJump(cell, target);
                    driver.SetRootPosAndSize(root, zoom);
                });
            }
            finally
            {
                ManualSwitch = false;
            }
            RestoreSelection(savedSelection);
        }

        private static readonly AccessTools.FieldRef<Selector, List<object>> SelectedRef =
            AccessTools.FieldRefAccess<Selector, List<object>>("selected");

        /// <summary>Re-adds the pre-switch selection after a level change wiped it, writing
        /// the selected list directly so no camera jump is retriggered. Only spawned things
        /// are restored (zones/plans are map-scoped and dropped); a fresh selection made
        /// during the switch (e.g. FollowPawn) is left alone.</summary>
        private static void RestoreSelection(List<object> saved)
        {
            if (saved == null || saved.Count == 0 || !ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            try
            {
                Selector selector = Find.Selector;
                List<object> current = SelectedRef(selector);
                if (current.Count > 0)
                {
                    return;
                }
                for (int i = 0; i < saved.Count; i++)
                {
                    if (!(saved[i] is Thing t) || t.Destroyed || !t.Spawned || current.Contains(t))
                    {
                        continue;
                    }
                    current.Add(t);
                    t.Notify_ThingSelected();
                    SelectionDrawer.Notify_Selected(t);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "level switch selection preserve");
            }
        }

        /// <summary>Switch the view to the pawn's current level, centered on the
        /// pawn at the current zoom, and keep it selected. Used when a selected
        /// colonist takes the stairs so the camera rides along (toggleable).</summary>
        public static void FollowPawn(Pawn pawn)
        {
            if (LevelLocked)
            {
                // Camera level lock: stay put; the pawn rides without us.
                return;
            }
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
