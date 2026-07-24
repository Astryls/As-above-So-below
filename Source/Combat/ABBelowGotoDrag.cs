using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vanilla-style goto interaction for below-selected drafted pawns: RIGHT-CLICK
    /// AND HOLD over open air shows pawn ghosts + goto circles at the shifted
    /// destination (exactly MultiPawnGotoController's look, which is current-map-only
    /// and never sees below pawns); RELEASE issues the gotos. Started by the float-menu
    /// prefix when the click is a pure move (no attack target under the cursor); the
    /// instant-order behaviour is replaced by press-preview-release, matching vanilla.
    /// Handles ONE OR MANY pawns: a group spreads over standable cells around the
    /// destination like vanilla's formation move. Fails open: any invalid state
    /// cancels the drag and the pawns simply receive no order.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABBelowGotoDrag
    {
        private static readonly List<Pawn> pawns = new List<Pawn>();

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        private static readonly HashSet<IntVec3> tmpTaken = new HashSet<IntVec3>();

        private static Material circleMat;

        internal static bool Active => pawns.Count > 0;

        internal static void Start(Pawn p)
        {
            pawns.Clear();
            if (p != null)
            {
                pawns.Add(p);
            }
        }

        internal static void Start(List<Pawn> ps)
        {
            pawns.Clear();
            if (ps == null)
            {
                return;
            }
            for (int i = 0; i < ps.Count && pawns.Count < 30; i++)
            {
                if (ps[i] != null)
                {
                    pawns.Add(ps[i]);
                }
            }
        }

        internal static void Cancel()
        {
            pawns.Clear();
        }

        /// <summary>Per-frame: draw the ghosts while the right button is held, issue
        /// the gotos on release. Called from LevelComp.MapComponentUpdate (viewed map
        /// only); one count check when idle.</summary>
        internal static void FrameUpdate(Map cur)
        {
            if (pawns.Count == 0)
            {
                return;
            }
            try
            {
                if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower) || sky != cur)
                {
                    Cancel();
                    return;
                }
                pawns.RemoveAll(p => p == null || p.Dead || !p.Spawned || !p.Drafted || p.Map != lower);
                if (pawns.Count == 0)
                {
                    Cancel();
                    return;
                }
                Vector3 mouse = UI.MouseMapPosition();
                IntVec3 skyCell = mouse.ToIntVec3();
                bool overAir = skyCell.InBounds(sky)
                    && sky.terrainGrid.TerrainAt(skyCell) == ABDefOf.AB_OpenAir;
                IntVec3 destCenter = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
                bool destOk = overAir && destCenter.InBounds(lower)
                    && destCenter.Standable(lower) && !destCenter.Fogged(lower);

                // Formation spread: one standable, unfogged cell per pawn around the
                // destination, recomputed each frame (mouse moves), deterministic order.
                tmpCells.Clear();
                if (destOk)
                {
                    tmpTaken.Clear();
                    int idx = 0;
                    for (int i = 0; i < pawns.Count; i++)
                    {
                        tmpCells.Add(CrossLevelOrders.NextSpreadCell(lower, destCenter, tmpTaken, ref idx));
                    }
                }

                if (!Input.GetMouseButton(1))
                {
                    // Released: order the moves like vanilla's controller would.
                    List<Pawn> ordered = new List<Pawn>(pawns);
                    List<IntVec3> cells = new List<IntVec3>(tmpCells);
                    Cancel();
                    if (!destOk)
                    {
                        return;
                    }
                    bool any = false;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        IntVec3 cell = i < cells.Count && cells[i].IsValid ? cells[i] : destCenter;
                        IntVec3 gotoLoc = RCellFinder.BestOrderedGotoDestNear(cell, ordered[i]);
                        if (gotoLoc.IsValid)
                        {
                            FloatMenuOptionProvider_DraftedMove.PawnGotoAction(cell, ordered[i], gotoLoc);
                            any = true;
                        }
                    }
                    if (any)
                    {
                        SoundDefOf.ColonistOrdered.PlayOneShotOnCamera();
                    }
                    return;
                }
                if (!destOk)
                {
                    return; // held over an invalid spot: no preview this frame
                }
                if (circleMat == null)
                {
                    circleMat = MaterialPool.MatFrom("UI/Overlays/Circle75Solid",
                        ShaderDatabase.Transparent,
                        GenColor.FromBytes(153, 207, 135) * new Color(1f, 1f, 1f, 0.4f));
                }
                for (int i = 0; i < pawns.Count && i < tmpCells.Count; i++)
                {
                    IntVec3 cell = tmpCells[i];
                    if (!cell.IsValid)
                    {
                        continue;
                    }
                    Vector3 drawLoc = LevelRenderer.ShiftedBelowDrawPos(cell.ToVector3Shifted());
                    drawLoc.y = AltitudeLayer.MetaOverlays.AltitudeFor();
                    pawns[i].Drawer.renderer.RenderPawnAt(drawLoc, Rot4.South);
                    Graphics.DrawMesh(MeshPool.plane10,
                        Matrix4x4.TRS(drawLoc + new Vector3(0f, 0.03f, 0f), Quaternion.identity, new Vector3(1.7f, 1f, 1.7f)),
                        circleMat, 0);
                }
            }
            catch (Exception e)
            {
                Cancel();
                ABGuard.Disable(ABGuard.Ui, e, "below goto drag");
            }
        }
    }
}
