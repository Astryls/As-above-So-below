using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE STAIR ANIMATION: a pawn shimmies and shrinks into a stairwell, then pops out the
    /// other side. Ported in spirit from V1.
    ///
    /// ⚠ THE FIRST VERSION OF THIS DROVE THE SHRINK OFF DISTANCE TO THE ANCHOR SO THAT IT
    /// COULD NEVER DELAY THE TELEPORT. That was over-cautious and it simply did not work, and
    /// the arithmetic says why in one line: `ABWormholePather.ArriveRadius` is 3, the shrink
    /// radius was 4, so the sweep carried the pawn the moment it entered the shrink zone.
    /// Peak progress was 1 - 3/4 = 0.25 for about one tick - a ~10% size change, one frame
    /// long. Reported, correctly, as "animations never play since pawns teleport before they
    /// can". A stateless effect is worth nothing if the state it reads never gets a chance to
    /// change.
    ///
    /// ⚠ SO THE HOP IS NOW HELD, AND THE HOLD IS WHAT MAKES THIS SAFE RATHER THAN WHAT MAKES
    /// IT RISKY. The objection to a hold was that a pawn frozen mid-animation is
    /// indistinguishable from a jammed one. At 18 ticks - a third of a second, less than a
    /// pawn already spends opening a door - that is not true. The properties that keep it
    /// safe are:
    ///   - It is bounded. `ReadyToCarry` returns true unconditionally once OutTicks have
    ///     elapsed, so the worst case is a third of a second, not a stall.
    ///   - It is self-cancelling. The record is refreshed only while the pawn is actually
    ///     being offered for carry; a pawn that walks away has its record swept and simply
    ///     stops animating. Nothing has to remember to clean up.
    ///   - It cannot outlive the transit. `ABWormholePather`'s 4000-tick timeout still drops
    ///     the pending record regardless of anything here.
    ///
    /// ⚠ BOTH CARRY TRIGGERS MUST CONSULT THE SAME GATE. The tick sweep and `PatherArrived`
    /// are the same question asked from two places, and §3 records what happened last time
    /// they disagreed: whether a transit completed depended on which fired first. Gating only
    /// the sweep would mean the animation played or not depending on exactly how a pawn
    /// finished its leg.
    ///
    /// ⚠ IT COMPOSES WITH THE DEPTH CUE INSTEAD OF COMPETING WITH IT. Both effects are a
    /// scale on the same `PawnDrawParms.matrix`, multiplied together in the ONE existing
    /// postfix rather than added as a second patch on the same method.
    /// </summary>
    public static class ABStairAnim
    {
        /// <summary>Ticks the pawn stands at the mouth of the stairwell, shrinking.</summary>
        private const int OutTicks = 18;

        /// <summary>Ticks the pop-out takes on the far side.</summary>
        private const int PopTicks = 22;

        /// <summary>How small the pawn gets before it vanishes into the stairwell.</summary>
        private const float MinScale = 0.32f;

        /// <summary>Shimmy: sideways travel in cells, and how fast it oscillates.</summary>
        private const float ShimmyAmplitude = 0.10f;

        private const float ShimmyRate = 17f;

        /// <summary>A record older than this without being re-offered belongs to a pawn that
        /// walked away. Deliberately only a few ticks: the sweep runs every tick, so a pawn
        /// still queueing to descend is refreshed constantly.</summary>
        private const int EntryStaleTicks = 10;

        private struct Entry
        {
            public int start;
            public int lastSeen;
        }

        /// <summary>pawn id -> when it reached the stairwell. Empty almost always.</summary>
        private static readonly Dictionary<int, Entry> entering = new Dictionary<int, Entry>();

        /// <summary>pawn id -> tick the hop happened, for the pop-out.</summary>
        private static readonly Dictionary<int, int> popped = new Dictionary<int, int>();

        private static readonly List<int> tmpExpired = new List<int>();

        /// <summary>
        /// THE GATE. Called by both carry triggers with a pawn that is standing at its near
        /// anchor. Returns false while the entry animation is still playing, true once the
        /// pawn should actually be moved.
        ///
        /// ⚠ CALLING THIS IS WHAT KEEPS THE RECORD ALIVE. It doubles as the "still here"
        /// heartbeat, which is what makes the hold self-cancelling without a second callback.
        /// </summary>
        public static bool ReadyToCarry(Pawn p)
        {
            if (p == null || Find.TickManager == null)
            {
                return true; // never block a carry because the animation is unavailable
            }
            int now = Find.TickManager.TicksGame;
            int id = p.thingIDNumber;
            if (!entering.TryGetValue(id, out Entry e))
            {
                entering[id] = new Entry { start = now, lastSeen = now };
                return false;
            }
            if (now - e.start >= OutTicks)
            {
                entering.Remove(id);
                return true;
            }
            e.lastSeen = now;
            entering[id] = e;
            return false;
        }

        /// <summary>Called the instant a pawn is carried across.</summary>
        public static void NotifyTransited(Pawn p)
        {
            if (p == null || Find.TickManager == null)
            {
                return;
            }
            entering.Remove(p.thingIDNumber);
            popped[p.thingIDNumber] = Find.TickManager.TicksGame;
        }

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                entering.Remove(p.thingIDNumber);
                popped.Remove(p.thingIDNumber);
            }
        }

        /// <summary>Expire finished pop-outs and abandoned entries. Runs every tick from the
        /// transit sweep; both dictionaries are empty almost always.</summary>
        public static void Sweep()
        {
            int now = Find.TickManager.TicksGame;
            if (popped.Count > 0)
            {
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
            }
            if (entering.Count > 0)
            {
                tmpExpired.Clear();
                foreach (KeyValuePair<int, Entry> kv in entering)
                {
                    if (now - kv.Value.lastSeen > EntryStaleTicks)
                    {
                        tmpExpired.Add(kv.Key);
                    }
                }
                for (int i = 0; i < tmpExpired.Count; i++)
                {
                    entering.Remove(tmpExpired[i]);
                }
            }
            tmpExpired.Clear();
        }

        /// <summary>
        /// 0..1 progress of the effect, 0 being normal size and 1 fully inside the stairwell.
        /// Returns 0 for the overwhelming majority of pawns, which is the fast path this is
        /// written around.
        /// </summary>
        private static float ProgressFor(Pawn pawn, out bool outgoing)
        {
            outgoing = true;
            if (pawn == null || !pawn.Spawned)
            {
                return 0f;
            }
            // ⚠ THE COUNT GUARD IS LOAD-BEARING AND NOT ONLY FOR SPEED. This runs from a
            // PawnRenderer.GetDrawParms postfix, and GetDrawParms is reached from
            // ParallelPreDraw - potentially on Unity job WORKER THREADS, several at once.
            // Reading a Dictionary concurrently is safe only while nothing writes to it, and
            // both are written from the game tick. Ticks and draws do not overlap in
            // RimWorld's frame, so the probe is safe - but gating on a plain int count means
            // the common case (nobody on the stairs anywhere) never walks a bucket chain off
            // the main thread at all.
            if (entering.Count == 0 && popped.Count == 0)
            {
                return 0f;
            }
            int now = Find.TickManager.TicksGame;
            int id = pawn.thingIDNumber;

            if (entering.Count > 0 && entering.TryGetValue(id, out Entry e))
            {
                int age = now - e.start;
                if (age >= 0 && age < OutTicks)
                {
                    return (float)age / OutTicks;
                }
                return 1f;
            }
            if (popped.Count > 0 && popped.TryGetValue(id, out int at))
            {
                int age = now - at;
                if (age >= 0 && age <= PopTicks)
                {
                    outgoing = false;
                    return 1f - ((float)age / PopTicks);
                }
            }
            return 0f;
        }

        /// <summary>Scale multiplier for this pawn, 1 when nothing is happening.</summary>
        public static float ScaleFor(Pawn pawn)
        {
            float t = ProgressFor(pawn, out bool _);
            if (t <= 0f)
            {
                return 1f;
            }
            // Smoothstep so the pawn eases into the hole rather than tracking a linear ramp,
            // which reads as the sprite glitching in size rather than moving.
            float eased = t * t * (3f - 2f * t);
            return Mathf.Lerp(1f, MinScale, eased);
        }

        /// <summary>Sideways shimmy offset in WORLD cells, zero when not animating.</summary>
        public static float ShimmyFor(Pawn pawn)
        {
            float t = ProgressFor(pawn, out bool outgoing);
            if (t <= 0f)
            {
                return 0f;
            }
            // Grows with the effect so it starts and ends still, and runs off realtime so it
            // stays smooth between ticks. Mirrored on the way out so entering and leaving do
            // not look like the same clip played twice.
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
