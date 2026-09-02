using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §96.d  THE PERMANENT WATER FIX: PUBLISH THE BAND'S SIZE, NOT THE STACK'S.
    ///
    /// SYMPTOM: on a banded map every body of water - lakes and ocean included, not just
    /// rivers - shows ripples running north-south, as vertical streaks rather than the
    /// roughly isotropic shimmer of an ordinary map.
    ///
    /// MECHANISM (lab-confirmed over two instrumented runs, §96.b/§96.c). The water shader
    /// UVs its surface by <c>worldPos.xz / _MapSize</c>. <c>WaterInfo.SetTextures</c>
    /// publishes that as the map's REAL dimensions, which on a stacked map is the whole
    /// column: 126 x 896. Dividing z by seven times more than x stretches every ripple
    /// seven times along z, which is exactly the reported streaking, and it explains the
    /// symptom's uniformity - it is a coordinate scale, so it applies to all water equally,
    /// with or without a river anywhere near it.
    ///
    /// THE FIX is to hand the shader the dimensions of the LEVEL the water is actually on.
    /// A band is square (§2), so that is (x, x) in practice, but the band height is read
    /// properly rather than assumed - if bands ever stop being square, this stays correct.
    ///
    /// ⚠⚠ READ THIS BEFORE "SIMPLIFYING" IT TOWARDS THE OLD REJECTED CHANGE. ABWaterBand
    /// carries a "REJECTED - DO NOT RETRY" banner about republishing <c>_MapSize</c> as one
    /// band, disproved in a single launch. That experiment was CONFOUNDED and rule 77
    /// applies: it shipped the republish BUNDLED with a band-folded, REPEAT-wrapped flow
    /// texture, and the flow texture was the thing that made water run north-south. Vanilla
    /// builds <c>riverFlowTexture</c> at full map size with <c>TextureWrapMode.Clamp</c> and
    /// that is already 1:1 correct for every band, because rivers only ever exist on the
    /// surface band. THIS CHANGE TOUCHES THE VECTOR AND NOTHING ELSE. Do not re-fold the
    /// flow texture; that is the part that was genuinely disproved.
    ///
    /// COLLATERAL: none by inspection. <c>_MapSize</c> is written in exactly ONE place in
    /// the entire game - <c>WaterInfo.SetTextures</c>, its last statement - so despite the
    /// generic name it is a water-system global, and a postfix there is both the only
    /// publisher and the last writer. Round 2 watched fog and edge effects for side effects
    /// and reported none.
    ///
    /// DELIVERY: global, deliberately. The lab also offered a material-local delivery
    /// (a material property shadows a global of the same name), which would be tighter
    /// still - but ONLY if the shader declares <c>_MapSize</c> as a property, and a value
    /// that vanilla only ever sets globally almost certainly is not declared. A
    /// material-local write that silently does nothing would ship a non-fix that looks like
    /// a fix, which is strictly worse than a scoped global. The property census is logged
    /// once below so the option can be revisited with data rather than a guess.
    ///
    /// ⚠ BANDED MAPS ONLY. An ordinary colony never reaches the assignment, so this cannot
    /// regress a non-banded save, and that is the whole risk argument.
    /// </summary>
    [HarmonyPatch(typeof(WaterInfo), nameof(WaterInfo.SetTextures))]
    public static class Patch_WaterInfo_ABBandSquareMapSize
    {
        /// <summary>Dev A/B switch. Off restores vanilla's stacked value so the streaking
        /// can be reproduced on demand without unloading the mod.</summary>
        internal static bool Enabled = true;

        private static bool censusDone;

        private static void Postfix(WaterInfo __instance)
        {
            try
            {
                if (!Enabled)
                {
                    return;
                }
                // ⚠ STAND DOWN WHILE THE LAB IS DRIVING. The water lab exists to A/B this
                // exact global; if it is overriding anything we must not fight it, or its
                // "square OFF" case would silently still be square and the rig would lie.
                if (ABWaterLab.AnyOverride)
                {
                    return;
                }
                Map map = __instance?.map;
                if (map == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || bands.bandHeight <= 0)
                {
                    return; // ordinary map - vanilla's value is correct
                }
                if (bands.bandHeight >= map.Size.z)
                {
                    return; // nothing stacked to correct for
                }
                MaybeLogPropertyCensus();
                Shader.SetGlobalVector(ShaderPropertyIDs.MapSize,
                    new Vector4(map.Size.x, bands.bandHeight));
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: band water _MapSize publish threw: " + e,
                    762195905);
            }
        }

        /// <summary>
        /// Once per session, record how many water materials DECLARE <c>_MapSize</c>.
        ///
        /// Not used to decide anything - the delivery above is fixed - but it is the one
        /// number that would justify moving to a material-local delivery later, and
        /// capturing it here means that decision never has to be a guess or another lab run.
        /// Behind verboseLogging like every other diagnostic.
        /// </summary>
        private static void MaybeLogPropertyCensus()
        {
            if (censusDone)
            {
                return;
            }
            censusDone = true;
            try
            {
                int total = 0;
                int withProp = 0;
                foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading)
                {
                    if (t == null || !t.IsWater || t.waterDepthMaterial == null)
                    {
                        continue;
                    }
                    total++;
                    if (t.waterDepthMaterial.HasProperty(ShaderPropertyIDs.MapSize))
                    {
                        withProp++;
                    }
                }
                ABLog.Dev("V2: band water _MapSize -> square publish active; " + withProp
                    + " of " + total + " water depth material(s) declare _MapSize"
                    + (withProp == 0
                        ? " (0 = material-local shadowing is impossible, global is the only"
                            + " delivery that can work)"
                        : " (material-local delivery is available if collateral ever appears)"));
            }
            catch (Exception)
            {
                // A census is not worth failing the frame over.
            }
        }
    }
}
