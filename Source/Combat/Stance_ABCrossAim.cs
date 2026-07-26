using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A render-only "busy aiming" stance for cross-gap shooting. The custom
    /// cross-level attack driver (JobDriver_ABCrossLevelAttack) manages its own
    /// warmup/burst/cooldown, but the pawn renderer only draws a weapon AIMED
    /// when curStance is a Stance_Busy with a valid focusTarg
    /// (PawnRenderUtility.DrawEquipmentAndApparelExtras). Without one, a
    /// gun-wielder renders in the carried-openly pose instead of tracking the
    /// target - the "pawns don't aim across levels" report.
    ///
    /// This stance exists purely so the renderer draws the aim. It:
    ///  - never casts a verb on expire (base Stance_Busy.Expire just drops to
    ///    Stance_Mobile - the driver owns firing, so no cross-map cast is ever
    ///    triggered through the stance);
    ///  - draws no warmup pie (that is Stance_Warmup's StanceDraw override; the
    ///    cross-level combat UI draws its own aim pie, so a plain Stance_Busy
    ///    subclass avoids a double pie);
    ///  - carries the target THING on the paired map as focusTarg. The renderer
    ///    computes the muzzle angle as (focusTarg.DrawPos - pawn.DrawPos)
    ///    .AngleFlat(), which uses only x/z - and the plumb below-view maps x/z
    ///    identically - so the weapon points exactly at where the target
    ///    appears through the gap.
    ///
    /// The driver refreshes ticksLeft every FireTick so it never lapses
    /// mid-engagement, and clears it in a finish action.
    /// </summary>
    public class Stance_ABCrossAim : Stance_Busy
    {
        public Stance_ABCrossAim()
        {
        }

        public Stance_ABCrossAim(int ticks, LocalTargetInfo focusTarg, Verb verb)
            : base(ticks, focusTarg, verb)
        {
        }
    }
}
