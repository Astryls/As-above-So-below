using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §73 TRANSIT CLIPS. Per-link-type crossing animations, user-picked from the mockups:
    /// STAIRS = "Tread steps" (five discrete hops along the run), LADDER = "Peek and drop"
    /// (a lean-over beat, then two big pulls), ELEVATOR = "Freight lift" (clank, fast drop
    /// with shake, landing bounce). Grand stairs share the stairs clip.
    ///
    /// ⚠⚠ THE RULE THIS FILE EXISTS AROUND (run #297): NOTHING COSMETIC MAY SIT ON THE CODE
    /// PATH THAT DECIDES WHETHER A TRANSIT HAPPENS. A previous version held the pawn at the
    /// stairwell by gating TryConsumeArrival (ReadyToCarry): that suppressed vanilla's
    /// PatherArrived, the leg never completed, the job re-issued StartPath, TrySegment
    /// re-segmented, and cross-level movement read as "the order does nothing". That method
    /// is deleted; its lesson is this design:
    ///
    ///   THE DELAY IS INVERTED. The carry stays byte-for-byte instant. The user-accepted
    ///   latency lands AFTER the hop: the arrived pawn is briefly immobilized on the far
    ///   side (vanilla StaggerFor - job state untouched, self-clearing) while a GHOST plays
    ///   the entry clip back at the origin mouth. Then the emerge clip plays over the freed
    ///   pawn, exactly like the old pop-out did.
    ///
    /// ⚠ STAGGER IS NOT A GUARANTEE. StaggerFor scales by StaggerDurationFactor and Anomaly
    /// awoken corpses refuse it outright, so the hold is aspirational per-pawn. Every phase
    /// therefore keys off ACTUAL state: the moment the pawn leaves its landing cell the
    /// ghost is cut and the emerge clip starts. A stagger-immune pawn simply gets a shorter
    /// show; nothing desyncs.
    ///
    /// ⚠ WHY THE GHOST IS A SEPARATE DRAW PASS AND NOT A MATRIX TRANSLATION. Bands are
    /// regions of one map and the camera is clamped to the viewed band, so during the ghost
    /// window the pawn (already at the destination) is CULLED whenever the player watches
    /// the origin band - there is no draw call to translate. DrawGhosts therefore invokes
    /// the pawn's three DynamicDrawPhases at the origin mouth, the exact discipline
    /// ABBelowDynamicDraw established (including the "all three phases or you draw stale"
    /// lesson and the arm/disarm-in-a-finally rule). While a clip is in its ghost window the
    /// pawn's NORMAL draw at the landing cell is suppressed to a dot by the pose's hide
    /// flag, and ABBelowDynamicDraw skips ghosting pawns so the see-below pass cannot draw
    /// a second copy through the stair opening.
    ///
    /// ⚠ KNOWN HONEST MISMATCH (user-accepted): during the ghost window the pawn is
    /// LOGICALLY at the destination - hittable there, turret-targetable - while its sprite
    /// is at the origin mouth one band away. This is the mirror-image cost of never
    /// delaying the real crossing. Priced fallback if the field hates it: skip ghost+hold
    /// for hostile pawns (one faction check in NotifyTransited).
    ///
    /// ⚠ IT COMPOSES WITH THE DEPTH CUE, SAME AS BEFORE. Pose and depth shrink multiply in
    /// the ONE GetDrawParms postfix (ABBelowShrink); a second patch on that method would
    /// silently depend on patch order forever.
    ///
    /// ⚠ NO ALPHA. There is no tint path, so clips must read scale-only: entries end with a
    /// fast final shrink ("swallowed by the opening") instead of a fade, and from-above
    /// arrivals pop in through a fast scale ramp instead of a fade-in.
    /// </summary>
    public static class ABStairAnim
    {
        public enum ClipKind { Stairs, Ladder, Elevator }

        // ---- durations (ticks). Mockup defaults, user-approved. -------------
        private const int StairsEntryTicks = 26;
        private const int StairsHoldTicks = 6;
        private const int StairsEmergeTicks = 26;

        private const int LadderEntryTicks = 40;   // 6 mount/peek + 34 climb
        private const float LadderMountFrac = 0.15f;
        private const int LadderHoldTicks = 8;
        private const int LadderEmergeTicks = 30;

        private const int ElevBoardTicks = 8;
        private const int ElevPerLevelTicks = 10;

        // ---- scale grammar ----------------------------------------------------
        private const float ScaleBelow = 0.32f;    // the shipped MinScale, kept
        private const float ScaleAbove = 1.14f;
        private const float ElevScaleBelow = 0.25f;
        private const float ElevScaleAbove = 1.15f;

        private struct Clip
        {
            public Pawn pawn;
            public ClipKind kind;
            public bool up;              // journey direction: true = to a higher band
            public int startTick;
            public int entryTicks;       // ghost window at the origin mouth
            public int holdTicks;        // hidden gap (nobody drawn anywhere)
            public int emergeTicks;
            public IntVec3 origin;       // near anchor cell: where the ghost is drawn
            public IntVec3 landing;      // where the pawn really is
            public IntVec3 farAnchor;    // far mouth: emerge slides from here to landing
            public Vector3 glideDir;     // unit-ish "into the mouth" direction at the origin
            public int emergeStart;      // age (ticks) when the emerge began; -1 while ghosting
            public bool dustDone;        // elevator landing puff fired
        }

        public struct ClipPose
        {
            public float offX, offZ;     // cells, applied in the pawn's local draw space
            public float rot;            // degrees about the up axis
            public float sx, sz;         // scale multipliers
            public bool hide;            // suppress this draw to a dot (ghost is elsewhere)
        }

        /// <summary>pawn id -> live clip. Empty almost always. Written ONLY from the main
        /// thread (notify / sweep / clear); read from Unity job worker threads via
        /// TryGetPose, which is safe because ticks and draws do not overlap and the count
        /// guard below keeps the common case off the bucket chain entirely.</summary>
        private static readonly Dictionary<int, Clip> clips = new Dictionary<int, Clip>();

        private static readonly List<int> tmpExpired = new List<int>();

        /// <summary>Armed to the ghosting pawn's id ONLY around the three draw phases
        /// DrawGhosts invokes, main thread, cleared in a finally - the BelowDrawOffsetZ
        /// discipline. TryGetPose returns the ENTRY pose while armed for that pawn and the
        /// hide pose for every other read of it.</summary>
        private static int ghostArmedId = -1;

        // ---- diagnostics ------------------------------------------------------
        // ⚠ A SILENT EARLY-RETURN IS INDISTINGUISHABLE FROM AN UNIMPLEMENTED FEATURE (§14).
        // The three counters read as a pipeline: clips started with zero anim frames means
        // the transit side fires and the draw side eats it; zero clips means the transit
        // side never fires. Narrated on request via CountersLine (rule 15).

        /// <summary>Clips started, i.e. NotifyTransited calls that produced one.</summary>
        internal static int PopsStarted;

        /// <summary>Ghost passes actually drawn (main thread).</summary>
        internal static int GhostsDrawn;

        /// <summary>Draw frames on which a clip pose reached a pawn matrix. Worker
        /// threads - hence Interlocked.</summary>
        internal static int AnimFramesApplied;

        /// <summary>Times IsAnimating forced a pawn off the cached atlas blit.</summary>
        internal static int CacheVetoes;

        internal static void NoteAnimApplied()
        {
            Interlocked.Increment(ref AnimFramesApplied);
        }

        internal static void NoteCacheVeto()
        {
            Interlocked.Increment(ref CacheVetoes);
        }

        internal static string CountersLine()
        {
            return "transit clips: started " + PopsStarted
                + ", ghost draws " + GhostsDrawn
                + ", anim frames " + AnimFramesApplied
                + ", cache vetoes " + CacheVetoes
                + ", live " + clips.Count;
        }

        /// <summary>Pawn-id keyed, so it must not cross a game load - see the banner on
        /// ABWormholePather.ResetForNewGame for what stale ids do to a loaded save.</summary>
        [ABGameReset]
        public static void ResetForNewGame()
        {
            clips.Clear();
            tmpExpired.Clear();
            ghostArmedId = -1;
            // Counters deliberately NOT reset: per-session diagnostics.
        }

        /// <summary>
        /// Called the instant a pawn is carried across, AFTER Position/Notify_Teleported -
        /// a pure observer of the hop, exactly like the old pop-out. Starts the clip and
        /// applies the post-hop hold.
        /// </summary>
        public static void NotifyTransited(Pawn p, Building_Door near, Building_Door far,
            IntVec3 prePos, IntVec3 landing)
        {
            if (p == null || Find.TickManager == null || !p.Spawned)
            {
                return;
            }
            if (ABMod.Settings == null || !ABMod.Settings.transitAnim)
            {
                return; // toggle off: the old instant hop, no clip, no hold
            }
            Building_ABStairs2 link = near as Building_ABStairs2;
            ABBandMap bands = ABBands.CompOf(p.Map);
            if (link == null || far == null || bands == null || !bands.Banded)
            {
                return;
            }
            int fromBand = bands.BandOf(near.Position);
            int toBand = bands.BandOf(landing);
            if (fromBand == toBand)
            {
                return; // not a cross-band hop; nothing to dramatize
            }

            Clip c = default;
            c.pawn = p;
            c.up = toBand > fromBand;
            c.startTick = Find.TickManager.TicksGame;
            c.origin = near.Position;
            c.landing = landing;
            c.farAnchor = far.Position;
            c.emergeStart = -1;

            if (link.LinksAllLevels)
            {
                c.kind = ClipKind.Elevator;
            }
            else if (link.def.defName.IndexOf("Ladder", System.StringComparison.Ordinal) >= 0)
            {
                c.kind = ClipKind.Ladder;
            }
            else
            {
                c.kind = ClipKind.Stairs;
            }

            IntVec3 approach = near.Position - prePos;
            Vector3 glide = new Vector3(approach.x, 0f, approach.z);
            if (glide.sqrMagnitude < 0.01f)
            {
                IntVec3 face = near.Rotation.FacingCell;
                glide = new Vector3(face.x, 0f, face.z);
            }
            c.glideDir = glide.normalized;

            int staggerTicks;
            switch (c.kind)
            {
                case ClipKind.Ladder:
                    c.entryTicks = LadderEntryTicks;
                    c.holdTicks = LadderHoldTicks;
                    c.emergeTicks = LadderEmergeTicks;
                    staggerTicks = c.entryTicks + c.holdTicks;
                    break;
                case ClipKind.Elevator:
                {
                    int levels = Mathf.Max(1, Mathf.Abs(toBand - fromBand));
                    int travel = levels * ElevPerLevelTicks;
                    c.entryTicks = ElevBoardTicks + travel / 2;
                    c.holdTicks = 0;
                    c.emergeTicks = travel - travel / 2;
                    staggerTicks = ElevBoardTicks + travel;
                    // The clank: the platform takes the load. Main thread, at the origin
                    // cell, so it is only ever seen by someone watching that band.
                    if (p.Map != null)
                    {
                        FleckMaker.ThrowDustPuff(c.origin.ToVector3Shifted(), p.Map, 1.0f);
                    }
                    break;
                }
                default:
                    c.entryTicks = StairsEntryTicks;
                    c.holdTicks = StairsHoldTicks;
                    c.emergeTicks = StairsEmergeTicks;
                    staggerTicks = c.entryTicks + c.holdTicks;
                    break;
            }

            clips[p.thingIDNumber] = c;
            PopsStarted++;

            // The user-accepted latency. Full stop (factor 0), never a slow-walk. If the
            // pawn resists (StaggerDurationFactor, awoken corpses), the sweep notices the
            // early move and cuts straight to the emerge clip - see the banner.
            p.stances?.stagger?.StaggerFor(staggerTicks, 0f);
        }

        public static void Clear(Pawn p)
        {
            if (p != null)
            {
                clips.Remove(p.thingIDNumber);
            }
        }

        /// <summary>True while any phase of a clip is live for this pawn. Forces the
        /// renderer off the cached atlas blit (which ignores the draw matrix).</summary>
        public static bool IsAnimating(Pawn pawn)
        {
            if (clips.Count == 0 || pawn == null)
            {
                return false;
            }
            return clips.ContainsKey(pawn.thingIDNumber);
        }

        /// <summary>True while the pawn is in its ghost/hold window: sprite at the origin
        /// mouth (or nowhere), logical body at the landing. Gates the see-below pass, the
        /// pawn label, and the hidden normal draw.</summary>
        public static bool IsGhosting(Pawn pawn)
        {
            if (clips.Count == 0 || pawn == null)
            {
                return false;
            }
            return clips.TryGetValue(pawn.thingIDNumber, out Clip c) && c.emergeStart < 0;
        }

        /// <summary>
        /// Advance clip state. Runs every tick from the transit sweep, main thread.
        /// The dictionary is empty almost always.
        /// </summary>
        public static void Sweep()
        {
            if (clips.Count == 0 || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpExpired.Clear();
            // Mutated copies are stashed and written AFTER the loop: rewriting a value
            // inside the foreach would invalidate the enumerator.
            foreach (KeyValuePair<int, Clip> kv in clips)
            {
                Clip c = kv.Value;
                Pawn p = c.pawn;
                int age = now - c.startTick;
                if (p == null || !p.Spawned || p.Dead
                    || age > c.entryTicks + c.holdTicks + c.emergeTicks + 600)
                {
                    tmpExpired.Add(kv.Key); // despawned mid-clip, or the watchdog margin
                    continue;
                }
                bool mutated = false;
                if (c.emergeStart < 0)
                {
                    // The hold ends when its time is up OR the moment the pawn genuinely
                    // moves - whichever is first. That second clause is what makes a
                    // stagger-immune pawn degrade to a shorter clip instead of a desync.
                    if (age >= c.entryTicks + c.holdTicks || p.Position != c.landing)
                    {
                        c.emergeStart = age < 0 ? 0 : age;
                        mutated = true;
                    }
                }
                else if (age - c.emergeStart > c.emergeTicks)
                {
                    tmpExpired.Add(kv.Key);
                }
                else if (c.kind == ClipKind.Elevator && !c.dustDone && c.emergeTicks > 0
                    && (age - c.emergeStart) / (float)c.emergeTicks >= 0.86f)
                {
                    // The landing thump, at the moment the bounce bottoms out.
                    c.dustDone = true;
                    mutated = true;
                    if (p.Map != null)
                    {
                        FleckMaker.ThrowDustPuff(c.landing.ToVector3Shifted(), p.Map, 1.2f);
                    }
                }
                if (mutated)
                {
                    pendingWrites.Add(new KeyValuePair<int, Clip>(kv.Key, c));
                }
            }
            for (int i = 0; i < pendingWrites.Count; i++)
            {
                clips[pendingWrites[i].Key] = pendingWrites[i].Value;
            }
            pendingWrites.Clear();
            for (int i = 0; i < tmpExpired.Count; i++)
            {
                clips.Remove(tmpExpired[i]);
            }
            tmpExpired.Clear();
        }

        private static readonly List<KeyValuePair<int, Clip>> pendingWrites =
            new List<KeyValuePair<int, Clip>>();

        /// <summary>
        /// The ghost pass. Runs after ABBelowDynamicDraw.DrawBelowPawns from the same
        /// DrawDynamicThings postfix: for every clip in its ghost window whose ORIGIN band
        /// is the one on camera, run the pawn's three draw phases at the origin mouth.
        /// All three phases, per the staleness lesson in ABBelowDynamicDraw - do not
        /// "optimise" this to DrawNowAt.
        /// </summary>
        public static void DrawGhosts(Map map)
        {
            if (clips.Count == 0 || map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            CameraDriver cam = Find.CameraDriver;
            if (cam == null || Find.TickManager == null)
            {
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            CellRect camView = cam.CurrentViewRect;
            int now = Find.TickManager.TicksGame;
            foreach (KeyValuePair<int, Clip> kv in clips)
            {
                Clip c = kv.Value;
                if (c.emergeStart >= 0)
                {
                    continue; // emerging: the normal draw at the landing carries the clip
                }
                int age = now - c.startTick;
                if (age < 0 || age >= c.entryTicks)
                {
                    continue; // the hold gap: nobody is drawn anywhere, like the mockup
                }
                Pawn p = c.pawn;
                if (p == null || !p.Spawned || p.Map != map)
                {
                    continue;
                }
                if (bands.BandOf(c.origin) != viewBand || !camView.Contains(c.origin))
                {
                    continue;
                }
                if (map.fogGrid.IsFogged(c.origin))
                {
                    continue;
                }
                try
                {
                    Vector3 loc = c.origin.ToVector3Shifted();
                    loc.y = p.DrawPos.y;
                    ghostArmedId = kv.Key;
                    // The cache decision is consumed inside ParallelPreDraw; IsAnimating is
                    // true for this pawn, so BeginPawn vetoes the blit and the pose matrix
                    // is honored.
                    ABBelowRenderCache.BeginPawn(p, 1f);
                    try
                    {
                        p.DynamicDrawPhaseAt(DrawPhase.EnsureInitialized, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.ParallelPreDraw, loc);
                        p.DynamicDrawPhaseAt(DrawPhase.Draw, loc);
                    }
                    finally
                    {
                        ghostArmedId = -1;
                        ABBelowRenderCache.EndPawn();
                    }
                    GhostsDrawn++;
                }
                catch (System.Exception e)
                {
                    Log.WarningOnce(ABLog.Tag + " V2 transit ghost draw failed for "
                        + p.LabelShortCap + ": " + e.Message, p.thingIDNumber ^ 762195877);
                }
            }
        }

        // ------------------------------------------------------------------ poses

        private static float Smooth(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>Dwell-pull-dwell staircase easing: n discrete advances.</summary>
        private static float StepEase(float p, int n)
        {
            p = Mathf.Clamp01(p);
            float s = p * n;
            int i = Mathf.FloorToInt(s);
            if (i >= n) i = n - 1;
            float f = s - i;
            float m = Smooth(Mathf.Clamp01((f - 0.18f) / 0.55f));
            return (i + m) / n;
        }

        /// <summary>The scale-only stand-in for an alpha fade: a fast final shrink,
        /// "swallowed by the opening". 1 until 88%, then down to ~0.1x.</summary>
        private static float Vanish(float p)
        {
            return p > 0.88f ? 1f - 0.9f * Smooth((p - 0.88f) / 0.12f) : 1f;
        }

        /// <summary>
        /// The one draw-side query. Returns the pose for this pawn's current phase, or
        /// false when it has no clip. Worker-thread safe: count guard first, dictionary
        /// never written outside the game tick, everything else pure math.
        /// </summary>
        public static bool TryGetPose(Pawn pawn, out ClipPose pose)
        {
            pose = default;
            pose.sx = pose.sz = 1f;
            if (clips.Count == 0 || pawn == null)
            {
                return false;
            }
            if (!clips.TryGetValue(pawn.thingIDNumber, out Clip c))
            {
                return false;
            }
            int age = Find.TickManager.TicksGame - c.startTick;
            if (age < 0)
            {
                return false;
            }
            if (c.emergeStart < 0)
            {
                if (ghostArmedId == pawn.thingIDNumber)
                {
                    pose = EntryPose(c, c.entryTicks > 0
                        ? Mathf.Clamp01(age / (float)c.entryTicks) : 1f);
                    return true;
                }
                // Normal draw at the landing while the ghost owns the sprite: a dot.
                pose.hide = true;
                return true;
            }
            float q = c.emergeTicks > 0
                ? Mathf.Clamp01((age - c.emergeStart) / (float)c.emergeTicks) : 1f;
            if (q >= 1f)
            {
                return false; // finished; the sweep removes it next tick
            }
            pose = EmergePose(c, q);
            return true;
        }

        private static ClipPose EntryPose(Clip c, float p)
        {
            ClipPose o = default;
            o.sx = o.sz = 1f;
            switch (c.kind)
            {
                case ClipKind.Stairs:
                {
                    float pe = StepEase(p, 5);
                    int tread = (int)(Mathf.Clamp01(p) * 5f);
                    if (tread > 4) tread = 4;
                    float f = Mathf.Clamp01(p * 5f - tread);
                    float hop = Mathf.Abs(Mathf.Sin(f * Mathf.PI));
                    float s = c.up ? Mathf.Lerp(1f, ScaleAbove, pe)
                                   : Mathf.Lerp(1f, ScaleBelow, pe);
                    s *= Vanish(p) * (1f + hop * 0.04f);
                    o.sx = o.sz = s;
                    o.offX = c.glideDir.x * 0.5f * pe;
                    o.offZ = c.glideDir.z * 0.5f * pe + hop * 0.05f;
                    o.rot = ((tread & 1) == 0 ? 1f : -1f) * 5f * Mathf.Sin(f * Mathf.PI);
                    break;
                }
                case ClipKind.Ladder:
                {
                    if (p < LadderMountFrac)
                    {
                        // The peek: lean over the shaft and check. Anticipation beat.
                        float mp = Smooth(p / LadderMountFrac);
                        o.rot = (c.up ? -10f : 14f) * mp;
                        o.sx = o.sz = c.up ? 1f - 0.05f * mp : 1f;
                        o.offX = c.glideDir.x * 0.12f * mp;
                        o.offZ = c.glideDir.z * 0.12f * mp;
                    }
                    else
                    {
                        // Two big committed pulls.
                        float pp = (p - LadderMountFrac) / (1f - LadderMountFrac);
                        float pe = StepEase(pp, 2);
                        int pull = pp < 0.5f ? 0 : 1;
                        float f = Mathf.Clamp01(pp * 2f - pull);
                        float sign = pull == 0 ? 1f : -1f;
                        float rock = sign * 0.09f * Mathf.Sin(f * Mathf.PI);
                        float s = c.up ? Mathf.Lerp(1f, ScaleAbove, pe)
                                       : Mathf.Lerp(1f, ScaleBelow, pe);
                        o.sx = o.sz = s * Vanish(pp);
                        o.offX = c.glideDir.x * (0.12f + 0.25f * pe) + rock;
                        o.offZ = c.glideDir.z * (0.12f + 0.25f * pe);
                        o.rot = sign * 7f * Mathf.Sin(f * Mathf.PI)
                              + (c.up ? -10f : 14f) * (1f - pp);
                    }
                    break;
                }
                default: // Elevator
                {
                    float boardFrac = c.entryTicks > 0
                        ? Mathf.Clamp01(ElevBoardTicks / (float)c.entryTicks) : 0.3f;
                    if (p < boardFrac)
                    {
                        // Step on; the platform takes the weight late in the board.
                        float bp = p / boardFrac;
                        if (bp > 0.55f)
                        {
                            o.offZ = -Mathf.Sin((bp - 0.55f) / 0.45f * Mathf.PI) * 0.05f;
                        }
                    }
                    else
                    {
                        // The drop (or hoist): fast, shaking.
                        float q = (p - boardFrac) / (1f - boardFrac);
                        float s = c.up ? Mathf.Lerp(1f, ElevScaleAbove, Smooth(q))
                                       : Mathf.Lerp(1f, ElevScaleBelow, Smooth(q));
                        if (c.up)
                        {
                            s *= Vanish(q);
                        }
                        o.sx = o.sz = s;
                        o.offX = Mathf.Sin(Time.realtimeSinceStartup * 70f) * 0.03f;
                    }
                    break;
                }
            }
            return o;
        }

        private static ClipPose EmergePose(Clip c, float q)
        {
            ClipPose o = default;
            o.sx = o.sz = 1f;
            bool fromAbove = !c.up; // a downward journey arrives from above
            // Scale-only stand-in for a fade-in: from-above arrivals pop in through a fast
            // ramp instead of materializing at 1.14 from nothing.
            float reveal = fromAbove ? Mathf.Lerp(0.05f, 1f, Smooth(q / 0.10f)) : 1f;
            float ox = c.farAnchor.x - c.landing.x;
            float oz = c.farAnchor.z - c.landing.z;
            switch (c.kind)
            {
                case ClipKind.Stairs:
                {
                    float pe = StepEase(q, 4);
                    int tread = (int)(Mathf.Clamp01(q) * 4f);
                    if (tread > 3) tread = 3;
                    float f = Mathf.Clamp01(q * 4f - tread);
                    float hop = Mathf.Abs(Mathf.Sin(f * Mathf.PI));
                    float s = fromAbove
                        ? Mathf.Lerp(ScaleAbove, 1f, pe)
                        : Mathf.Lerp(ScaleBelow, 1f, pe)
                          * (1f + 0.05f * Mathf.Sin(Mathf.PI * Mathf.Clamp01((q - 0.72f) / 0.28f)));
                    s *= reveal * (1f + hop * 0.04f);
                    o.sx = o.sz = s;
                    o.offX = ox * (1f - pe);
                    o.offZ = oz * (1f - pe) + hop * 0.05f;
                    o.rot = ((tread & 1) == 0 ? -1f : 1f) * 5f * Mathf.Sin(f * Mathf.PI);
                    break;
                }
                case ClipKind.Ladder:
                {
                    float s;
                    float rock = 0f;
                    if (fromAbove)
                    {
                        // Two big drops out of the ceiling, then a hop off the last rung.
                        float pe = StepEase(q, 2);
                        int pull = q < 0.5f ? 0 : 1;
                        float f = Mathf.Clamp01(q * 2f - pull);
                        float sign = pull == 0 ? -1f : 1f;
                        rock = sign * 0.09f * Mathf.Sin(f * Mathf.PI);
                        s = Mathf.Lerp(ScaleAbove, 1f, pe) * reveal;
                        o.rot = sign * 7f * Mathf.Sin(f * Mathf.PI);
                        if (q > 0.86f)
                        {
                            o.offZ += Mathf.Sin(Mathf.PI * (q - 0.86f) / 0.14f) * 0.06f;
                        }
                    }
                    else if (q < 0.25f)
                    {
                        // The head-pop: small, with an overshoot bounce.
                        float qq = q / 0.25f;
                        s = Mathf.Lerp(0.18f, 0.55f, Smooth(qq))
                          * (1f + 0.35f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, qq * 1.15f)));
                    }
                    else
                    {
                        // Haul out in two pulls.
                        float pp = (q - 0.25f) / 0.75f;
                        float pe = StepEase(pp, 2);
                        int pull = pp < 0.5f ? 0 : 1;
                        float f = Mathf.Clamp01(pp * 2f - pull);
                        float sign = pull == 0 ? 1f : -1f;
                        rock = sign * 0.08f * Mathf.Sin(f * Mathf.PI);
                        s = Mathf.Lerp(0.55f, 1f, pe);
                        o.rot = sign * 7f * Mathf.Sin(f * Mathf.PI);
                    }
                    o.sx = o.sz = s;
                    o.offX = ox * (1f - q) + rock;
                    o.offZ = oz * (1f - q);
                    break;
                }
                default: // Elevator
                {
                    float jt = Mathf.Sin(Time.realtimeSinceStartup * 70f) * 0.03f
                             * (q < 0.85f ? 1f : 0.3f);
                    float s = fromAbove
                        ? Mathf.Lerp(ElevScaleAbove, 1f, Smooth(q)) * reveal
                        : Mathf.Lerp(0.28f, 1f, Smooth(q));
                    if (q > 0.86f)
                    {
                        // The landing bounce: overshoot with a squash. The dust puff is
                        // thrown from the sweep at the same moment.
                        float b = Mathf.Sin(Mathf.PI * (q - 0.86f) / 0.14f);
                        o.sx = s * (1f + 0.10f * b);
                        o.sz = s * (1f - 0.12f * b);
                        o.offZ = -0.03f * b;
                    }
                    else
                    {
                        o.sx = o.sz = s;
                    }
                    o.offX = jt;
                    break;
                }
            }
            return o;
        }
    }

    /// <summary>
    /// The pawn's name label reads DrawPos, which during the ghost window is the landing
    /// cell - a label floating over an empty cell while the sprite is a band away. Hidden
    /// for those few ticks; everything else about the overlay is untouched.
    /// </summary>
    [HarmonyPatch(typeof(PawnUIOverlay), nameof(PawnUIOverlay.DrawPawnGUIOverlay))]
    public static class Patch_PawnUIOverlay_ABGhostLabel
    {
        private static readonly AccessTools.FieldRef<PawnUIOverlay, Pawn> PawnRef =
            AccessTools.FieldRefAccess<PawnUIOverlay, Pawn>("pawn");

        private static bool Prefix(PawnUIOverlay __instance)
        {
            try
            {
                return !ABStairAnim.IsGhosting(PawnRef(__instance));
            }
            catch
            {
                return true; // never lose labels over a cosmetic effect
            }
        }
    }
}
