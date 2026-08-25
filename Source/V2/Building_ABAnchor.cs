using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE - a wormhole endpoint, and the prototype for the V2 stairwell.
    ///
    /// Subclasses Building_Door on purpose (a locked design decision): a door cell is
    /// the ONLY thing RegionTypeUtility.GetExpectedRegionType turns into a
    /// RegionType.Portal region, and Portal is what lets the wormhole conduct
    /// connectivity without merging rooms or temperature. Subclassing also inherits
    /// correct forbidden-passage and pawn-permission semantics for free.
    ///
    /// Door behaviours we do NOT want are suppressed by overriding AlwaysOpen and
    /// FreePassage: no open/close animation, no close delay, no "wait for door" stalls.
    /// A stairwell is a hole, not a door.
    /// </summary>
    public class Building_ABAnchor : Building_Door
    {
        /// <summary>Runtime-only for the spike (see ABBands.Register - V2 must scribe
        /// this on the map instead).</summary>
        public Building_ABAnchor partner;

        protected override bool AlwaysOpen => true;

        public override bool FreePassage => true;

        public override bool PawnCanOpen(Pawn p) => true;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            // The region for this cell only becomes Portal once the door exists, so the
            // link can only be armed after the rebuild that spawning triggers. The
            // rebuild postfix handles it; this is just the belt-and-braces nudge.
            ABWormhole.RearmAll(map);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            ABWormhole.Unlink(this, map);
            base.DeSpawn(mode);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string mine = partner != null && partner.Spawned
                ? "AB2 wormhole anchor -> " + partner.Position
                : "AB2 wormhole anchor (unlinked)";
            return string.IsNullOrEmpty(s) ? mine : s + "\n" + mine;
        }
    }
}
