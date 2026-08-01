using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// "Goes IN a wall and faces out of it" - the VTE_WallMountedVent rule.
    ///
    /// ⚠ THIS IS NOT `isAttachment`, AND THE DIFFERENCE IS THE WHOLE BUG IT FIXES.
    /// RimWorld has two unrelated notions of wall-mounted and they place in different cells:
    ///
    ///   isAttachment + Placeworker_AttachedToWall  - the thing occupies the OPEN cell
    ///       BESIDE a wall, its Rotation points INTO that wall, and the wall must declare
    ///       `supportsWallAttachments`. Vanilla WallLamp works this way.
    ///   canPlaceOverWall + this                    - the thing occupies the WALL'S OWN cell
    ///       and its Rotation points OUT of the wall into the room. Vanilla Vent, VTE's
    ///       WallMountedVent and WallMountedCooler all work this way.
    ///
    /// The first was tried and is wrong for risers on both counts. Practically, ordinary
    /// walls do not set `supportsWallAttachments` (only a handful of natural and Anomaly
    /// structures do), so attachments simply refuse to place on a normal colony wall - which
    /// is exactly the "breaker boxes aren't placeable" report. And conceptually a riser is a
    /// conduit passing THROUGH the wall, not a lamp hanging off it.
    ///
    /// ⚠ AND IT RESTORES THE ORDINARY ROTATION CONVENTION. Because Rotation now points out
    /// of the wall rather than into it, `_south` is once again the view facing the player -
    /// the normal RimWorld habit - not the inverted one an attachment would need.
    ///
    /// Deliberately reimplemented rather than using VEF's `PlaceWorker_OnWall`: naming a VEF
    /// type would make every riser def depend on Vanilla Expanded Framework, including the
    /// vanilla-power, Bad Hygiene, Rimefeller and Rimatomics ones that have nothing to do
    /// with it. The rule is six lines; the dependency would be permanent.
    /// </summary>
    public class PlaceWorker_ABOnWall : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc,
            Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // The face has to look into open space. Placing one wall-deep inside a thick
            // wall would hide it completely and give it nothing to serve.
            IntVec3 facing = loc + rot.FacingCell;
            if (facing.InBounds(map))
            {
                // ⚠ IsWall is a PROPERTY in 1.6. VEF's shipped DLL calls it as a method,
                // so a decompile of their PlaceWorker_OnWall shows `IsWall()` - that is a
                // stale signature from an older game version, not the current API.
                Building ahead = facing.GetEdifice(map);
                if (ahead != null && ahead.def.IsWall)
                {
                    return new AcceptanceReport("AB_RiserFaceBlocked".Translate());
                }
            }
            if (loc.InBounds(map))
            {
                Building host = loc.GetEdifice(map);
                if (host != null && host.def.IsWall)
                {
                    return true;
                }
            }
            return new AcceptanceReport("AB_RiserNeedsWall".Translate());
        }
    }
}
