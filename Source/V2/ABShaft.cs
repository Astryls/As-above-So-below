using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The answer to one cross-band shot question, so every consumer reads the same fields
    /// instead of re-deriving them. <see cref="opening"/> is the cell the shot actually
    /// passes through, which is what the renderer and the threat marker need and what the
    /// old bool-returning API threw away.
    /// </summary>
    public struct ABShotSolution
    {
        public bool valid;

        /// <summary>The cell in the UPPER band the shot passes through. Invalid for
        /// <see cref="overhead"/> solutions, which do not use an opening at all.</summary>
        public IntVec3 opening;

        /// <summary>Distance the rest of the game should believe. Feeds range checks AND
        /// accuracy, so the two can never disagree.</summary>
        public float distance;

        /// <summary>Signed: targetBand - rootBand. Positive means the shooter is firing UP.
        /// Consumers that only care how far apart the two are use <see cref="Levels"/>.</summary>
        public int bandDelta;

        /// <summary>The two bands this solution was computed for. Carried so a consumer can
        /// CHECK a parked solution belongs to the shot in front of it - see
        /// ABCombatRelay.TryTakeSolution, where the band pair is the discriminator that lets
        /// the caster identity be dropped (a manned turret launches with the MANNING PAWN as
        /// launcher, not the turret that owns the verb).</summary>
        public int rootBand;

        public int targetBand;

        /// <summary>Resolved by the map-coordinate rule rather than by an opening: mortars
        /// and anything else whose projectile flies overhead. See ABShaft's banner.</summary>
        public bool overhead;

        public int Levels => Mathf.Abs(bandDelta);

        public bool FiringUp => bandDelta > 0;
    }

    /// <summary>
    /// THE CROSS-BAND FIRING GEOMETRY, and the only place it is computed.
    ///
    /// Everything else in the mod comes free from the banded design because hauling, needs,
    /// work and storage are GRAPH problems and the wormhole RegionLink makes the graph
    /// correct. Combat is the one GEOMETRY problem: GenSight and weapon range are flat 2D
    /// cell maths, and a pawn one level up is literally one Slot (128 to 256 cells) north
    /// behind an impassable gutter. Out of range, no line of sight, no target.
    ///
    /// ⚠ THIS FILE REPLACES THE "SHAFT" RULE WITH A "BALCONY" RULE, and the difference is
    /// the whole point of the rewrite. The old rule was: you may hit only what is DIRECTLY
    /// BENEATH an opening. That is one cell of tolerance plus a 12-cell fudge, so it played
    /// as "stand on exactly the right tile or nothing happens". The new rule is the one the
    /// renderer already implies:
    ///
    ///     YOU MAY SHOOT WHAT YOU CAN SEE. Your field of fire onto another level is the
    ///     UNION OF THE CONES under every opening you have line of sight to.
    ///
    /// So a colonist standing well back from the lip of a gap can cover the floor below,
    /// and a wide shaft gives a wide field of fire while an arrow slit gives a narrow one.
    /// Geometry does the balancing instead of a magic number.
    ///
    /// THREE TESTS MAKE UP A SOLUTION, and each is checked in the band it belongs to:
    ///   1. the opening's COLUMN is open air all the way from the upper band to the lower
    ///      one (this is what makes multi-level shafts work, and what stops a shot passing
    ///      through an intervening floor),
    ///   2. line of sight WITHIN THE UPPER BAND from the upper participant to the opening,
    ///   3. line of sight WITHIN THE LOWER BAND from the opening's mouth to the lower
    ///      participant, capped by <see cref="MaxDriftPerLevel"/> so a small hole cannot be
    ///      used as a sideways window through the floor above.
    ///
    /// ⚠⚠ IT IS SYMMETRIC, AND THAT IS DELIBERATE. The mod is downward-only in its
    /// RENDERING (TryResolveVisibleBelow, the see-through click rule, the below view), but
    /// there is no reason for its PHYSICS to be. The same solution serves a colonist firing
    /// down a stairwell and a raider firing back up it, so "upward combat" is not a second
    /// code path that can rot - it is the same call with the arguments the other way round.
    ///
    /// ⚠ MORTARS DO NOT USE ANY OF THIS. By the user's call, anything whose projectile flies
    /// overhead hits MAP COORDINATES: levels are ignored entirely, no opening is needed, and
    /// range is measured on the band-local horizontal only - so minimum range still applies
    /// horizontally and a mortar still cannot hit something directly beneath itself. That is
    /// the `overhead` branch of TrySolve, and it also CLOSES A SHIPPED BUG: vanilla's
    /// TryFindShootLineFromTo returns a shot line for any no-line-of-sight verb purely on
    /// raw distance, and one Slot is well inside mortar range, so until now a mortar could
    /// shell your basement through solid rock at 256 cells with no opening whatsoever.
    /// </summary>
    public static class ABShaft
    {
        /// <summary>How far sideways a shot may drift from the opening, per level dropped.
        /// This is the CONE half-width under a hole, measured from the opening's mouth to
        /// the lower participant - NOT from the shooter to the target. That distinction is
        /// the balcony rule: the shooter may stand as far back from the lip as its weapon
        /// range allows, because test 2 (line of sight in its own band) is what governs
        /// there.</summary>
        public const float MaxDriftPerLevel = 12f;

        /// <summary>Range charged for each level crossed, so a vertical shot is not free.
        /// Roughly the fiction's height of one storey; small enough that a pistol can still
        /// fire through a floor, large enough that stacked levels add up.</summary>
        public const float VerticalCostPerLevel = 3f;

        // Counters for `AB2: combat report`. Observe-only, by §36's rule: none of these may
        // ever be read by a decision.
        public static int solves;

        public static int cacheHits;

        public static int fastHits;

        public static int walks;

        public static int misses;

        public static int overheadSolves;

        public static void ResetCounters()
        {
            solves = 0;
            cacheHits = 0;
            fastHits = 0;
            walks = 0;
            misses = 0;
            overheadSolves = 0;
        }

        public static string CounterReport()
        {
            return "solves=" + solves + " (fastPath=" + fastHits + " lineWalk=" + walks
                + " overhead=" + overheadSolves + " miss=" + misses + ") cacheHits=" + cacheHits;
        }

        /// <summary>
        /// Same-tick memo, because one shot is solved many times over: the AI scans
        /// candidates, the verb re-validates on cast, the stance re-checks every tick and the
        /// targeter re-asks every FRAME. Keyed on the two cells plus the range asked for.
        ///
        /// ⚠ SAME-TICK IS THE CORRECT LIFETIME, not "until something changes". The only
        /// inputs are terrain and buildings, neither of which can change without a tick
        /// passing - and a tick-stamped memo needs no invalidation hooks at all, which is one
        /// less thing to be wrong. Frames while PAUSED reuse it happily, which is exactly the
        /// case where the targeter asks hardest.
        ///
        /// ⚠ [ThreadStatic] rather than a lock: GetDrawParms already proved that this mod's
        /// helpers get reached from worker threads, and a torn Dictionary is an unfindable
        /// bug. A per-thread array costs 512 structs on the one or two threads that ask.
        /// </summary>
        private const int CacheSize = 512;

        private struct CacheEntry
        {
            public long key;

            public int tick;

            public float range;

            public float minRange;

            public ABShotSolution sol;
        }

        [System.ThreadStatic]
        private static CacheEntry[] cache;

        /// <summary>
        /// When non-null, every rejection point in the solve appends the value that caused it.
        ///
        /// ⚠ THE SAME CODE PATH, INSTRUMENTED - NOT A SECOND EXPLAINER. Writing a parallel
        /// "why not" routine is how you get a diagnostic that agrees with your belief about
        /// the solver instead of with the solver (§14: TWO RESOLVERS FOR ONE QUANTITY IS A BUG
        /// WITH A DELAY FUSE). And it prints INTERMEDIATE VALUES, not a verdict: the verdict is
        /// already visible in game as "the pawn did not shoot".
        /// </summary>
        [System.ThreadStatic]
        private static System.Text.StringBuilder trace;

        private static void Trace(string line)
        {
            trace?.AppendLine("    " + line);
        }

        /// <summary>Runs the REAL solve with tracing on, bypassing the memo (a cache hit would
        /// return no trace at all). Used by `AB2: why can't this pawn shoot that`.</summary>
        public static string Explain(Map map, IntVec3 root, IntVec3 target, float range,
            float minRange, bool overheadFire)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return "    map is not banded\n";
            }
            if (!root.InBounds(map) || !target.InBounds(map))
            {
                return "    root or target out of bounds\n";
            }
            int bandRoot = bands.BandOf(root);
            int bandTarg = bands.BandOf(target);
            sb.AppendLine("    root " + root + " band " + bandRoot + ", target " + target
                + " band " + bandTarg + ", range " + range.ToString("0.0") + ", minRange "
                + minRange.ToString("0.0") + ", overhead=" + overheadFire);
            if (bandRoot == bandTarg)
            {
                sb.AppendLine("    same band - vanilla handles this, we never look");
                return sb.ToString();
            }
            if (bands.InGutter(root) || bands.InGutter(target))
            {
                sb.AppendLine("    root or target is IN THE GUTTER - no band owns it");
                return sb.ToString();
            }
            trace = sb;
            try
            {
                Solve(map, bands, root, target, bandRoot, bandTarg, range, minRange,
                    overheadFire, out ABShotSolution sol);
                sb.AppendLine("    => " + (sol.valid
                    ? "SOLVED via " + (sol.overhead ? "overhead fire" : "opening " + sol.opening)
                        + ", effective distance " + sol.distance.ToString("0.0")
                    : "NO SOLUTION"));
            }
            finally
            {
                trace = null;
            }
            return sb.ToString();
        }

        /// <summary>
        /// The one entry point. <paramref name="overheadFire"/> selects the map-coordinate
        /// rule (mortars); everything else goes through the balcony solve.
        /// </summary>
        public static bool TrySolve(Map map, IntVec3 root, IntVec3 target, float range,
            float minRange, bool overheadFire, out ABShotSolution sol)
        {
            sol = default(ABShotSolution);
            if (map == null)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            if (!root.InBounds(map) || !target.InBounds(map))
            {
                return false;
            }
            int bandRoot = bands.BandOf(root);
            int bandTarg = bands.BandOf(target);
            if (bandRoot == bandTarg)
            {
                return false; // same band: vanilla's job, and it is better at it
            }
            // ⚠ THE GUTTER IS NOT A PLACE. Nothing legitimately stands there, and letting a
            // gutter cell resolve a band would make every arithmetic below lie.
            if (bands.InGutter(root) || bands.InGutter(target))
            {
                return false;
            }

            long key = Key(map, root, target, overheadFire);
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            if (cache == null)
            {
                cache = new CacheEntry[CacheSize];
            }
            int slotIdx = (int)((key ^ (key >> 21)) & (CacheSize - 1));
            if (cache[slotIdx].key == key && cache[slotIdx].tick == now
                && cache[slotIdx].range == range && cache[slotIdx].minRange == minRange)
            {
                cacheHits++;
                sol = cache[slotIdx].sol;
                return sol.valid;
            }

            solves++;
            Solve(map, bands, root, target, bandRoot, bandTarg, range, minRange, overheadFire,
                out sol);
            if (!sol.valid)
            {
                misses++;
            }
            cache[slotIdx].key = key;
            cache[slotIdx].tick = now;
            cache[slotIdx].range = range;
            cache[slotIdx].minRange = minRange;
            cache[slotIdx].sol = sol;
            return sol.valid;
        }

        private static long Key(Map map, IntVec3 a, IntVec3 b, bool overhead)
        {
            long ia = map.cellIndices.CellToIndex(a);
            long ib = map.cellIndices.CellToIndex(b);
            return (ia << 33) | (ib << 1) | (overhead ? 1L : 0L);
        }

        private static void Solve(Map map, ABBandMap bands, IntVec3 root, IntVec3 target,
            int bandRoot, int bandTarg, float range, float minRange, bool overheadFire,
            out ABShotSolution sol)
        {
            sol = default(ABShotSolution);
            sol.bandDelta = bandTarg - bandRoot;
            sol.rootBand = bandRoot;
            sol.targetBand = bandTarg;

            // ⚠ ANISOTROPY: the raw (target - root) vector runs THROUGH THE STACK and is
            // meaningless. Every distance here is taken with the target brought into the
            // shooter's own band, which is the only honest horizontal separation. §1's
            // warning about naive DistanceTo is exactly this trap.
            IntVec3 targetHere = bands.Translate(target, bandRoot);
            float horizontal = (targetHere - root).LengthHorizontal;

            if (overheadFire)
            {
                // MAP COORDINATES. Levels do not exist for a shell: it goes up and comes
                // down wherever it was aimed. Minimum range therefore still bites, measured
                // horizontally, which is why a mortar cannot drop one on the level directly
                // below its own feet.
                Trace("overhead: band-local horizontal " + horizontal.ToString("0.0")
                    + " vs range " + range.ToString("0.0") + " / minRange "
                    + minRange.ToString("0.0"));
                if (horizontal > range || horizontal < minRange)
                {
                    Trace(horizontal > range ? "REJECT: beyond maximum range"
                        : "REJECT: inside minimum range (a mortar cannot hit its own feet)");
                    return;
                }
                overheadSolves++;
                sol.valid = true;
                sol.overhead = true;
                sol.opening = IntVec3.Invalid;
                sol.distance = horizontal;
                return;
            }

            int drop = Mathf.Abs(sol.bandDelta);
            float dist = horizontal + VerticalCostPerLevel * drop;
            Trace("levels apart " + drop + ", band-local horizontal "
                + horizontal.ToString("0.0") + " + vertical cost "
                + (VerticalCostPerLevel * drop).ToString("0.0") + " = " + dist.ToString("0.0")
                + " vs range " + range.ToString("0.0"));
            if (dist > range || dist < minRange)
            {
                Trace(dist > range ? "REJECT: beyond maximum range"
                    : "REJECT: inside minimum range");
                return;
            }
            sol.distance = dist;

            bool firingUp = sol.bandDelta > 0;
            int upperBand = firingUp ? bandTarg : bandRoot;
            int lowerBand = firingUp ? bandRoot : bandTarg;
            IntVec3 upper = firingUp ? target : root;
            IntVec3 lower = firingUp ? root : target;
            float driftCap = MaxDriftPerLevel * drop;

            // FAST PATH: straight down (or up) the shaft. The opening directly over the lower
            // participant, which is the old rule and still the overwhelmingly common case -
            // someone standing in a stairwell mouth. One terrain read and one line of sight.
            Trace("upper participant " + upper + " (band " + upperBand + "), lower " + lower
                + " (band " + lowerBand + "), drift cap " + driftCap.ToString("0.0"));
            IntVec3 direct = bands.Translate(lower, upperBand);
            Trace("fast path: opening directly over the lower participant at " + direct);
            if (Accepts(map, bands, direct, upper, lower, upperBand, lowerBand, driftCap))
            {
                fastHits++;
                sol.valid = true;
                sol.opening = direct;
                return;
            }

            // BALCONY PATH: walk the line from the lower participant's column toward the
            // upper participant and take the first opening that works. Walking from the
            // TARGET end outward is what makes this the balcony rule rather than a wider
            // fudge factor: the openings nearest the target are the ones a shooter standing
            // back from the lip would actually use, and the drift cap terminates the walk
            // early instead of being tested as an afterthought.
            walks++;
            Trace("balcony path: walking the line from " + direct + " toward " + upper);
            IntVec3 from = direct;
            int dx = upper.x - from.x;
            int dz = upper.z - from.z;
            int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            if (steps <= 0)
            {
                Trace("REJECT: the two participants share a column and the fast path failed");
                return;
            }
            int maxSteps = Mathf.Min(steps, Mathf.CeilToInt(driftCap));
            for (int i = 1; i <= maxSteps; i++)
            {
                IntVec3 candidate = new IntVec3(
                    from.x + Mathf.RoundToInt(dx * (i / (float)steps)), 0,
                    from.z + Mathf.RoundToInt(dz * (i / (float)steps)));
                if (candidate == direct)
                {
                    continue; // already tried, and the fast path is the only place it belongs
                }
                if (Accepts(map, bands, candidate, upper, lower, upperBand, lowerBand, driftCap))
                {
                    sol.valid = true;
                    sol.opening = candidate;
                    return;
                }
            }
            Trace("REJECT: no cell on the line from the target's column to the shooter is an "
                + "open column with sight at both ends (tried " + maxSteps + ")");
        }

        /// <summary>The three tests, in the cheap-first order that matters: a terrain read,
        /// then a column walk of at most bandCount reads, then two line-of-sight traces.</summary>
        private static bool Accepts(Map map, ABBandMap bands, IntVec3 opening, IntVec3 upper,
            IntVec3 lower, int upperBand, int lowerBand, float driftCap)
        {
            if (!opening.InBounds(map) || bands.BandOf(opening) != upperBand
                || bands.InGutter(opening))
            {
                Trace(opening + ": off map, wrong band, or in the gutter");
                return false;
            }
            // The cone under the hole. Measured from the OPENING to the LOWER participant,
            // which is the only pair this cap is about.
            IntVec3 openingInLower = bands.Translate(opening, lowerBand);
            float drift = (openingInLower - lower).LengthHorizontal;
            if (drift > driftCap)
            {
                Trace(opening + ": drift " + drift.ToString("0.0") + " exceeds cap "
                    + driftCap.ToString("0.0") + " (outside the cone under the hole)");
                return false;
            }
            if (!ColumnOpen(map, bands, opening, upperBand, lowerBand))
            {
                return false; // ColumnOpen traces the offending cell itself
            }
            if (opening != upper
                && !GenSight.LineOfSight(upper, opening, map, skipFirstCell: true))
            {
                Trace(opening + ": no line of sight from " + upper
                    + " to the opening within band " + upperBand);
                return false;
            }
            if (openingInLower != lower
                && !GenSight.LineOfSight(openingInLower, lower, map, skipFirstCell: true))
            {
                Trace(opening + ": open column and sight from above, but no line of sight from "
                    + "the opening's mouth " + openingInLower + " to " + lower
                    + " within band " + lowerBand);
                return false;
            }
            Trace(opening + ": ACCEPTED (drift " + drift.ToString("0.0") + ")");
            return true;
        }

        /// <summary>
        /// Is this column open air the whole way from <paramref name="upperBand"/> down to
        /// <paramref name="lowerBand"/>?
        ///
        /// ⚠ INTERNAL BECAUSE THE RENDER RELAYS SHARE IT: "can a bullet pass through this
        /// column" and "can the viewer see a thing flying above this column" are the same
        /// predicate, and §14 has the receipt for keeping one copy (THREE COPIES OF ONE
        /// TRANSFORM, TWO RIGHT). ABCombatRelay and ABSkyfallerRelay both call it with the
        /// thing's CURRENT cell to decide whether something on a band above the view shows
        /// through the holes in the ceiling.
        ///
        /// ⚠⚠ THIS IS DELIBERATELY *NOT* ABBands.TryResolveVisibleBelow, AND THE REASON IS
        /// LOAD-BEARING. That method is THE one descent rule for everything that LOOKS down,
        /// and it descends through <c>ABBands.ShowsBelow</c>, which accepts AB_WallTop as
        /// well as AB_OpenAir: seeing the top of a wall on the level below is legitimate,
        /// because the wall IS what you see. A bullet may not pass through it. Being able to
        /// SEE a surface is not being able to SHOOT through it, and the terrain file has
        /// carried that note since AB_WallTop was added.
        ///
        /// So this is a strict-open-air sibling, and the price of the copy is that it must be
        /// kept in step by hand. It is the ONLY descent in the mod allowed to differ, and it
        /// differs in exactly one predicate.
        /// </summary>
        internal static bool ColumnOpen(Map map, ABBandMap bands, IntVec3 opening, int upperBand,
            int lowerBand)
        {
            if (map.terrainGrid.TerrainAt(opening) != ABDefOf.AB_OpenAir)
            {
                Trace(opening + ": terrain is "
                    + map.terrainGrid.TerrainAt(opening).defName
                    + ", not AB_OpenAir - nothing to shoot through");
                return false;
            }
            // Every band STRICTLY between the two must be open air at this column too. For
            // adjacent bands this loop does not execute at all, which is why the common case
            // costs a single terrain read.
            for (int b = upperBand - 1; b > lowerBand; b--)
            {
                IntVec3 c = bands.Translate(opening, b);
                if (!c.InBounds(map) || bands.InGutter(c)
                    || map.terrainGrid.TerrainAt(c) != ABDefOf.AB_OpenAir)
                {
                    Trace(opening + ": column blocked at " + c + " (band " + b + ", terrain "
                        + (c.InBounds(map) ? map.terrainGrid.TerrainAt(c).defName : "off map")
                        + ") - the shot would have to pass through an intervening floor");
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Does anything whose projectile flies overhead use the map-coordinate rule?
        /// Two conditions, because they are not the same set: a mortar's shell has
        /// <c>flyOverhead</c>, while some verbs simply do not require line of sight.
        /// Both mean "the shot does not travel through the room", so both ignore levels.
        /// </summary>
        public static bool IsOverheadFire(Verb verb)
        {
            if (verb == null || verb.verbProps == null)
            {
                return false;
            }
            return !verb.verbProps.requireLineOfSight || verb.ProjectileFliesOverhead();
        }
    }
}
