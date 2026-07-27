using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 see-below: through every open-air cell of a band, show the band underneath.
    ///
    /// The V1 version of this feature needed `SectionLayer_ABBelowThings` PLUS
    /// `DrawPosOffsetPatcher` - hundreds of DrawPos getters patched on ParallelPreDraw
    /// worker threads - purely because the level below lived on a different Map and every
    /// draw position had to be lied about. None of that exists here.
    ///
    /// In V2 the level below is the same Map, one band down, so the whole feature is:
    /// print the below cell's content normally (it prints at its own real position, in the
    /// band below), then TRANSLATE the vertices this print just emitted up by one Slot.
    /// Nothing is lied to; we simply move triangles after they are generated. That is the
    /// same contained trick V1 already used for its below-thing shrink, applied to
    /// position instead of scale.
    ///
    /// Masking is by construction: only cells whose own terrain is AB_OpenAir print
    /// anything, so rooftops and mountain caps are opaque and can never lose a
    /// render-queue contest against below content (V1's hardest-won rendering lesson).
    /// </summary>
    public class SectionLayer_ABBelowV2 : SectionLayer
    {
        private readonly List<int> vertCountsBefore = new List<int>();

        /// <summary>Below content is tinted down so depth reads at a glance.</summary>
        private static readonly Color32 BelowTint = new Color32(165, 165, 175, 255);

        public SectionLayer_ABBelowV2(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.Things
                | (ulong)MapMeshFlagDefOf.Buildings
                | (ulong)MapMeshFlagDefOf.FogOfWar;
        }

        public override bool Visible => ABGuard.On(ABGuard.Rendering);

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            if (!ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            try
            {
                int slot = bands.Slot;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainGrid terrainGrid = map.terrainGrid;
                FogGrid fog = map.fogGrid;
                ThingGrid thingGrid = map.thingGrid;
                float scale = Mathf.Clamp(ABMod.Settings?.belowThingScale ?? 0.85f, 0.5f, 1f);
                bool doScale = scale < 0.999f;
                float terrainAlt = AltitudeLayer.TerrainScatter.AltitudeFor();
                bool printed = false;

                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    // Only bands that HAVE something beneath them, and never the gutter.
                    if (bands.BandOf(c) <= 0 || bands.InGutter(c))
                    {
                        continue;
                    }
                    if (terrainGrid.TerrainAt(c) != air)
                    {
                        continue; // opaque by construction
                    }
                    IntVec3 below = new IntVec3(c.x, c.y, c.z - slot);
                    if (!below.InBounds(map) || bands.InGutter(below))
                    {
                        continue;
                    }
                    // Mirror vanilla: never reveal what the colony has not explored.
                    if (fog.IsFogged(below))
                    {
                        continue;
                    }

                    if (PrintBelowTerrain(map, terrainGrid, below, c, terrainAlt))
                    {
                        printed = true;
                    }

                    List<Thing> things = thingGrid.ThingsListAtFast(below);
                    for (int i = 0; i < things.Count; i++)
                    {
                        Thing t = things[i];
                        DrawerType drawer = t.def.drawerType;
                        if (drawer != DrawerType.MapMeshOnly && drawer != DrawerType.MapMeshAndRealTime)
                        {
                            continue; // realtime things are not part of the map mesh
                        }
                        if (!t.def.seeThroughFog && fog.IsFogged(t.Position))
                        {
                            continue;
                        }
                        if (!IsPrintAnchor(t, below, map, bands, terrainGrid, air, slot))
                        {
                            continue;
                        }
                        try
                        {
                            SnapshotVertCounts();
                            t.Print(this);
                            TransformNewVerts(t.TrueCenter(), slot, scale, doScale && CanScale(t));
                            printed = true;
                        }
                        catch (Exception e)
                        {
                            Log.WarningOnce(ABLog.Tag + " V2 below print failed for " + t.LabelCap
                                + ": " + e.Message, t.thingIDNumber ^ 762195870);
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
                ABGuard.Disable(ABGuard.Rendering, e, "V2 below layer");
            }
        }

        /// <summary>
        /// One below-terrain quad, built EXACTLY the way vanilla SectionLayer_Terrain
        /// builds its own: four corner verts, vertex colours, two tris, and NO uvs.
        ///
        /// The uvs are the crux. RimWorld's terrain shaders derive their sampling from
        /// WORLD POSITION, so vanilla never writes uvs for a terrain quad. The first cut of
        /// this layer used Printer_Plane, which DOES write 0..1 uvs per quad, and the below
        /// terrain came out as a dark muddy smear - the run #8 "lower level textures don't
        /// look right" report.
        ///
        /// The material must come from TerrainGrid.GetMaterial (which honours the cell's
        /// paint colour and pollution variant), NOT from def.graphic.MatSingle.
        ///
        /// Verts are emitted at the ABOVE cell's coordinates directly, so unlike the thing
        /// prints this needs no vertex translation afterwards.
        /// </summary>
        private bool PrintBelowTerrain(Map map, TerrainGrid terrainGrid, IntVec3 below,
            IntVec3 above, float altitude)
        {
            TerrainDef def = terrainGrid.TerrainAt(below);
            if (def == null || def.dontRender)
            {
                return false;
            }
            Material mat = terrainGrid.GetMaterial(def, false, terrainGrid.ColorAt(below));
            if (mat == null)
            {
                return false;
            }
            LayerSubMesh sub = GetSubMesh(mat);
            if (sub == null)
            {
                return false;
            }
            int count = sub.verts.Count;
            sub.verts.Add(new Vector3(above.x, altitude, above.z));
            sub.verts.Add(new Vector3(above.x, altitude, above.z + 1));
            sub.verts.Add(new Vector3(above.x + 1, altitude, above.z + 1));
            sub.verts.Add(new Vector3(above.x + 1, altitude, above.z));
            sub.colors.Add(BelowTint);
            sub.colors.Add(BelowTint);
            sub.colors.Add(BelowTint);
            sub.colors.Add(BelowTint);
            sub.tris.Add(count);
            sub.tris.Add(count + 1);
            sub.tris.Add(count + 2);
            sub.tris.Add(count);
            sub.tris.Add(count + 2);
            sub.tris.Add(count + 3);
            return true;
        }

        /// <summary>Multi-cell things print exactly once, from the first occupied cell
        /// (row-major) that has open air above it - not from the root cell, which may sit
        /// under a rooftop while the rest of the body is exposed.</summary>
        private static bool IsPrintAnchor(Thing t, IntVec3 belowCell, Map map, ABBandMap bands,
            TerrainGrid terrainGrid, TerrainDef air, int slot)
        {
            if (t.def.size.x == 1 && t.def.size.z == 1)
            {
                return t.Position.x == belowCell.x && t.Position.z == belowCell.z;
            }
            CellRect rect = t.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    IntVec3 above = new IntVec3(x, 0, z + slot);
                    if (!above.InBounds(map) || bands.InGutter(above))
                    {
                        continue;
                    }
                    if (terrainGrid.TerrainAt(above) == air)
                    {
                        return q.x == belowCell.x && q.z == belowCell.z;
                    }
                }
            }
            return false;
        }

        /// <summary>Rock and linked graphics keep full size and stay flush: shrinking each
        /// cell about its own centre tears a mountain or a wall run into a gappy field
        /// (V1 run #50). Everything else shrinks in place for the depth illusion.</summary>
        private static bool CanScale(Thing t)
        {
            ThingDef d = t.def;
            if (d.mineable || (d.building != null && d.building.isNaturalRock))
            {
                return false;
            }
            GraphicData g = d.graphicData;
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

        /// <summary>Translates the vertices the last print emitted up one band, optionally
        /// shrinking them about the translated centre. Altitude (y) is left alone.</summary>
        private void TransformNewVerts(Vector3 thingCenter, int slot, float scale, bool doScale)
        {
            float cx = thingCenter.x;
            float cz = thingCenter.z + slot;
            List<LayerSubMesh> subs = subMeshes;
            for (int i = 0; i < subs.Count; i++)
            {
                List<Vector3> verts = subs[i].verts;
                int from = i < vertCountsBefore.Count ? vertCountsBefore[i] : 0;
                for (int j = from; j < verts.Count; j++)
                {
                    Vector3 v = verts[j];
                    float x = v.x;
                    float z = v.z + slot;
                    if (doScale)
                    {
                        x = cx + (x - cx) * scale;
                        z = cz + (z - cz) * scale;
                    }
                    verts[j] = new Vector3(x, v.y, z);
                }
            }
        }
    }

    /// <summary>
    /// Change propagation. A section only regenerates when its OWN cells are dirtied, so a
    /// wall built on the surface would never repaint the sky band that looks down on it.
    /// Mirroring the dirty flag one band up keeps the see-below view live.
    ///
    /// Terminates naturally: with three bands the mirror walks 0 -> 1 -> 2 and stops, and
    /// the re-entrancy latch makes that guarantee explicit rather than incidental.
    /// </summary>
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDirty),
        new Type[] { typeof(IntVec3), typeof(ulong), typeof(bool), typeof(bool) })]
    public static class Patch_MapDrawer_ABMirrorDirtyUp
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> MapRef =
            AccessTools.FieldRefAccess<MapDrawer, Map>("map");

        private static bool mirroring;

        private static void Postfix(MapDrawer __instance, IntVec3 loc, ulong dirtyFlags)
        {
            if (mirroring)
            {
                return;
            }
            try
            {
                Map map = MapRef(__instance);
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                IntVec3 above = new IntVec3(loc.x, loc.y, loc.z + bands.Slot);
                if (!above.InBounds(map) || bands.InGutter(above))
                {
                    return;
                }
                mirroring = true;
                try
                {
                    map.mapDrawer.MapMeshDirty(above, dirtyFlags);
                }
                finally
                {
                    mirroring = false;
                }
            }
            catch (Exception e)
            {
                mirroring = false;
                Log.ErrorOnce(ABLog.Tag + " V2: dirty mirror threw: " + e, 762195871);
            }
        }
    }
}
