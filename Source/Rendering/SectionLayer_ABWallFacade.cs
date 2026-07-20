using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The slab-edge wall facade (user-directed round 17): where a slab cell
    /// (rooftop or built sky floor) sits over a supporting wall with open air
    /// directly south, the wall's own sprite is reprinted and drawn shifted
    /// south by the below-view depth offset, so the sliver protruding past
    /// the slab edge shows the REAL wall texture in place of the skirt's
    /// plain dark face. A sky wall standing on that rim cell then continues
    /// visually into the below wall's exposed top edge - one continuous tall
    /// wall with the same faux-perspective tilt as the rest of the below
    /// view. The skirt still draws beneath at its own lower queue, covering
    /// spans where nothing printable stands below (doorways, plain roof
    /// edges), so the slab silhouette never breaks.
    ///
    /// The slab does the clipping, not geometry: sky terrain draws above
    /// this layer's queue (BelowQueueCeiling - 55, just over the skirt's
    /// -60), so everything over slab cells is covered and only the offset
    /// sliver hanging over the air cell survives. Identity-anchored like the
    /// skirt, never the camera parallax matrix: the facade must hug the slab
    /// edge it extrudes from, and the ground sliding slightly beneath it
    /// while parallax is on is the same accepted limitation the skirt
    /// already documents. The south offset applies at draw time, so the
    /// depth shift slider takes effect live with no reprint.
    /// Kill switch: Rendering.
    /// </summary>
    public class SectionLayer_ABWallFacade : SectionLayer
    {
        public SectionLayer_ABWallFacade(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)ABDefOf.AB_BelowThings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible =>
            ABGuard.On(ABGuard.Rendering) && (ABMod.Settings?.drawWallFacade ?? true);

        private readonly List<int> vertsBefore = new List<int>();
        private readonly List<int> trisBefore = new List<int>();

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering) || map.Level() != 1)
            {
                return;
            }
            try
            {
                Map lower = map.LowerMap();
                if (lower == null || lower.Disposed)
                {
                    return;
                }
                TerrainGrid skyTerrain = map.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                FogGrid lowerFog = lower.fogGrid;
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(lower) || !IsSouthRimCell(map, skyTerrain, c, air, cap))
                    {
                        continue;
                    }
                    Building ed = lower.edificeGrid[c];
                    if (!ABRimPrint.QualifiesAsSupport(ed)
                        || (!ed.def.seeThroughFog && lowerFog.IsFogged(ed.Position)))
                    {
                        continue;
                    }
                    if (!IsFirstQualifyingCell(map, lower, skyTerrain, ed, c, air, cap))
                    {
                        continue;
                    }
                    ABRimPrint.Snapshot(subMeshes, vertsBefore, trisBefore);
                    try
                    {
                        ed.Print(this);
                        printed = true;
                    }
                    catch (Exception e)
                    {
                        ABRimPrint.Rollback(subMeshes, vertsBefore, trisBefore);
                        Log.WarningOnce(ABLog.Tag + " Facade print failed for " + ed.LabelCap
                            + ": " + e.Message, ed.thingIDNumber ^ 762195851);
                    }
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "wall facade layer");
            }
        }

        /// <summary>Slab cell (anything but open air and mountain cap - the
        /// skirt's own solidity rule, so facade and skirt always agree on
        /// where the edge is) whose south sky neighbor is open air.</summary>
        private static bool IsSouthRimCell(Map sky, TerrainGrid grid, IntVec3 c,
            TerrainDef air, TerrainDef cap)
        {
            if (!c.InBounds(sky))
            {
                return false;
            }
            TerrainDef t = grid.TerrainAt(c);
            if (t == null || t == air || t == cap)
            {
                return false;
            }
            IntVec3 s = c + IntVec3.South;
            return s.InBounds(sky) && grid.TerrainAt(s) == air;
        }

        private static bool IsFirstQualifyingCell(Map sky, Map lower, TerrainGrid grid,
            Building ed, IntVec3 c, TerrainDef air, TerrainDef cap)
        {
            CellRect rect = ed.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (q.InBounds(lower) && IsSouthRimCell(sky, grid, q, air, cap))
                    {
                        return q.x == c.x && q.z == c.z;
                    }
                }
            }
            return false;
        }

        /// <summary>Draws through queue clones shifted south by the depth
        /// offset; the sky map's own terrain covers everything over slab
        /// cells, leaving only the protruding sliver.</summary>
        public override void DrawLayer()
        {
            if (!Visible)
            {
                return;
            }
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                LayerSubMesh sub = subs[i];
                if (sub.finalized && !sub.disabled)
                {
                    LevelRenderer.DrawWallFacadeSubMesh(sub);
                }
            }
        }
    }
}
