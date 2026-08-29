using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §78 TRANSIT CLIPS, "TRAVELER" - the user-picked language from the second round of
    /// motion studies (mockups/anim-C-traveler.html), with the delay UN-inverted.
    ///
    /// ⚠⚠ READ THIS BEFORE TOUCHING THE TIMING. §73 shipped an INVERTED delay: the carry was
    /// instant and the latency landed afterwards, as a ghost redrawn at the origin mouth
    /// while the real pawn was already at the destination. That existed because an earlier
    /// attempt to hold the pawn BEFORE the hop broke cross-level movement outright (run
    /// #297): it held by gating TryConsumeArrival/ReadyToCarry, which suppressed vanilla's
    /// PatherArrived, so the leg never completed, the job re-issued StartPath, TrySegment
    /// re-segmented, and the order read as doing nothing.
    ///
    /// The delay is now BEFORE the hop again, by user request, and the #297 trap is avoided
    /// by a different mechanism, which lives in ABWormholePather.BeginCrossing:
    ///
    ///   THE TRANSIT DECISION IS STILL MADE AND CONSUMED IN THE SAME TICK IT ALWAYS WAS.
    ///   The record leaves `pending` exactly as before; nothing gates the carry. What is
    ///   deferred is only the POSITION WRITE, by a self-contained timer that cannot decline
    ///   to fire. The pawn is held by StopDead + StaggerFor, not by refusing an arrival.
    ///
    /// The payoff is that this file gets much smaller and much more honest:
    ///  - NO GHOST. During the entry clip the pawn is genuinely at the origin, so its own
    ///    ordinary draw plays the clip. DrawGhosts and the hide pose are retired.
    ///  - NO MISMATCH. §73 shipped a known, user-accepted lie: for the whole clip the pawn
    ///    was logically at the destination (hittable there, turret-targetable) while drawn a
    ///    band away. That is gone - body and sprite agree on every tick again.
    ///  - THE MIDPOINT IS REAL. Traveler was authored so the two halves match in scale and
    ///    position at the crossover; the teleport now happens exactly at that frame, so the
    ///    camera cut (ABBandView.FollowTransit) lands on a matched pose.
    ///
    /// ⚠ NO ALPHA, STILL. Re-verified against 1.6: PawnDrawParms.tint is writable, but pawn
    /// materials are cutout, so alpha only lands if PawnRenderFlags.Invisible is also set -
    /// which routes every node through InvisibilityMatPool.GetInvisibleMat, a distortion
    /// shader with a hard-coded cyan multiply. That is a cloak, not a fade. Scale remains
    /// the only vanish channel.
    ///
    /// ⚠ FACING IS A REAL CHANNEL AND WE NEVER USED IT (rule 41). PawnDrawParms.facing is a
    /// plain writable Rot4 on the struct our GetDrawParms postfix already takes by ref.
    /// Nobody set it, so a pawn walking north into a stairwell kept whatever the pather left
    /// it with - usually south, i.e. walking backwards down the stairs.
    ///
    /// ⚠ FORWARD IS A PROPERTY OF THE LINK, NEVER OF THE ARRIVAL (rule 42). The art leads
    /// the way it faces and is entered from the opposite edge (measured: every *_south
    /// sprite has its notch on its NORTH edge). The old code derived the glide from
    /// `near.Position - prePos`, the direction the pawn happened to come from, which walked
    /// it through the handrail on three approaches out of four. And the FAR end is a
    /// DIFFERENT link with its own axis: the counterpart shares this one's Rotation (a
    /// footprint invariant - see Building_ABStairs2), so a pawn surfacing there arrives at
    /// the deep end of that run and walks out along MINUS its facing.
    /// </summary>
    public static class ABStairAnim
    {
        public enum ClipKind { Stairs, Grand, Ladder, Elevator }

        /// <summary>ClipPose.facing values. Match Rot4's AsInt; -1 means "leave the pawn's
        /// own facing alone".</summary>
        public const int FaceNone = -1;
        public const int FaceNorth = 0;
        public const int FaceEast = 1;
        public const int FaceSouth = 2;
        public const int FaceWest = 3;

        // ---- durations (ticks) ------------------------------------------------
        // ⚠ THESE ARE THE ONE KNOB, AND THEY WERE CHOSEN, NOT GUESSED. The user watched
        // the Traveler study at 0.30 playback and approved that pacing. In the study both
        // halves played SIMULTANEOUSLY inside one window; sequentially they cannot, so the
        // approved window is split evenly between them. That preserves the felt length of
        // the whole crossing (stairs 1.33s) at the cost of each half's curve running twice
        // as fast as it did on screen. If the motion now reads hurried rather than the
        // crossing reading long, DOUBLE these - that is the other defensible reading of the
        // same approval, and it costs 2.67s per staircase.
        private const int StairsEntry = 90;
        private const int GrandEntry = 90;
        private const int LadderEntry = 110;

        /// <summary>Elevator rides scale with distance, but sub-linearly and capped: a
        /// sky-to-basement ride is three times the levels, not three times the patience.</summary>
        private const int ElevEntryBase = 90;
        private const int ElevEntryPerLevel = 16;
        private const int ElevEntryMax = 130;

        /// <summary>V1's arrival flourish was 22 ticks for every link type and it does not
        /// need to be longer: the pawn is already where it belongs, this only eases it out
        /// of the stairwell.</summary>
        private const int EmergeTicks = 22;

        // ---- V1 CLIMB GRAMMAR (ported verbatim from AASB1 ClimbAnimation) ------
        // ⚠ THE VERTICAL TERM IS THE WHOLE FIX. §78 read a crossing as "travel along the
        // link's run axis and shrink", which is correct for a descent and WRONG for a climb:
        // the field report was "the animation isn't going up the stairs but in and down".
        // V1 separates the two ideas - a slide toward the stairwell, PLUS a screen-space
        // rise or sink - so going up looks like going up no matter which way the run points.
        private const float ClimbSink = -0.35f;   // z offset, descending
        private const float ClimbRise = 0.30f;    // z offset, ascending
        private const float ClimbShrink = 0.28f;  // scale lost, descending
        private const float ClimbGrow = 0.10f;    // scale gained, ascending
        private const float EmergeVert = 0.30f;
        private const float EmergeBigger = 0.12f; // arriving from above
        private const float EmergeSmaller = 0.18f; // arriving from below

        /// <summary>Longest slide toward the stairwell centre, in cells. V1's value.</summary>
        private const float SlideMax = 1.35f;

        private enum Phase { Entry, Emerge }

        private struct Clip
        {
            public Pawn pawn;
            public ClipKind kind;
            public bool up;
            public Phase phase;
            public int phaseStart;
            public int entryTicks;
            public int emergeTicks;

            /// <summary>ENTRY ONLY. The whole walk, in cells: from where the pawn actually
            /// stands to just past the drawn mouth. Every entry pose scales this by its own
            /// easing, so the clip always STARTS at offset zero.
            ///
            /// ⚠⚠ THIS REPLACED AN ABSOLUTE "snap the sprite onto the link" OFFSET AND THAT
            /// WAS A FIELD-REPORTED BUG (§78b): "animation makes pawn teleport into the
            /// centre of the stairs". The pawn is only required to be within ArriveRadius
            /// (3 cells) of the anchor, so an offset applied whole on frame one moved the
            /// sprite up to three cells instantly. Travel, not teleport.</summary>
            public float travelX, travelZ;

            /// <summary>Unit direction of the CURRENT phase's motion: the entry walk, or
            /// the exit walk at the far end. Drives facing and the lateral rail offset.
            ///
            /// ⚠ DERIVED FROM THE ACTUAL GEOMETRY, NOT FROM Rotation.FacingCell. The link's
            /// axis is the right answer only when the pawn is standing on the link's entry
            /// side, and nothing yet guarantees that (see EntryCellFor - it has a fallback,
            /// and the fallback must not produce a pawn walking one way while facing the
            /// other). Taking the direction from the vector we are actually going to move
            /// along makes every case self-consistent.</summary>
            public float dirX, dirZ;

            /// <summary>Emerge only: the far link's drawn centre relative to the landing
            /// cell, i.e. the mouth the pawn rose out of.</summary>
            public float ox, oz;

            public bool dustDone;
        }

        public struct ClipPose
        {
            public float offX, offZ;     // cells, applied in the pawn's local draw space
            public float rot;            // degrees about the up axis
            public float sx, sz;         // scale multipliers
            public int facing;           // -1 = leave vanilla facing alone
        }

        /// <summary>pawn id -> live clip. Empty almost always. Written ONLY from the main
        /// thread (begin / carry / sweep / clear); read from Unity job worker threads via
        /// TryGetPose, which is safe because ticks and draws do not overlap and the count
        /// guard below keeps the common case off the bucket chain entirely.</summary>
        private static readonly Dictionary<int, Clip> clips = new Dictionary<int, Clip>();

        private static readonly List<int> tmpExpired = new List<int>();

        private static readonly List<KeyValuePair<int, Clip>> pendingWrites =
            new List<KeyValuePair<int, Clip>>();

        // ---- diagnostics ------------------------------------------------------
        // ⚠ A SILENT EARLY-RETURN IS INDISTINGUISHABLE FROM AN UNIMPLEMENTED FEATURE (§14).
        internal static int PopsStarted;
        internal static int CarriesSeen;
        internal static int AnimFramesApplied;
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
                + ", carried " + CarriesSeen
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
            pendingWrites.Clear();
            // Counters deliberately NOT reset: per-session diagnostics.
        }

        // =================================================================== art

        /// <summary>
        /// ⚠ THE ART DOES NOT SIT CENTRED IN ITS OWN FOOTPRINT. Measured off the shipped
        /// PNGs (alpha bounding-box centre, in cells, indexed by Rot4.AsInt): AB_StairsDown
        /// facing south is drawn 0.22 cells NORTH of the cell it occupies, and the grand
        /// staircase facing north is 0.37 off. A pawn animated to the CELL centre visibly
        /// walks off the drawn treads, which is the second half of the "walks over the
        /// railing" report.
        ///
        /// ⚠ THE EAST/WEST ROWS ARE HONEST TRANSCRIPTIONS OF BROKEN ART, NOT FIXES. The
        /// grand staircase's east and west sprites are the north composition unrotated, and
        /// AB_LadderUp_east is a 0.14-cell sliver drawn 0.30 cells west of its own cell.
        /// These numbers make the animation agree with what is actually on screen; the art
        /// itself is the user's to redraw (§77c).
        ///
        ///
        /// ⚠⚠ THE TWO STAIRCASE EAST/WEST ROWS ARE ZERO ON PURPOSE (§85.9) - THEY ARE NOT
        /// MISSING MEASUREMENTS. AB2_StairsDown/Up now carry drawOffsetEast/drawOffsetWest in
        /// their graphicData, which MOVES the art onto the 1x2 footprint; once the sprite is
        /// pinned, "where is the art relative to the cell" is zero by construction. Re-adding
        /// the old transcriptions (-0.11,0.42) / (-0.16,0) would double-count the pin and walk
        /// the clip back off the treads.
        ///
        /// ⚠ MEASURE, DO NOT EYEBALL: Tools/MeasureSprites.ps1 prints the alpha box of any
        /// sprite in cells. Run against the shipped PNGs it reproduces every number in this
        /// table to two decimals, which is how the east/west pin was derived.
        /// </summary>
        private static readonly Dictionary<string, Vector2[]> ArtOffsets =
            new Dictionary<string, Vector2[]>
        {
            // east / west are ZEROED BY THE §85.9 PIN - see the banner. north / south are
            // still the measured composition and are still correct: both fit inside the 1x2
            // footprint uncorrected, so they were left alone.
            { "AB2_StairsDown", new[] { new Vector2(0f, -0.20f), new Vector2(0f, 0f),
                                        new Vector2(0f, 0.22f), new Vector2(0f, 0f) } },
            { "AB2_StairsUp", new[] { new Vector2(0f, -0.31f), new Vector2(0f, 0f),
                                      new Vector2(0f, -0.11f), new Vector2(0f, 0f) } },
            { "AB2_GrandStairsDown", new[] { new Vector2(0f, -0.21f), new Vector2(0f, -0.21f),
                                             new Vector2(0f, 0.23f), new Vector2(0f, -0.21f) } },
            { "AB2_GrandStairsUp", new[] { new Vector2(0.02f, -0.37f), new Vector2(0f, 0f),
                                           new Vector2(0.02f, -0.02f), new Vector2(0f, 0f) } },
            { "AB2_LadderDown", new[] { new Vector2(0f, 0.04f), new Vector2(0f, -0.03f),
                                        new Vector2(0f, 0f), new Vector2(0f, -0.03f) } },
            { "AB2_LadderUp", new[] { new Vector2(0f, 0f), new Vector2(-0.30f, 0f),
                                      new Vector2(0f, 0f), new Vector2(0.30f, 0f) } }
        };

        private static Vector2 ArtOff(Thing link)
        {
            if (link?.def == null)
            {
                return Vector2.zero;
            }
            return ArtOffsets.TryGetValue(link.def.defName, out Vector2[] rows)
                ? rows[link.Rotation.AsInt & 3]
                : Vector2.zero;
        }

        // ================================================================ facing

        private static int FaceOf(float dx, float dz)
        {
            if (Mathf.Abs(dx) > Mathf.Abs(dz))
            {
                return dx > 0f ? FaceEast : FaceWest;
            }
            return dz > 0f ? FaceNorth : FaceSouth;
        }

        private static int Opposite(int f)
        {
            switch (f)
            {
                case FaceNorth: return FaceSouth;
                case FaceSouth: return FaceNorth;
                case FaceEast: return FaceWest;
                case FaceWest: return FaceEast;
                default: return FaceNone;
            }
        }

        // ================================================================= entry

        private static ClipKind KindOf(Building_ABStairs2 link)
        {
            if (link.LinksAllLevels)
            {
                return ClipKind.Elevator;
            }
            string n = link.def.defName;
            if (n.IndexOf("Ladder", System.StringComparison.Ordinal) >= 0)
            {
                return ClipKind.Ladder;
            }
            if (n.IndexOf("Grand", System.StringComparison.Ordinal) >= 0)
            {
                return ClipKind.Grand;
            }
            return ClipKind.Stairs;
        }

        /// <summary>
        /// Start the ENTRY half, played over the pawn where it really is: at the origin,
        /// before any teleport. Returns false when there is nothing to play, in which case
        /// the caller must hop instantly (the pre-§78 behaviour).
        ///
        /// ⚠ THIS IS A PURE OBSERVER AND IT MUST STAY ONE. It reports how long to hold; it
        /// never decides WHETHER to cross. ABWormholePather has already consumed the transit
        /// record by the time this runs.
        /// </summary>
        public static bool Begin(Pawn p, Building_Door near, Building_Door far,
            int fromBand, int toBand, out int entryTicks)
        {
            entryTicks = 0;
            if (p == null || Find.TickManager == null || !p.Spawned)
            {
                return false;
            }
            if (ABMod.Settings == null || !ABMod.Settings.transitAnim)
            {
                return false; // toggle off: instant hop, no clip, no hold
            }
            if (!(near is Building_ABStairs2 link) || far == null || fromBand == toBand)
            {
                return false;
            }

            Clip c = default;
            c.pawn = p;
            c.kind = KindOf(link);
            c.up = toBand > fromBand;
            c.phase = Phase.Entry;
            c.phaseStart = Find.TickManager.TicksGame;

            int climb;
            switch (c.kind)
            {
                case ClipKind.Grand: climb = GrandEntry; break;
                case ClipKind.Ladder: climb = LadderEntry; break;
                case ClipKind.Elevator:
                {
                    int levels = Mathf.Max(1, Mathf.Abs(toBand - fromBand));
                    climb = Mathf.Min(ElevEntryMax,
                        ElevEntryBase + ElevEntryPerLevel * (levels - 1));
                    break;
                }
                default: climb = StairsEntry; break;
            }
            c.entryTicks = climb;
            c.emergeTicks = EmergeTicks;

            // The target: the link's drawn centre, shifted by the art's bounding-box centre
            // so the walk lands on the treads that are actually painted rather than on the
            // cell they nominally occupy.
            Vector2 a = ArtOff(near);
            Vector3 mouth = near.TrueCenter();
            Vector3 pawnAt = p.Position.ToVector3Shifted();
            float tx = mouth.x + a.x - pawnAt.x;
            float tz = mouth.z + a.y - pawnAt.z;
            float len = Mathf.Sqrt(tx * tx + tz * tz);
            if (len < 0.05f)
            {
                // Already standing on the mouth: no vector to take a direction from, so
                // fall back to the link's own axis.
                IntVec3 face = near.Rotation.FacingCell;
                c.dirX = face.x;
                c.dirZ = face.z;
            }
            else
            {
                c.dirX = tx / len;
                c.dirZ = tz / len;
            }
            // ⚠ CAPPED, V1's SlideMax. The slide is a flourish toward the opening, not a
            // substitute for walking: a pawn that stopped short must not cover the whole gap
            // by sliding, it should just lean into it.
            float slide = Mathf.Min(len, SlideMax);
            c.travelX = c.dirX * slide;
            c.travelZ = c.dirZ * slide;

            clips[p.thingIDNumber] = c;
            PopsStarted++;
            entryTicks = climb;

            if (c.kind == ClipKind.Elevator && p.Map != null)
            {
                // The clank: the platform takes the load.
                FleckMaker.ThrowDustPuff(mouth, p.Map, 1.0f);
            }
            return true;
        }

        /// <summary>
        /// The teleport just happened. Flip the clip to its EMERGE half, anchored on the FAR
        /// link - which has its own axis and its own art offset.
        /// </summary>
        public static void NotifyCarried(Pawn p, Building_Door far, IntVec3 landing)
        {
            if (p == null || Find.TickManager == null)
            {
                return;
            }
            if (!clips.TryGetValue(p.thingIDNumber, out Clip c))
            {
                return; // no clip (toggle off, or an instant hop): nothing to flip
            }
            CarriesSeen++;
            c.phase = Phase.Emerge;
            c.phaseStart = Find.TickManager.TicksGame;
            c.travelX = 0f;
            c.travelZ = 0f;
            if (far != null)
            {
                // The far link's drawn mouth, relative to the cell the pawn was set down
                // on. The emerge walks from there to zero, so the pawn climbs out of the
                // opening instead of appearing beside it.
                //
                // ⚠ §85: THE MOUTH, NOT THE MIDDLE. This was TrueCenter - the centre of the
                // whole building - so on a 2x2 staircase the clip began with the pawn drawn
                // standing on the solid half of the art and walking out THROUGH it. The
                // anchor is now the midpoint of the OPEN EDGE, which for a 1x1 ladder is
                // still exactly TrueCenter. Combined with a landing cell one step outside
                // that edge, the emerge is a single cell of travel out of the hole.
                Vector2 a = ArtOff(far);
                Vector3 mouth = ABLinkApproach.MouthPoint(far);
                Vector3 landAt = landing.ToVector3Shifted();
                c.ox = mouth.x + a.x - landAt.x;
                c.oz = mouth.z + a.y - landAt.z;
                // Exit direction = mouth -> landing, i.e. the walk we are about to draw.
                // Equal to -far.Rotation.FacingCell whenever the landing is on the far
                // link's entry side (rule 42); taken from the real vector so that it stays
                // honest when LandingCell had to settle for somewhere else.
                float ex = -c.ox;
                float ez = -c.oz;
                float el = Mathf.Sqrt(ex * ex + ez * ez);
                if (el < 0.05f)
                {
                    IntVec3 face = far.Rotation.FacingCell;
                    c.dirX = -face.x;
                    c.dirZ = -face.z;
                }
                else
                {
                    c.dirX = ex / el;
                    c.dirZ = ez / el;
                }
            }
            clips[p.thingIDNumber] = c;
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

        /// <summary>
        /// RETIRED BY §78 AND DELIBERATELY LEFT AS A CONSTANT FALSE.
        ///
        /// It used to mean "this pawn's sprite is at the origin mouth while its body is
        /// already at the destination", which gated the see-below pass and the name label.
        /// With the delay un-inverted that state cannot occur: during the entry half the
        /// pawn IS at the origin, and after the carry it IS at the destination. Kept so the
        /// two call sites in ABBelowDynamicDraw and the label patch need no edit, and named
        /// here rather than deleted silently so the next reader knows it is answered, not
        /// forgotten (§14).
        /// </summary>
        public static bool IsGhosting(Pawn pawn)
        {
            return false;
        }

        /// <summary>
        /// RETIRED BY §78. See IsGhosting. The ghost pass existed only because the pawn had
        /// already been teleported; there is no longer anything to redraw anywhere.
        /// </summary>
        public static void DrawGhosts(Map map)
        {
        }

        /// <summary>
        /// Advance clip state. Runs every tick from the transit sweep, main thread. The
        /// dictionary is empty almost always.
        ///
        /// ⚠ THIS DOES NOT DRIVE THE TELEPORT. The hold timer lives in ABWormholePather,
        /// which owns transits; this only ages the cosmetic half so that a clip whose pawn
        /// died, despawned or never got carried cannot linger.
        /// </summary>
        public static void Sweep()
        {
            if (clips.Count == 0 || Find.TickManager == null)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            tmpExpired.Clear();
            foreach (KeyValuePair<int, Clip> kv in clips)
            {
                Clip c = kv.Value;
                Pawn p = c.pawn;
                int age = now - c.phaseStart;
                int span = c.phase == Phase.Entry ? c.entryTicks : c.emergeTicks;
                if (p == null || !p.Spawned || p.Dead || age > span + 600)
                {
                    tmpExpired.Add(kv.Key);
                    continue;
                }
                if (c.phase == Phase.Emerge && age > span)
                {
                    tmpExpired.Add(kv.Key);
                    continue;
                }
                if (c.phase == Phase.Emerge && c.kind == ClipKind.Elevator && !c.dustDone
                    && span > 0 && age / (float)span >= 0.86f)
                {
                    c.dustDone = true;
                    pendingWrites.Add(new KeyValuePair<int, Clip>(kv.Key, c));
                    if (p.Map != null)
                    {
                        FleckMaker.ThrowDustPuff(p.Position.ToVector3Shifted(), p.Map, 1.2f);
                    }
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

        // ------------------------------------------------------------------ poses

        private static float Smooth(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// The one draw-side query. Returns the pose for this pawn's current phase, or false
        /// when it has no clip. Worker-thread safe: count guard first, dictionary never
        /// written outside the game tick, everything else pure math.
        /// </summary>
        public static bool TryGetPose(Pawn pawn, out ClipPose pose)
        {
            pose = default;
            pose.sx = pose.sz = 1f;
            pose.facing = FaceNone;
            if (clips.Count == 0 || pawn == null)
            {
                return false;
            }
            if (!clips.TryGetValue(pawn.thingIDNumber, out Clip c))
            {
                return false;
            }
            int age = Find.TickManager.TicksGame - c.phaseStart;
            if (age < 0)
            {
                return false;
            }
            if (c.phase == Phase.Entry)
            {
                float p = c.entryTicks > 0 ? Mathf.Clamp01(age / (float)c.entryTicks) : 1f;
                pose = EntryPose(c, p, age);
                return true;
            }
            float q = c.emergeTicks > 0 ? Mathf.Clamp01(age / (float)c.emergeTicks) : 1f;
            if (q >= 1f)
            {
                return false; // finished; the sweep removes it next tick
            }
            pose = EmergePose(c, q);
            return true;
        }

        /// <summary>
        /// THE CLIMB, ported from V1's AnimationWorker_ABClimb.
        ///
        /// Three independent terms, and keeping them independent is the point:
        ///   SLIDE - an eased lean toward the stairwell's drawn centre, capped at SlideMax.
        ///   BOB   - a step (or rung) cadence driven by TICKS, not by progress, so the
        ///           footfall rate is the same whatever the clip's duration.
        ///   VERT  - a screen-space rise or sink, the term that makes a climb read as a
        ///           climb. §78 had only slide+shrink, so ascending looked like descending
        ///           along a different axis ("not going up the stairs but in and down").
        ///
        /// ⚠ SCALE IS UNIFORM, ALWAYS, AND THAT IS NOT A STYLE CHOICE. A non-uniform scale
        /// composed with a rotation is a SHEAR, and the pawn's own render nodes contribute
        /// further scales either side of ours - so `sx != sz` while `rot != 0` skewed the
        /// sprite into a parallelogram. V1 returns (k, 1, k) everywhere for this reason.
        /// </summary>
        private static ClipPose EntryPose(Clip c, float p, int ageTicks)
        {
            ClipPose o = default;
            o.facing = FaceOf(c.dirX, c.dirZ);

            float ease = Smooth(p);
            bool ladder = c.kind == ClipKind.Ladder;
            bool elevator = c.kind == ClipKind.Elevator;

            // Ladders get a slower, taller rung cadence; an elevator has no footfall at all.
            float period = ladder ? 16f : 12f;
            float amp = ladder ? 0.08f : 0.05f;
            float bob = elevator ? 0f
                : Mathf.Abs(Mathf.Sin(ageTicks * Mathf.PI / period)) * amp;

            float vert = (c.up ? ClimbRise : ClimbSink) * ease;
            float k = c.up ? 1f + ClimbGrow * ease : 1f - ClimbShrink * ease;

            o.offX = c.travelX * ease;
            o.offZ = c.travelZ * ease + bob + vert;
            o.sx = o.sz = k;

            // Alternating sway, one cycle per two steps. Stairs only: a ladder is climbed
            // square-on, and a swaying elevator passenger reads as drunk.
            o.rot = (ladder || elevator) ? 0f
                : Mathf.Sin(ageTicks * Mathf.PI / 24f) * 3f;

            if (elevator)
            {
                // The car takes the load and the cable judders. Realtime, not progress, so
                // it does not visibly step at low game speeds.
                o.offX += Mathf.Sin(Time.realtimeSinceStartup * 41f) * 0.02f;
                // Riding the car means facing out of it, not along the shaft.
                o.facing = Opposite(o.facing);
            }
            return o;
        }

        /// <summary>
        /// THE ARRIVAL FLOURISH, ported from V1's AnimationWorker_ABEmerge. The pawn is
        /// already standing where it belongs; this only eases it out of the stairwell.
        ///
        /// ⚠ THE VERTICAL TERM IS INVERTED RELATIVE TO THE CLIMB, WHICH IS WHAT MAKES THE
        /// TWO HALVES READ AS ONE MOVEMENT. A pawn that went DOWN arrives from above, so it
        /// starts raised and slightly enlarged and settles; a pawn that went UP arrives from
        /// below, so it starts sunken and small and grows. Getting this backwards makes a
        /// descent end by rising out of the floor.
        /// </summary>
        private static ClipPose EmergePose(Clip c, float q)
        {
            ClipPose o = default;
            o.facing = FaceOf(c.dirX, c.dirZ);

            // Ease-out remainder: 1 the instant the pawn lands, 0 once settled.
            float e = 1f - Mathf.Clamp01(q);
            e *= e;

            float vert = c.up ? -EmergeVert : EmergeVert;
            float k = c.up ? 1f - EmergeSmaller * e : 1f + EmergeBigger * e;

            o.offX = c.ox * e;
            o.offZ = (c.oz + vert) * e;
            o.sx = o.sz = k;
            o.rot = 0f;
            return o;
        }
    }

    /// <summary>
    /// THE NAME LABEL HAS TO GO WHERE THE SPRITE WENT.
    ///
    /// Field report (§78b): "their nameplate is in the wrong location". Every overlay in
    /// the game positions itself from `thing.DrawPos` - the pawn's TRUE position - while a
    /// transit clip draws the sprite somewhere else entirely, by design. So the label sat
    /// on the cell the pawn legally occupies while the pawn appeared to be inside the
    /// stairwell, and the further the clip travelled the more obviously they diverged.
    ///
    /// §73 solved this by HIDING the label for the duration. Moving it is strictly better:
    /// the player keeps the name, and it keeps agreeing with the body.
    ///
    /// ⚠ THE DELTA IS COMPUTED IN SCREEN SPACE, NOT ADDED IN WORLD SPACE, because the
    /// vanilla result has already been projected AND vertically flipped
    /// (`result.y = screenHeight - result.y`). Projecting both endpoints and taking the
    /// difference keeps the patch correct at any zoom without duplicating that flip - but
    /// it does mean the y term subtracts.
    ///
    /// ⚠ EVERY overlay routed through this helper moves, which is what we want: label,
    /// and anything else vanilla or a mod positions with it.
    /// </summary>
    [HarmonyPatch(typeof(GenMapUI), nameof(GenMapUI.LabelDrawPosFor),
        new[] { typeof(Thing), typeof(float) })]
    public static class Patch_GenMapUI_ABClipLabel
    {
        private static void Postfix(Thing thing, ref Vector2 __result)
        {
            try
            {
                if (!(thing is Pawn p))
                {
                    return;
                }
                if (!ABStairAnim.TryGetPose(p, out ABStairAnim.ClipPose pose))
                {
                    return;
                }
                if (pose.offX == 0f && pose.offZ == 0f)
                {
                    return;
                }
                Camera cam = Find.Camera;
                if (cam == null)
                {
                    return;
                }
                Vector3 at = p.DrawPos;
                Vector3 to = at + new Vector3(pose.offX, 0f, pose.offZ);
                Vector3 sa = cam.WorldToScreenPoint(at);
                Vector3 sb = cam.WorldToScreenPoint(to);
                float scale = Prefs.UIScale;
                __result.x += (sb.x - sa.x) / scale;
                __result.y -= (sb.y - sa.y) / scale;
            }
            catch
            {
                // Never lose labels over a cosmetic effect.
            }
        }
    }
}
