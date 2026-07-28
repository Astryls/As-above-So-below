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
        public Building_ABStairs2 counterpart;

        /// <summary>Guards the recursive spawn: the counterpart must not try to build its
        /// own counterpart back again.</summary>
        private static bool spawningCounterpart;

        private ABBandStairsExt ext;

        public ABBandStairsExt Ext => ext ?? (ext = def.GetModExtension<ABBandStairsExt>());

        public int LevelDelta => Ext?.levelDelta ?? -1;

        protected override bool AlwaysOpen => true;

        public override bool FreePassage => true;

        public override bool PawnCanOpen(Pawn p) => true;

        /// <summary>No sliding door halves. The def uses MapMeshOnly so the graphic is
        /// PRINTED like any other building - which also means a stairwell on the surface
        /// shows up in the sky level's see-below view, where a RealtimeOnly door would
        /// not.</summary>
        protected override bool CanDrawMovers => false;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (respawningAfterLoad)
            {
                // Links are runtime state; rebuild them from the saved counterpart ref.
                if (counterpart != null && counterpart.Spawned)
                {
                    ABWormhole.Link(this, counterpart);
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
                Messages.Message("AB2: this map has no bands - stairs do nothing here.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            int myBand = bands.BandOf(Position);
            int targetBand = myBand + LevelDelta;
            if (!bands.BandExists(targetBand))
            {
                Messages.Message("AB2: no level in that direction.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            IntVec3 farCell = bands.Translate(Position, targetBand);
            if (!farCell.InBounds(map))
            {
                return;
            }

            // Resolve the counterpart BEFORE carving: the pocket has to be sized from the
            // far building's actual FOOTPRINT, and until we know its def we do not know how
            // big that is. (Carving first, with a fixed rect around the centre cell, is what
            // walled pawns in - see CarveLanding.)
            ThingDef cpDef = CounterpartDef();
            if (cpDef == null)
            {
                Log.Error(ABLog.Tag + " V2: no counterpart stairs def for " + def.defName + ".");
                return;
            }
            Building_ABStairs2 cp = farCell.GetFirstThing<Building_ABStairs2>(map);
            CellRect farFootprint = cp != null
                ? cp.OccupiedRect()
                : GenAdj.OccupiedRect(farCell, Rot4.North, cpDef.Size);
            CarveLanding(map, bands, farFootprint, targetBand);

            if (cp == null)
            {
                spawningCounterpart = true;
                try
                {
                    Thing t = ThingMaker.MakeThing(cpDef, Stuff ?? GenStuff.DefaultStuffFor(cpDef));
                    cp = (Building_ABStairs2)GenSpawn.Spawn(t, farCell, map, WipeMode.Vanish);
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
            counterpart = cp;
            cp.counterpart = this;

            ABWormhole.Link(this, cp);
            bands.Open(targetBand);
            map.fogGrid.Unfog(farCell);
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
            Building_ABStairs2 cp = counterpart;
            counterpart = null;
            if (cp != null)
            {
                cp.counterpart = null;
            }
            base.DeSpawn(mode);
            // A shaft has two ends; destroying one collapses the other.
            if (cp != null && cp.Spawned && !spawningCounterpart)
            {
                spawningCounterpart = true;
                try
                {
                    cp.Destroy(DestroyMode.Vanish);
                }
                finally
                {
                    spawningCounterpart = false;
                }
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref counterpart, "AB2_counterpart");
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string mine = counterpart != null && counterpart.Spawned
                ? "Connects to level " + ABBands.LevelOf(Map, counterpart.Position)
                : "Not connected";
            return string.IsNullOrEmpty(s) ? mine : s + "\n" + mine;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (counterpart == null || !counterpart.Spawned)
            {
                yield break;
            }
            yield return new Command_Action
            {
                defaultLabel = "AB2: view other end",
                defaultDesc = "Jump the camera to the far end of this stairwell.",
                action = delegate
                {
                    ABBandView.JumpTo(Map, counterpart.Position);
                }
            };
        }
    }
}
