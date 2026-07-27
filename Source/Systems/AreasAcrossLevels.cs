using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Allowed areas across levels (bug report 2026-07-24: "zones are not
    /// respected over each level"). RimWorld 1.6 stores a pawn's allowed area
    /// PER MAP (Dictionary&lt;Map, Area&gt;), and the Restrict tab writes only the
    /// entry for the pawn's current map - so a pawn restricted on one level was
    /// completely unrestricted the moment it took the stairs.
    ///
    /// Column semantics (user-confirmed design): a restriction applies to the
    /// WHOLE column. On a level where an area of the same kind and label exists
    /// (Home mirrors to each level's own Home; custom areas match by label),
    /// that area's cells apply. On a level with no counterpart the pawn is
    /// allowed NOWHERE - no job targets, no wander - but can still traverse it
    /// to reach its stairs, exactly like vanilla pawns walking through
    /// non-allowed cells of one big map. A pawn stranded outside its column
    /// area walks home via the stairs (seek-allowed-area postfix).
    ///
    /// Implementation is a resolver at the single ownership chokepoint (the
    /// two restriction getters) plus tiny postfixes at the consumers that must
    /// treat a foreign-map area as "nowhere on this level": InAllowedArea and
    /// the pathfinder's area cost. Everything fails open through ABGuard.Areas.
    /// </summary>
    internal static class AreasAcrossLevels
    {
        internal static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Dictionary<Map, Area>> AllowedAreasRef =
            AccessTools.FieldRefAccess<Pawn_PlayerSettings, Dictionary<Map, Area>>("allowedAreas");

        internal static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Pawn> PawnRef =
            AccessTools.FieldRefAccess<Pawn_PlayerSettings, Pawn>("pawn");

        // ---------------------------------------------------------- resolver

        private const int CacheTtlTicks = 240;

        private static int version;

        private struct CacheLine
        {
            public int tick;
            public int version;
            public Area area;
        }

        private static readonly Dictionary<long, CacheLine> cache = new Dictionary<long, CacheLine>();

        internal static void Bump()
        {
            version++;
            if (cache.Count > 2048)
            {
                cache.Clear();
            }
        }

        /// <summary>The column-resolved restriction for a pawn standing on
        /// map cur. Null = unrestricted. An area whose Map != cur is the
        /// "allowed nowhere on this level" sentinel. Cached, because the
        /// forbid checks call this per cell in scan loops.</summary>
        internal static Area ResolveCached(Pawn_PlayerSettings ps, Pawn pawn, Map cur, Area hint)
        {
            long key = ((long)pawn.thingIDNumber << 32) | (uint)cur.uniqueID;
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(key, out CacheLine line)
                && line.version == version && now - line.tick < CacheTtlTicks)
            {
                return line.area;
            }
            Area resolved = Resolve(ps, pawn, cur, hint);
            cache[key] = new CacheLine { tick = now, version = version, area = resolved };
            return resolved;
        }

        private static Area Resolve(Pawn_PlayerSettings ps, Pawn pawn, Map cur, Area hint)
        {
            LevelComp controller = cur.Controller();
            if (controller == null || controller.MapByLevel.Count <= 1)
            {
                // Not a column: vanilla per-map semantics stand untouched.
                return hint;
            }
            Area src = hint;
            if (src == null)
            {
                Dictionary<Map, Area> dict = AllowedAreasRef(ps);
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m == cur || m.Disposed)
                    {
                        continue;
                    }
                    if (dict.TryGetValue(m, out Area a) && a != null)
                    {
                        src = a;
                        break;
                    }
                }
            }
            if (src == null)
            {
                return null;
            }
            if (src.Map == cur)
            {
                return src;
            }
            Area counterpart = ResolveForMap(src, cur);
            if (counterpart != null)
            {
                return counterpart;
            }
            // No counterpart here: the whole level is off-limits. The foreign
            // area itself is the sentinel; consumers key on Map mismatch. Only
            // safe when the maps are plumb twins (same size), which column
            // levels always are - anything else fails open to unrestricted.
            if (src.Map != null && src.Map.Size == cur.Size)
            {
                return src;
            }
            return null;
        }

        /// <summary>The same-kind, same-label area on another map, empty ones
        /// excluded (vanilla treats a TrueCount==0 area as no restriction, so
        /// an empty counterpart must fall through to the sentinel instead).</summary>
        internal static Area ResolveForMap(Area src, Map map)
        {
            if (src == null || map == null)
            {
                return null;
            }
            if (src.Map == map)
            {
                return src;
            }
            if (src is Area_Home)
            {
                Area home = map.areaManager.Home;
                return home != null && home.TrueCount > 0 ? home : null;
            }
            List<Area> all = map.areaManager.AllAreas;
            for (int i = 0; i < all.Count; i++)
            {
                Area a = all[i];
                if (a.GetType() == src.GetType() && a.TrueCount > 0 && a.Label == src.Label)
                {
                    return a;
                }
            }
            return null;
        }
    }

    /// <summary>Deleted areas must fall out of the resolver cache at once,
    /// not after its TTL - a 4-second ghost restriction is enough to strand a
    /// wander decision. Vanilla already strips the dictionary entries.</summary>
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.Notify_AreaRemoved))]
    internal static class Patch_AreaRemoved_BumpResolver
    {
        private static void Postfix()
        {
            AreasAcrossLevels.Bump();
        }
    }

    /// <summary>Restrict-tab writes mirror across the column: the assigned
    /// area's own map anchors the real reference, every level with a matching
    /// counterpart gets that counterpart, and levels without one get their
    /// entry cleared so the resolver's sentinel takes over. Clearing the
    /// restriction clears the whole column. Runs after the vanilla setter so
    /// its interrupt-current-job logic already fired.</summary>
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
    internal static class Patch_AreaRestriction_MirrorAcrossColumn
    {
        private static void Postfix(Pawn_PlayerSettings __instance, Area value)
        {
            if (!ABGuard.On(ABGuard.Areas))
            {
                return;
            }
            try
            {
                Pawn pawn = AreasAcrossLevels.PawnRef(__instance);
                Map refMap = value?.Map ?? pawn?.MapHeld;
                if (refMap == null)
                {
                    return;
                }
                LevelComp controller = refMap.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return;
                }
                Dictionary<Map, Area> dict = AreasAcrossLevels.AllowedAreasRef(__instance);
                foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
                {
                    Map m = kvp.Value;
                    if (m == null || m.Disposed)
                    {
                        continue;
                    }
                    if (value == null)
                    {
                        dict.Remove(m);
                        continue;
                    }
                    if (m == value.Map)
                    {
                        dict[m] = value;
                        continue;
                    }
                    Area counterpart = AreasAcrossLevels.ResolveForMap(value, m);
                    if (counterpart != null)
                    {
                        dict[m] = counterpart;
                    }
                    else
                    {
                        dict.Remove(m);
                    }
                }
                AreasAcrossLevels.Bump();
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "area restriction mirror");
            }
        }
    }

    /// <summary>Effective restriction, column-resolved. Null from vanilla on a
    /// linked level no longer means unrestricted when a sibling level carries
    /// the pawn's restriction; a legacy foreign-map entry (pre-fix saves could
    /// hold one) is re-resolved the same way.</summary>
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap), MethodType.Getter)]
    internal static class Patch_EffectiveAreaRestriction_Column
    {
        private static void Postfix(Pawn_PlayerSettings __instance, ref Area __result)
        {
            if (!LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Areas))
            {
                return;
            }
            try
            {
                Pawn pawn = AreasAcrossLevels.PawnRef(__instance);
                Map cur = pawn?.MapHeld;
                if (cur == null)
                {
                    return;
                }
                if (__result != null && __result.Map == cur)
                {
                    return;
                }
                if (!__instance.RespectsAllowedArea)
                {
                    // Vanilla nulls the restriction for lords, guests, roamers;
                    // never resurrect it for them.
                    return;
                }
                __result = AreasAcrossLevels.ResolveCached(__instance, pawn, cur, __result);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "effective area resolve");
            }
        }
    }

    /// <summary>The raw (UI-facing) restriction property, column-resolved the
    /// same way so the Restrict tab shows the real assignment on every level
    /// instead of "unrestricted" wherever the dictionary lacks an entry.</summary>
    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Getter)]
    internal static class Patch_AreaRestrictionGetter_Column
    {
        private static void Postfix(Pawn_PlayerSettings __instance, ref Area __result)
        {
            if (!ABGuard.On(ABGuard.Areas))
            {
                return;
            }
            try
            {
                Pawn pawn = AreasAcrossLevels.PawnRef(__instance);
                Map cur = pawn?.MapHeld;
                if (cur == null)
                {
                    return;
                }
                if (__result != null && __result.Map == cur)
                {
                    return;
                }
                __result = AreasAcrossLevels.ResolveCached(__instance, pawn, cur, __result);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "area getter resolve gated");
            }
        }
    }

    /// <summary>A foreign-map (sentinel) restriction means this whole level is
    /// off-limits for job targets and wander spots. Movement is untouched -
    /// pawns may traverse the level to reach stairs, exactly like walking
    /// through non-allowed cells of one big vanilla map.
    ///
    /// Prefix-replaces the tiny vanilla body rather than postfixing it: this
    /// runs per cell inside scan loops, and a postfix would call the effective
    /// getter a second time (vanilla's body already calls it once). One call,
    /// identical vanilla semantics for same-map areas, sentinel handled.
    /// Other mods' patches on this method still run (Harmony executes
    /// remaining prefixes and all postfixes even when the original is
    /// skipped); the guard fails open to the vanilla body.</summary>
    [HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.InAllowedArea))]
    internal static class Patch_InAllowedArea_Column
    {
        private static bool Prefix(IntVec3 c, Pawn forPawn, ref bool __result)
        {
            if (!LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Areas))
            {
                return true;
            }
            try
            {
                Pawn_PlayerSettings ps = forPawn.playerSettings;
                if (ps == null)
                {
                    __result = true;
                    return false;
                }
                Area area = ps.EffectiveAreaRestrictionInPawnCurrentMap;
                if (area == null)
                {
                    __result = true;
                    return false;
                }
                if (area.Map != forPawn.MapHeld)
                {
                    // Sentinel: restriction lives on another level and has no
                    // counterpart here - the whole level is outside the area.
                    __result = false;
                    return false;
                }
                __result = !(area.TrueCount > 0 && !area[c]);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "allowed area check");
                return true;
            }
        }
    }

    /// <summary>The pathfinder charges extra for cells outside the allowed
    /// area. A sentinel (foreign-map) area must not leak its plumb-projected
    /// grid into path costs on a level it does not describe - pathing across a
    /// transit level is free, as vanilla movement through non-allowed cells is.</summary>
    [HarmonyPatch(typeof(PathUtility), nameof(PathUtility.GetAllowedArea))]
    internal static class Patch_PathAllowedArea_Column
    {
        private static void Postfix(Pawn pawn, ref Area __result)
        {
            if (__result == null || !LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Areas))
            {
                return;
            }
            if (pawn?.MapHeld != null && __result.Map != pawn.MapHeld)
            {
                __result = null;
            }
        }
    }

    /// <summary>A pawn whose restriction lives on another level walks home:
    /// when the vanilla seek-allowed-area giver finds nothing on this map and
    /// the column resolution says "nowhere here", issue the stairs job toward
    /// the restriction's level (hop by hop; the postfix fires again after each
    /// transfer until the pawn stands on the right level).</summary>
    [HarmonyPatch(typeof(JobGiver_SeekAllowedArea), "TryGiveJob")]
    internal static class Patch_SeekAllowedArea_Column
    {
        private const int RetryTicks = 600;

        private static readonly ABPawnCooldown cooldown = new ABPawnCooldown();

        private static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !LevelCensus.AnyLevelColumns || !ABGuard.On(ABGuard.Areas))
            {
                return;
            }
            try
            {
                Pawn_PlayerSettings ps = pawn?.playerSettings;
                if (ps == null || !pawn.Spawned || pawn.Drafted || !ps.RespectsAllowedArea)
                {
                    return;
                }
                Area area = ps.EffectiveAreaRestrictionInPawnCurrentMap;
                if (area == null || area.Map == null || area.Map == pawn.MapHeld)
                {
                    // Unrestricted, or restricted on THIS map - vanilla owns it.
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!cooldown.Ready(pawn, now))
                {
                    return;
                }
                int dir = Math.Sign(area.Map.Level() - pawn.Map.Level());
                Map next = dir > 0 ? comp.upperMap : dir < 0 ? comp.lowerMap : null;
                if (next == null || !CrossLevelWork.TryStairsJobToward(pawn, next, out Job job))
                {
                    // No way home from here right now; retry later instead of
                    // rescanning every think cycle.
                    cooldown.ChargeUntil(pawn, now + RetryTicks);
                    return;
                }
                __result = job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "cross level seek allowed area");
            }
        }
    }

    /// <summary>Restrict-tab palette lists the union of the whole column's
    /// areas (deduplicated by kind+label), so an upstairs-only area can be
    /// assigned to a pawn currently downstairs. Selection highlights compare
    /// by identity (kind+label) because the pawn's stored instance may be the
    /// counterpart on its own level rather than the drawn one. Falls back to
    /// the vanilla drawer whenever the viewed map is not a column or the
    /// union adds nothing.</summary>
    [HarmonyPatch(typeof(AreaAllowedGUI), nameof(AreaAllowedGUI.DoAllowedAreaSelectors))]
    internal static class Patch_AreaPalette_ColumnUnion
    {
        private static bool dragging;

        private static bool Prefix(Rect rect, Pawn p)
        {
            if (!ABGuard.On(ABGuard.Areas) || !ABGuard.On(ABGuard.Ui))
            {
                return true;
            }
            try
            {
                Map cur = Find.CurrentMap;
                if (cur == null || p?.playerSettings == null)
                {
                    return true;
                }
                LevelComp controller = cur.Controller();
                if (controller == null || controller.MapByLevel.Count <= 1)
                {
                    return true;
                }
                List<Area> union = BuildUnion(cur, controller);
                if (union == null)
                {
                    // Nothing beyond the current map's own list: pure vanilla.
                    return true;
                }
                Draw(rect, p, union);
                return false;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Areas, e, "column area palette");
                return true;
            }
        }

        /// <summary>Current map's assignable areas in vanilla order, then
        /// foreign areas whose kind+label has no local representative. Null
        /// when the union equals the local list.</summary>
        private static List<Area> BuildUnion(Map cur, LevelComp controller)
        {
            List<Area> union = new List<Area>();
            List<Area> local = cur.areaManager.AllAreas;
            for (int i = 0; i < local.Count; i++)
            {
                if (local[i].AssignableAsAllowed())
                {
                    union.Add(local[i]);
                }
            }
            bool anyForeign = false;
            foreach (KeyValuePair<int, Map> kvp in controller.MapByLevel)
            {
                Map m = kvp.Value;
                if (m == null || m == cur || m.Disposed)
                {
                    continue;
                }
                List<Area> areas = m.areaManager.AllAreas;
                for (int i = 0; i < areas.Count; i++)
                {
                    Area a = areas[i];
                    if (!a.AssignableAsAllowed())
                    {
                        continue;
                    }
                    bool represented = false;
                    for (int j = 0; j < union.Count; j++)
                    {
                        if (union[j].GetType() == a.GetType() && union[j].Label == a.Label)
                        {
                            represented = true;
                            break;
                        }
                    }
                    if (!represented)
                    {
                        union.Add(a);
                        anyForeign = true;
                    }
                }
            }
            return anyForeign ? union : null;
        }

        /// <summary>Vanilla layout, reimplemented because the original's
        /// per-area drawer is private and reference-compares the selection.</summary>
        private static void Draw(Rect rect, Pawn p, List<Area> union)
        {
            float width = rect.width / (union.Count + 1);
            Text.WordWrap = false;
            Text.Font = GameFont.Tiny;
            DoAreaSelector(new Rect(rect.x, rect.y, width, rect.height), p, null);
            for (int i = 0; i < union.Count; i++)
            {
                DoAreaSelector(new Rect(rect.x + (i + 1) * width, rect.y, width, rect.height), p, union[i]);
            }
            Text.WordWrap = true;
            Text.Font = GameFont.Small;
        }

        private static void DoAreaSelector(Rect rect, Pawn p, Area area)
        {
            MouseoverSounds.DoRegion(rect);
            rect = rect.ContractedBy(1f);
            GUI.DrawTexture(rect, area != null ? area.ColorTexture : BaseContent.GreyTex);
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = AreaUtility.AreaAllowedLabel_Area(area);
            Rect labelRect = rect;
            labelRect.xMin += 3f;
            labelRect.yMin += 2f;
            Widgets.Label(labelRect, label);
            Area assigned = p.playerSettings.AreaRestrictionInPawnCurrentMap;
            bool selected = area == null ? assigned == null : SameIdentity(assigned, area);
            if (selected)
            {
                Widgets.DrawBox(rect, 2);
            }
            if (Event.current.rawType == EventType.MouseUp && Event.current.button == 0)
            {
                dragging = false;
            }
            if (!Input.GetMouseButton(0) && Event.current.type != EventType.MouseDown)
            {
                dragging = false;
            }
            if (Mouse.IsOver(rect))
            {
                area?.MarkForDraw();
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    dragging = true;
                }
                if (dragging && !selected)
                {
                    p.playerSettings.AreaRestrictionInPawnCurrentMap = AssignTarget(area, p);
                    SoundDefOf.Designate_DragStandard_Changed_NoCam.PlayOneShotOnCamera();
                }
            }
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(rect, label);
        }

        private static bool SameIdentity(Area a, Area b)
        {
            return a != null && b != null
                && (a == b || (a.GetType() == b.GetType() && a.Label == b.Label));
        }

        /// <summary>Prefer the pawn's own level's counterpart as the stored
        /// reference; the mirror setter normalizes the rest either way.</summary>
        private static Area AssignTarget(Area area, Pawn p)
        {
            if (area == null)
            {
                return null;
            }
            if (p.MapHeld == null || area.Map == p.MapHeld)
            {
                return area;
            }
            return AreasAcrossLevels.ResolveForMap(area, p.MapHeld) ?? area;
        }
    }
}
