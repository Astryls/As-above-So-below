using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Registry of live cross-gap projectiles (Model B): rounds we spawned on the
    /// TARGET's map and launched by hand across the sky &lt;-&gt; surface gap. It exists
    /// for ONE purpose - the see-below render exemption.
    ///
    /// The one-way mirror (LevelRenderer.TryDrawFilteredDynamic) normally only draws a
    /// lower-map thing where the sky cell above it is OPEN AIR and unroofed/unfogged, so
    /// a real bullet crossing the gap would wink out behind any solid sky floor and only
    /// flash at the hole. A registered round is instead drawn along its WHOLE path
    /// regardless of the cell above it, so the true 1:1 projectile itself reads the shot
    /// (this replaces the old fake tracer bolt). Rendering is otherwise 100% vanilla -
    /// the exemption only lifts the visibility gate, it does not change how the round is
    /// drawn (plumb x/z, altitude-dropped, full scale).
    ///
    /// Membership is by thingIDNumber, which is unique and monotone for a session, so a
    /// stale id can never collide with a live projectile. A Queue bounds the set with
    /// FIFO eviction - cross-gap rounds are short-lived, so a few hundred slots cover any
    /// realistic simultaneous-fire load and old ids roll off on their own. Keyed on the
    /// base Thing (never Verse.Projectile) so a Combat Extended ProjectileCE - which is
    /// NOT a Verse.Projectile - can be registered too, and no foreign type ever appears
    /// in a signature. Cleared with the rest of the static combat state on load
    /// (ABGameComp.FinalizeInit).
    /// </summary>
    internal static class CrossGapProjectiles
    {
        private const int Cap = 256;
        private static readonly HashSet<int> ids = new HashSet<int>();
        private static readonly Queue<int> order = new Queue<int>();

        /// <summary>Tag a freshly-launched cross-gap round so the see-below pass draws it
        /// across the whole gap. Accepts any Thing (vanilla Projectile or a CE
        /// ProjectileCE local); no-op on null.</summary>
        internal static void Register(Thing proj)
        {
            if (proj == null)
            {
                return;
            }
            int id = proj.thingIDNumber;
            if (ids.Add(id))
            {
                order.Enqueue(id);
                while (order.Count > Cap)
                {
                    ids.Remove(order.Dequeue());
                }
            }
        }

        /// <summary>True for a round we launched across the gap - the render exemption.
        /// Fast path is one bool short-circuit when nothing is in flight; only the id set
        /// (which only ever holds our own registered rounds) is consulted.</summary>
        internal static bool IsCrossGap(Thing t)
        {
            return ids.Count > 0 && t != null && ids.Contains(t.thingIDNumber);
        }

        [ABGameReset]
        internal static void ClearAll()
        {
            ids.Clear();
            order.Clear();
        }
    }
}
