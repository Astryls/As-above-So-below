using System;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vanilla-style goto interaction for a below-selected drafted pawn: RIGHT-CLICK
    /// AND HOLD over open air shows the pawn ghost + goto circle at the shifted
    /// destination (exactly MultiPawnGotoController's look, which is current-map-only
    /// and never sees below pawns); RELEASE issues the goto. Started by the float-menu
    /// prefix when the click is a pure move (no attack target under the cursor); the
    /// instant-order behaviour is replaced by press-preview-release, matching vanilla.
    /// Single pawn only (cross-level orders are single-pawn by design). Fails open:
    /// any invalid state cancels the drag and the pawn simply receives no order.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABBelowGotoDrag
    {
        private static Pawn pawn;

        private static Material circleMat;

        internal static bool Active => pawn != null;

        internal static void Start(Pawn p)
        {
            pawn = p;
        }

        internal static void Cancel()
        {
            pawn = null;
        }

        /// <summary>Per-frame: draw the ghost while the right button is held, issue
        /// the goto on release. Called from LevelComp.MapComponentUpdate (viewed map
        /// only); one null check when idle.</summary>
        internal static void FrameUpdate(Map cur)
        {
            if (pawn == null)
            {
                return;
            }
            try
            {
                if (pawn.Dead || !pawn.Spawned || !pawn.Drafted
                    || !BelowSelection.TryGetBelowView(out Map sky, out Map lower)
                    || sky != cur || pawn.Map != lower)
                {
                    Cancel();
                    return;
                }
                Vector3 mouse = UI.MouseMapPosition();
                IntVec3 skyCell = mouse.ToIntVec3();
                bool overAir = skyCell.InBounds(sky)
                    && sky.terrainGrid.TerrainAt(skyCell) == ABDefOf.AB_OpenAir;
                IntVec3 dest = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
                bool destOk = overAir && dest.InBounds(lower) && dest.Standable(lower) && !dest.Fogged(lower);

                if (!Input.GetMouseButton(1))
                {
                    // Released: order the move like vanilla's controller would.
                    Pawn p = pawn;
                    Cancel();
                    if (destOk)
                    {
                        IntVec3 gotoLoc = RCellFinder.BestOrderedGotoDestNear(dest, p);
                        if (gotoLoc.IsValid)
                        {
                            FloatMenuOptionProvider_DraftedMove.PawnGotoAction(dest, p, gotoLoc);
                            SoundDefOf.ColonistOrdered.PlayOneShotOnCamera();
                        }
                    }
                    return;
                }
                if (!destOk)
                {
                    return; // held over an invalid spot: no preview this frame
                }
                Vector3 drawLoc = LevelRenderer.ShiftedBelowDrawPos(dest.ToVector3Shifted());
                drawLoc.y = AltitudeLayer.MetaOverlays.AltitudeFor();
                pawn.Drawer.renderer.RenderPawnAt(drawLoc, Rot4.South);
                if (circleMat == null)
                {
                    circleMat = MaterialPool.MatFrom("UI/Overlays/Circle75Solid",
                        ShaderDatabase.Transparent,
                        GenColor.FromBytes(153, 207, 135) * new Color(1f, 1f, 1f, 0.4f));
                }
                Graphics.DrawMesh(MeshPool.plane10,
                    Matrix4x4.TRS(drawLoc + new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(1.7f, 1f, 1.7f)),
                    circleMat, 0);
            }
            catch (Exception e)
            {
                Cancel();
                ABGuard.Disable(ABGuard.Ui, e, "below goto drag");
            }
        }
    }
}
