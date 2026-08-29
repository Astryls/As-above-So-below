using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §85.10 PER-ROTATION DRAW SIZE for a vertical link's art ("overdraw").
    ///
    /// ⚠ WHY THIS NEEDS A DEF EXTENSION AT ALL. Vanilla gives per-rotation draw OFFSETS
    /// (graphicData.drawOffsetNorth/East/South/West) but only ONE draw SIZE, and
    /// Graphic.Print/MeshAt apply it as <c>rot.IsHorizontal ? drawSize.Rotated() : drawSize</c>
    /// - so east/west are forced to be the north/south size with its axes swapped. That is
    /// correct when the four sprites are one composition rotated, and wrong for this art pack,
    /// where the east/west PNGs were drawn at a different scale entirely (measured: the plain
    /// staircase is 0.42 x 0.74 of its image north/south but 0.62 x 0.59 east/west - see
    /// Tools/MeasureSprites.ps1). One shared size cannot serve both.
    ///
    /// Sizes here are in CELLS, in MAP SPACE, exactly as authored per rotation - they are NOT
    /// rotated again. What Tools/LinkApproachTagger.html shows is what the game draws.
    ///
    /// ⚠ OPTIONAL, AND ABSENT MEANS VANILLA. No extension, a list that is not exactly four
    /// long, or a non-positive entry, and every path falls through to base Graphic_Multi
    /// behaviour. Shipping this class with no def using it changes nothing.
    /// </summary>
    public class ABLinkArtExt : DefModExtension
    {
        /// <summary>One per rotation, Rot4 order (north, east, south, west), in cells.</summary>
        public List<Vector2> drawSizes;
    }

    /// <summary>
    /// texPath -> per-rotation draw size. Keyed by PATH rather than by def on purpose: the
    /// blueprint and the placement ghost are different ThingDefs (and, for the ghost, a
    /// different Graphic instance built with a different shader) but they all share the
    /// SOURCE TEXTURE PATH, so one registration covers built + blueprint + ghost and they
    /// cannot drift apart. That is the "make sure blueprints match" requirement satisfied
    /// structurally instead of by remembering to edit two blocks.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABLinkArt
    {
        private static readonly Dictionary<string, Vector2[]> sizes =
            new Dictionary<string, Vector2[]>();

        /// <summary>
        /// ⚠ A STATIC CTOR IS THE RIGHT HOOK AND THE TIMING IS NOT AN ACCIDENT.
        /// [StaticConstructorOnStartup] runs after the DefDatabase is populated and on the
        /// main thread, which is the same guarantee ABBlueprintLook relies on. Draw calls all
        /// happen later, so nothing can read this table before it is built.
        /// </summary>
        static ABLinkArt()
        {
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef def = defs[i];
                ABLinkArtExt ext = def.GetModExtension<ABLinkArtExt>();
                if (ext?.drawSizes == null || ext.drawSizes.Count != 4
                    || def.graphicData?.texPath == null)
                {
                    continue;
                }
                sizes[def.graphicData.texPath] = ext.drawSizes.ToArray();
            }
        }

        public static bool TrySize(string path, Rot4 rot, out Vector2 size)
        {
            size = default;
            if (path == null || sizes.Count == 0
                || !sizes.TryGetValue(path, out Vector2[] rows))
            {
                return false;
            }
            size = rows[rot.AsInt & 3];
            return size.x > 0f && size.y > 0f;
        }
    }

    /// <summary>
    /// Graphic_Multi that honours <see cref="ABLinkArtExt"/>'s per-rotation size.
    ///
    /// Only two members need overriding, because vanilla reads the size in exactly two
    /// places: MeshAt (every realtime draw - blueprints, ghosts, selection) and Print (the
    /// MapMeshOnly path the built links use). The per-rotation OFFSET is left to vanilla's
    /// own drawOffsetNorth/East/South/West, which both paths already apply, so there is no
    /// second implementation of it to get out of step.
    ///
    /// ⚠⚠ GetColoredVersion IS THE THIRD OVERRIDE AND IT IS THE ONE THAT WOULD HAVE BITTEN.
    /// Verse's Graphic_Multi.GetColoredVersion hard-codes <c>GraphicDatabase.Get&lt;Graphic_Multi&gt;</c>,
    /// so the moment a stuffed link resolves its colour - which is EVERY link, they are all
    /// Metallic/Woody/Stony - the graphic silently degrades to the base class and the
    /// per-rotation sizes stop applying. The unstuffed def would have looked perfect in the
    /// debug inspector while nothing on the map obeyed it.
    /// </summary>
    public class Graphic_ABLink : Graphic_Multi
    {
        public override Graphic GetColoredVersion(Shader newShader, Color newColor,
            Color newColorTwo)
        {
            return GraphicDatabase.Get<Graphic_ABLink>(path, newShader, drawSize, newColor,
                newColorTwo, data, maskPath);
        }

        public override Mesh MeshAt(Rot4 rot)
        {
            if (!ABLinkArt.TrySize(path, rot, out Vector2 s))
            {
                return base.MeshAt(rot);
            }
            // Flip handling copied from Verse.Graphic.MeshAt: a missing _west PNG is drawn as
            // a mirrored _east, and the mesh - not the material - is what carries that.
            return (rot == Rot4.West && WestFlipped) || (rot == Rot4.East && EastFlipped)
                ? MeshPool.GridPlaneFlip(s)
                : MeshPool.GridPlane(s);
        }

        public override void Print(SectionLayer layer, Thing thing, float extraRotation)
        {
            if (thing == null || !ABLinkArt.TrySize(path, thing.Rotation, out Vector2 size))
            {
                base.Print(layer, thing, extraRotation);
                return;
            }
            // Verse.Graphic.Print, verbatim apart from `size`. The atlas lookup is kept: a
            // building printed with its raw material instead of the atlas replacement still
            // renders, but it breaks batching for the whole section.
            bool flip = (thing.Rotation == Rot4.West && WestFlipped)
                        || (thing.Rotation == Rot4.East && EastFlipped);
            float angle = AngleFromRot(thing.Rotation) + extraRotation;
            if (flip && data != null)
            {
                angle += data.flipExtraRotation;
            }
            Vector3 center = thing.TrueCenter() + DrawOffset(thing.Rotation);
            Material mat = MatAt(thing.Rotation, thing);
            TryGetTextureAtlasReplacementInfo(mat, thing.def.category.ToAtlasGroup(), flip,
                vertexColors: true, out mat, out Vector2[] uvs, out Color32 vertexColor);
            Printer_Plane.PrintPlane(layer, center, size, mat, angle, flip, uvs,
                new Color32[4] { vertexColor, vertexColor, vertexColor, vertexColor });
            ShadowGraphic?.Print(layer, thing, 0f);
        }
    }
}
