using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Faint ceiling hint on the surface (T6 #7, minimal scope by decision): cells
    /// sitting under a BUILT sky-level floor draw a subtle dark panel texture over
    /// surface content, conveying "there is a structure overhead" without hiding
    /// play. The full live underside mirror stays a future tranche.
    ///
    /// Cell set: the sky terrain at the cell is player-removable (a built floor).
    /// Open air (nothing above), walkable rooftop (just the top of the surface's
    /// own roof), and natural sky-mountain rock draw nothing, so the overlay only
    /// appears where the player actually built up. Scoped to the surface looking
    /// at the sky: the basement's ceiling is trivially "the entire surface" and
    /// would be pure noise.
    ///
    /// Regeneration keys on the Roofs mesh flag: sky floor add/remove is paired
    /// with a surface roof write by LevelSync, and OnSkyTerrainChanged explicitly
    /// dirties the surface section for floor changes over an existing rooftop
    /// (where no roof write happens). LevelComp nudges a one-time whole-map regen
    /// after load so hints are not stale when surface sections build before the
    /// sky map links restore. Kill switch: Rendering; also toggleable in settings.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABCeiling : SectionLayer
    {
        private static readonly Material CeilingMat =
            MaterialPool.MatFrom("Terrain/AB_CeilingHint", ShaderDatabase.Transparent);

        public SectionLayer_ABCeiling(Section section) : base(section)
        {
            relevantChangeTypes = MapMeshFlagDefOf.Roofs;
        }

        public override bool Visible =>
            ABGuard.On(ABGuard.Rendering) && (ABMod.Settings?.showCeilingHint ?? true);

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            Map map = section.map;
            if (!ABGuard.On(ABGuard.Rendering) || map.Level() != 0)
            {
                return;
            }
            Map sky = map.UpperMap();
            if (sky == null || sky.Disposed)
            {
                return;
            }
            try
            {
                TerrainGrid skyTerrain = sky.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                TerrainDef rooftop = ABDefOf.AB_RoofSurface;
                float y = AltitudeLayer.Weather.AltitudeFor();
                LayerSubMesh sub = null;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(sky))
                    {
                        continue;
                    }
                    TerrainDef top = skyTerrain.TerrainAt(c);
                    if (top == air || top == rooftop || !top.Removable)
                    {
                        continue;
                    }
                    if (sub == null)
                    {
                        sub = GetSubMesh(CeilingMat);
                    }
                    int vi = sub.verts.Count;
                    sub.verts.Add(new Vector3(c.x, y, c.z));
                    sub.verts.Add(new Vector3(c.x, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                    sub.verts.Add(new Vector3(c.x + 1, y, c.z));
                    sub.uvs.Add(new Vector3(0f, 0f, 0f));
                    sub.uvs.Add(new Vector3(0f, 1f, 0f));
                    sub.uvs.Add(new Vector3(1f, 1f, 0f));
                    sub.uvs.Add(new Vector3(1f, 0f, 0f));
                    sub.tris.Add(vi);
                    sub.tris.Add(vi + 1);
                    sub.tris.Add(vi + 2);
                    sub.tris.Add(vi);
                    sub.tris.Add(vi + 2);
                    sub.tris.Add(vi + 3);
                }
                if (sub != null)
                {
                    FinalizeMesh(MeshParts.All);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "ceiling hint layer");
            }
        }
    }
}
