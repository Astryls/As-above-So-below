using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §99.C - A MAP-LEVEL BIOME QUESTION ASKED WHILE DRAWING ONE BAND GETS THAT BAND'S ANSWER.
    ///
    /// THE CASE THAT FOUND IT. ReGrowth 2 gives its biomes their look by swapping terrain
    /// MATERIALS rather than by placing different terrain: <c>TerrainByBiome</c> supplies a
    /// texture path and <c>TerrainGrid_GetMaterial_Patch</c> substitutes the material. The
    /// selection is:
    ///
    ///     public static bool TryGetBiomeSpecificTerrain(this TerrainDef def, Map map, ...)
    ///     {
    ///         ...
    ///         BiomeDef biome = map.Biome;      // &lt;-- map-level, once, for everything
    ///
    /// On a banded map that paints EVERY level with the surface biome's textures. A basement
    /// band running a Biomes! Caverns biome is drawn as though it were the surface.
    ///
    /// ⚠ WHY THIS IS NOT A REGROWTH PATCH. The obvious fix - patch their method - is both
    /// fragile (a soft dependency, reflected by name, versioned independently) and too
    /// narrow: <c>map.Biome</c> during terrain drawing is a question ANY mod can ask, and
    /// several do. And it cannot be fixed at their call site anyway, because
    /// <c>TerrainGrid.GetMaterial(TerrainDef, bool, ColorDef)</c> TAKES NO CELL - by the time
    /// their code runs, the cell is gone. The only place that still knows where it is
    /// drawing is the caller, <c>SectionLayer_Terrain.Regenerate</c>.
    ///
    /// So the fix is at the funnel (rule 38: own the draw, at the funnel): while a terrain
    /// section is regenerating, <c>map.Biome</c> answers with the biome of the band that
    /// section sits in. Every consumer - ReGrowth, us, and anything written later - is
    /// corrected at once, and this file names no third-party type at all.
    ///
    /// ⚠ THE SCOPE IS AS NARROW AS IT IS POSSIBLE TO MAKE IT. Armed only for the duration of
    /// one <c>SectionLayer_Terrain.Regenerate</c> call, on one map, and read through a plain
    /// static bool first. <c>map.Biome</c> is asked constantly during ordinary play; outside
    /// a section regen this costs one bool load.
    ///
    /// ⚠ KNOWN IMPRECISION, RECORDED RATHER THAN HIDDEN. A section is 17x17 and a band slot
    /// is 192 rows, and 192 is not a multiple of 17 - so a section CAN straddle a band
    /// boundary, and this gives every cell in it the biome of the section's centre. For a
    /// texture swap that is invisible (the straddled rows are gutter open air, which draws
    /// no terrain), but it would NOT be good enough if this scope were ever reused for
    /// something with gameplay consequences. Do not widen it without revisiting that.
    /// </summary>
    internal static class ABBandSectionBiome
    {
        internal static bool Active;

        private static Map scopedMap;

        private static BiomeDef scopedBiome;

        internal static bool TryGet(Map map, out BiomeDef biome)
        {
            if (Active && scopedBiome != null && ReferenceEquals(map, scopedMap))
            {
                biome = scopedBiome;
                return true;
            }
            biome = null;
            return false;
        }

        internal static void Push(Map map, BiomeDef biome)
        {
            scopedMap = map;
            scopedBiome = biome;
            Active = biome != null;
        }

        internal static void Pop()
        {
            Active = false;
            scopedMap = null;
            scopedBiome = null;
        }
    }

    /// <summary>
    /// Arms the section biome scope around terrain section regeneration.
    ///
    /// <c>SectionLayer_Terrain.Regenerate</c> is the one place that has both the map and the
    /// cells, and it is the sole caller of <c>TerrainGrid.GetMaterial</c> - so it is exactly
    /// the bracket that turns a map-level biome lookup into a band-level one.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_Terrain), nameof(SectionLayer_Terrain.Regenerate))]
    public static class Patch_SectionLayer_Terrain_ABBandBiome
    {
        private static void Prefix(SectionLayer_Terrain __instance, out bool __state)
        {
            __state = false;
            try
            {
                // The Section carries its own map reference; MapDrawLayer.Map is protected,
                // and reaching for it would be a needless reflection dependency when the
                // section already knows.
                Section section = Traverse.Create(__instance).Field("section").GetValue<Section>();
                Map map = section?.map;
                if (map == null || !ABBands.Banded(map))
                {
                    return;
                }
                IntVec3 centre = section.CellRect.CenterCell;
                BiomeDef biome = ABBandEnv.BiomeOf(map, centre);
                if (biome == null || biome == map.Biome)
                {
                    return; // nothing to correct on this section
                }
                ABBandSectionBiome.Push(map, biome);
                __state = true;
            }
            catch
            {
                __state = false;
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                ABBandSectionBiome.Pop();
            }
        }
    }

    /// <summary>
    /// The redirect itself.
    ///
    /// ⚠ A PREFIX ON A PROPERTY THAT THE WHOLE GAME READS. It is safe only because
    /// <c>ABBandSectionBiome.Active</c> is false everywhere except inside one section
    /// regeneration, and because the answer it substitutes is the MORE correct one: within a
    /// band, the band's biome is what that ground actually is (§28 - a fiction for one
    /// subsystem is fact to the rest; here the per-band biome is not a fiction at all, it is
    /// what ABBandEnv already tells every other consumer).
    /// </summary>
    [HarmonyPatch(typeof(Map), nameof(Map.Biome), MethodType.Getter)]
    public static class Patch_Map_ABSectionBandBiome
    {
        private static bool Prefix(Map __instance, ref BiomeDef __result)
        {
            if (!ABBandSectionBiome.Active)
            {
                return true;
            }
            if (ABBandSectionBiome.TryGet(__instance, out BiomeDef biome))
            {
                __result = biome;
                return false;
            }
            return true;
        }
    }
}
