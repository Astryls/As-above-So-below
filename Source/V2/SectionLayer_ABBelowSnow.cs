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
            // work here. ⚠ §36c-B1: the mirror is NO LONGER flag-agnostic - it sends only
            // AB_BelowThings upward, so every below layer must list that flag or it goes
            // stale on below-band changes. The vanilla flags below cover OWN-band inputs.
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Snow
                | (ulong)MapMeshFlagDefOf.Terrain
                | (ulong)MapMeshFlagDefOf.FogOfWar
                | (ulong)ABDefOf.AB_BelowThings;
        }

        /// <summary>Mirrors vanilla's own gate so the snow debug toggle keeps working on
        /// below views, then ours on top.</summary>
        public override bool Visible => DebugViewSettings.drawSnow && ABGuard.On(ABGuard.Rendering);

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

                // terrainGrid / fog locals removed with TryResolveSnowSource: the shared
                // gate resolves both internally, so hoisting them here was fetching two
                // grids per section regenerate that nothing then read.
                SnowGrid snow = map.snowGrid;
                CellRect rect = section.CellRect;
                bool any = false;
                // Visibility for the section AND a one-cell apron, resolved once. The kernel
                // below needs the answer for all eight neighbours of every edge cell, and the
                // apron is what keeps that from re-entering the shared gate 8x per cell.
                BuildVisibilityCache(map, bands, rect);

                // Iteration order MUST match MakeBaseGeometry's (x outer, z inner, nine
                // vertices per cell) or colours land on the wrong corners.
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    for (int z = rect.minZ; z <= rect.maxZ; z++)
                    {
                        IntVec3 c = new IntVec3(x, 0, z);
                        // THE shared see-below gate (band, gutter, see-through, descent,
                        // legibility) - identical by construction to the one
                        // SectionLayer_ABBelowV2 applies, which is what stops snow ever
                        // appearing in a cell whose terrain the below layer did not print.
                        // Read from the cache; the cache is the gate.
                        if (!VisibleAt(c, out int drop))
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
                        // Deliberately not re-resolving the DESCENT per neighbour: vanilla
                        // smooths snow across a cell's eight neighbours, and mixing samples
                        // from two different bands into one smoothing kernel would put a
                        // visible seam wherever the level below changes depth. The centre
                        // cell decides which level is being looked at; the kernel then reads
                        // that level, which is also what vanilla does within a band.
                        //
                        // ⚠ BUT "WHICH LEVEL" AND "IS IT VISIBLE AT ALL" ARE TWO QUESTIONS,
                        // AND ONLY THE FIRST WAS BEING ASKED.
                        //
                        // Skipping the visibility test as well meant a neighbour standing
                        // under a ROCKY PEAK still contributed the snow lying beneath that
                        // peak - snow that is not on screen. The kernel therefore held full
                        // opacity right up to the peak (a hard, cell-aligned edge against the
                        // rock, since the opaque cell writes alpha 0 across all nine of its
                        // own vertices), and the only place alpha could ramp down was the
                        // interior, wherever the level below happened to be bare. Reported as
                        // "snow fades inward towards other snow covered tiles instead of
                        // outward toward the rocky peaks", which is precisely that inversion.
                        //
                        // An invisible neighbour now reads ZERO, so the fade lands at the
                        // silhouette of what is actually being looked at - the same way
                        // vanilla snow fades out as it approaches a wall.
                        float centre = snow.GetDepth(new IntVec3(x, 0, z - drop));
                        for (int k = 0; k < 9; k++)
                        {
                            IntVec3 n = c + GenAdj.AdjacentCellsAndInsideForUV[k];
                            IntVec3 nb = new IntVec3(n.x, 0, n.z - drop);
                            // Vanilla falls back to the centre depth for out-of-bounds
                            // neighbours, so snow never fades against the map edge. A
                            // neighbour outside the playable band - off map, or across the
                            // gutter at either end - is the banded equivalent of that edge
                            // and must get the SAME treatment, or every band seam grows a
                            // fade stripe.
                            if (!n.InBounds(map) || bands.InGutter(n)
                                || !nb.InBounds(map) || bands.InGutter(nb))
                            {
                                adjDepth[k] = centre;
                                adjPolluted[k] = 0f;
                                continue;
                            }
                            if (!VisibleAt(n, out int _))
                            {
                                // Inside the band but opaque from here (peak, ledge, roof) or
                                // illegible (fogged): off-screen, so it contributes nothing.
                                adjDepth[k] = 0f;
                                adjPolluted[k] = 0f;
                                continue;
                            }
                            adjDepth[k] = snow.GetDepth(nb);
                            adjPolluted[k] = map.pollutionGrid.IsPolluted(nb) ? 1f : 0f;
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

        // TryResolveSnowSource lived here: band test, gutter test, ShowsBelow, descent, fog.
        // It was the fifth verbatim copy of that preamble in the see-below stack, and the
        // family's history is that copies drift and one of them silently loses the descent
        // (see ABBands.TryResolveVisibleFrom, and the lighting overlay it had already
        // happened to). Replaced by the shared gate at the call site above.

        /// <summary>See-below visibility for this section plus a one-cell apron, rebuilt at
        /// the top of every Regenerate. The apron is the point: the smoothing kernel of an
        /// edge cell reaches one cell outside the section, and the whole bug this fixes was
        /// the kernel not asking the question at all.
        ///
        /// Cheaper than the code it replaces, not more expensive: one gate call per apron
        /// cell (19x19 = 361 on a full section) instead of one per section cell plus eight
        /// per visible cell.</summary>
        private bool[] visCache;

        private int[] dropCache;

        private int cacheMinX;

        private int cacheMinZ;

        private int cacheWidth;

        private int cacheHeight;

        private void BuildVisibilityCache(Map map, ABBandMap bands, CellRect rect)
        {
            cacheMinX = rect.minX - 1;
            cacheMinZ = rect.minZ - 1;
            cacheWidth = rect.Width + 2;
            cacheHeight = rect.Height + 2;
            int need = cacheWidth * cacheHeight;
            if (visCache == null || visCache.Length < need)
            {
                visCache = new bool[need];
                dropCache = new int[need];
            }
            for (int x = 0; x < cacheWidth; x++)
            {
                for (int z = 0; z < cacheHeight; z++)
                {
                    IntVec3 p = new IntVec3(cacheMinX + x, 0, cacheMinZ + z);
                    int i = (x * cacheHeight) + z;
                    // The shared gate handles off-map cells itself, so the apron needs no
                    // bounds test of its own.
                    visCache[i] = ABBands.TryResolveVisibleFrom(map, bands, p,
                        requireUnfogged: true, out IntVec3 _, out int d);
                    dropCache[i] = d;
                }
            }
        }

        private bool VisibleAt(IntVec3 c, out int drop)
        {
            drop = 0;
            int x = c.x - cacheMinX;
            int z = c.z - cacheMinZ;
            if (x < 0 || z < 0 || x >= cacheWidth || z >= cacheHeight)
            {
                return false; // outside the apron: never asked for, never visible
            }
            int i = (x * cacheHeight) + z;
            drop = dropCache[i];
            return visCache[i];
        }

        private readonly float[] adjDepth = new float[9];

        private readonly float[] adjPolluted = new float[9];

        private static readonly CachedTexture PollutedSnowTex = new CachedTexture("Other/SnowPolluted");
    }
}
