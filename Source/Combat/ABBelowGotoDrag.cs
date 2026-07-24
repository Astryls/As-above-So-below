using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Vanilla-parity formation goto for cross-level selections (parity pass
    /// 2026-07-24). Mirrors MultiPawnGotoController exactly - press shows per-pawn
    /// ghosts + goto circles, DRAGGING paints a start->end line the group
    /// distributes along (lerped roots, BestOrderedGotoDestNear spacing with the
    /// shared dests exclusion, DragGoto tick on cell change, 10-tick recompute
    /// cadence, between-line, hovering pawn labels), RELEASE issues the gotos.
    ///
    /// Handles every cross-level mix vanilla's controller cannot see:
    ///  - pawns on the viewed map ordered through OPEN AIR onto the level below
    ///    (ghosts drawn at the shifted below positions);
    ///  - pawns on the level below ordered on their own level from the sky view;
    ///  - mixed selections spanning both levels - pawns already on the target
    ///    level get plain gotos, the rest ride the stairs and walk to their
    ///    assigned formation cell on arrival (destination computed from their
    ///    stairwell exit via a virtual position swap, so walls are respected).
    /// Pawns with no usable stair route are excluded with one message.
    /// Fails open: any invalid state cancels the drag and no orders are issued.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABBelowGotoDrag
    {
        private const int MaxPawns = 30;

        private const float RecomputeFrequencyTicks = 10f;

        private static readonly Color FeedbackColor = GenColor.FromBytes(153, 207, 135);

        private static readonly List<Pawn> pawns = new List<Pawn>();

        private static readonly List<IntVec3> dests = new List<IntVec3>();

        /// <summary>Per-pawn stair entry for cross-level pawns; null when the
        /// pawn already stands on the target map.</summary>
        private static readonly List<Building_ABStairs> entries = new List<Building_ABStairs>();

        private static Map targetMap;

        private static IntVec3 start;

        private static IntVec3 end;

        private static int? lastUpdateTicks;

        private static Material circleMat;

        private static Material lineMat;

        internal static bool Active => pawns.Count > 0;

        /// <summary>Begin the interaction. Pawns may live on the target map or on
        /// any level linked to it; cross pawns without a usable stair route are
        /// dropped (one message). startCell is in TARGET-map coordinates.</summary>
        internal static void Start(List<Pawn> ps, Map target, IntVec3 startCell)
        {
            Cancel();
            if (ps == null || target == null || target.Disposed || !startCell.InBounds(target))
            {
                return;
            }
            bool noStairsShown = false;
            for (int i = 0; i < ps.Count && pawns.Count < MaxPawns; i++)
            {
                Pawn p = ps[i];
                if (p == null || !p.Spawned || p.Dead)
                {
                    continue;
                }
                Building_ABStairs entry = null;
                if (p.Map != target)
                {
                    entry = CrossLevelWork.NearestUsableStairsCached(p, target);
                    if (entry == null || entry.CounterpartTowards(target) == null)
                    {
                        if (!noStairsShown)
                        {
                            noStairsShown = true;
                            Map cur = Find.CurrentMap;
                            string dir = (target.Level() > (cur?.Level() ?? 0))
                                ? "AB_LevelAbove".Translate() : "AB_LevelBelow".Translate();
                            Messages.Message("AB_NoStairsToLevel".Translate(dir), p,
                                MessageTypeDefOf.RejectInput, historical: false);
                        }
                        continue;
                    }
                }
                pawns.Add(p);
                entries.Add(entry);
                dests.Add(IntVec3.Invalid);
            }
            if (pawns.Count == 0)
            {
                return;
            }
            targetMap = target;
            start = end = startCell;
            lastUpdateTicks = null;
        }

        internal static void Cancel()
        {
            pawns.Clear();
            dests.Clear();
            entries.Clear();
            targetMap = null;
            lastUpdateTicks = null;
        }

        /// <summary>Per-frame: track the mouse, recompute the formation on the
        /// vanilla cadence, draw the preview while the right button is held, and
        /// issue the gotos on release. Called from LevelComp.MapComponentUpdate
        /// (viewed map only); one count check when idle.</summary>
        internal static void FrameUpdate(Map cur)
        {
            if (pawns.Count == 0)
            {
                return;
            }
            try
            {
                if (targetMap == null || targetMap.Disposed)
                {
                    Cancel();
                    return;
                }
                bool belowMode = targetMap != cur;
                Map sky = null;
                if (belowMode
                    && (!BelowSelection.TryGetBelowView(out sky, out Map lower)
                        || sky != cur || lower != targetMap))
                {
                    Cancel(); // view switched mid-drag: vanilla deactivates too
                    return;
                }
                for (int i = pawns.Count - 1; i >= 0; i--)
                {
                    Pawn p = pawns[i];
                    bool onTarget = entries[i] == null;
                    if (p == null || p.Dead || !p.Spawned || !p.Drafted
                        || (onTarget && p.Map != targetMap)
                        || (!onTarget && (p.Map == targetMap || !entries[i].Spawned
                            || entries[i].CounterpartTowards(targetMap) == null)))
                    {
                        pawns.RemoveAt(i);
                        entries.RemoveAt(i);
                        dests.RemoveAt(i);
                    }
                }
                if (pawns.Count == 0)
                {
                    Cancel();
                    return;
                }

                // Mouse -> end cell in target-map coordinates. Through open air
                // when aiming below; direct cell otherwise.
                Vector3 mouse = UI.MouseMapPosition();
                bool endOk;
                IntVec3 mouseEnd;
                if (belowMode)
                {
                    IntVec3 skyCell = mouse.ToIntVec3();
                    mouseEnd = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
                    endOk = skyCell.InBounds(sky)
                        && sky.terrainGrid.TerrainAt(skyCell) == ABDefOf.AB_OpenAir
                        && mouseEnd.InBounds(targetMap);
                }
                else
                {
                    mouseEnd = mouse.ToIntVec3();
                    endOk = mouseEnd.InBounds(targetMap);
                }

                // Vanilla cadence: recompute when the hovered cell changes (with
                // the DragGoto tick) or every 10 ticks (reservations shift).
                if (endOk)
                {
                    int ticksGame = Find.TickManager.TicksGame;
                    if (mouseEnd != end || !lastUpdateTicks.HasValue
                        || (float)ticksGame > (float?)lastUpdateTicks + RecomputeFrequencyTicks)
                    {
                        if (mouseEnd != end)
                        {
                            SoundDefOf.DragGoto.PlayOneShotOnCamera();
                        }
                        end = mouseEnd;
                        lastUpdateTicks = ticksGame;
                        RecomputeDestinations();
                    }
                }

                if (!Input.GetMouseButton(1))
                {
                    IssueGotoJobs();
                    return;
                }
                DrawPreview(belowMode);
            }
            catch (Exception e)
            {
                Cancel();
                ABGuard.Disable(ABGuard.Ui, e, "cross level goto drag");
            }
        }

        /// <summary>Vanilla MultiPawnGotoController.RecomputeDestinations, with a
        /// virtual position swap for pawns that will arrive via the stairs: their
        /// spacing is computed from their stairwell exit, so reachability and
        /// walls on the target level are respected.</summary>
        private static void RecomputeDestinations()
        {
            for (int i = 0; i < dests.Count; i++)
            {
                dests[i] = IntVec3.Invalid;
            }
            float denom = (pawns.Count <= 1) ? 1 : (pawns.Count - 1);
            for (int j = 0; j < pawns.Count; j++)
            {
                Pawn pawn = pawns[j];
                if (!pawn.Spawned)
                {
                    continue;
                }
                IntVec3 root;
                if (targetMap.exitMapGrid.IsExitCell(end))
                {
                    root = end;
                }
                else
                {
                    float t = (float)j / denom;
                    root = (start.ToVector3() + (end.ToVector3() - start.ToVector3()) * t).ToIntVec3();
                }
                IntVec3 dest;
                if (entries[j] == null)
                {
                    dest = RCellFinder.BestOrderedGotoDestNear(root, pawn, c => CanGoTo(pawn, c));
                }
                else
                {
                    Building_ABStairs exit = entries[j].CounterpartTowards(targetMap);
                    if (exit == null)
                    {
                        continue;
                    }
                    if (!ABVirtualPosition.TrySwap(pawn, targetMap, exit.Position, out ABVirtualPosition.Token token))
                    {
                        continue;
                    }
                    try
                    {
                        dest = RCellFinder.BestOrderedGotoDestNear(root, pawn, c => CanGoTo(pawn, c));
                    }
                    finally
                    {
                        ABVirtualPosition.Restore(pawn, token);
                    }
                }
                if (ModsConfig.BiotechActive && pawn.IsColonyMech
                    && !MechanitorUtility.InMechanitorCommandRange(pawn, dest))
                {
                    dest = IntVec3.Invalid;
                }
                dests[j] = dest;
            }
        }

        private static bool CanGoTo(Pawn pawn, IntVec3 c)
        {
            if (dests.Contains(c))
            {
                return false;
            }
            if (ModsConfig.BiotechActive && pawn.IsColonyMech
                && !MechanitorUtility.InMechanitorCommandRange(pawn, c))
            {
                return false;
            }
            return true;
        }

        /// <summary>Release: pawns on the target level get the vanilla goto;
        /// cross pawns ride the stairs and walk to their assigned formation cell
        /// on arrival (rerouted toward it, replayed via ABPendingOrders).</summary>
        private static void IssueGotoJobs()
        {
            List<Pawn> ordered = new List<Pawn>(pawns);
            List<IntVec3> cells = new List<IntVec3>(dests);
            List<Building_ABStairs> stairs = new List<Building_ABStairs>(entries);
            Map target = targetMap;
            IntVec3 endCopy = end;
            Cancel();
            bool any = false;
            for (int i = 0; i < ordered.Count; i++)
            {
                Pawn p = ordered[i];
                IntVec3 dest = cells[i];
                if (!dest.IsValid)
                {
                    continue;
                }
                if (stairs[i] == null)
                {
                    FloatMenuOptionProvider_DraftedMove.PawnGotoAction(endCopy, p, dest);
                    any = true;
                    continue;
                }
                Building_ABStairs entry = stairs[i];
                Building_ABStairs exit = entry.CounterpartTowards(target);
                if (exit == null)
                {
                    continue;
                }
                StairRouter.Reroute(p, target, dest, ref entry, ref exit);
                Pawn pawnCopy = p;
                IntVec3 destCopy = dest;
                CrossLevelOrders.RouteThenRun(p, target, entry, delegate
                {
                    IntVec3 gotoLoc = RCellFinder.BestOrderedGotoDestNear(destCopy, pawnCopy);
                    if (gotoLoc.IsValid)
                    {
                        FloatMenuOptionProvider_DraftedMove.PawnGotoAction(endCopy, pawnCopy, gotoLoc);
                    }
                });
                any = true;
            }
            if (any)
            {
                SoundDefOf.ColonistOrdered.PlayOneShotOnCamera();
            }
        }

        /// <summary>Ghost pawns + goto circles at every assigned cell and the
        /// vanilla between-line from drag start to the hovered cell, drawn at the
        /// shifted below positions when the target is the level below.</summary>
        private static void DrawPreview(bool belowMode)
        {
            if (circleMat == null)
            {
                circleMat = MaterialPool.MatFrom("UI/Overlays/Circle75Solid",
                    ShaderDatabase.Transparent, FeedbackColor * new Color(1f, 1f, 1f, 0.4f));
                lineMat = MaterialPool.MatFrom("UI/Overlays/ThickLine",
                    ShaderDatabase.Transparent, FeedbackColor * new Color(1f, 1f, 1f, 0.18f));
            }
            float alt = AltitudeLayer.MetaOverlays.AltitudeFor();
            float circleAlt = alt + 0.03658537f;
            float lineAlt = alt - 0.03658537f;
            Vector3 scale = new Vector3(1.7f, 1f, 1.7f);
            for (int i = 0; i < pawns.Count; i++)
            {
                IntVec3 c = dests[i];
                if (!c.IsValid || !pawns[i].Spawned || c.Fogged(targetMap))
                {
                    continue;
                }
                Vector3 drawLoc = WorldPos(c, belowMode, alt);
                pawns[i].Drawer.renderer.RenderPawnAt(drawLoc, Rot4.South);
                Vector3 circlePos = drawLoc;
                circlePos.y = circleAlt;
                Graphics.DrawMesh(MeshPool.plane10,
                    Matrix4x4.TRS(circlePos, Quaternion.identity, scale), circleMat, 0);
            }
            Vector3 a = WorldPos(start, belowMode, lineAlt);
            Vector3 b = WorldPos(end, belowMode, lineAlt);
            GenDraw.DrawLineBetween(a, b, lineMat, 0.9f);
        }

        /// <summary>Hovering pawn labels over the ghosts, exactly vanilla's OnGUI
        /// pass. Called from LevelComp.MapComponentOnGUI on the viewed map.</summary>
        internal static void OnGUIUpdate(Map cur)
        {
            if (pawns.Count == 0 || targetMap == null)
            {
                return;
            }
            try
            {
                bool belowMode = targetMap != cur;
                for (int i = 0; i < pawns.Count; i++)
                {
                    IntVec3 c = dests[i];
                    if (!c.IsValid || !pawns[i].Spawned || c.Fogged(targetMap))
                    {
                        continue;
                    }
                    Vector2 min = WorldPos(c, belowMode, 0f, shifted: false).MapToUIPosition();
                    Vector2 max = (WorldPos(c, belowMode, 0f, shifted: false)
                        + new Vector3(1f, 0f, 1f)).MapToUIPosition();
                    Vector2 pos = new Vector2((min.x + max.x) * 0.5f, Mathf.Max(min.y, max.y) + 5f);
                    GenMapUI.DrawPawnLabel(pawns[i], pos, 0.5f);
                }
            }
            catch (Exception e)
            {
                Cancel();
                ABGuard.Disable(ABGuard.Ui, e, "cross level goto labels");
            }
        }

        /// <summary>A target-map cell's draw position on the viewed map: shifted
        /// through the see-below transform when aiming at the level below.
        /// shifted=true centers the cell (ghost/circle draw); false returns the
        /// cell corner (UI rect math).</summary>
        private static Vector3 WorldPos(IntVec3 c, bool belowMode, float alt, bool shifted = true)
        {
            Vector3 world = shifted ? c.ToVector3Shifted() : c.ToVector3();
            if (belowMode)
            {
                world = LevelRenderer.ShiftedBelowDrawPos(world);
            }
            if (shifted)
            {
                world.y = alt;
            }
            return world;
        }
    }
}
