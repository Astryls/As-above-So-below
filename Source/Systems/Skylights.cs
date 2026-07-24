using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Skylight zones (2026-07-24): a vanilla-roofing-style paint-and-build
    /// mechanic that swaps the slab between two levels for tough glass. Painted
    /// on the level you are VIEWING, affecting the slab under your feet:
    ///  - surface view: a floor cell becomes glass, revealing the basement;
    ///  - sky view: a rooftop cell becomes glass, revealing the surface room.
    /// Stacked panes (sky glass or open air over a surface glass floor) let the
    /// sky view see two levels down.
    ///
    /// Work-only, no materials (vanilla build-roof parity, user directive).
    /// Light: skylights pass real daylight via invisible glower shafts
    /// (AB_SkylightShaft) spawned on the lit map. The shaft implements
    /// IThingGlower so a bone-stock CompGlower turns on and off with the sun:
    /// overlight near the pane means plants get true full-sun growth (user
    /// directive). Everything downstream (lighting overlay, plant growth, mood,
    /// surgery, darkness combat) is vanilla behavior reacting to real glow -
    /// zero glow read-path patches.
    ///
    /// Sealed: glass passes ONLY vision and light. No rain, no temperature
    /// exchange, no shooting, no falling, no pathing change beyond a normal
    /// standable floor. Roof grids underneath are never touched, so roof
    /// collapse, insulation, and infestation math stay vanilla.
    ///
    /// Known accepted limitations (documented in the schematic): shaft light
    /// follows the celestial sun for the column tile and ignores weather and
    /// eclipses; the two-deep sky view of a basement shows printed content
    /// only (no live basement pawns from the sky).
    /// Kill switch: Areas (zone/work), Rendering (draw paths, in LevelRenderer).
    /// </summary>
    public class SkylightMapComp : MapComponent, ICellBoolGiver
    {
        /// <summary>Built glass cells on THIS map (the pane lives on the upper
        /// map of the slab it replaces).</summary>
        private HashSet<IntVec3> panes = new HashSet<IntVec3>();

        /// <summary>Player-painted desired state; builders reconcile: planned
        /// and not glass gets built, glass and not planned gets removed.</summary>
        private HashSet<IntVec3> planned = new HashSet<IntVec3>();

        /// <summary>Original terrain per pane, restored on removal.</summary>
        private Dictionary<IntVec3, TerrainDef> originals = new Dictionary<IntVec3, TerrainDef>();

        /// <summary>Bumped on every pane/planned mutation; AnyWork memoizes
        /// against it so the per-pawn work-scan gate is O(1) instead of a walk
        /// over both sets. -1 start forces the first computation.</summary>
        private int workVersion;
        private int anyWorkVersion = -1;
        private bool anyWorkCached;

        private CellBoolDrawer drawer;
        private bool drawRequested;

        public SkylightMapComp(Map map) : base(map)
        {
        }

        public int PaneCount => panes.Count;

        public int PlannedCount => planned.Count;

        public bool IsPane(IntVec3 c) => panes.Contains(c);

        public bool IsPlanned(IntVec3 c) => planned.Contains(c);

        public TerrainDef OriginalAt(IntVec3 c)
        {
            return originals.TryGetValue(c, out TerrainDef t) ? t : null;
        }

        public void SetPlanned(IntVec3 c, bool on)
        {
            bool changed = on ? planned.Add(c) : planned.Remove(c);
            if (changed)
            {
                workVersion++;
                drawer?.SetDirty();
            }
        }

        internal void RegisterPane(IntVec3 c, TerrainDef original)
        {
            if (panes.Add(c))
            {
                SkylightSystem.GlobalPaneCount++;
                workVersion++;
            }
            if (original != null)
            {
                originals[c] = original;
            }
        }

        internal void DropPane(IntVec3 c)
        {
            if (panes.Remove(c))
            {
                SkylightSystem.GlobalPaneCount--;
                workVersion++;
            }
            originals.Remove(c);
        }

        /// <summary>Stable copy of the pane set for cross-map reconcile.</summary>
        public List<IntVec3> PaneCellsSnapshot()
        {
            return new List<IntVec3>(panes);
        }

        /// <summary>Cells with outstanding reconcile work, both directions.</summary>
        public IEnumerable<IntVec3> WorkCells()
        {
            foreach (IntVec3 c in planned)
            {
                if (!panes.Contains(c))
                {
                    yield return c;
                }
            }
            foreach (IntVec3 c in panes)
            {
                if (!planned.Contains(c))
                {
                    yield return c;
                }
            }
        }

        /// <summary>Memoized per mutation version: WorkGiver.ShouldSkip reads
        /// this per construction pawn per work scan, and the sets only change
        /// through the three mutators above.</summary>
        public bool AnyWork
        {
            get
            {
                if (anyWorkVersion != workVersion)
                {
                    anyWorkVersion = workVersion;
                    anyWorkCached = ComputeAnyWork();
                }
                return anyWorkCached;
            }
        }

        private bool ComputeAnyWork()
        {
            if (planned.Count == 0 && panes.Count == 0)
            {
                return false;
            }
            foreach (IntVec3 c in planned)
            {
                if (!panes.Contains(c))
                {
                    return true;
                }
            }
            foreach (IntVec3 c in panes)
            {
                if (!planned.Contains(c))
                {
                    return true;
                }
            }
            return false;
        }

        // --- planned-area overlay (vanilla area drawer language) ---

        public Color Color => new Color(0.35f, 0.75f, 0.9f);

        public bool GetCellBool(int index)
        {
            return planned.Contains(map.cellIndices.IndexToCell(index));
        }

        public Color GetCellExtraColor(int index)
        {
            return Color.white;
        }

        public void MarkForDraw()
        {
            drawRequested = true;
        }

        public override void MapComponentUpdate()
        {
            if (drawRequested)
            {
                if (drawer == null)
                {
                    drawer = new CellBoolDrawer(this, map.Size.x, map.Size.z);
                }
                drawer.MarkForDraw();
                drawRequested = false;
            }
            drawer?.CellBoolDrawerUpdate();
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            SkylightSystem.GlobalPaneCount += panes.Count;
            // Consistency pass after load: every pane needs its shaft, every
            // shaft needs its pane. Cheap (pane counts are small).
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                try
                {
                    SkylightSystem.ValidateShafts(map);
                }
                catch (Exception e)
                {
                    ABLog.Dev("Skylight shaft validation failed: " + e.Message);
                }
            });
        }

        public override void MapRemoved()
        {
            base.MapRemoved();
            SkylightSystem.GlobalPaneCount -= panes.Count;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref panes, "panes", LookMode.Value);
            Scribe_Collections.Look(ref planned, "planned", LookMode.Value);
            Scribe_Collections.Look(ref originals, "originals", LookMode.Value, LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (panes == null)
                {
                    panes = new HashSet<IntVec3>();
                }
                if (planned == null)
                {
                    planned = new HashSet<IntVec3>();
                }
                if (originals == null)
                {
                    originals = new Dictionary<IntVec3, TerrainDef>();
                }
            }
        }
    }

    public static class SkylightSystem
    {
        /// <summary>Live pane count across all maps; lets hot paths (render,
        /// mesh mirror) skip all skylight work when the save has none.</summary>
        internal static int GlobalPaneCount;

        public static bool FeatureOn => ABMod.Settings == null || ABMod.Settings.skylights;

        /// <summary>Per-map comp cache mirroring LevelExtensions.Levels():
        /// GetComponent is a list scan and this resolver rides per-frame
        /// (DrawBelowStatic), per-SetTerrain, and per-mesh-dirty paths. Every
        /// map constructs its comp with the map itself, so a resolved comp is
        /// stable for the map's lifetime; the CWT drops entries when a map is
        /// collected and is thread safe for the render-adjacent callers.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, SkylightMapComp> compCache =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Map, SkylightMapComp>();

        public static SkylightMapComp CompFor(Map map)
        {
            if (map == null)
            {
                return null;
            }
            if (compCache.TryGetValue(map, out SkylightMapComp comp))
            {
                return comp;
            }
            comp = map.GetComponent<SkylightMapComp>();
            if (comp != null)
            {
                try
                {
                    compCache.Add(map, comp);
                }
                catch (ArgumentException)
                {
                    // Benign race: another thread cached it first.
                }
            }
            return comp;
        }

        public static bool AnyPanes(Map map)
        {
            if (GlobalPaneCount <= 0 || map == null)
            {
                return false;
            }
            SkylightMapComp comp = CompFor(map);
            return comp != null && comp.PaneCount > 0;
        }

        /// <summary>A map can host skylights when a real level lies below it.</summary>
        public static bool MapEligible(Map map)
        {
            if (map == null)
            {
                return false;
            }
            Map below = map.LowerMap();
            return below != null && !below.Disposed;
        }

        /// <summary>Whether one cell may be fitted with glass right now.
        /// Reason keys are Keyed strings.</summary>
        public static AcceptanceReport CellAllowed(Map map, IntVec3 c)
        {
            if (!MapEligible(map))
            {
                return new AcceptanceReport("AB_SkylightNoLevelBelow".Translate());
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            if (t == ABDefOf.AB_Skylight)
            {
                return true; // already glass: valid target state
            }
            if (c.GetEdifice(map) != null)
            {
                return new AcceptanceReport("AB_SkylightBlocked".Translate());
            }
            if (map.Level() == 1)
            {
                // Sky level: only the steel rooftop can be reglazed. Mountain
                // caps, plateau ground, and open air cannot.
                if (t != ABDefOf.AB_RoofSurface)
                {
                    return new AcceptanceReport("AB_SkylightNeedsRooftop".Translate());
                }
                return true;
            }
            // Ground level: any standable dry ground or built floor.
            if (t == null || t.passability != Traversability.Standable || t.IsWater
                || t == ABDefOf.AB_OpenAir || t == ABDefOf.AB_RoofSurface || t == ABDefOf.AB_MountainTop)
            {
                return new AcceptanceReport("AB_SkylightBadFloor".Translate());
            }
            return true;
        }

        /// <summary>Installs the glass pane at one cell: swap terrain, remember
        /// the original, and give the level below its light shaft.</summary>
        public static void PlaceSkylight(Map map, IntVec3 c)
        {
            SkylightMapComp comp = CompFor(map);
            if (comp == null || comp.IsPane(c))
            {
                return;
            }
            TerrainDef original = map.terrainGrid.TerrainAt(c);
            comp.RegisterPane(c, original);
            map.terrainGrid.SetTerrain(c, ABDefOf.AB_Skylight);
            Map below = map.LowerMap();
            if (below != null && !below.Disposed)
            {
                EnsureShaft(below, c);
                // A new pane can open the chain for a shaft two levels down
                // (sky pane over an existing surface pane): nudge it now
                // instead of waiting for its rare tick.
                RefreshShaftAt(below.LowerMap(), c);
            }
        }

        /// <summary>Removes the pane and restores the recorded slab.</summary>
        public static void RemoveSkylight(Map map, IntVec3 c)
        {
            SkylightMapComp comp = CompFor(map);
            if (comp == null || !comp.IsPane(c))
            {
                return;
            }
            TerrainDef restore = comp.OriginalAt(c)
                ?? (map.Level() == 1 ? ABDefOf.AB_RoofSurface : TerrainDefOf.Soil);
            comp.DropPane(c);
            map.terrainGrid.SetTerrain(c, restore);
            Map below = map.LowerMap();
            if (below != null && !below.Disposed)
            {
                DespawnShaft(below, c);
                RefreshShaftAt(below.LowerMap(), c);
            }
        }

        /// <summary>Anything else replacing a pane's terrain (new flooring laid
        /// over it, bombardment, rooftop sync reverting to open air) dissolves
        /// the pane silently - work-only means nothing is owed back.</summary>
        internal static void NotifyTerrainChanged(Map map, IntVec3 c, TerrainDef newTerr)
        {
            if (GlobalPaneCount <= 0 || newTerr == ABDefOf.AB_Skylight)
            {
                return;
            }
            SkylightMapComp comp = CompFor(map);
            if (comp == null || !comp.IsPane(c))
            {
                return;
            }
            comp.DropPane(c);
            comp.SetPlanned(c, false);
            Map below = map.LowerMap();
            if (below != null && !below.Disposed)
            {
                DespawnShaft(below, c);
                RefreshShaftAt(below.LowerMap(), c);
            }
        }

        // --- light shafts ---

        internal static void EnsureShaft(Map litMap, IntVec3 c)
        {
            if (litMap == null || litMap.Disposed || !c.InBounds(litMap))
            {
                return;
            }
            if (ShaftAt(litMap, c) != null)
            {
                return;
            }
            Thing shaft = ThingMaker.MakeThing(ABDefOf.AB_SkylightShaft);
            GenSpawn.Spawn(shaft, c, litMap);
        }

        internal static void DespawnShaft(Map litMap, IntVec3 c)
        {
            if (litMap == null || litMap.Disposed || !c.InBounds(litMap))
            {
                return;
            }
            Thing shaft = ShaftAt(litMap, c);
            shaft?.Destroy(DestroyMode.Vanish);
        }

        private static Thing ShaftAt(Map map, IntVec3 c)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(c);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def == ABDefOf.AB_SkylightShaft)
                {
                    return things[i];
                }
            }
            return null;
        }

        private static void RefreshShaftAt(Map map, IntVec3 c)
        {
            if (map == null || map.Disposed || !c.InBounds(map))
            {
                return;
            }
            (ShaftAt(map, c) as Thing_ABSkylightShaft)?.RefreshLit();
        }

        /// <summary>Load-time reconcile: orphan shafts despawn, missing shafts
        /// spawn. Runs per map; looks one level up for the owning pane.</summary>
        internal static void ValidateShafts(Map map)
        {
            if (map == null || map.Disposed)
            {
                return;
            }
            Map above = map.UpperMap();
            SkylightMapComp aboveComp = above != null && !above.Disposed ? CompFor(above) : null;
            List<Thing> all = map.listerThings.ThingsOfDef(ABDefOf.AB_SkylightShaft);
            for (int i = all.Count - 1; i >= 0; i--)
            {
                Thing shaft = all[i];
                if (aboveComp == null || !aboveComp.IsPane(shaft.Position))
                {
                    shaft.Destroy(DestroyMode.Vanish);
                }
            }
            if (aboveComp != null)
            {
                foreach (IntVec3 c in aboveComp.PaneCellsSnapshot())
                {
                    EnsureShaft(map, c);
                }
            }
        }

        /// <summary>True when the cell on this map receives sky through the pane
        /// chain above it. Called by the shaft's IThingGlower.</summary>
        public static bool CellSkyOpenThroughPanes(Map litMap, IntVec3 c)
        {
            Map above = litMap?.UpperMap();
            if (above == null || above.Disposed)
            {
                return false;
            }
            SkylightMapComp aboveComp = CompFor(above);
            if (aboveComp == null || !aboveComp.IsPane(c))
            {
                return false;
            }
            if (above.Level() >= 1)
            {
                // Pane on the topmost level: always open to the sky.
                return true;
            }
            // Pane on the surface: the surface cell itself must see sky -
            // either unroofed, or under a sky-level pane in turn.
            if (!above.roofGrid.Roofed(c))
            {
                return true;
            }
            Map sky = above.UpperMap();
            if (sky == null || sky.Disposed)
            {
                return false;
            }
            SkylightMapComp skyComp = CompFor(sky);
            return skyComp != null && skyComp.IsPane(c);
        }
    }

    /// <summary>Invisible one-cell daylight source under a skylight pane. A
    /// stock CompGlower does the actual lighting; this thing gates it on the
    /// celestial sun plus the pane chain via IThingGlower and re-evaluates on
    /// rare tick, exactly the cadence vanilla lamps use for power flicks.</summary>
    public class Thing_ABSkylightShaft : ThingWithComps, IThingGlower
    {
        /// <summary>Lazily cached: the comp list never changes after
        /// InitializeComps, and RefreshLit runs per shaft per rare tick.</summary>
        private CompGlower glowerInt;

        private CompGlower Glower => glowerInt ?? (glowerInt = GetComp<CompGlower>());

        public bool ShouldBeLitNow()
        {
            if (!Spawned || Map == null)
            {
                return false;
            }
            if (!SkylightSystem.FeatureOn)
            {
                return false;
            }
            if (GenCelestial.CurCelestialSunGlow(Map) < 0.4f)
            {
                return false;
            }
            return SkylightSystem.CellSkyOpenThroughPanes(Map, Position);
        }

        public void RefreshLit()
        {
            if (Spawned)
            {
                Glower?.UpdateLit(Map);
            }
        }

        public override void TickRare()
        {
            base.TickRare();
            RefreshLit();
        }
    }

    /// <summary>One safety net catches every path that swaps terrain out from
    /// under a pane: player flooring, mortar craters, rooftop sync, dev tools.</summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    internal static class Patch_TerrainGrid_SetTerrain_Skylight
    {
        private static void Postfix(TerrainGrid __instance, IntVec3 c, TerrainDef newTerr, Map ___map)
        {
            if (SkylightSystem.GlobalPaneCount <= 0)
            {
                return;
            }
            try
            {
                SkylightSystem.NotifyTerrainChanged(___map, c, newTerr);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "skylight terrain watch");
            }
        }
    }
}
