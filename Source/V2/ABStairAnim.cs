using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE STAIR ANIMATION: a pawn shimmies and shrinks into a stairwell, then pops out the
    /// other side. Ported in spirit from V1, rebuilt so it cannot touch the AI.
    ///
    /// \u26a0 THE TELEPORT IS NOT DELAYED AND MUST NEVER BE. The obvious implementation is to
    /// hold the pawn at the near anchor for N ticks while it shrinks, then move it. That
    /// would put a timed window inside the one subsystem that has already produced three
    /// separate "pawn stuck at the stairs" bugs (stacked landings, mismatched arrival radii,
    /// stale cross-band records), and a pawn frozen mid-animation is indistinguishable from a
    /// pawn that is genuinely jammed. So the transit stays exactly as instantaneous as it was
    /// and this file is PURELY a renderer effect. Nothing here can strand anyone.
    ///
    /// That constraint is what dictates the two halves being driven differently:
    ///
    ///  - SHRINKING IN is driven by DISTANCE to the near anchor while a transit is pending.
    ///    It is a pure function of position, holds no state, needs no cleanup, and if the
    ///    pawn changes its mind and walks away the scale simply returns to 1 on its own. A
    ///    tick-based version would have to guess when the pawn was going to arrive.
    ///  - POPPING OUT is driven by TICKS SINCE THE HOP, because after the teleport there is
    ///    no distance left to read. That is the only piece of state here, it is one int per
    ///    transiting pawn, and it expires by itself.
    ///
    /// \u26a0 IT COMPOSES WITH THE DEPTH CUE INSTEAD OF COMPETING WITH IT. Both effects are a
    /// scale on the same `PawnDrawParms.matrix`, so they are multiplied together in the ONE
    /// existing postfix rather than added as a second patch on the same method. Two patches
    /// right-multiplying the same matrix would work by accident today and break the moment
    /// their order changed.
    /// </summary>
    public static class ABStairAnim
    {
        /// <summary>Distance from the anchor at which the shrink starts. Slightly wider than
        /// ABWormholePather.ArriveRadius so the pawn is already visibly shrinking by the time
        /// it is close enough to be carried, rather than snapping at the last moment.</summary>
        private const float ShrinkRadius = 4f;

        /// <summary>How small the pawn gets at the mouth of the stairwell.</summary>
        private const float MinScale = 0.35f;

        /// <summary>Ticks the pop-out takes on the far side.</summary>
        private const int PopTicks = 26;

        /// <summary>Shimmy: sideways travel in cells, and how fast it oscillates.</summary>
        private const float ShimmyAmplitude = 0.09f;

        private const float ShimmyRate = 15f;

        /// <summary>pawn id -> tick the hop happened. Small, and swept as it expires.</summary>
        private static readonly Dictionary<int, int> popped = new Dictionary<int, int>();

        private static readonly List<int> tmpExpired = new List<int>();

        /// <summary>Called from ABWormholePather the instant a pawn is carried across.</summary>
        public static void NotifyTransited(Pawn p)
        {
            if (p == null || Find.TickManager == null)
            {
                return;
            }
            popped[p.thingIDNumber] = Find.TickManager.TicksGame;
        }

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                popped.Remove(p.thingIDNumber);
            }
        }

        /// <summary>Expire finished pop-outs. Cheap: the dictionary is empty almost always,
        /// and this shares the transit sweep's tick so it costs nothing extra.</summary>
        public static void Sweep()
        {
            if (popped.Count == 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpExpired.Clear();
            foreach (KeyValuePair<int, int> kv in popped)
            {
                if (now - kv.Value > PopTicks)
                {
                    tmpExpired.Add(kv.Key);
                }
            }
            for (int i = 0; i < tmpExpired.Count; i++)
            {
                popped.Remove(tmpExpired[i]);
            }
            tmpExpired.Clear();
        }

        /// <summary>
        /// 0..1 progress of the effect for this pawn, where 0 is "normal size" and 1 is
        /// "fully inside the stairwell". Returns 0 for the overwhelming majority of pawns,
        /// which is the fast path this is written around.
        /// </summary>
        private static float ProgressFor(Pawn pawn, out bool outgoing)
        {
            outgoing = true;
            if (pawn == null || !pawn.Spawned)
            {
                return 0f;
            }
            // ⚠ BOTH COUNT GUARDS ARE LOAD-BEARING, AND NOT ONLY FOR SPEED. This runs from a
            // PawnRenderer.GetDrawParms postfix, and GetDrawParms is reached from
            // ParallelPreDraw - i.e. potentially on Unity job WORKER THREADS, several at once.
            // Reading a Dictionary concurrently is safe only while nothing writes to it, and
            // both dictionaries are written from the game tick. Ticks and draws do not
            // overlap in RimWorld's frame, so the probe is safe - but gating on a plain int
            // count means the overwhelmingly common case (no transit anywhere on the map)
            // never walks a bucket chain from a worker thread at all.
            if (!ABWormholePather.AnyPending && popped.Count == 0)
            {
                return 0f;
            }

            // Pop-out first: it is a dictionary probe against a near-always-empty map, and a
            // pawn that just landed has no pending transit to test anyway.
            if (popped.Count > 0 && popped.TryGetValue(pawn.thingIDNumber, out int at))
            {
                int age = Find.TickManager.TicksGame - at;
                if (age >= 0 && age <= PopTicks)
                {
                    outgoing = false;
                    return 1f - ((float)age / PopTicks);
                }
            }

            if (!ABWormholePather.AnyPending
                || !ABWormholePather.TryGetPending(pawn, out IntVec3 nearCell, out IntVec3 _,
                    out LocalTargetInfo _))
            {
                return 0f;
            }
            // \u26a0 SAME-BAND TEST, NOT JUST DISTANCE. A pending record whose anchor is on
            // another band belongs to a leg the pawn has not started yet; without this a pawn
            // standing directly above or below its own stairwell would shrink for no visible
            // reason, because the two cells are close in x but a whole Slot apart in z.
            if (!ABBands.SameBand(pawn.Map, pawn.Position, nearCell))
            {
                return 0f;
            }
            float d = pawn.Position.DistanceTo(nearCell);
            if (d >= ShrinkRadius)
            {
                return 0f;
            }
            return 1f - (d / ShrinkRadius);
        }

        /// <summary>Scale multiplier for this pawn, 1 when nothing is happening.</summary>
        public static float ScaleFor(Pawn pawn)
        {
            float t = ProgressFor(pawn, out bool _);
            if (t <= 0f)
            {
                return 1f;
            }
            // Smoothstep so the pawn eases into the hole rather than tracking its own
            // footsteps linearly - a linear ramp reads as the sprite glitching in size.
            float eased = t * t * (3f - 2f * t);
            return Mathf.Lerp(1f, MinScale, eased);
        }

        /// <summary>Sideways shimmy offset in local draw space, zero when not animating.</summary>
        public static float ShimmyFor(Pawn pawn)
        {
            float t = ProgressFor(pawn, out bool outgoing);
            if (t <= 0f)
            {
                return 0f;
            }
            // Oscillation grows with the effect so it starts and ends still, and runs off
            // RealtimeSinceStartup so it keeps moving smoothly between ticks. Direction is
            // mirrored on the way out so entering and leaving do not look like the same clip.
            float phase = Time.realtimeSinceStartup * ShimmyRate * (outgoing ? 1f : -1f);
            return Mathf.Sin(phase) * ShimmyAmplitude * t;
        }

        /// <summary>True when this pawn is mid-effect. Used to force the renderer off its
        /// cached atlas blit, which ignores the draw matrix entirely.</summary>
        public static bool IsAnimating(Pawn pawn)
        {
            return ProgressFor(pawn, out bool _) > 0f;
        }
    }
}
