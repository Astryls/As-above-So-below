using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 GLOBAL CLICK-THROUGH: an open-air cell is not a place. The cursor points at what
    /// the column SHOWS.
    ///
    /// ⚠⚠ THIS INVERTS THE MODEL THE MOD HAD USED SINCE WINDOW 7. The descend used to be
    /// OPT-IN, added by hand at each consumer that mattered - the float menu, the target
    /// list, the target cell, left-click selection. Four patches, and every new consumer
    /// (a dev tool, a psycast, another mod's targeter) started out broken and had to be
    /// noticed, diagnosed and patched individually. It is now OPT-OUT: the two methods every
    /// consumer in the game bottoms out in - <c>UI.MouseCell</c> and
    /// <c>UI.MouseMapPosition</c> - answer with the visible cell, and the handful of callers
    /// that genuinely need the RAW cursor suppress it.
    ///
    /// The justification is that there is no reading of "I clicked the empty sky" that means
    /// the sky. Open air is Impassable, unselectable, unbuildable and undesignatable; every
    /// verb that resolves onto it draws the red CannotShoot cursor. So the raw answer is
    /// never the useful one, and the see-through answer is always what the player meant.
    ///
    /// ⚠ THE CHOKEPOINT IS REAL AND IT IS TWO METHODS. Both are one-line wrappers over
    /// <c>UI.UIToMapPosition</c>. That third method is NOT patched and must not be: it is a
    /// pure screen-to-map transform used for drag-box corners, the camera's own centre and
    /// map-to-UI round trips, none of which are "where is the cursor pointing".
    ///
    /// WHAT THIS REPLACED, so nobody re-adds it (rule 50 - grep our own patch list first):
    ///   * Patch_GenUI_ABThingsUnderMouseSeeThrough - DELETED. Vanilla now finds below-level
    ///     things natively, because the clickPos it searches around has already descended.
    ///   * Patch_GenUI_ABTargetsAtSeeThrough - DELETED. Same reason for the bare CELL half;
    ///     TargetsAt yields UI.MouseCell(), which descends at the source now.
    /// Both are tombstoned in ABCombatTargeting. The remaining descend patches
    /// (Patch_FloatMenuMakerMap_ABClickThrough, Patch_Selector_ABSelectThrough) are NOT
    /// redundant - they encode ORDER POLICY and SELECTION POLICY, which are different
    /// questions from pointing, and their inputs are deliberately suppressed back to raw.
    /// </summary>
    /// <remarks>
    /// ⚠ POINTING IS NOT ORDERING, AND KEEPING THEM APART IS WHAT MAKES THIS SAFE.
    /// "Which cell is under the cursor" is answered here, globally. "Where should these
    /// pawns go" is a different question that also depends on the pawns, and it stays in
    /// ABBelowClickThrough.TryTranslateForOrder - which is field-verified (§33b/§33f) and
    /// whose whole model assumes a RAW cursor. Rather than rewrite it against a new input
    /// contract, Selector.HandleMapClicks is suppressed, so it receives exactly the value it
    /// always has. Same for left-click selection, whose curtain clamp compares the cursor
    /// against the VIEWED band's rows and would reject every open-air click outright if it
    /// were handed a descended cell.
    /// </remarks>
    public static class ABMouseDescend
    {
        /// <summary>Dev A/B switch ("AB2: bisect - toggle click-through" also gates this via
        /// ABBelowClickThrough.Enabled). Set false to get vanilla pointing back.</summary>
        internal static bool Enabled = true;

        /// <summary>Observe-only, read by `AB2: combat report`.</summary>
        public static int descends;

        // ------------------------------------------------------------------------------
        // SUPPRESSION
        // ------------------------------------------------------------------------------

        private static int suppress;

        /// <summary>⚠ FRAME-STAMPED, AND THAT IS A DELIBERATE BACKSTOP (rule 40's shape).
        /// Every push/pop pair below completes inside one frame, so a counter still standing
        /// on a later frame can only be a leak - an exception that escaped past a Finalizer,
        /// a foreign mod skipping a patched method with a prefix. Left alone it would wedge
        /// pointing back to vanilla for the rest of the session and look exactly like "the
        /// feature silently doesn't work". Stamping it means the worst case is one bad
        /// frame.</summary>
        private static int suppressFrame = -1;

        internal static void Push()
        {
            if (suppressFrame != Time.frameCount)
            {
                suppress = 0;
                suppressFrame = Time.frameCount;
            }
            suppress++;
        }

        internal static void Pop()
        {
            if (suppress > 0)
            {
                suppress--;
            }
        }

        internal static bool Suppressed => suppress > 0 && suppressFrame == Time.frameCount;

        /// <summary>
        /// The RAW cursor, for our own code that needs the un-descended value.
        ///
        /// ⚠ EXPLICIT RATHER THAN AMBIENT, because relying on a caller being inside a
        /// suppressed region is relying on a call graph nobody re-checks.
        /// <c>Selector.SelectUnderMouse</c> is called from three places, only two of which
        /// are under <c>HandleMapClicks</c>; that is exactly the kind of detail that rots.
        /// </summary>
        public static Vector3 RawMouseMapPosition()
        {
            Push();
            try
            {
                return UI.MouseMapPosition();
            }
            finally
            {
                Pop();
            }
        }

        // ------------------------------------------------------------------------------
        // THE RESOLVE, MEMOISED
        // ------------------------------------------------------------------------------

        // ⚠ UI.MouseCell() IS CALLED DOZENS OF TIMES PER FRAME - 228 call sites across 46
        // vanilla files, several inside per-cell loops - and the resolve behind it is a
        // terrain read plus a walk down the column plus a fog probe. Memoised on the RAW
        // cell rather than on the frame alone: OnGUI runs once per GUI EVENT, and a
        // MouseDown event carries its own mouse position, so two different raw cells can be
        // asked for within one frameCount.
        private static int memoFrame = -1;

        private static IntVec3 memoRaw = IntVec3.Invalid;

        private static int memoDrop;

        private static bool TryDrop(IntVec3 raw, out int drop)
        {
            drop = 0;
            if (!Enabled || Suppressed || !ABBelowClickThrough.Enabled
                || !ABGuard.On(ABGuard.Rendering))
            {
                return false;
            }
            if (Current.ProgramState != ProgramState.Playing)
            {
                return false;
            }
            if (memoFrame == Time.frameCount && memoRaw == raw)
            {
                drop = memoDrop;
                return drop > 0;
            }
            Map map = Find.CurrentMap;
            ABBandMap bands = map == null ? null : ABBands.CompOf(map);
            int found = 0;
            if (bands != null && bands.Banded
                && ABBands.TryResolveVisibleFrom(map, bands, raw, requireUnfogged: true,
                    out IntVec3 _, out int d))
            {
                found = d;
            }
            memoFrame = Time.frameCount;
            memoRaw = raw;
            memoDrop = found;
            drop = found;
            return found > 0;
        }

        internal static IntVec3 Descend(IntVec3 raw)
        {
            try
            {
                if (!TryDrop(raw, out int drop))
                {
                    return raw;
                }
                descends++;
                return new IntVec3(raw.x, raw.y, raw.z - drop);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 global click-through (cell)");
                return raw;
            }
        }

        internal static Vector3 Descend(Vector3 raw)
        {
            try
            {
                // Keep the sub-cell fraction: callers that compare against Thing.TrueCenter
                // within a quarter cell (GenUI.ThingsUnderMouse does, twice) need it.
                if (!TryDrop(IntVec3.FromVector3(raw), out int drop))
                {
                    return raw;
                }
                descends++;
                return new Vector3(raw.x, raw.y, raw.z - drop);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 global click-through (position)");
                return raw;
            }
        }

        public static string CounterReport()
        {
            return "mouse descend: applied=" + descends
                + " enabled=" + Enabled + " suppressedNow=" + Suppressed;
        }
    }

    /// <summary>Half of the chokepoint. See the banner in <see cref="ABMouseDescend"/>.</summary>
    [HarmonyPatch(typeof(UI), nameof(UI.MouseCell))]
    public static class Patch_UI_ABMouseCellDescend
    {
        private static void Postfix(ref IntVec3 __result)
        {
            __result = ABMouseDescend.Descend(__result);
        }
    }

    /// <summary>The other half. Both are needed: vanilla resolves THINGS from a Vector3
    /// clickPos but the bare CELL from UI.MouseCell(), and patching one without the other is
    /// precisely the §82a defect that shipped for a year.</summary>
    [HarmonyPatch(typeof(UI), nameof(UI.MouseMapPosition))]
    public static class Patch_UI_ABMouseMapPositionDescend
    {
        private static void Postfix(ref Vector3 __result)
        {
            __result = ABMouseDescend.Descend(__result);
        }
    }

    /// <summary>
    /// THE OPT-OUT LIST: every caller that means the RAW cursor.
    ///
    /// One patch class with a TargetMethods list rather than one class per method, so the
    /// list reads as a list and the whole exclusion policy is auditable in one place.
    /// A Finalizer (not a postfix) pops the counter, so an exception thrown inside any of
    /// these cannot leak suppression into the rest of the frame.
    ///
    /// THREE REASONS A METHOD IS ON THIS LIST, and each entry says which:
    ///   (a) DESIGNATION AND PLACEMENT - the user's explicit call: build and mine stay
    ///       clamped to the viewed level (rule 1). Descending here would mean building
    ///       through the floor, and worse, a drag rect with one corner on each side of a
    ///       band seam designates straight across the gutter - §46's field report, which
    ///       was about mining ghost walls one level down, walking back in a new door.
    ///   (b) A DELTA OF TWO MOUSE READS - arithmetic that is only valid if both reads share
    ///       a coordinate space. Descending one and not the other builds an oscillator
    ///       (rule 24).
    ///   (c) OURS ALREADY - a path where this mod has field-verified policy code that was
    ///       written against a raw cursor. Suppressing costs nothing and preserves a tested
    ///       behaviour instead of re-deriving it (rule 49).
    /// </summary>
    [HarmonyPatch]
    public static class Patch_ABMouseDescendSuppress
    {
        /// <summary>
        /// ⚠ NAMES EVERY ENTRY IT COULD NOT RESOLVE (rule 33). A null dropped silently here
        /// is not a cosmetic gap - it is one exclusion quietly removed. Losing the
        /// CameraDriver setter alone means the camera jumping a level on a scroll notch, and
        /// the failure would present as a camera bug with nothing in the log tying it to a
        /// rename. Losing them ALL (a wholesale vanilla refactor) is reported as an error,
        /// because at that point the opt-out policy does not exist.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            int found = 0;
            int missing = 0;
            foreach (KeyValuePair<string, MethodBase> entry in Targets())
            {
                if (entry.Value == null)
                {
                    missing++;
                    Log.Warning(ABLog.Tag + " V2: click-through exclusion \"" + entry.Key
                        + "\" did not resolve; that caller will now see the DESCENDED cursor.");
                    continue;
                }
                found++;
                yield return entry.Value;
            }
            if (found == 0)
            {
                Log.Error(ABLog.Tag + " V2: NO click-through exclusions resolved (" + missing
                    + " missing). Designators and the camera will see the descended cursor;"
                    + " set ABMouseDescend.Enabled = false to restore vanilla pointing.");
            }
            else
            {
                ABLog.Dev("Global click-through: " + found + " exclusion(s) installed, "
                    + missing + " missing.");
            }
        }

        private static KeyValuePair<string, MethodBase> E(string name, MethodBase m)
        {
            return new KeyValuePair<string, MethodBase>(name, m);
        }

        private static IEnumerable<KeyValuePair<string, MethodBase>> Targets()
        {
            // (c) OURS ALREADY. HandleMapClicks carries BOTH the right-click float-menu
            // clickPos - owned by Patch_FloatMenuMakerMap_ABClickThrough, whose order model
            // (§33b: pull onto the pawns' band unless they can genuinely reach) assumes a raw
            // cursor - AND `dragBox.start`, which is projected back to screen space by
            // DragBox.ScreenRect. A descended start would anchor the selection rectangle a
            // whole Slot away from where the player pressed the button.
            yield return E("Selector.HandleMapClicks",
                AccessTools.Method(typeof(Selector), "HandleMapClicks"));
            // Left-click selection is owned by Patch_Selector_ABSelectThrough, which both
            // appends what the column shows AND clamps away the curtain void. Its clamp
            // compares the cursor against the VIEWED band's rows, so a descended cursor
            // would fall outside them and select NOTHING over open air - the exact opposite
            // of the feature. Listed explicitly rather than left to HandleMapClicks because
            // SelectUnderMouse has a third caller outside it.
            yield return E("Selector.SelectUnderMouse",
                AccessTools.Method(typeof(Selector), "SelectUnderMouse"));
            yield return E("Selector.SelectAllMatchingObjectUnderMouseOnScreen",
                AccessTools.Method(typeof(Selector),
                    "SelectAllMatchingObjectUnderMouseOnScreen"));

            // (a) DESIGNATION AND PLACEMENT.
            yield return E("DesignatorManager.ProcessInputEvents",
                AccessTools.Method(typeof(DesignatorManager),
                    nameof(DesignatorManager.ProcessInputEvents)));
            yield return E("DesignatorManager.DesignatorManagerUpdate",
                AccessTools.Method(typeof(DesignatorManager),
                    nameof(DesignatorManager.DesignatorManagerUpdate)));
            yield return E("DesignatorManager.DesignationManagerOnGUI",
                AccessTools.Method(typeof(DesignatorManager),
                    nameof(DesignatorManager.DesignationManagerOnGUI)));
            yield return E("DesignationDragger.StartDrag",
                AccessTools.Method(typeof(DesignationDragger),
                    nameof(DesignationDragger.StartDrag)));
            yield return E("DesignationDragger.GetCurrentBoundary",
                AccessTools.Method(typeof(DesignationDragger), "GetCurrentBoundary"));
            // Designator_Place's own reads (ghost, place workers, rotation handling). All are
            // reached from the DesignatorManager entries above, so these are belt and braces
            // for a subclass override that does not call base - but a build ghost drawn one
            // level down is a very visible failure, and ABDesignatorClamp is only a backstop
            // for the ACCEPT/REJECT half, not for where the ghost is drawn.
            yield return E("Designator_Place.SelectedUpdate",
                AccessTools.Method(typeof(Designator_Place),
                    nameof(Designator_Place.SelectedUpdate)));
            yield return E("Designator_Place.DrawMouseAttachments",
                AccessTools.Method(typeof(Designator_Place),
                    nameof(Designator_Place.DrawMouseAttachments)));
            yield return E("Designator_Place.HandleRotation",
                AccessTools.Method(typeof(Designator_Place), "HandleRotation"));

            // (b) A DELTA OF TWO MOUSE READS. CameraDriver's RootSize setter implements
            // zoom-to-mouse as `vector = MouseMapPosition(); <zoom>; rootPos += vector -
            // MouseMapPosition()`. The second read is taken AFTER the camera moved, so the
            // two reads are of different map positions - and if one of them happens to sit
            // over open air and the other over solid floor, the delta gains a spurious Slot
            // and the camera jumps a whole level on a scroll notch. Rule 24 exactly.
            yield return E("CameraDriver.RootSize setter",
                AccessTools.PropertySetter(typeof(CameraDriver), "RootSize"));

            // (c) OURS ALREADY. MultiPawnGotoController.ProcessInputEvents assigns
            // `end = UI.MouseCell()` raw, and §33f's normalise patch is built on that being
            // raw cursor noise which it then pulls onto START's band. Letting `end` descend
            // might well be an improvement - it would agree with `start` more often - but it
            // would silently alter a system that took four runs to get right, so it is
            // suppressed and left as a deliberate follow-up.
            yield return E("MultiPawnGotoController.ProcessInputEvents",
                AccessTools.Method(typeof(MultiPawnGotoController),
                    nameof(MultiPawnGotoController.ProcessInputEvents)));
        }

        private static void Prefix()
        {
            ABMouseDescend.Push();
        }

        private static void Finalizer()
        {
            ABMouseDescend.Pop();
        }
    }
}
