using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Direction a V2 stairwell travels, in levels. +1 up, -1 down.</summary>
    public class ABBandStairsExt : DefModExtension
    {
        public int levelDelta = -1;
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

            CarveLanding(map, bands, farCell, targetBand);

            ThingDef cpDef = CounterpartDef();
            if (cpDef == null)
            {
                Log.Error(ABLog.Tag + " V2: no counterpart stairs def for " + def.defName + ".");
                return;
            }
            Building_ABStairs2 cp = farCell.GetFirstThing<Building_ABStairs2>(map);
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
            // The far end travels the opposite way.
            string name = LevelDelta < 0 ? "AB2_StairsUp" : "AB2_StairsDown";
            return DefDatabase<ThingDef>.GetNamedSilentFail(name);
        }

        /// <summary>Makes the far cell (and a small apron around it) usable: clears rock
        /// below, lays a platform above, unfogs, and leaves the roof alone below so the
        /// basement stays a roofed interior exactly as a mined-out shaft would.</summary>
        private static void CarveLanding(Map map, ABBandMap bands, IntVec3 center, int targetBand)
        {
            bool sky = targetBand > bands.surfaceBand;
            CellRect apron = CellRect.CenteredOn(center, 1).ClipInsideMap(map);
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
