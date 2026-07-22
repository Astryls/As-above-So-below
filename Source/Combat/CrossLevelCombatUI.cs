using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Player feedback for cross-level combat. Three layers:
    ///
    /// 1. Targeter hover CURSOR (TargeterOnGUI prefix): vanilla's target-under-mouse
    ///    query is current-map-only, so hovering a below target showed "cannot shoot".
    ///    We draw the source's proper cursor when a below target would be accepted.
    /// 2. Targeter hover CROSSHAIR (TargeterUpdate postfix): the rotating target
    ///    highlight, drawn at the position the below target actually renders at
    ///    (shifted), so aiming across the gap reads exactly like same-map aiming.
    /// 3. ALWAYS-ON engagement lines (drawn per frame from LevelComp.MapComponentUpdate,
    ///    not selection-gated): every active cross-level shooter - pawn or turret -
    ///    draws a line to its target whenever either end is on the viewed map, with the
    ///    vanilla aim pie while a pawn warms up. Endpoints are altitude-clamped to the
    ///    overlay layer (the raw see-below shift carries y = -2.5, which buried the
    ///    line under the below-view band - the round-2 "no line" report).
    ///
    /// Registry: JobDriver_ABCrossLevelAttack registers/unregisters its pawn (load-safe
    /// via MakeNewToils); turrets are enumerated from CrossLevelTurret's own store.
    /// Everything gates on ABGuard.Ui and empty-set early-outs.
    /// </summary>
    internal static class CrossLevelCombatUI
    {
        /// <summary>Pawns currently holding the cross-level attack job. Maintained by
        /// the driver (register in MakeNewToils, remove in a finish action), cleared on
        /// game load; draw-time filtering self-heals any stale entry.</summary>
        internal static readonly HashSet<Pawn> ActiveShooters = new HashSet<Pawn>();

        private static readonly List<Pawn> tmpStale = new List<Pawn>();

        /// <summary>Per-frame engagement visuals for the viewed map. Called from
        /// LevelComp.MapComponentUpdate; zero cost when nothing cross-fires.</summary>
        internal static void DrawEngagementVisuals(Map cur)
        {
            if (ActiveShooters.Count == 0)
            {
                return;
            }
            Map below = cur.Levels()?.lowerMap;
            tmpStale.Clear();
            foreach (Pawn p in ActiveShooters)
            {
                if (p == null || p.Dead || !p.Spawned
                    || !(p.jobs?.curDriver is JobDriver_ABCrossLevelAttack driver))
                {
                    tmpStale.Add(p);
                    continue;
                }
                Thing target = driver.Target;
                if (target == null || target.Destroyed || !target.Spawned)
                {
                    continue;
                }
                if (p.Map == cur)
                {
                    // Shooter on the viewed level; target below (shifted) or above
                    // (column-aligned).
                    Vector3 end = target.MapHeld == below
                        ? LevelRenderer.ShiftedBelowDrawPos(target.DrawPos)
                        : target.DrawPos;
                    DrawLine(p.DrawPos, end);
                    if (driver.Warming)
                    {
                        GenDraw.DrawAimPie(p, new LocalTargetInfo(target),
                            (int)(driver.WarmupTicksLeft * 0.5f), 0.2f);
                    }
                    if (Find.Selector.IsSelected(p))
                    {
                        // The vanilla "who am I attacking" target highlight, which
                        // vanilla itself cannot draw for a cross-map job target.
                        DrawTargetMarker(end);
                    }
                }
                else if (below != null && p.Map == below)
                {
                    // Shooter seen through the hole, firing up at the viewed level.
                    DrawLine(LevelRenderer.ShiftedBelowDrawPos(p.DrawPos), target.DrawPos);
                    if (Find.Selector.IsSelected(p))
                    {
                        DrawTargetMarker(target.DrawPos);
                    }
                }
            }
            for (int i = 0; i < tmpStale.Count; i++)
            {
                ActiveShooters.Remove(tmpStale[i]);
            }
        }

        /// <summary>Line with both endpoints clamped to the overlay altitude - the raw
        /// shifted vector carries the below-band's negative y.</summary>
        internal static void DrawLine(Vector3 a, Vector3 b)
        {
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            a.y = y;
            b.y = y;
            GenDraw.DrawLineBetween(a, b);
        }

        /// <summary>The vanilla target highlight on the victim of a selected
        /// cross-level attacker. One marker only - the crosshair IS vanilla's look
        /// (the extra red circle read as a doubled UI, round-5 report).</summary>
        internal static void DrawTargetMarker(Vector3 at)
        {
            GenDraw.DrawTargetHighlightWithLayer(at, AltitudeLayer.MetaOverlays);
        }

        /// <summary>Shared hover classification for the cursor prefix and the
        /// crosshair postfix: the below target this source would accept at the mouse,
        /// if any. Same-map targets always defer to vanilla.</summary>
        internal static bool TryGetBelowHover(ITargetingSource source, out LocalTargetInfo target, out Vector3 drawAt)
        {
            target = LocalTargetInfo.Invalid;
            drawAt = Vector3.zero;
            if (source == null)
            {
                return false;
            }
            if (!BelowSelection.TryGetBelowView(out Map sky, out Map lower) || sky != Find.CurrentMap)
            {
                return false;
            }
            Vector3 mouse = UI.MouseMapPosition();

            if (source.Caster is Building_Turret turret)
            {
                Verb_LaunchProjectile tv = CrossLevelTurret.LauncherVerb(turret);
                IntVec3 skyCell = mouse.ToIntVec3();
                if (tv == null || !skyCell.InBounds(sky))
                {
                    return false;
                }
                bool arc = CrossLevelTurret.IsArc(tv);
                bool overOpenAir = sky.terrainGrid.TerrainAt(skyCell) == ABDefOf.AB_OpenAir;
                if (turret.Map == sky && overOpenAir)
                {
                    List<Thing> belowThings = BelowSelection.SelectablesUnderMouse(sky, lower, mouse);
                    if (belowThings.Count > 0 && !arc
                        && CrossLevelTurret.TurretCanFire(turret, belowThings[0], tv, out _))
                    {
                        target = belowThings[0];
                        drawAt = LevelRenderer.ShiftedBelowDrawPos(belowThings[0].DrawPos);
                        return true;
                    }
                    if (arc)
                    {
                        IntVec3 cell = LevelRenderer.ScreenToBelowPos(mouse).ToIntVec3();
                        if (cell.InBounds(lower)
                            && CrossLevelCombat.CanArcFireAt(sky, turret.Position, cell, lower, tv, out _))
                        {
                            target = cell;
                            drawAt = LevelRenderer.ShiftedBelowDrawPos(cell.ToVector3Shifted());
                            return true;
                        }
                    }
                }
                else if (turret.Map == lower && !overOpenAir)
                {
                    if (arc && CrossLevelCombat.CanArcFireAt(lower, turret.Position, skyCell, sky, tv, out _))
                    {
                        target = skyCell;
                        drawAt = skyCell.ToVector3Shifted();
                        return true;
                    }
                    if (!arc)
                    {
                        foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(AttackAnyParams, thingsOnly: true))
                        {
                            if (lt.Thing != null && CrossLevelTurret.TurretCanFire(turret, lt.Thing, tv, out _))
                            {
                                target = lt.Thing;
                                drawAt = lt.Thing.DrawPos;
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            Pawn caster = source.CasterPawn;
            if (caster == null)
            {
                return false;
            }
            // Same-level target under the mouse? Vanilla handles it perfectly.
            TargetingParameters sameMapParams = source.targetParams ?? AttackAnyParams;
            foreach (LocalTargetInfo lt in GenUI.TargetsAtMouse(sameMapParams, thingsOnly: true))
            {
                if (lt.Thing != null)
                {
                    return false;
                }
            }
            List<Thing> belowList = BelowSelection.SelectablesUnderMouse(sky, lower, mouse);
            for (int i = 0; i < belowList.Count; i++)
            {
                // The source's own targetParams decide validity - friendlies included,
                // exactly like vanilla's targeter (round-4 parity fix).
                bool valid = belowList[i] != caster && source.targetParams != null
                    && source.targetParams.CanTarget(new TargetInfo(belowList[i]), source);
                if (valid)
                {
                    target = belowList[i];
                    drawAt = LevelRenderer.ShiftedBelowDrawPos(belowList[i].DrawPos);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Cached: ForAttackAny() allocates a fresh TargetingParameters per
        /// call, and hover logic runs every GUI frame while a targeter is up.</summary>
        internal static readonly TargetingParameters AttackAnyParams = TargetingParameters.ForAttackAny();
    }

    /// <summary>Hover cursor: the source's own attack/ability cursor over a valid
    /// below target instead of vanilla's "cannot shoot".</summary>
    [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterOnGUI))]
    internal static class Patch_Targeter_OnGUI_BelowTarget
    {
        private static bool Prefix(Targeter __instance)
        {
            if (!ABGuard.On(ABGuard.Ui))
            {
                return true;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return true;
            }
            try
            {
                ITargetingSource source = __instance.targetingSource;
                if (source == null)
                {
                    return true; // melee/squad action path draws its own cursor
                }
                if (!CrossLevelCombatUI.TryGetBelowHover(source, out LocalTargetInfo target, out _))
                {
                    return true;
                }
                source.OnGUI(target);
                if (source.GetVerb?.verbProps?.mouseTargetingText != null)
                {
                    Widgets.MouseAttachedLabel(source.GetVerb.verbProps.mouseTargetingText);
                }
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "cross level targeter cursor");
                return true;
            }
        }
    }

    /// <summary>Hover crosshair: the rotating target highlight at the position the
    /// below target renders at, so cross-gap aiming reads like same-map aiming.</summary>
    [HarmonyPatch(typeof(Targeter), nameof(Targeter.TargeterUpdate))]
    internal static class Patch_Targeter_Update_BelowTarget
    {
        private static void Postfix(Targeter __instance)
        {
            if (!ABGuard.On(ABGuard.Ui))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                ITargetingSource source = __instance.targetingSource;
                if (source == null)
                {
                    return;
                }
                if (CrossLevelCombatUI.TryGetBelowHover(source, out _, out Vector3 drawAt))
                {
                    GenDraw.DrawTargetHighlightWithLayer(drawAt, AltitudeLayer.MetaOverlays);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "cross level targeter crosshair");
            }
        }
    }
}
