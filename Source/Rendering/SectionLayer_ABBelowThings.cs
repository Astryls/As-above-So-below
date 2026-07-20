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
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (skyTerrain.TerrainAt(c) != air || !c.InBounds(lower))
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
                        try
                        {
                            t.Print(this);
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
