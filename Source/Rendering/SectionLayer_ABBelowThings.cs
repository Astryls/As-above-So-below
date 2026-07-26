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
        // Always-on regen tallies (plain int adds, negligible): the below-view
        // diagnostic dev tool dumps and resets them. Added for the run-33
        // "prints empty until re-dirtied" regression so the failing early-out
        // names itself instead of being theorized about.
        internal static int DiagCalls;
        internal static int DiagNoComp;
        internal static int DiagNoLower;
        internal static int DiagAirCells;
        internal static int DiagConsidered;
        internal static int DiagPrinted;
        internal static int DiagPrintErrors;

        internal static void DiagReset()
        {
            DiagCalls = DiagNoComp = DiagNoLower = DiagAirCells = DiagConsidered = DiagPrinted = DiagPrintErrors = 0;
        }

        internal static string DiagSummary()
        {
            return "regens=" + DiagCalls + " earlyNoComp=" + DiagNoComp + " earlyNoLower=" + DiagNoLower
                + " airCells=" + DiagAirCells + " considered=" + DiagConsidered
                + " printed=" + DiagPrinted + " printErrors=" + DiagPrintErrors;
        }

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
                DiagCalls++;
                LevelComp comp = map.Levels();
                if (comp == null || comp.level < 0)
                {
                    if (comp == null)
                    {
                        DiagNoComp++;
                    }
                    return;
                }
                Map lower = comp.lowerMap;
                if (lower == null || lower.Disposed)
                {
                    DiagNoLower++;
                    return;
                }
                // Sky-view feature only (skylights removed 2026-07-24): the
                // ground level prints no below content.
                if (comp.level == 0)
                {
                    return;
                }
                TerrainGrid skyTerrain = map.terrainGrid;
                TerrainDef air = ABDefOf.AB_OpenAir;
                FogGrid lowerFog = lower.fogGrid;
                // Honor CAI 5000 fog of war in the below view (option B). Null
                // unless CAI is in overlay mode; resolved once per regen so the
                // default setup pays nothing per cell.
                Func<IntVec3, bool> caiFog = ABCombatAICompat.GetOverlayFogChecker(lower);
                float scale = Mathf.Clamp(ABMod.Settings?.belowThingScale ?? 0.85f, 0.5f, 1f);
                bool doScale = scale < 0.999f;
                bool printed = false;
                foreach (IntVec3 c in section.CellRect)
                {
                    if (!c.InBounds(lower))
                    {
                        continue;
                    }
                    // Open air ONLY. Cap cells print nothing below them: the
                    // atlas fill owns the whole mountain look, and the ground's
                    // rock walls printed at the old "mass boundary" showed
                    // through the edge tiles' transparent wavy margin as a dim,
                    // south-shifted duplicate of the mass lip (the run-24 double
                    // border). Through that margin the below TERRAIN now shows
                    // instead - exactly a vanilla rock group sitting on ground.
                    // Fogged below content stays behind the opaque air mask.
                    TerrainDef top = skyTerrain.TerrainAt(c);
                    if (top != air)
                    {
                        continue;
                    }
                    DiagAirCells++;
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
                        // Multi-cell things print once - from their first
                        // occupied cell that is open air on THIS level, not
                        // the root cell: a thing rooted under the mass or a
                        // rooftop with its body sticking out under open air
                        // (Medieval Overhaul's 2x2 rock formations hugging a
                        // mountain edge) would otherwise never print and
                        // vanish from the sky view entirely. Deterministic
                        // across section boundaries exactly like the rim
                        // print layers' first-qualifying-cell rule.
                        IntVec3 pos = t.Position;
                        if (t.def.size.x != 1 || t.def.size.z != 1)
                        {
                            if (!IsBelowPrintAnchor(t, c, map, skyTerrain, air))
                            {
                                continue;
                            }
                        }
                        else if (pos.x != c.x || pos.z != c.z)
                        {
                            continue;
                        }
                        // Vanilla bakes only unfogged things; mirror that so the
                        // below view never reveals what surface pawns have not
                        // explored (fog lifting below reprints via the mirror).
                        if (!t.def.seeThroughFog && (lowerFog.IsFogged(pos) || (caiFog != null && caiFog(pos))))
                        {
                            continue;
                        }
                        DiagConsidered++;
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
                            DiagPrinted++;
                        }
                        catch (Exception e)
                        {
                            DiagPrintErrors++;
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

        /// <summary>First occupied cell (row-major scan) that is open air on
        /// the sky level; the print anchors there. Cells outside the sky map
        /// bounds cannot anchor (nothing would ever iterate them).</summary>
        private static bool IsBelowPrintAnchor(Thing t, IntVec3 c, Map sky,
            TerrainGrid skyTerrain, TerrainDef air)
        {
            CellRect rect = t.OccupiedRect();
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    IntVec3 q = new IntVec3(x, 0, z);
                    if (q.InBounds(sky))
                    {
                        TerrainDef qt = skyTerrain.TerrainAt(q);
                        if (qt == air)
                        {
                            return q.x == c.x && q.z == c.z;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>The "fake zoom out" filter: linked graphics (walls, fences,
        /// conduits) are excluded - each cell prints its own quad, so per-cell
        /// shrink would open a gap at every cell boundary. Natural rock and
        /// ores are excluded BY DEF, not by link type: Better Mountains swaps
        /// rock graphics to non-linked Graphic_Random wholesale, and shrinking
        /// each rock cell about its own center tore the surface mountains
        /// into a gappy field when seen from the sky (run #50). Rock stays
        /// full-size and flush regardless of who drew it. Everything else
        /// shrinks in place about its center.</summary>
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
