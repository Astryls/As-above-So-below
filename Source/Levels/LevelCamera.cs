using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Level view switching that keeps the camera position and zoom, so moving
    /// between levels feels like looking through the floor instead of teleporting.
    /// </summary>
    public static class LevelCamera
    {
        public static void JumpPreservingView(Map target)
        {
            if (target == null || target.Disposed)
            {
                return;
            }
            CameraDriver driver = Find.CameraDriver;
            IntVec3 mapPos = driver.MapPosition;
            float zoom = driver.ZoomRootSize;
            Vector3 root = new Vector3(mapPos.x, 0f, mapPos.z);
            if (target.rememberedCameraPos != null)
            {
                target.rememberedCameraPos.rootPos = root;
                target.rememberedCameraPos.rootSize = zoom;
            }
            CameraJumper.TryJump(mapPos, target);
            driver.SetRootPosAndSize(root, zoom);
        }
    }
}
