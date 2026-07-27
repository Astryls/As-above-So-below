using System;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Cheap global gates for hot Harmony patches (temperature getters fire on
    /// every read game wide; the cross-level job/area/UI patches fire per think
    /// eval, per cell and per frame): when the game has no sky, no basement, or
    /// no columns at all they early out on static reads alone, before touching
    /// the per-map comp cache.
    ///
    /// The counts are keyed to the Game via a weak reference because MapRemoved
    /// never fires on game unload; a stale count from an abandoned game only
    /// ever degrades the optimization, never correctness (the game-match check
    /// reads false). Checked count-first so the common no-column case never
    /// touches the weak reference.
    ///
    /// Extracted from LevelComp (refactor R3) so the census is a standalone
    /// concern: patches read the gates here, and LevelComp only feeds the counts
    /// via <see cref="NoteLevel"/> from FinalizeInit/MapRemoved.
    /// </summary>
    public static class LevelCensus
    {
        private static WeakReference countGame;
        private static int skyCount;
        private static int basementCount;

        private static bool CountGameCurrent =>
            countGame != null && countGame.Target == (object)Current.Game;

        public static bool AnySkyLevels => skyCount > 0 && CountGameCurrent;

        public static bool AnyBasementLevels => basementCount > 0 && CountGameCurrent;

        /// <summary>Any linked pocket level (sky OR basement) exists in the
        /// current game. A pocket map only ever exists linked to a ground, so
        /// this is the universal "any multi-level column exists" gate for the
        /// hot cross-level patches: when it is false, no map can be part of a
        /// column, so every gated patch is a strict no-op and can bail on a
        /// single static read.</summary>
        public static bool AnyLevelColumns => (skyCount > 0 || basementCount > 0) && CountGameCurrent;

        /// <summary>Adjust the pocket-level counts for the current game. Called
        /// by LevelComp.FinalizeInit (+1) and MapRemoved (-1) for level != 0.</summary>
        public static void NoteLevel(int lvl, int delta)
        {
            Game cur = Current.Game;
            if (cur == null)
            {
                return;
            }
            if (countGame == null || countGame.Target != (object)cur)
            {
                countGame = new WeakReference(cur);
                skyCount = 0;
                basementCount = 0;
            }
            if (lvl == 1)
            {
                skyCount = Math.Max(0, skyCount + delta);
            }
            else if (lvl == -1)
            {
                basementCount = Math.Max(0, basementCount + delta);
            }
        }
    }
}
