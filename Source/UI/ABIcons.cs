using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Shared lazy icon lookups; previously duplicated between the play
    /// settings buttons and the pawn gizmos.</summary>
    [StaticConstructorOnStartup]
    internal static class ABIcons
    {
        private static Texture2D up;
        private static Texture2D down;

        public static Texture2D UpStairs =>
            up ?? (up = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsUp")?.uiIcon);

        public static Texture2D DownStairs =>
            down ?? (down = DefDatabase<ThingDef>.GetNamedSilentFail("AB_StairsDown")?.uiIcon);
    }
}
