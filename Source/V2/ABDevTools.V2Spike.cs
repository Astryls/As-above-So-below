using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 SPIKE harness. Builds a SEALED chamber (no door, no gap) with an item inside,
    /// then joins its interior to the outside world with nothing but a synthetic
    /// RegionLink. If vanilla then hauls the item out on its own, the V2 thesis holds:
    /// parity is INHERITED, not emulated.
    ///
    /// The chamber is the control. Before the link is armed the interior is provably
    /// unreachable; after arming, the same query must flip to true with no other change.
    /// </summary>
    public static partial class ABDevTools
    {
        private const int ChamberOuter = 7;

        [DebugAction("As above", "AB2: spike - build wormhole chamber", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2BuildWormholeChamber()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            ThingDef anchorDef = DefDatabase<ThingDef>.GetNamedSilentFail("AB2_WormholeAnchor");
            if (anchorDef == null)
            {
                Messages.Message("AB2 spike: AB2_WormholeAnchor def missing.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            IntVec3 center = V2FindChamberSpot(map);
            CellRect outer = CellRect.CenteredOn(center, ChamberOuter / 2);
            CellRect interior = outer.ContractedBy(1);

            // --- build the sealed box -------------------------------------------
            foreach (IntVec3 c in outer)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                ClearCell(map, c);
                map.fogGrid.Unfog(c);
            }
            ThingDef wall = ThingDefOf.Wall;
            ThingDef wallStuff = GenStuff.DefaultStuffFor(wall);
            foreach (IntVec3 c in outer.EdgeCells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                Thing w = ThingMaker.MakeThing(wall, wallStuff);
                GenSpawn.Spawn(w, c, map, WipeMode.Vanish);
                w.SetFaction(Faction.OfPlayer);
            }
            foreach (IntVec3 c in interior)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }

            IntVec3 innerAnchorCell = center;
            IntVec3 itemCell = center + new IntVec3(1, 0, 1);
            if (!interior.Contains(itemCell))
            {
                itemCell = interior.CenterCell;
            }

            // Outside anchor: far enough from the box that it is unambiguously outside.
            IntVec3 outerAnchorCell = V2FindOutsideCell(map, outer);

            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();

            // --- CONTROL: sealed interior must be unreachable BEFORE arming -------
            bool reachBefore = map.reachability.CanReach(outerAnchorCell, innerAnchorCell,
                PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly));

            // --- place + link the anchors ----------------------------------------
            ThingDef anchorStuff = GenStuff.DefaultStuffFor(anchorDef);
            ClearCell(map, innerAnchorCell);
            ClearCell(map, outerAnchorCell);
            Building_ABAnchor inner = (Building_ABAnchor)ThingMaker.MakeThing(anchorDef, anchorStuff);
            GenSpawn.Spawn(inner, innerAnchorCell, map, WipeMode.Vanish);
            inner.SetFaction(Faction.OfPlayer);
            Building_ABAnchor outerAnchor = (Building_ABAnchor)ThingMaker.MakeThing(anchorDef, anchorStuff);
            GenSpawn.Spawn(outerAnchor, outerAnchorCell, map, WipeMode.Vanish);
            outerAnchor.SetFaction(Faction.OfPlayer);

            // Band layout: the chamber interior is band 1, the rest of the map band 0.
            // Same consumer API as the real V2 stacked layout.
            ABBands.Register(map, ABBandLayout.TestRect(interior));
            ABWormhole.Link(inner, outerAnchor);
            inner.partner = outerAnchor;
            outerAnchor.partner = inner;
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            ABWormhole.RearmAll(map);

            // --- the payload the colony must fetch on its own ---------------------
            ClearCell(map, itemCell);
            Thing steel = ThingMaker.MakeThing(ThingDefOf.Steel);
            steel.stackCount = 50;
            GenSpawn.Spawn(steel, itemCell, map, WipeMode.Vanish);

            bool reachAfter = map.reachability.CanReach(outerAnchorCell, innerAnchorCell,
                PathEndMode.OnCell, TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly));

            Log.Warning(ABLog.Tag + " V2 spike: chamber at " + center
                + " interior=" + interior + " innerAnchor=" + innerAnchorCell
                + " outerAnchor=" + outerAnchorCell
                + " | reachable before link=" + reachBefore + " after link=" + reachAfter);
            Messages.Message("AB2 spike: chamber built. Reach before=" + reachBefore
                + " after=" + reachAfter + ". Now run the assertions.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: spike - run assertions", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2RunSpikeAssertions()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            StringBuilder sb = new StringBuilder();
            int pass = 0;
            int fail = 0;

            void Check(string name, bool ok, string detail)
            {
                if (ok)
                {
                    pass++;
                    sb.AppendLine("PASS  " + name + (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail));
                }
                else
                {
                    fail++;
                    sb.AppendLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : "  -- " + detail));
                }
            }

            List<Building_ABAnchor> anchors = new List<Building_ABAnchor>();
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Building_ABAnchor a && a.Spawned)
                {
                    anchors.Add(a);
                }
            }
            if (anchors.Count < 2)
            {
                Messages.Message("AB2 spike: build the chamber first.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Building_ABAnchor innerAnchor = null;
            Building_ABAnchor outerAnchor = null;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (ABBands.BandOf(map, anchors[i].Position) == 1)
                {
                    innerAnchor = anchors[i];
                }
                else
                {
                    outerAnchor = anchors[i];
                }
            }
            if (innerAnchor == null || outerAnchor == null)
            {
                Messages.Message("AB2 spike: could not identify inner/outer anchors (band layout lost on reload?).",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            IntVec3 innerCell = innerAnchor.Position;
            IntVec3 outerCell = outerAnchor.Position;

            // --- 1. reachability crosses the wormhole ---------------------------
            bool reach = map.reachability.CanReach(outerCell, innerCell, PathEndMode.OnCell,
                TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly));
            Check("1. CanReach crosses the wormhole", reach,
                "outer " + outerCell + " -> inner " + innerCell);

            // --- 2. portal regions, rooms NOT merged ----------------------------
            Region ri = map.regionGrid.GetValidRegionAt_NoRebuild(innerCell);
            Region ro = map.regionGrid.GetValidRegionAt_NoRebuild(outerCell);
            bool portals = ri != null && ro != null
                && ri.type == RegionType.Portal && ro.type == RegionType.Portal;
            Check("2a. both anchors are Portal regions", portals,
                "inner=" + (ri != null ? ri.type.ToString() : "null")
                + " outer=" + (ro != null ? ro.type.ToString() : "null"));

            Room roomIn = (innerCell + IntVec3.North).GetRoom(map);
            Room roomOut = (outerCell + IntVec3.North).GetRoom(map);
            bool roomsSeparate = roomIn != null && roomOut != null && roomIn != roomOut;
            Check("2b. rooms did NOT merge across the wormhole", roomsSeparate,
                "innerRoom=" + (roomIn != null ? roomIn.ID.ToString() : "null")
                + " (temp " + (roomIn != null ? roomIn.Temperature.ToString("F1") : "?") + ")"
                + " outerRoom=" + (roomOut != null ? roomOut.ID.ToString() : "null")
                + " (temp " + (roomOut != null ? roomOut.Temperature.ToString("F1") : "?") + ")");

            // --- 3. the transitive win: region-based search sees through it ------
            Thing found = GenClosest.ClosestThingReachable(outerCell, map,
                ThingRequest.ForDef(ThingDefOf.Steel), PathEndMode.ClosestTouch,
                TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly), 9999f);
            bool sealedFind = found != null && ABBands.BandOf(map, found.Position) == 1;
            Check("3. ClosestThingReachable finds the sealed-in item", sealedFind,
                found != null ? "found " + found.LabelCap + " at " + found.Position
                    + " (band " + ABBands.BandOf(map, found.Position) + ")"
                    : "found nothing");

            // --- 4. movement segmentation resolves a transit --------------------
            bool transit = ABWormhole.TryGetTransit(map, outerCell + IntVec3.North, innerCell,
                out Building_Door near, out Building_Door far);
            Check("4. StartPath segmentation resolves a transit pair", transit && near != null && far != null,
                transit ? "near=" + near.Position + " far=" + far.Position : "no transit resolved");

            sb.AppendLine();
            sb.AppendLine("wormhole pairs on map: " + ABWormhole.PairCount(map));
            sb.AppendLine("band layout registered: " + ABBands.Banded(map));
            sb.AppendLine();
            sb.AppendLine("ASSERTION 5 (the real gate) is LIVE, not scriptable:");
            sb.AppendLine("  unpause and watch. A colonist must haul the 50 steel out of the");
            sb.AppendLine("  sealed chamber with ZERO hauling code written. If that happens,");
            sb.AppendLine("  vanilla is doing cross-band logistics for free and V2 is proven.");

            Report("V2 wormhole spike", sb, pass, fail);
        }

        [DebugAction("As above", "AB2: spike - teardown", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2TeardownSpike()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            List<Thing> all = new List<Thing>(map.listerThings.AllThings);
            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Building_ABAnchor a && a.Spawned)
                {
                    a.Destroy(DestroyMode.Vanish);
                    n++;
                }
            }
            ABBands.Clear(map);
            map.reachability.ClearCache();
            Messages.Message("AB2 spike: removed " + n + " anchors, cleared band layout.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        private static IntVec3 V2FindChamberSpot(Map map)
        {
            IntVec3 origin = Find.CameraDriver != null ? UI.MouseCell() : map.Center;
            if (!origin.InBounds(map))
            {
                origin = map.Center;
            }
            foreach (IntVec3 c in GenRadial.RadialCellsAround(origin, 30f, useCenter: true))
            {
                CellRect r = CellRect.CenteredOn(c, ChamberOuter / 2 + 1);
                if (!r.InBounds(map))
                {
                    continue;
                }
                bool ok = true;
                foreach (IntVec3 rc in r)
                {
                    if (!rc.Standable(map) || rc.GetEdifice(map) != null)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                {
                    return c;
                }
            }
            return origin;
        }

        private static IntVec3 V2FindOutsideCell(Map map, CellRect chamber)
        {
            CellRect avoid = chamber.ExpandedBy(2);
            foreach (IntVec3 c in GenRadial.RadialCellsAround(chamber.CenterCell, 26f, useCenter: false))
            {
                if (!c.InBounds(map) || avoid.Contains(c))
                {
                    continue;
                }
                if (c.Standable(map) && c.GetEdifice(map) == null && !c.Fogged(map)
                    && c.GetRoom(map) != null && c.GetRoom(map).PsychologicallyOutdoors)
                {
                    return c;
                }
            }
            return chamber.CenterCell + new IntVec3(chamber.Width + 4, 0, 0);
        }
    }
}
