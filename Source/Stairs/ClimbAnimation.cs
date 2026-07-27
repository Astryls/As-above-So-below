using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Per-pawn snapshot driving the procedural climb/emerge
    /// animations. Written only on the main thread (toil actions, transfer,
    /// game component tick); read from the render workers, which may run on
    /// worker threads during the parallel pre-draw phase - hence the
    /// ConcurrentDictionary store and pure-math readers.</summary>
    internal struct ABClimbState
    {
        public int startTick;
        public int durationTicks;
        /// <summary>+1 climbing up, -1 climbing down.</summary>
        public int delta;
        /// <summary>Rung bob (vertical, no sway) instead of a step bob.</summary>
        public bool ladder;
        /// <summary>Arrival flourish on the destination map instead of a climb.</summary>
        public bool emerge;
        /// <summary>World-space slide: climb slides the pawn onto the stairwell
        /// as it sinks/rises; emerge slides it from the stairwell to its
        /// landing cell.</summary>
        public float slideX;
        public float slideZ;
    }

    /// <summary>Vanilla-render-tree climb animation for stairs and ladders:
    /// while the climb toil runs, the pawn bobs step by step and sinks+shrinks
    /// (down) or rises+grows (up) into the stairwell; after the transfer a
    /// short emerge flourish plays the reverse on the arrival map. Elevators
    /// are excluded (a ride, not a climb). Uses the native AnimationDef /
    /// BaseAnimationWorker pipeline so it composes with Yayo's, Melee
    /// Animation, and facial mods, and never stomps a foreign animation.
    /// Purely cosmetic: every entry point fails open via ABGuard.Rendering.</summary>
    public static class ClimbAnimation
    {
        private const int EmergeTicks = 22;
        /// <summary>Longest slide toward/from a stairwell center, in cells.
        /// Touch-adjacent cells are ~1 cell out; grand stairs corners a bit more.</summary>
        private const float SlideMax = 1.35f;
        /// <summary>Grace past the expected climb duration before the visuals
        /// auto-hide (a stalled job should not leave a permanently sunk pawn).</summary>
        private const int ClimbGraceTicks = 60;

        private static readonly ConcurrentDictionary<int, ABClimbState> states =
            new ConcurrentDictionary<int, ABClimbState>();

        /// <summary>Main-thread only: emerge animations to clear once finished.</summary>
        private static readonly List<(Pawn pawn, int clearTick)> pendingClears =
            new List<(Pawn pawn, int clearTick)>();

        private static AnimationDef climbDef;
        private static AnimationDef emergeDef;

        internal static AnimationDef ClimbDef =>
            climbDef ?? (climbDef = DefDatabase<AnimationDef>.GetNamedSilentFail("AB_Climb"));

        internal static AnimationDef EmergeDef =>
            emergeDef ?? (emergeDef = DefDatabase<AnimationDef>.GetNamedSilentFail("AB_Emerge"));

        private static bool Active =>
            ABGuard.On(ABGuard.Rendering) && (ABMod.Settings?.climbAnimations ?? true);

        /// <summary>Begin the climb visuals for a pawn standing at a stairwell.
        /// Called from the climb toil's pre-init on the main thread.</summary>
        public static void StartClimb(Pawn p, Building_ABStairs stairs)
        {
            try
            {
                if (!Active || p == null || !p.Spawned || stairs == null || !stairs.Spawned
                    || stairs is Building_ABElevator)
                {
                    return;
                }
                ABStairsExtension ext = stairs.def.GetModExtension<ABStairsExtension>();
                if (ext == null || ext.utilityOnly || ext.deltaLevel == 0)
                {
                    return;
                }
                AnimationDef def = ClimbDef;
                PawnRenderer renderer = p.Drawer?.renderer;
                if (def == null || renderer == null)
                {
                    return;
                }
                // Never stomp a foreign animation (Anomaly lunges, other mods).
                AnimationDef cur = renderer.CurAnimation;
                if (cur != null && cur != def && cur != EmergeDef)
                {
                    return;
                }
                Vector3 slide = stairs.DrawPos - p.DrawPos;
                slide.y = 0f;
                if (slide.magnitude > SlideMax)
                {
                    slide = slide.normalized * SlideMax;
                }
                states[p.thingIDNumber] = new ABClimbState
                {
                    startTick = GenTicks.TicksGame,
                    durationTicks = Mathf.Max(1, stairs.ClimbTicksFor(p)),
                    delta = ext.deltaLevel,
                    ladder = ext.ladder,
                    emerge = false,
                    slideX = slide.x,
                    slideZ = slide.z
                };
                if (cur != def)
                {
                    renderer.SetAnimation(def);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "climb animation start");
            }
        }

        /// <summary>Play the short arrival flourish after a stair transfer:
        /// the pawn slides out of the arrival stairwell, settling to its real
        /// position and scale. delta is the travel direction just completed
        /// (+1 arrived above, -1 arrived below).</summary>
        public static void StartEmerge(Pawn p, Building_ABStairs arrival, int delta)
        {
            try
            {
                if (!Active || p == null || !p.Spawned || delta == 0
                    || arrival == null || !arrival.Spawned || arrival is Building_ABElevator)
                {
                    return;
                }
                AnimationDef def = EmergeDef;
                PawnRenderer renderer = p.Drawer?.renderer;
                if (def == null || renderer == null)
                {
                    return;
                }
                AnimationDef cur = renderer.CurAnimation;
                if (cur != null && cur != def && cur != ClimbDef)
                {
                    return;
                }
                Vector3 slide = arrival.DrawPos - p.DrawPos;
                slide.y = 0f;
                if (slide.magnitude > SlideMax)
                {
                    slide = slide.normalized * SlideMax;
                }
                states[p.thingIDNumber] = new ABClimbState
                {
                    startTick = GenTicks.TicksGame,
                    durationTicks = EmergeTicks,
                    delta = delta,
                    ladder = arrival.def.GetModExtension<ABStairsExtension>()?.ladder ?? false,
                    emerge = true,
                    slideX = slide.x,
                    slideZ = slide.z
                };
                renderer.SetAnimation(def);
                if (pendingClears.Count > 64)
                {
                    pendingClears.Clear();
                }
                pendingClears.Add((p, GenTicks.TicksGame + EmergeTicks + 5));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "emerge animation start");
            }
        }

        /// <summary>End the climb visuals (toil finish action: success or any
        /// interrupt). Emerge states are left for the janitor - the climb
        /// job's own finish runs AFTER the transfer toil has already started
        /// the arrival flourish, and must not cut it short.</summary>
        public static void Stop(Pawn p)
        {
            try
            {
                if (p == null
                    || !states.TryGetValue(p.thingIDNumber, out ABClimbState s) || s.emerge)
                {
                    return;
                }
                states.TryRemove(p.thingIDNumber, out _);
                PawnRenderer renderer = p.Drawer?.renderer;
                if (renderer != null && renderer.CurAnimation == ClimbDef)
                {
                    renderer.SetAnimation(null);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "climb animation stop");
            }
        }

        /// <summary>Janitor, called from ABGameComp on the main thread: clears
        /// finished emerge animations. A pawn that started a new climb before
        /// its clear tick keeps that climb (state was overwritten, current
        /// animation is the climb def - both checks below skip it).</summary>
        [ABGameTick(20)]
        public static void Tick()
        {
            if (pendingClears.Count == 0)
            {
                return;
            }
            int now = GenTicks.TicksGame;
            for (int i = pendingClears.Count - 1; i >= 0; i--)
            {
                (Pawn pawn, int clearTick) = pendingClears[i];
                if (now < clearTick)
                {
                    continue;
                }
                pendingClears.RemoveAt(i);
                if (pawn == null || pawn.Destroyed)
                {
                    continue;
                }
                try
                {
                    if (states.TryGetValue(pawn.thingIDNumber, out ABClimbState s) && s.emerge
                        && now - s.startTick >= s.durationTicks)
                    {
                        states.TryRemove(pawn.thingIDNumber, out _);
                    }
                    PawnRenderer renderer = pawn.Drawer?.renderer;
                    if (renderer != null && renderer.CurAnimation == EmergeDef
                        && !states.ContainsKey(pawn.thingIDNumber))
                    {
                        renderer.SetAnimation(null);
                    }
                }
                catch (Exception e)
                {
                    ABGuard.Disable(ABGuard.Rendering, e, "climb animation janitor");
                }
            }
        }

        /// <summary>Thread-safe state read for the render workers. Climb states
        /// get a grace window past their duration, then auto-hide.</summary>
        internal static bool TryGetState(Pawn pawn, bool emerge, out ABClimbState s)
        {
            if (pawn != null && states.TryGetValue(pawn.thingIDNumber, out s) && s.emerge == emerge)
            {
                int t = GenTicks.TicksGame - s.startTick;
                int limit = emerge ? s.durationTicks : s.durationTicks + ClimbGraceTicks;
                if (t >= 0 && t <= limit)
                {
                    return true;
                }
            }
            s = default;
            return false;
        }
    }

    /// <summary>Climb visuals: step/rung bob, alternating lean (stairs only),
    /// slide onto the stairwell, and a progressive sink+shrink (down) or
    /// rise+grow (up). All methods are pure math over the snapshot state;
    /// safe on the parallel pre-draw threads.</summary>
    public class AnimationWorker_ABClimb : BaseAnimationWorker
    {
        public override bool Enabled(AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            return parms.pawn != null && !parms.pawn.Downed
                && ClimbAnimation.TryGetState(parms.pawn, emerge: false, out _);
        }

        public override void PostDraw(AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms, Matrix4x4 matrix)
        {
        }

        public override Vector3 OffsetAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            if (!ClimbAnimation.TryGetState(parms.pawn, emerge: false, out ABClimbState s))
            {
                return Vector3.zero;
            }
            int t = GenTicks.TicksGame - s.startTick;
            float p = Mathf.Clamp01((float)t / s.durationTicks);
            float ease = p * p * (3f - 2f * p);
            float period = s.ladder ? 16f : 12f;
            float amp = s.ladder ? 0.08f : 0.05f;
            float bob = Mathf.Abs(Mathf.Sin(t * Mathf.PI / period)) * amp;
            float vert = s.delta < 0 ? -0.35f * ease : 0.30f * ease;
            return new Vector3(s.slideX * ease, 0f, s.slideZ * ease + bob + vert);
        }

        public override float AngleAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            if (!ClimbAnimation.TryGetState(parms.pawn, emerge: false, out ABClimbState s) || s.ladder)
            {
                return 0f;
            }
            int t = GenTicks.TicksGame - s.startTick;
            // One lean cycle per two steps: alternating left/right sway.
            return Mathf.Sin(t * Mathf.PI / 24f) * 3f;
        }

        public override Vector3 ScaleAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            if (!ClimbAnimation.TryGetState(parms.pawn, emerge: false, out ABClimbState s))
            {
                return Vector3.one;
            }
            int t = GenTicks.TicksGame - s.startTick;
            float p = Mathf.Clamp01((float)t / s.durationTicks);
            float ease = p * p * (3f - 2f * p);
            float k = s.delta < 0 ? 1f - 0.28f * ease : 1f + 0.10f * ease;
            return new Vector3(k, 1f, k);
        }

        public override GraphicStateDef GraphicStateAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            return null;
        }
    }

    /// <summary>Arrival flourish: the pawn eases out of the stairwell to its
    /// landing cell. Arriving below (after a descent) it settles down from
    /// slightly raised and enlarged; arriving above it grows up from slightly
    /// sunken and small - continuing the departure motion.</summary>
    public class AnimationWorker_ABEmerge : BaseAnimationWorker
    {
        public override bool Enabled(AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            return parms.pawn != null && !parms.pawn.Downed
                && ClimbAnimation.TryGetState(parms.pawn, emerge: true, out _);
        }

        public override void PostDraw(AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms, Matrix4x4 matrix)
        {
        }

        public override Vector3 OffsetAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            if (!ClimbAnimation.TryGetState(parms.pawn, emerge: true, out ABClimbState s))
            {
                return Vector3.zero;
            }
            float e = Remaining(s);
            float vert = s.delta < 0 ? 0.30f : -0.30f;
            return new Vector3(s.slideX * e, 0f, (s.slideZ + vert) * e);
        }

        public override float AngleAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            return 0f;
        }

        public override Vector3 ScaleAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            if (!ClimbAnimation.TryGetState(parms.pawn, emerge: true, out ABClimbState s))
            {
                return Vector3.one;
            }
            float e = Remaining(s);
            float k = s.delta < 0 ? 1f + 0.12f * e : 1f - 0.18f * e;
            return new Vector3(k, 1f, k);
        }

        public override GraphicStateDef GraphicStateAtTick(int tick, AnimationDef def, PawnRenderNode node, AnimationPart part, PawnDrawParms parms)
        {
            return null;
        }

        /// <summary>Ease-out remainder: 1 at spawn, 0 once settled.</summary>
        private static float Remaining(ABClimbState s)
        {
            float p = Mathf.Clamp01((GenTicks.TicksGame - s.startTick) / (float)s.durationTicks);
            float e = 1f - p;
            return e * e;
        }
    }
}
