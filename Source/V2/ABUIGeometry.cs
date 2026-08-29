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
    /// V2 UI geometry: the presentation half of the band problem.
    ///
    /// Same root cause as the combat maths. Every one of these draws or hit-tests against a
    /// target's REAL cell, which for anything on another band is a Slot away - so the
    /// targeting cursor cannot find a pawn you are looking straight at, and job lines shoot
    /// off the top of the screen toward a destination 256 cells north.
    ///
    /// These are the "geometry, not graph" cases: the wormhole fixes reachability and
    /// vanilla's logic follows, but nothing about a RegionLink teaches the UI that two cells
    /// 256 apart are vertically adjacent.
    /// </summary>
    public static class ABUIGeometry
    {
        /// <summary>
        /// THE ONE TRANSFORM: a world position, lifted from whatever band it sits on into the
        /// band currently being VIEWED.
        ///
        /// ⚠ VIEW BAND, NOT PAWN BAND, AND THAT IS A BUG FIX. This used to localize onto the
        /// commanded pawn's own band, which is identical whenever you are looking at that
        /// pawn's level - so it was right in every situation anyone tested. It is wrong the
        /// moment you look DOWN at a pawn, because `ABBelowDynamicDraw` draws a below pawn at
        /// `DrawPos.z + drop`, i.e. lifted into the viewed band. Localizing its overlays onto
        /// the pawn's real band therefore drew them a full Slot below the sprite they belong
        /// to - off screen. Every selection overlay must agree with where the pawn is DRAWN,
        /// and the renderer is the authority on that.
        ///
        /// ⚠ THIS TRANSFORM EXISTED THREE TIMES BEFORE THIS WAS WRITTEN and the copies had
        /// drifted apart: `LocalizeForPawn` here (pawn band), `Lift` inside the
        /// MultiPawnGotoController patch (view band), and `loc.z += drop` inside
        /// ABBelowDynamicDraw (view band). Two of three were right, which is exactly how a
        /// duplicated invariant dies. Everything routes through here now - do not write a
        /// fourth.
        /// </summary>
        public static Vector3 LiftToView(Map map, Vector3 world)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return world;
            }
            return LiftToView(bands, ABBandView.CurrentBand(map), world);
        }

        /// <summary>Same transform with the band comp and view band already resolved, for
        /// callers drawing many points in one pass.</summary>
        public static Vector3 LiftToView(ABBandMap bands, int viewBand, Vector3 world)
        {
            if (bands == null || !bands.Banded)
            {
                return world;
            }
            int band = bands.BandOf(world.ToIntVec3());
            if (band < 0 || band == viewBand)
            {
                return world;
            }
            return new Vector3(world.x, world.y, world.z + (viewBand - band) * bands.Slot);
        }

        /// <summary>Cell overload, shifted to the cell centre at a fixed altitude.</summary>
        public static Vector3 LiftToView(ABBandMap bands, int viewBand, IntVec3 c, float altitude)
        {
            return LiftToView(bands, viewBand, c.ToVector3ShiftedWithAltitude(altitude));
        }

        /// <summary>Kept as the name every UI call site already uses. Delegates to the view
        /// band transform; the pawn argument now only supplies the map.</summary>
        public static Vector3 LocalizeForPawn(Pawn pawn, Vector3 world)
        {
            if (pawn == null || !pawn.Spawned)
            {
                return world;
            }
            return LiftToView(pawn.Map, world);
        }
    }

    // ⚠⚠ TOMBSTONE - `Patch_GenUI_ABTargetsAtThroughFloor` LIVED HERE (window 8) AND WAS
    // DELETED IN WINDOW 12. It was a PREFIX on GenUI.TargetsAt that rewrote `clickPos` down
    // to the visible band. Window 9's ABCombatTargeting then added a POSTFIX on the SAME
    // METHOD doing the same job properly - and the two silently cancelled each other out for
    // a whole shipped year.
    //
    // HOW THEY FOUGHT, because the shape is worth keeping:
    //   * vanilla's TargetsAt resolves THINGS from the `clickPos` argument but the bare CELL
    //     from `UI.MouseCell()`, which no prefix can touch;
    //   * so the prefix fixed the thing half and could never fix the cell half;
    //   * and Harmony hands a POSTFIX the arguments AS THE PREFIX LEFT THEM, so
    //     Patch_GenUI_ABTargetsAtSeeThrough received an ALREADY-DESCENDED clickPos, asked
    //     "is this cell open air?" about a cell that is usually solid floor, got NO, and
    //     declined to substitute. The yielded target stayed the raw sky cell.
    //
    // Player-visible result: you could point a psycast at a PAWN one level down (thing half,
    // via the prefix) but every CELL-targeted verb - jump packs, Verb_CastAbilityJump, Skip's
    // destination, any location psycast - resolved onto the empty air cell, where
    // JumpUtility.ValidJumpTarget refuses (open air is Impassable) and drew the red
    // TexCommand.CannotShoot cursor. Reported as "jumping and psycasts click through the air
    // tiles".
    //
    // ⚠ THE LESSON IS A NEW SHAPE OF §33b: TWO OF OUR OWN PATCHES ON ONE VANILLA METHOD.
    // The old rule was "when a fix lands in a click handler, find every other handler for the
    // same gesture". This one is tighter - GREP OUR OWN [HarmonyPatch] LIST FOR THE TARGET
    // METHOD BEFORE ADDING A PATCH TO IT. Nothing warns you: Harmony applies both happily,
    // and a prefix that pre-consumes a postfix's input is invisible in every log.
    //
    // Do not reinstate it. The see-through target rule is ONE pair of postfixes now:
    // Patch_GenUI_ABThingsUnderMouseSeeThrough (append the things the column shows) and
    // Patch_GenUI_ABTargetsAtSeeThrough (substitute the cell the column shows).

    /// <summary>
    /// JOB LINES. The white lines from a selected pawn to its destination and queued
    /// targets are drawn to real positions, so a cross-band job draws a line hundreds of
    /// cells off the screen - read as "the pathing line skips around".
    ///
    /// Reimplemented rather than postfixed because the geometry is a CHAIN: each segment
    /// starts where the previous ended, so individual endpoints cannot be corrected after
    /// the fact.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.DrawLinesBetweenTargets))]
    public static class Patch_JobTracker_ABLocalizeJobLines
    {
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

        private static bool Prefix(Pawn_JobTracker __instance)
        {
            Pawn pawn;
            try
            {
                pawn = PawnRef(__instance);
                if (pawn == null || !pawn.Spawned)
                {
                    return true;
                }
                ABBandMap bands = ABBands.CompOf(pawn.Map);
                if (bands == null || !bands.Banded)
                {
                    return true; // ordinary map: vanilla
                }
            }
            catch
            {
                return true;
            }

            try
            {
                float alt = AltitudeLayer.Item.AltitudeFor();
                // ⚠ THE CHAIN START WAS THE ONE UNLIFTED POINT IN THIS METHOD, AND IT DREW A
                // LINE STRAIGHT UP OR DOWN THE MAP. Every other endpoint below goes through
                // LocalizeForPawn; this one used the pawn's raw world position. On the pawn's
                // own band that is identical, so it was invisible - but when you look DOWN at
                // a pawn the renderer draws it lifted into the viewed band while this point
                // stayed a full Slot below, so the first segment ran vertically off the
                // screen toward the pawn's real coordinates. Exactly the same defect as the
                // one fixed in LocalizeForPawn itself, in the same file, missed because the
                // fix was applied to the helper and not audited at the call sites.
                Vector3 a = ABUIGeometry.LocalizeForPawn(pawn, pawn.DrawPos);
                a.y = alt;
                if (pawn.pather.curPath != null)
                {
                    a = ABUIGeometry.LocalizeForPawn(pawn, pawn.pather.Destination.CenterVector3);
                }
                else if (__instance.curJob != null && __instance.curJob.def != JobDefOf.LayDown
                    && __instance.curJob.targetA.IsValid
                    && (!__instance.curJob.targetA.HasThing
                        || (__instance.curJob.targetA.Thing.Spawned
                            && __instance.curJob.targetA.Thing.Map == pawn.Map)))
                {
                    Vector3 b = ABUIGeometry.LocalizeForPawn(pawn, __instance.curJob.targetA.CenterVector3);
                    GenDraw.DrawLineBetween(a, b, alt);
                    a = b;
                }
                JobQueue queue = __instance.jobQueue;
                if (queue == null)
                {
                    return false;
                }
                for (int i = 0; i < queue.Count; i++)
                {
                    Job job = queue[i].job;
                    if (job.targetA.IsValid)
                    {
                        if (!job.targetA.HasThing
                            || (job.targetA.Thing.Spawned && job.targetA.Thing.Map == pawn.Map))
                        {
                            Vector3 c = ABUIGeometry.LocalizeForPawn(pawn, job.targetA.CenterVector3);
                            GenDraw.DrawLineBetween(a, c, alt);
                            a = c;
                        }
                        continue;
                    }
                    List<LocalTargetInfo> queueA = job.targetQueueA;
                    if (queueA == null)
                    {
                        continue;
                    }
                    for (int j = 0; j < queueA.Count; j++)
                    {
                        if (!queueA[j].HasThing
                            || (queueA[j].Thing.Spawned && queueA[j].Thing.Map == pawn.Map))
                        {
                            Vector3 d = ABUIGeometry.LocalizeForPawn(pawn, queueA[j].CenterVector3);
                            GenDraw.DrawLineBetween(a, d, alt);
                            a = d;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: job line draw threw: " + e, 762195880);
            }
            return false;
        }
    }
}
