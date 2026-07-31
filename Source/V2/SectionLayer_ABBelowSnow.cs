using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 see-below: SNOW on the level underneath.
    ///
    /// THE BUG THIS FIXES, and it is the §6b water story a second time. Looking down from a
    /// level above, snow lying on a lower level was invisible. The cause is not masking,
    /// translation or the descent rule: it is that <see cref="SectionLayer_ABBelowV2"/> is a
    /// faithful port of vanilla's <c>SectionLayer_Terrain</c> AND NOTHING ELSE, while
    /// vanilla draws snow in a completely separate pass, <c>SectionLayer_Snow</c>. No
    /// mirror of that pass existed, so below-snow was never emitted at all.
    ///
    /// The near-miss that made it look handled: <c>CellTerrainAt</c> genuinely samples
    /// <c>map.snowGrid.GetDepth(below)</c>, from the correct cell. But <c>CellTerrain</c>'s
    /// snow coverage is consumed only by <c>MaterialFor</c>, to decide whether the POLLUTED
    /// terrain variant applies. It never draws snow. So the right value was being read for
    /// an entirely different purpose, which is exactly the kind of thing that survives a
    /// code review.
    ///
    /// Schematic lesson, verbatim: when a symptom says "X is missing", ask how many PASSES
    /// X takes. Water took two. Snow takes its own.
    ///
    /// WHY THIS LAYER IS SO MUCH SIMPLER THAN THE TERRAIN MIRROR. Vanilla's snow layer does
    /// not emit per-cell geometry at all - it builds ONE solid 9-vertices-per-cell grid over
    /// the section and then writes only vertex COLOURS, with snow depth in the alpha
    /// channel. That grid is positional, and our section already sits in the VIEWING band,
    /// so the geometry is already in the right place. Nothing needs translating; only the
    /// cells we SAMPLE move. Hence no vertex fix-up pass and no `- Slot` arithmetic here.
    /// </summary>
    [StaticConstructorOnStartup]
    public class SectionLayer_ABBelowSnow : SectionLayer
    {
        public SectionLayer_ABBelowSnow(Section section) : base(section)
        {
            // Snow, obviously. Terrain because whether a cell shows anything below at all is
            // a terrain question (AB_OpenAir), and FogOfWar because unexplored ground below
            // must not leak its snow through the fog.
            //
            // Cross-band propagation is already solved: Patch_MapDrawer_ABMirrorDirtyUp
            // mirrors every dirty flag verbatim to EVERY band above, so a melt on the
            // surface invalidates the sky sections that look down on it without any extra
            // work here. That patch is flag-agnostic, which is why adding a new layer costs
            // one line rather than a new propagation path.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Snow
                | (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.FogOfWar;
        }

        /// <summary>Mirrors vanilla's own gate so the snow debug toggle keeps working on
        /// below views, then ours on top.</summary>
        public override bool Visible => DebugViewSettings.drawSnow && ABGuard.On(ABGuard.Rendering);

        /// <summary>Snow moves with the ground it lies on under perspective mode. Nothing is
        /// pinned here: unlike SectionLayer_ABBelowV2 this layer emits no mask geometry, only
        /// the snow sheet itself.</summary>
        public override void DrawLayer()
        {
            if (!Visible)
            {
                return;
            }
            if (!ABDepthView.PerspectiveActive)
            {
                base.DrawLayer();
                return;
            }
            ABDepthView.DrawSubMeshes(subMeshes);
        }

        public override void Regenerate()
        {
            LayerSubMesh sub = GetSubMesh(MatBases.Snow);
            if (sub == null)
            {
                return;
            }
            Map map = section.map;
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
            {
                sub.disabled = true;
                return;
            }
            try
            {
                if (ModsConfig.BiotechActive)
                {
                    sub.material.SetTexture(ShaderPropertyIDs.PollutedTex, PollutedSnowTex.Texture);
                }
                // Geometry is built ONCE and reused, exactly as vanilla does; only colours
                // are rewritten per regenerate. Deliberately NOT ClearSubMeshes(All), which
                // every other AB layer calls: those rebuild geometry each time, this one
                // must not, or the colour count stops matching the vertex count.
                //
                // Altitude matches SectionLayer_ABBelowV2's below-terrain print
                // (TerrainScatter) rather than vanilla's Terrain, which reproduces the exact
                // relationship vanilla has: snow sits at the same altitude as the terrain it
                // covers, and the ordering between them is settled by material render QUEUE.
                if (sub.mesh.vertexCount == 0)
                {
                    SectionLayerGeometryMaker_Solid.MakeBaseGeometry(section, sub,
                        AltitudeLayer.TerrainScatter);
                }
                sub.Clear(MeshParts.Colors);

                TerrainGrid terrainGrid = map.terrainGrid;
                FogGrid fog = map.fogGrid;
                SnowGrid snow = map.snowGrid;
                CellRect rect = section.CellRect;
                bool any = false;

                // Iteration order MUST match MakeBaseGeometry's (x outer, z inner, nine
                // vertices per cell) or colours land on the wrong corners.
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        int drop;
                        if (!TryResolveSnowSource(map, bands, terrainGrid, fog, c, out drop))
                        {
                            // No visible ground below: nine fully transparent vertices.
                            // The count must still be emitted, or every later cell's
                            // colours shift onto the wrong vertices.
                            for (int k = 0; k < 9; k++)
                            {
                                sub.colors.Add(new Color32(0, byte.MaxValue, byte.MaxValue, 0));
                            }
                            continue;
                        }

                        // Nine neighbour samples, all taken at the SAME drop as the centre.
                        //
                        // Deliberately not re-resolving the descent per neighbour: vanilla
                        // smooths snow across a cell's eight neighbours, and mixing samples
                        // from two different bands into one smoothing kernel would put a
                        // visible seam wherever the level below changes depth. The centre
                        // cell decides which level is being looked at; the kernel then reads
                        // that level, which is also what vanilla does within a band.
                        float centre = snow.GetDepth(new IntVec3(x, 0, z - drop));
                        for (int k = 0; k < 9; k++)
                        {
                            IntVec3 n = c + GenAdj.AdjacentCellsAndInsideForUV[k];
                            IntVec3 nb = new IntVec3(n.x, 0, n.z - drop);
                            // Vanilla falls back to the centre depth for out-of-bounds
                            // neighbours; a neighbour that has crossed into the gutter is
                            // the banded equivalent and gets the same treatment.
                            adjDepth[k] = (n.InBounds(map) && nb.InBounds(map) && !bands.InGutter(nb))
                                ? snow.GetDepth(nb)
                                : centre;
                            adjPolluted[k] = (nb.InBounds(map) && map.pollutionGrid.IsPolluted(nb))
                                ? 1f
                                : 0f;
                        }

                        for (int v = 0; v < 9; v++)
                        {
                            List<int> weights = SectionLayer_Snow.vertexWeights[v];
                            float depth = 0f;
                            float polluted = 0f;
                            for (int w = 0; w < weights.Count; w++)
                            {
                                depth += adjDepth[weights[w]];
                                polluted += adjPolluted[weights[w]];
                            }
                            depth /= weights.Count;
                            polluted /= weights.Count;
                            if (depth > 0.01f)
                            {
                                any = true;
                            }
                            sub.colors.Add(new Color32(
                                Convert.ToByte(Mathf.Clamp01(polluted) * 255f),
                                byte.MaxValue,
                                byte.MaxValue,
                                Convert.ToByte(Mathf.Clamp01(depth) * 255f)));
                        }
                    }
                }

                if (any)
                {
                    sub.disabled = false;
                    sub.FinalizeMesh(MeshParts.Colors);
                }
                else
                {
                    sub.disabled = true;
                }
            }
            catch (Exception e)
            {
                sub.disabled = true;
                Log.ErrorOnce(ABLog.Tag + " V2: below-snow layer threw: " + e, 733391104);
            }
        }

        /// <summary>
        /// Is there snow-bearing ground visible through this cell, and how far down?
        ///
        /// Gated identically to SectionLayer_ABBelowV2's own per-cell test, so snow can
        /// never appear in a cell whose terrain the below layer did not print - which is
        /// what would otherwise paint snow over an opaque rooftop or a mountain cap.
        /// </summary>
        private static bool TryResolveSnowSource(Map map, ABBandMap bands,
            TerrainGrid terrainGrid, FogGrid fog, IntVec3 c, out int drop)
        {
            drop = 0;
            if (!c.InBounds(map) || bands.BandOf(c) <= 0 || bands.InGutter(c))
            {
                return false;
            }
            if (!ABBands.ShowsBelow(terrainGrid.TerrainAt(c)))
            {
                return false; // opaque from here
            }
            // THE one descent rule (§5). Never `- Slot`: from a high level the band directly
            // beneath is usually open air too, and a single step lands in the void.
            if (!ABBands.TryResolveVisibleBelow(map, bands, c, out IntVec3 below, out drop))
            {
                return false;
            }
            return !fog.IsFogged(below);
        }

        private readonly float[] adjDepth = new float[9];

        private readonly float[] adjPolluted = new float[9];

        private static readonly CachedTexture PollutedSnowTex = new CachedTexture("Other/SnowPolluted");
    }
}
