using System.Collections.Generic;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// T11 world identity and storyteller honesty.
    ///
    /// TILE: our pocket levels physically ARE the column's world location, but
    /// vanilla pocket map parents have no tile, so every world-facing system
    /// (transport pod range, launch targeting, trader distance) sees an invalid
    /// origin. One filtered postfix on WorldObject.Tile gives our pocket
    /// parents the ground map's tile; Map.Tile routes through it (MapInfo.Tile
    /// => parent.Tile), so rooftop launch pads just work. Foreign pocket maps
    /// (labyrinth, undercave) are untouched: their source map is not one of our
    /// columns.
    ///
    /// WEALTH: DefaultThreatPointsNow reads the surface map's wealth and pawns
    /// only, so goods and colonists parked on other levels were invisible to
    /// raid scaling - the classic z-level wealth-hiding exploit. The ground
    /// map's storyteller getters aggregate the whole column (children keep
    /// their own getters untouched, and vanilla already redirects pocket-map
    /// targets to the source map, so nothing double counts).
    /// Kill switch: world.
    /// </summary>
    internal static class ColumnWorld
    {
        /// <summary>True when the given map is one of our pocket levels and its
        /// column ground map is alive. Outputs the ground map.</summary>
        internal static bool TryGetColumnGround(Map map, out Map ground)
        {
            ground = null;
            if (map == null || map.Disposed)
            {
                return false;
            }
            LevelComp comp = map.Levels();
            if (comp == null || comp.level == 0)
            {
                return false;
            }
            ground = comp.groundMap;
            return ground != null && !ground.Disposed;
        }
    }

    [HarmonyPatch(typeof(WorldObject), "Tile", MethodType.Getter)]
    internal static class Patch_WorldObject_Tile
    {
        private static void Postfix(WorldObject __instance, ref PlanetTile __result)
        {
            // Cheapest filter first: real world objects exit on one check.
            if (__result.Valid || !(__instance is PocketMapParent pmp))
            {
                return;
            }
            if (!ABGuard.On(ABGuard.World))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.worldIntegration)
            {
                return;
            }
            Map level = pmp.Map;
            if (level == null || !ColumnWorld.TryGetColumnGround(level, out Map ground))
            {
                return;
            }
            __result = ground.Tile;
        }
    }

    [HarmonyPatch(typeof(Map), "PlayerWealthForStoryteller", MethodType.Getter)]
    internal static class Patch_Map_ColumnWealth
    {
        private static void Postfix(Map __instance, ref float __result)
        {
            if (!ABGuard.On(ABGuard.World))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.columnWealth)
            {
                return;
            }
            if (Find.Storyteller?.difficulty == null || Find.Storyteller.difficulty.fixedWealthMode)
            {
                return;
            }
            LevelComp comp = __instance.Levels();
            if (comp == null || comp.level != 0)
            {
                return;
            }
            // Every level in the column, so wealth cannot be hidden by parking
            // it two or more levels up (raised sky cap) or in the basement.
            List<Map> levels = comp.LinkedLevelMaps();
            for (int i = 0; i < levels.Count; i++)
            {
                __result += ChildWealth(levels[i]);
            }
        }

        /// <summary>The vanilla player-home wealth formula applied to a linked
        /// level: items + half buildings + pawns. Children never take the home
        /// branch themselves, so this is the only place their stock counts.</summary>
        private static float ChildWealth(Map child)
        {
            if (child == null || child.Disposed || child.wealthWatcher == null)
            {
                return 0f;
            }
            return child.wealthWatcher.WealthItems
                + child.wealthWatcher.WealthBuildings * 0.5f
                + child.wealthWatcher.WealthPawns;
        }
    }

    [HarmonyPatch(typeof(Map), "PlayerPawnsForStoryteller", MethodType.Getter)]
    internal static class Patch_Map_ColumnPawns
    {
        private static void Postfix(Map __instance, ref IEnumerable<Pawn> __result)
        {
            if (!ABGuard.On(ABGuard.World))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.columnWealth)
            {
                return;
            }
            LevelComp comp = __instance.Levels();
            if (comp == null || comp.level != 0 || !comp.HasMultiLevels)
            {
                return;
            }
            __result = WithColumn(__result, comp.LinkedLevelMaps());
        }

        private static IEnumerable<Pawn> WithColumn(IEnumerable<Pawn> surface, List<Map> levels)
        {
            foreach (Pawn p in surface)
            {
                yield return p;
            }
            for (int i = 0; i < levels.Count; i++)
            {
                Map m = levels[i];
                if (m == null || m.Disposed)
                {
                    continue;
                }
                foreach (Pawn p in m.mapPawns.PawnsInFaction(RimWorld.Faction.OfPlayer))
                {
                    yield return p;
                }
            }
        }
    }
}
