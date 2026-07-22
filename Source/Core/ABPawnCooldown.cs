using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Shared per-pawn cooldown gate for the cross-level scan family (work,
    /// emergency work, joy, need migration, fetch). One instance per cooldown
    /// domain, replacing five hand-rolled copies of the same dictionary
    /// pattern. Bounded: the map clears itself past 512 entries (dead pawns'
    /// ids simply age out); losing cooldown state early only means one extra
    /// scan, never a correctness problem.
    /// </summary>
    public sealed class ABPawnCooldown
    {
        private const int MaxEntries = 512;

        private readonly Dictionary<int, int> nextAllowedTick = new Dictionary<int, int>();

        /// <summary>True when the pawn is off cooldown at the given tick.</summary>
        public bool Ready(Pawn pawn, int now)
        {
            return !(nextAllowedTick.TryGetValue(pawn.thingIDNumber, out int next) && now < next);
        }

        /// <summary>Charge the cooldown so the pawn is gated until the given tick.</summary>
        public void ChargeUntil(Pawn pawn, int untilTick)
        {
            if (nextAllowedTick.Count > MaxEntries)
            {
                nextAllowedTick.Clear();
            }
            nextAllowedTick[pawn.thingIDNumber] = untilTick;
        }
    }
}
