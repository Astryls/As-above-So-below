using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Blueprints of AB2 buildables render VANILLA-STYLE, but from the REAL art (§74,
    /// reworked window 15).
    ///
    /// History, because this pass has now flipped twice. Vanilla's
    /// ThingDefGenerator_Buildings.NewBlueprintDef_Thing builds every blueprint graphic
    /// with an unconditional BlueprintColor wash - (0.82, 0.92, 1.0) at 0.6 alpha -
    /// through either the Transparent shader (defs that author blueprintGraphicData: the
    /// stairs family) or EdgeDetect (everything else: the columns). Over the old MORTON
    /// placeholder art that read as a washed-out blue smear, so §74.b replaced it with the
    /// finished art, white, translucent. FIELD REPORT (window 15) killed that look: even
    /// at 0.42 alpha, stuff-tinted finished art reads as an ALREADY-BUILT staircase - and
    /// under Toggleable Overlays' default slider (1.0, adopted by §79.4) it was literally
    /// opaque. So the wash is BACK, vanilla's own default-branch treatment exactly:
    /// EdgeDetect over the real art, BlueprintColor, colorTwo white, no stuff tint.
    ///
    /// What survives of §74 is the REBUILD, and it is still load-bearing: the authored
    /// blueprintGraphicData blocks never carried the ladder's per-rotation drawOffsets, so
    /// the generator's own graphic sat centred while the built ladder hugs the wall, and
    /// the door-class ghost fix below still needs a rotatable blueprint graphic to wrap.
    ///
    /// Timing: HarmonyBoot is [StaticConstructorOnStartup], which runs AFTER
    /// GenerateImpliedDefs_PreResolve, so a postfix on the generator can never see these
    /// defs - the blueprint ThingDefs already exist before we can patch anything. This
    /// pass therefore MUTATES the generated defs and re-resolves the cached graphic.
    /// GraphicData.CopyFrom nulls cachedGraphic, and static ctors run on the main thread
    /// inside ExecuteWhenFinished, after the def PostLoad callbacks that resolved
    /// def.graphic - so texture loads are legal here and the reassignment sticks. Even if
    /// a PostLoad callback ran later, it would re-resolve from bp.graphicData, which is
    /// already ours: self-healing in either order.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABBlueprintLook
    {
        /// <summary>Vanilla's own blueprint wash alpha (the 0.6 in BlueprintColor). The
        /// rgb stays the wash blue regardless; this is only the translucency.</summary>
        internal const float DefaultBlueprintAlpha = 0.6f;

        /// <summary>
        /// The live alpha. NOT a const any more, because Toggleable Overlays owns a global
        /// "blueprint transparency" slider and rewrites <c>graphic.color.a</c> on every
        /// blueprint def both at startup and on every settings write; rather than race it
        /// (rule 39 - and mod load order would decide the startup winner anyway) that slider
        /// is ADOPTED through <see cref="ApplyAlpha"/>. See ToggleableOverlaysCompat §4.
        /// With the wash restored their default (1.0) now means "opaque BLUE plan" - the
        /// same thing it means for every vanilla blueprint - instead of "looks built".
        /// </summary>
        internal static float BlueprintAlpha = DefaultBlueprintAlpha;

        /// <summary>The blueprint defs this pass owns, kept so the alpha can be re-applied
        /// later without re-running the whole match.</summary>
        private static readonly List<ThingDef> Retinted = new List<ThingDef>();

        /// <summary>GraphicData caches its resolved Graphic and nothing public invalidates
        /// it, so a colour change after startup would otherwise never reach the screen.
        /// Resolved defensively: if the field is ever renamed the retint still works at
        /// startup, only the live re-apply stops taking effect.</summary>
        private static readonly AccessTools.FieldRef<GraphicData, Graphic> CachedGraphicRef =
            ResolveCachedGraphicRef();

        private static AccessTools.FieldRef<GraphicData, Graphic> ResolveCachedGraphicRef()
        {
            try
            {
                return AccessTools.FieldRefAccess<GraphicData, Graphic>("cachedGraphic");
            }
            catch
            {
                return null;
            }
        }

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
                gd.CopyFrom(def.graphicData);            // the real art: offsets, rotations, link data
                gd.shadowData = null;                    // plans cast no shadow (generator invariant)
                // Vanilla's default branch verbatim (EdgeDetect + wash): what "a blueprint
                // looks like" to a player. Only the alpha is live, for the TO slider.
                gd.shaderType = ShaderTypeDefOf.EdgeDetect;
                Color wash = ThingDefGenerator_Buildings.BlueprintColor;
                gd.color = new Color(wash.r, wash.g, wash.b, BlueprintAlpha);
                gd.colorTwo = Color.white;
                gd.renderQueue = 2950;                   // the generator's blueprint queue
                bp.graphicData = gd;
                bp.graphic = gd.Graphic;                 // def.graphic was already resolved; replace it
                Retinted.Add(bp);
                touched++;
            }
            // Rule 33: a filter that can reject everything must say so.
            if (touched == 0)
            {
                Log.Warning(ABLog.Tag + " blueprint retint matched no defs; blueprints will show vanilla blue.");
            }
        }

        /// <summary>
        /// Set the plan alpha and rewrite the defs.
        ///
        /// Rgb is preserved (the wash), only alpha moves. The GraphicData cache must be
        /// dropped for the change to reach the screen - hence the field ref. Already-spawned
        /// blueprints keep the graphic they resolved on first draw, which matches Toggleable
        /// Overlays' own mid-game behaviour, so a slider change shows up on the next plan
        /// placed rather than retroactively.
        ///
        /// Safe to call before this class's own static ctor has run: touching it here forces
        /// the ctor first, so <see cref="Retinted"/> is always populated by the time the loop
        /// below executes.
        /// </summary>
        internal static void ApplyAlpha(float alpha)
        {
            BlueprintAlpha = alpha;
            for (int i = 0; i < Retinted.Count; i++)
            {
                ThingDef bp = Retinted[i];
                if (bp?.graphicData == null)
                {
                    continue;
                }
                Color c = bp.graphicData.color;
                bp.graphicData.color = new Color(c.r, c.g, c.b, alpha);
                if (CachedGraphicRef != null)
                {
                    CachedGraphicRef(bp.graphicData) = null;
                    bp.graphic = bp.graphicData.Graphic;
                }
            }
        }
    }

    /// <summary>
    /// KEPT AS A SHELL (window 15). The stairs defs name this class in blueprintClass and
    /// spawned plans in existing saves resolve their thingClass through the def, so the
    /// type must keep existing - but the stuff-tint DrawColor override it used to carry is
    /// deliberately GONE. Vanilla blueprints draw the uniform BlueprintColor wash whatever
    /// the stuff (Blueprint_Build has no DrawColor override, and Thing.DrawColor falls
    /// through to graphicData.color because a blueprint def is not MadeFromStuff);
    /// stuff-tinting the plan was half of what made it read as the built thing.
    /// </summary>
    public class Blueprint_ABBuild : Blueprint_Build
    {
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
