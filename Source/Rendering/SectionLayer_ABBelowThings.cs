using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Below-things view, printed per cell (the per-cell-filter approach
    /// Z-Levels beta proved viable, implemented independently here): this
    /// SKY-map layer reprints the lower map's map-mesh things into its own
    /// submeshes, but ONLY for cells that are open air on this level. Covered
    /// cells (rooftop, mountain cap, built floors) print NOTHING below them,
    /// so the roof is opaque BY CONSTRUCTION and can never lose a render
    /// -queue contest against below content - the failure mode that let
    /// grass, walls, and items paint over the steel tiles while the below
    /// view redrew the lower map's own baked section meshes (those bake
    /// every cell and cannot be filtered per cell; playtest 2026-07-20).
    ///
    /// Submeshes draw through the forced below-band queues
    /// (LevelRenderer.DrawBelowSubMesh), so everything sky-side still renders
    /// above the view below - the one-way mirror holds. Linked graphics
    /// (walls, conduits) resolve against the LOWER map's grids because each
    /// print goes through the thing's own Graphic with its real map.
    ///
    /// Regeneration: the AB_BelowThings flag is mirrored per cell from the
    /// lower map's dirty events (LevelSync.OnLowerMeshDirty), plus this map's
    /// own Terrain flag (air/rooftop flips change the printed set).
    /// Kill switch: Rendering.
    /// </summary>
    public class SectionLayer_ABBelowThings : SectionLayer
    {
        public SectionLayer_ABBelowThings(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)ABDefOf.AB_BelowThings | (ulong)MapMeshFlagDefOf.Terrain;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering);

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            try
            {
                LevelComp comp = map.Levels();
                if (comp == null || comp.level <= 0)
                {
                    return;
                }
                Map lower = comp.lowerMap;
                if (lower == null || lower.Disposed)
                {
                    return;
                }
                TerrainGrid skyTerrain = map.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef cap = ABDefOf.AB_MountainTop;
                FogGrid lowerFog = lower.fogGrid;
                float scale = Mathf.Clamp(ABMod.Settings?.belowThingScale ?? 0.85f, 0.5f, 1f);
                bool doScale = scale < 0.999f;
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(lower))
                    {
                        continue;
                    }
                    // Print under open air AND under the mountain cap: every
                    // below rock sits under a natural roof, which the genstep
                    // maps to AB_MountainTop, never air (measured: 16995 vs 0).
                    // The cap layer skips its flat fill exactly where the below
                    // face is explored, so these prints show as the visible
                    // mountain faces; fogged content stays behind the fog fill
                    // (cap cells) or the opaque mask (air cells).
                    TerrainDef top = skyTerrain.TerrainAt(c);
                    if (top != air && top != cap)
                    {
                        continue;
                    }
                    List<Thing> things = lower.thingGrid.ThingsListAtFast(c);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        DrawerType drawer = t.def.drawerType;
                        if (drawer != DrawerType.MapMeshOnly && drawer != DrawerType.MapMeshAndRealTime)
                        {
                            // Realtime things belong to the filtered dynamic pass.
                            continue;
                        }
                        // Multi-cell things print once, from their root cell.
                        IntVec3 pos = t.Position;
                        if (pos.x != c.x || pos.z != c.z)
                        {
                            continue;
                        }
                        // Vanilla bakes only unfogged things; mirror that so the
                        // below view never reveals what surface pawns have not
                        // explored (fog lifting below reprints via the mirror).
                        if (!t.def.seeThroughFog && lowerFog.IsFogged(pos))
                        {
                            continue;
                        }
                        try
                        {
                            if (doScale && CanScale(t))
                            {
                                SnapshotVertCounts();
                                t.Print(this);
                                ScaleNewVerts(t.TrueCenter(), scale);
                            }
                            else
                            {
                                t.Print(this);
                            }
                            printed = true;
                        }
                        catch (Exception e)
                        {
                            Log.WarningOnce(ABLog.Tag + " Below print failed for " + t.LabelCap
                                + ": " + e.Message, t.thingIDNumber ^ 762195848);
                        }
                    }
                }
                if (printed)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "below things layer");
            }
        }

        /// <summary>Per-submesh vertex counts captured just before one thing
        /// prints; anything appended past these marks belongs to that thing.</summary>
        private readonly List<int> vertCountsBefore = new List<int>();

        /// <summary>The "fake zoom out" filter: linked graphics (walls, fences,
        /// conduits, natural rock) are excluded - each cell prints its own
        /// quad, so per-cell shrink would open a gap at every cell boundary,
        /// and the mountain-edge rock faces must stay flush with the cap
        /// layer's fill. Everything else shrinks in place about its center.</summary>
        private static bool CanScale(Thing t)
        {
            GraphicData g = t.def.graphicData;
            return g == null || g.linkType == LinkDrawerType.None;
        }

        private void SnapshotVertCounts()
        {
            vertCountsBefore.Clear();
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                vertCountsBefore.Add(subs[i].verts.Count);
            }
        }

        /// <summary>Shrinks the vertices this thing just printed about its own
        /// center (x/z only; altitude untouched). Submeshes created during the
        /// print start scaling from index 0.</summary>
        private void ScaleNewVerts(Vector3 center, float scale)
        {
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> verts = subs[i].verts;
                int from = i < vertCountsBefore.Count ? vertCountsBefore[i] : 0;
                for (int j = from; j < verts.Count; j++)
                {
                    Vector3 v = verts[j];
                    verts[j] = new Vector3(
                        center.x + (v.x - center.x) * scale,
                        v.y,
                        center.z + (v.z - center.z) * scale);
                }
            }
        }

        /// <summary>Draws through the forced below-band queue clones instead of
        /// the native materials, so the sky map's own terrain, floors, and
        /// things always render above this view.</summary>
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
                    LevelRenderer.DrawBelowSubMesh(sub);
                }
            }
        }
    }
}
