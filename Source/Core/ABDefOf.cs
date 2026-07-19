using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    [DefOf]
    public static class ABDefOf
    {
        public static KeyBindingDef AB_ViewLevelUp;
        public static KeyBindingDef AB_ViewLevelDown;

        public static JobDef AB_UseStairs;
        public static JobDef AB_HaulAcrossLevels;
        public static JobDef AB_RescueAcrossLevels;
        public static JobDef AB_CaptureAcrossLevels;

        public static MapGeneratorDef AB_Basement;
        public static MapGeneratorDef AB_Sky;

        public static TerrainDef AB_OpenAir;
        public static TerrainDef AB_RoofSurface;
        public static TerrainDef AB_RockRoofSurface;

        static ABDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ABDefOf));
        }
    }
}
