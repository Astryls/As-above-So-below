using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Level view switching that keeps the camera position and zoom, so moving
    /// between levels feels like looking through the floor instead of
    /// teleporting. Reads the driver's precise root position (T10 #5): the
    /// public MapPosition truncates to the cell, which made every switch snap
    /// to cell centers and read as a small jolt while panning.
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
            CameraJumper.TryJump(cell, target);
            driver.SetRootPosAndSize(root, zoom);
        }
    }
}
