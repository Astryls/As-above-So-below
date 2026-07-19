using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    [DefOf]
    public static class ABDefOf
    {
        public static KeyBindingDef AB_ViewLevelUp;
        public static KeyBindingDef AB_ViewLevelDown;

        static ABDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ABDefOf));
        }
    }
}
