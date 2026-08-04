using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Direction a V2 stairwell travels, in levels. +1 up, -1 down.</summary>
    public class ABBandStairsExt : DefModExtension
    {
        public int levelDelta = -1;

        /// <summary>defName of the building spawned at the far end. Lets ladders pair with
        /// ladders and stairs with stairs instead of everything collapsing to one type.</summary>
        public string counterpartDef;

        /// <summary>The elevator: one shaft member on EVERY level of the column, all
        /// pairwise linked, instead of a single far end. levelDelta is ignored when set.</summary>
        public bool linksAllLevels;
    }

    /// <summary>
    /// V2 stairwell. Replaces V1's Building_ABStairs entirely.
    ///
    /// Subclasses Building_Door because a door cell is the only thing that becomes a
    /// RegionType.Portal region, and Portal is what lets ABWormhole join two bands
    /// WITHOUT merging their rooms, temperatures or vacuum. Door behaviour proper is
    /// suppressed (AlwaysOpen / FreePassage) - a stairwell is a hole, not a door - while
    /// the inherited forbidden-passage and pawn-permission semantics are kept, because
    /// those are exactly right for a shaft.
    ///
    /// Spawning one does four things: carve a landing on the far side, spawn the
    /// counterpart directly above/below (bands are aligned 1:1, so the far cell is the
    /// same in-band x/z), join the pair with a wormhole RegionLink, and mark the target
    /// band open. From that moment the two bands are ONE connectivity graph and vanilla
    /// does the rest - hauling, work scanning, needs, prisoners, everything.
    /// </summary>
    public class Building_ABStairs2 : Building_Door
    {
        /// <summary>Every far end this link connects to. Stairs and ladders have exactly
        /// one; the elevator has one per other level of the column.
        ///
        /// This replaced a single scribed `counterpart` reference - the rework the elevator
        /// was deferred for. Old saves are migrated in ExposeData: the legacy
        /// "AB2_counterpart" key is still read and merged into the list at PostLoadInit, so
        /// pre-elevator stairs keep their pairing across the upgrade.</summary>
        private List<Building_ABStairs2> counterparts = new List<Building_ABStairs2>();

        private Building_ABStairs2 legacyCounterpart;

        public IReadOnlyList<Building_ABStairs2> Counterparts => counterparts;

        /// <summary>The end a camera-jump or go-order should prefer: the one on the band the
        /// player is looking at (they are looking at where they want to go), else the first
        /// live one.</summary>
        public Building_ABStairs2 BestCounterpartFor(int viewBand)
        {
            Building_ABStairs2 first = null;
            for (int i = 0; i < counterparts.Count; i++)
            {
                Building_ABStairs2 cp = counterparts[i];
                if (cp == null || !cp.Spawned)
                {
                    continue;
                }
                if (first == null)
                {
                    first = cp;
                }
                ABBandMap bands = ABBands.CompOf(cp.Map);
                if (bands != null && bands.BandOf(cp.Position) == viewBand)
                {
                    return cp;
                }
            }
            return first;
        }

        internal void AddCounterpart(Building_ABStairs2 cp)
        {
            if (cp != null && cp != this && !counterparts.Contains(cp))
            {
                counterparts.Add(cp);
            }
        }

        /// <summary>Guards the recursive spawn: the counterpart must not try to build its
        /// own counterpart back again.</summary>
        private static bool spawningCounterpart;

        /// <summary>Guards shaft collapse: destroying one member destroys them all, and
        /// each of those DeSpawns must not re-enter the collapse.</summary>
        private static bool collapsingShaft;

        private ABBandStairsExt ext;

        public ABBandStairsExt Ext => ext ?? (ext = def.GetModExtension<ABBandStairsExt>());

        public int LevelDelta => Ext?.levelDelta ?? -1;

        public bool LinksAllLevels => Ext?.linksAllLevels ?? false;

        protected override bool AlwaysOpen => true;

        public override bool FreePassage => true;

        public override bool PawnCanOpen(Pawn p) => true;

        /// <summary>No sliding door halves. The def uses MapMeshOnly so the graphic is
        /// PRINTED like any other building - which also means a stairwell on the surface
        /// shows up in the sky level's see-below view, where a RealtimeOnly door would
        /// not.</summary>
        protected override bool CanDrawMovers => false;

        /// <summary>Large enough that TicksTillFullyOpened clamps to 0 forever. The door
        /// never ticks, so this is set once and stays.</summary>
        private const int AlreadyFullyOpen = 100000;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);

            // FORCE THE DOOR PERMANENTLY OPEN. This is the fix for pawns freezing beside a
            // stairwell, and it is subtle enough to be worth spelling out.
            //
            // Pawn_PathFollower.NextCellDoorToWaitForOrManuallyOpen returns a door - making
            // the pawn stand still in a Stance_Cooldown instead of stepping - when ALL of:
            //     door.SlowsPawns && (!door.Open || door.TicksTillFullyOpened > 0)
            //         && door.PawnCanOpen(pawn)
            //
            // Every one of those was true for a stairwell:
            //   * SlowsPawns is `DoorPowerOn ? TicksToOpenNow > 20 : true` - a link has no
            //     power comp, so it takes the unpowered branch and is ALWAYS true.
            //   * PawnCanOpen is overridden to true here.
            //   * Open is `openInt`, and openInt only ever flips inside Building_Door.Tick.
            //     The def sets <tickerType>Never</tickerType> (deliberately - thousands of
            //     link cells should not tick), so Tick NEVER RUNS and openInt is false for
            //     the life of the building.
            //
            // So the pawn asked the door to open, waited TicksTillFullyOpened, the door
            // could not possibly open, and it retried forever. Overriding AlwaysOpen did not
            // help because AlwaysOpen only feeds StuckOpen and the auto-close logic - it
            // never assigns openInt.
            //
            // Setting both fields directly is what a ticking door would have reached on its
            // own, and it keeps tickerType Never. FreePassage/BlocksPawn were already correct,
            // which is exactly why the diagnostics looked clean: links armed 9/9, CanReach
            // true, a 15-node path found, every neighbour walkable - and a pawn that would
            // not move.
            openInt = true;
            ticksSinceOpen = AlreadyFullyOpen;

            if (respawningAfterLoad)
            {
                // Links are runtime state; rebuild them from the saved counterpart refs.
                // Each pair links when its SECOND end spawns (the first end sees the other
                // still unspawned and skips); ABWormhole.Link dedupes, so both trying is fine.
                for (int i = 0; i < counterparts.Count; i++)
                {
                    if (counterparts[i] != null && counterparts[i].Spawned)
                    {
                        ABWormhole.Link(this, counterparts[i]);
                    }
                }
                return;
            }
            if (spawningCounterpart)
            {
                return;
            }
            TryEstablish(map);
        }

        private void TryEstablish(Map map)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                // Backstop only: PlaceWorker_ABLinkApproach refuses this at placement.
                // Reachable via paths that bypass PlaceWorkers (dev spawn, quest spawn).
                Messages.Message("AB_StairsNoBands".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            int myBand = bands.BandOf(Position);

            // The elevator serves the whole column; stairs and ladders serve one direction.
            List<int> targets = new List<int>();
            if (LinksAllLevels)
            {
                for (int b = 0; b < bands.bandCount; b++)
                {
                    if (b != myBand)
                    {
                        targets.Add(b);
                    }
                }
            }
            else
            {
                targets.Add(myBand + LevelDelta);
            }

            List<Building_ABStairs2> established = new List<Building_ABStairs2>();
            for (int i = 0; i < targets.Count; i++)
            {
                int targetBand = targets[i];
                if (!bands.BandExists(targetBand))
                {
                    if (!LinksAllLevels)
                    {
                        // Backstop only: PlaceWorker_ABLinkApproach refuses this at
                        // placement (§29e). Reachable via dev/quest spawns.
                        Messages.Message("AB_StairsNoLevel".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                    }
                    continue;
                }
                Building_ABStairs2 cp = EstablishEnd(map, bands, targetBand);
                if (cp == null)
                {
                    continue;
                }
                AddCounterpart(cp);
                cp.AddCounterpart(this);
                ABWormhole.Link(this, cp);
                bands.Open(targetBand);
                established.Add(cp);
            }

            // Full mesh for the elevator: the sky and basement cars link DIRECTLY too, so a
            // sky-to-basement trip is one transit instead of a hop through the surface.
            for (int i = 0; i < established.Count; i++)
            {
                for (int j = i + 1; j < established.Count; j++)
                {
                    established[i].AddCounterpart(established[j]);
                    established[j].AddCounterpart(established[i]);
                    ABWormhole.Link(established[i], established[j]);
                }
            }
        }

        /// <summary>Find-or-spawn the far end on one band: carve the landing (sized from the
        /// real footprint), spawn with matching rotation, unfog. Returns null on failure.</summary>
        private Building_ABStairs2 EstablishEnd(Map map, ABBandMap bands, int targetBand)
        {
            IntVec3 farCell = bands.Translate(Position, targetBand);
            if (!farCell.InBounds(map))
            {
                return null;
            }

            // Resolve the counterpart BEFORE carving: the pocket has to be sized from the
            // far building's actual FOOTPRINT, and until we know its def we do not know how
            // big that is. (Carving first, with a fixed rect around the centre cell, is what
            // walled pawns in - see CarveLanding.)
            ThingDef cpDef = CounterpartDef();
            if (cpDef == null)
            {
                Log.Error(ABLog.Tag + " V2: no counterpart stairs def for " + def.defName + ".");
                return null;
            }
            Building_ABStairs2 cp = farCell.GetFirstThing<Building_ABStairs2>(map);
            CellRect farFootprint = cp != null
                ? cp.OccupiedRect()
                : GenAdj.OccupiedRect(farCell, Rotation, cpDef.Size);
            CarveLanding(map, bands, farFootprint, targetBand);

            if (cp == null)
            {
                spawningCounterpart = true;
                try
                {
                    Thing t = ThingMaker.MakeThing(cpDef, Stuff ?? GenStuff.DefaultStuffFor(cpDef));
                    // ROTATION MUST MATCH THIS END. GenAdj.AdjustForRotation shifts a
                    // building's OccupiedRect by a cell for any EVEN dimension:
                    //     if (size.x % 2 == 0) center.x += num;
                    //     if (size.z % 2 == 0) center.z += num2;
                    // The 1x1 ladder and the 3x3 grand stairs are odd on both axes and so are
                    // rotation-independent - but the 2x2 stairs are even on both. Spawning the
                    // counterpart with the default Rot4.North while the player places this end
                    // facing South (defaultPlacingRot) put the two footprints a cell out of
                    // alignment, even though Position translated 1:1 between bands.
                    //
                    // ABWormhole.TryCellPairs then paired cell i to cell i and armed every
                    // link, so nothing looked broken - but the linked cells were spatially
                    // offset from the visible stairwell. Which cell a pawn happened to step
                    // into decided whether the crossing behaved, which is the classic
                    // "stairs work sometimes" symptom. TryCellPairs documents the matching
                    // rotation as an invariant; this is the line that makes it true.
                    cp = (Building_ABStairs2)GenSpawn.Spawn(t, farCell, map, Rotation,
                        WipeMode.Vanish);
                    if (Faction != null)
                    {
                        cp.SetFaction(Faction);
                    }
                }
                finally
                {
                    spawningCounterpart = false;
                }
            }
            map.fogGrid.Unfog(farCell);

            // FLOOD the fog away, don't just poke holes in it. CarveLanding unfogs its
            // apron cell-by-cell, and a per-cell Unfog never propagates - so when a new
            // link's landing broke into a pre-existing open space (a Biomes! Caverns
            // cavern above all), the connected area stayed black even though the pawn
            // standing on the landing could see straight into it. Vanilla reveals a
            // breached cavern with FloodFillerFog.FloodUnfog when the last wall is mined;
            // this is the same event by another door, so it gets the same treatment.
            // Flooding from a standable apron cell spreads through everything connected
            // and stops at walls and fog blockers exactly like a mining breach.
            try
            {
                CellRect around = (cp != null ? cp.OccupiedRect()
                    : GenAdj.OccupiedRect(farCell, Rotation, def.Size)).ExpandedBy(1);
                foreach (IntVec3 c in around)
                {
                    if (c.InBounds(map) && c.Standable(map))
                    {
                        FloodFillerFog.FloodUnfog(c, map);
                        break;
                    }
                }
            }
            catch (System.Exception e)
            {
                ABLog.Dev("V2: landing flood-unfog failed (ignored): " + e.Message);
            }
            return cp;
        }

        private ThingDef CounterpartDef()
        {
            string name = Ext?.counterpartDef;
            if (string.IsNullOrEmpty(name))
            {
                // The far end travels the opposite way.
                name = LevelDelta < 0 ? "AB2_StairsUp" : "AB2_StairsDown";
            }
            return DefDatabase<ThingDef>.GetNamedSilentFail(name);
        }

        /// <summary>Makes the far END (and an apron around it) usable: clears rock below,
        /// lays a platform above, unfogs, and leaves the roof alone below so the basement
        /// stays a roofed interior exactly as a mined-out shaft would.
        ///
        /// TAKES THE FOOTPRINT, NOT THE CENTRE CELL. The original carved
        /// <c>CellRect.CenteredOn(center, 1)</c> - a fixed 3x3 around the building's
        /// Position - which is only correct for the 1x1 ladder. The arithmetic for the
        /// larger links is brutal:
        ///   - 2x2 stairs: the footprint runs from Position to Position+1, so the 3x3 apron
        ///     leaves NO margin past the far edge - clearance on two sides only.
        ///   - 3x3 grand stairs: the footprint IS Position +/- 1, i.e. exactly the apron.
        ///     Zero approach cells were carved and the arriving pawn was sealed in solid
        ///     rock on every side. Observed as a colonist stranded on the stairs.
        ///
        /// The margin is ABWormholePather.LandingRadius, and the two MUST agree: LandingCell
        /// sets a transiting pawn down anywhere within LandingRadius of the anchor, so a
        /// pocket smaller than that can drop a pawn straight into unmined rock. Carving one
        /// cell more than strictly needed also gives arrivals room to step aside instead of
        /// stacking on the anchor, which is the congestion that caused earlier stair jams.</summary>
        private static void CarveLanding(Map map, ABBandMap bands, CellRect footprint, int targetBand)
        {
            bool sky = targetBand > bands.surfaceBand;
            CellRect apron = footprint.ExpandedBy(ABWormholePather.LandingRadius).ClipInsideMap(map);
            CellRect bandRect = bands.RectOfBand(targetBand);
            foreach (IntVec3 c in apron)
            {
                if (!c.InBounds(map) || !bandRect.Contains(c))
                {
                    continue;
                }
                Building edifice = c.GetEdifice(map);
                if (edifice != null && edifice.def.building != null && edifice.def.building.isNaturalRock)
                {
                    edifice.Destroy(DestroyMode.Vanish);
                }
                if (sky)
                {
                    // A platform to stand on, otherwise the pawn arrives in open air.
                    if (map.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                    {
                        map.terrainGrid.SetTerrain(c, ABDefOf.AB_RoofSurface);
                    }
                }
                else if (map.terrainGrid.TerrainAt(c) == ABDefOf.AB_OpenAir)
                {
                    map.terrainGrid.SetTerrain(c, TerrainDefOf.Gravel);
                }
                map.fogGrid.Unfog(c);
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            ABWormhole.Unlink(this, map);
            List<Building_ABStairs2> cps = new List<Building_ABStairs2>(counterparts);
            counterparts.Clear();
            for (int i = 0; i < cps.Count; i++)
            {
                cps[i]?.counterparts.Remove(this);
            }
            base.DeSpawn(mode);
            // A shaft is one structure: destroying any member collapses all of it. The
            // latch stops each cascading DeSpawn from re-entering the collapse.
            if (collapsingShaft)
            {
                return;
            }
            collapsingShaft = true;
            try
            {
                for (int i = 0; i < cps.Count; i++)
                {
                    if (cps[i] != null && cps[i].Spawned)
                    {
                        cps[i].Destroy(DestroyMode.Vanish);
                    }
                }
            }
            finally
            {
                collapsingShaft = false;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref counterparts, "AB2_counterparts", LookMode.Reference);
            // Legacy single-counterpart saves (pre-elevator). Read the old key and merge.
            Scribe_References.Look(ref legacyCounterpart, "AB2_counterpart");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (counterparts == null)
                {
                    counterparts = new List<Building_ABStairs2>();
                }
                counterparts.RemoveAll(c => c == null);
                if (legacyCounterpart != null)
                {
                    AddCounterpart(legacyCounterpart);
                    legacyCounterpart = null;
                }
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string mine = "Not connected";
            if (counterparts.Count > 0 && Spawned)
            {
                List<string> levels = new List<string>();
                for (int i = 0; i < counterparts.Count; i++)
                {
                    if (counterparts[i] != null && counterparts[i].Spawned)
                    {
                        levels.Add(ABBands.LevelOf(Map, counterparts[i].Position).ToString());
                    }
                }
                if (levels.Count > 0)
                {
                    mine = "Connects to level" + (levels.Count > 1 ? "s " : " ")
                        + string.Join(", ", levels);
                }
            }
            return string.IsNullOrEmpty(s) ? mine : s + "\n" + mine;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            for (int i = 0; i < counterparts.Count; i++)
            {
                Building_ABStairs2 cp = counterparts[i];
                if (cp == null || !cp.Spawned)
                {
                    continue;
                }
                int level = ABBands.LevelOf(Map, cp.Position);
                yield return new Command_Action
                {
                    defaultLabel = "AB2: view level " + level,
                    defaultDesc = "Jump the camera to this link's end on level " + level + ".",
                    action = delegate
                    {
                        ABBandView.JumpTo(Map, cp.Position);
                    }
                };
            }
        }
    }
}
