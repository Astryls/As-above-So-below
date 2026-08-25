using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE STAIR ANIMATION. CURRENTLY THE POP-OUT ONLY: a pawn emerges small on the far side
    /// and grows to full size. The entry half (shrink into the stairwell) is BUILT BUT NOT
    /// WIRED - see ReadyToCarry and the history below before touching it.
    ///
    /// ⚠⚠ THIS FILE HAS BROKEN CROSS-LEVEL MOVEMENT ONCE. THE RULE THAT CAME OUT OF IT:
    /// NOTHING COSMETIC MAY SIT ON THE CODE PATH THAT DECIDES WHETHER A TRANSIT HAPPENS.
    /// The pop-out obeys that rule by construction - it is a pure read of "when did the hop
    /// occur", recorded AFTER the fact, and no part of the carry consults it. Any future
    /// entry animation must be driven from outside the carry decision the same way.
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
    /// ⚠ THE SECOND VERSION HELD THE HOP FOR 18 TICKS SO THE SHRINK COULD PLAY, AND THAT IS
    /// WHAT BROKE MOVEMENT (run #297). The reasoning at the time was that the hold was
    /// bounded, self-cancelling and outlived by the 4000-tick timeout - all true, and all
    /// beside the point. Gating `TryConsumeArrival` suppresses vanilla's `PatherArrived`
    /// entirely, so the leg never completes, the job re-issues `StartPath`, `TrySegment`
    /// segments again (the real destination is still on another band) and the pawn re-arrives
    /// at the same anchor next tick. The bounded-ness of the hold is irrelevant when the
    /// thing it blocks is the loop's own exit condition.
    ///
    /// ⚠ THE ARGUMENT THAT LED THERE WAS "BOTH CARRY TRIGGERS MUST CONSULT THE SAME GATE",
    /// which is correct in itself (§3 records what happens when the sweep and `PatherArrived`
    /// disagree) and was applied to the wrong thing. Making two triggers agree is right when
    /// they decide the SAME OUTCOME; it is not a licence to put a new condition on both.
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

        /// <summary>
        /// How close the pawn must be to the anchor before the entry animation is allowed to
        /// hold it.
        ///
        /// ⚠ THIS IS DELIBERATELY TIGHTER THAN `ABWormholePather.ArriveRadius` (3), AND THAT
        /// GAP IS A BUG THIS CONSTANT EXISTS TO FIX. The carry radius is 3 because a pawn can
        /// legitimately come to rest up to LandingRadius cells short when other pawns are
        /// standing on the stairwell - tightening it there caused the stairs to jam. But
        /// reusing it for the ANIMATION meant the hold began the moment the pawn came within
        /// three cells, so it stopped and shrank in the middle of the floor. Reported as
        /// "yes but they do so before they reach the stairs".
        ///
        /// A pawn that is blocked out at 2-3 cells simply carries with no entry animation.
        /// That is the right degradation: there is no stairwell mouth to animate INTO from
        /// over there, and refusing to carry would re-create the jam ArriveRadius exists to
        /// prevent.
        /// </summary>
        private const float AnimateRadius = 1.5f;

        /// <summary>A record older than this without being re-offered belongs to a pawn that
        /// walked away. Deliberately only a few ticks: the sweep runs every tick, so a pawn
        /// still queueing to descend is refreshed constantly.</summary>
        private const int EntryStaleTicks = 10;

        private struct Entry
        {
            public int start;
            public int lastSeen;
        }

        // ---- diagnostics ------------------------------------------------------
        // ⚠ A SILENT EARLY-RETURN IS INDISTINGUISHABLE FROM AN UNIMPLEMENTED FEATURE (§14),
        // and this file is three silent early-returns deep on purpose. When the field report
        // "the stair animations are gone" arrived there was NO way to tell from a log which
        // half had died - the transit side (NotifyTransited never called) or the draw side
        // (state set, matrix never multiplied) - because both fail as clean no-ops. These
        // counters exist so `AB2: transit health` can answer that in one line. They always
        // count (an int increment); they are only ever narrated on request (rule 15).

        /// <summary>Pop-outs started, i.e. NotifyTransited calls. Main thread only.</summary>
        internal static int PopsStarted;

        /// <summary>Draw frames on which a stair scale/shimmy actually reached a pawn
        /// matrix. Incremented from the GetDrawParms postfix, which runs on Unity job
        /// WORKER threads during ParallelPreDraw - hence Interlocked, not ++.</summary>
        internal static int AnimFramesApplied;

        /// <summary>Times IsAnimating forced a pawn off the cached atlas blit. Incremented
        /// from the ParallelGetPreRenderResults prefix - also worker-threaded.</summary>
        internal static int CacheVetoes;

        internal static void NoteAnimApplied()
        {
            Interlocked.Increment(ref AnimFramesApplied);
        }

        internal static void NoteCacheVeto()
        {
            Interlocked.Increment(ref CacheVetoes);
        }

        /// <summary>One line for the transit-health report. The three counters read as a
        /// pipeline: pops > 0 with frames == 0 means the transit side fires and the draw
        /// side eats it; pops == 0 means the transit side never fires; both > 0 means the
        /// animation is being applied and anything invisible is downstream of the matrix
        /// (cache blit, another mod's renderer, occlusion).</summary>
        internal static string CountersLine()
        {
            return "stair anim: pops " + PopsStarted
                + ", anim frames " + AnimFramesApplied
                + ", cache vetoes " + CacheVetoes
                + ", live entering " + entering.Count + " / popped " + popped.Count;
        }

        /// <summary>pawn id -> when it reached the stairwell. Empty almost always.</summary>
        private static readonly Dictionary<int, Entry> entering = new Dictionary<int, Entry>();

        /// <summary>pawn id -> tick the hop happened, for the pop-out.</summary>
        private static readonly Dictionary<int, int> popped = new Dictionary<int, int>();

        private static readonly List<int> tmpExpired = new List<int>();

        /// <summary>Pawn-id keyed, so it must not cross a game load - see the banner on
        /// ABWormholePather.ResetForNewGame for what stale ids do to a loaded save.</summary>
        [ABGameReset]
        public static void ResetForNewGame()
        {
            entering.Clear();
            popped.Clear();
            tmpExpired.Clear();
            // Counters deliberately NOT reset: they are per-session diagnostics, and "has a
            // pop EVER fired since launch" is exactly the question they exist to answer.
        }

        /// <summary>
        /// ⚠⚠ BUILT AND DELIBERATELY NOT WIRED. DO NOT RECONNECT THIS WITHOUT READING §33c.
        ///
        /// This was called from BOTH carry triggers to hold a pawn at the stairwell for 18
        /// ticks so the entry animation could play. It broke cross-level movement outright
        /// (run #297: "can't command pawns across levels anymore") and both call sites have
        /// been reverted. The suspected mechanism, unproven: gating `TryConsumeArrival`
        /// suppresses vanilla's `PatherArrived`, so the leg never completes, the job re-issues
        /// `StartPath`, `TrySegment` segments again because the real destination is still on
        /// another band, and the pawn re-arrives at the same anchor - a re-segmentation loop
        /// indistinguishable from "the order does nothing".
        ///
        /// It is kept, unwired, so the next attempt starts from the analysis rather than
        /// rebuilding it blind. THE LESSON IS THE POINT: a cosmetic effect must never sit on
        /// the code path that decides whether a transit happens at all. Any future entry
        /// animation has to be driven from OUTSIDE the carry decision.
        /// </summary>
        public static bool ReadyToCarry(Pawn p, IntVec3 anchor)
        {
            if (p == null || Find.TickManager == null)
            {
                return true; // never block a carry because the animation is unavailable
            }
            if (!anchor.IsValid || !p.Position.InHorDistOf(anchor, AnimateRadius))
            {
                // Standing off the stairwell (blocked by other pawns): no animation, no hold.
                entering.Remove(p.thingIDNumber);
                return true;
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
            PopsStarted++;
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
