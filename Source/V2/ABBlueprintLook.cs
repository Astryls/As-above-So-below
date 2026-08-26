using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Blueprints of AB2 buildables render as their FINISHED art (§74).
    ///
    /// Vanilla's ThingDefGenerator_Buildings.NewBlueprintDef_Thing builds every blueprint
    /// graphic with an unconditional BlueprintColor wash - (0.82, 0.92, 1.0) at 0.6 alpha -
    /// through either the Transparent shader (defs that author blueprintGraphicData: the
    /// stairs family) or EdgeDetect (everything else: the columns). Both read as a washed
    /// out blue smear over the MORTON art, which is what "blueprints don't look like the
    /// finished building" was. The color assignment is hardcoded C#, unreachable from XML;
    /// the per-def blueprintGraphicData blocks in AB2_Stairs.xml stay as a no-dll fallback
    /// (blue but at least per-def art) and this pass is the real look.
    ///
    /// Timing: HarmonyBoot is [StaticConstructorOnStartup], which runs AFTER
    /// GenerateImpliedDefs_PreResolve, so a postfix on the generator can never see these
    /// defs - the blueprint ThingDefs already exist before we can patch anything. This
    /// pass therefore MUTATES the generated defs: rebuild the blueprint GraphicData from
    /// the source def's real graphicData (art, class, drawSize, per-rotation offsets, link
    /// data - the authored blueprintGraphicData blocks never carried the ladder's
    /// drawOffsets, so its blueprint also sat centred while the built ladder hugs the
    /// wall), drop the blue wash, keep translucency so a plan never reads as a built
    /// thing, and re-resolve the cached graphic. GraphicData.CopyFrom nulls cachedGraphic,
    /// and static ctors run on the main thread inside ExecuteWhenFinished, after the def
    /// PostLoad callbacks that resolved def.graphic - so texture loads are legal here and
    /// the reassignment sticks. Even if a PostLoad callback ran later, it would re-resolve
    /// from bp.graphicData, which is already ours: self-healing in either order.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABBlueprintLook
    {
        /// <summary>
        /// Finished art at 70% alpha: recognizable at a glance, still clearly a plan.
        /// One number to taste - raise toward 1.0 for a more solid look.
        /// </summary>
        internal const float BlueprintAlpha = 0.70f;

        static ABBlueprintLook()
        {
            int touched = 0;
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                if (def.category != ThingCategory.Building
                    || def.blueprintDef == null
                    || def.designationCategory == null   // excludes the spike + generated carriers
                    || def.graphicData == null
                    || def.modContentPack == null
                    || !string.Equals(def.modContentPack.PackageId, "astryl.asabovesobelow2",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                ThingDef bp = def.blueprintDef;
                GraphicData gd = new GraphicData();
                gd.CopyFrom(def.graphicData);            // the real finished-art data
                gd.shadowData = null;                    // plans cast no shadow (generator invariant)
                gd.shaderType = ShaderTypeDefOf.Transparent;
                gd.color = new Color(1f, 1f, 1f, BlueprintAlpha);
                gd.renderQueue = 2950;                   // the generator's blueprint queue
                bp.graphicData = gd;
                bp.graphic = gd.Graphic;                 // def.graphic was already resolved; replace it
                touched++;
            }
            // Rule 33: a filter that can reject everything must say so.
            if (touched == 0)
            {
                Log.Warning(ABLog.Tag + " blueprint retint matched no defs; blueprints will show vanilla blue.");
            }
        }
    }

    /// <summary>
    /// Blueprint_Build plus the finished building's stuff tint. The retinted blueprint art
    /// is white at 0.7 alpha; the links are stuffed (Metallic/Woody/Stony) and the BUILT
    /// thing draws stuff-colored, so without this a wooden staircase planned in sandstone
    /// and a granite one planned identically. stuffToUse is assigned by
    /// GenConstruct.PlaceBlueprintForBuild before spawn; Thing.Graphic sees DrawColor
    /// differ from the graphic's color and serves the recolored version through the same
    /// GetColoredVersion path the built thing uses. White fallback covers a null stuff.
    /// </summary>
    public class Blueprint_ABBuild : Blueprint_Build
    {
        public override Color DrawColor
        {
            get
            {
                ThingDef built = def.entityDefToBuild as ThingDef;
                if (stuffToUse != null && built != null)
                {
                    Color c = built.GetColorForStuff(stuffToUse);
                    return new Color(c.r, c.g, c.b, ABBlueprintLook.BlueprintAlpha);
                }
                return base.DrawColor;
            }
        }
    }

    /// <summary>
    /// GhostUtility.GhostGraphicFor special-cases every Building_Door: it ignores
    /// useBlueprintGraphicAsGhost entirely and returns the uiIcon as a non-rotating
    /// Graphic_Single, so a stairs placement ghost showed the south face at every
    /// rotation (the def comment's claim that useBlueprintGraphicAsGhost covered the
    /// ghost was wrong for door-class defs). Bypass the door branch for our link defs
    /// only: wrap the (now finished-art) blueprint graphic in the same EdgeDetect +
    /// ghost-color treatment vanilla gives every normal building, which keeps the
    /// white/red validity feedback and restores rotation. Columns are not doors and
    /// already take the correct vanilla path.
    /// </summary>
    [HarmonyPatch(typeof(GhostUtility), nameof(GhostUtility.GhostGraphicFor))]
    public static class ABGhostGraphicForLinks
    {
        /// <summary>Keyed like vanilla's own ghost cache (def x ghost color).</summary>
        private static readonly Dictionary<int, Graphic> cache = new Dictionary<int, Graphic>();

        public static bool Prefix(ThingDef thingDef, Color ghostCol, ref Graphic __result)
        {
            if (thingDef == null
                || thingDef.blueprintDef == null
                || !thingDef.HasModExtension<ABBandStairsExt>())
            {
                return true;    // vanilla for everything that is not an AB2 link
            }
            int seed = Gen.HashCombine(0, thingDef);
            seed = Gen.HashCombineStruct(seed, ghostCol);
            if (!cache.TryGetValue(seed, out Graphic g))
            {
                Graphic src = thingDef.blueprintDef.graphic;
                GraphicData data = null;
                if (src.data != null)
                {
                    data = new GraphicData();
                    data.CopyFrom(src.data);
                    data.shadowData = null;
                }
                g = GraphicDatabase.Get(src.GetType(), src.path, ShaderTypeDefOf.EdgeDetect.Shader,
                    src.drawSize, ghostCol, Color.white, data, null);
                cache.Add(seed, g);
            }
            __result = g;
            return false;
        }
    }
}
