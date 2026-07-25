using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WVC - Work Modes (wvc.sergkart.biotech.MoreMechanoidsWorkModes) soft
    /// compat. Their ten mech work modes run through OWN-declared givers
    /// (JobGiver_GetEnergy_Charger subclasses JobGiver_GetEnergy directly,
    /// SmartAIFollowOverseer subclasses JobGiver_AIFollowPawn), so none of our
    /// verified vanilla mech patches bind for WVC-mode mechs - patches attach
    /// per method DECLARATION. Five seams, all house-pattern (postfix, keep
    /// any local result, one-hop stairs routing, per-pawn cooldowns):
    ///
    /// 1. Charger: their giver calls the map-scoped GetClosestCharger; a WVC
    ///    mech working on a level without a charger drains and self-shutdowns
    ///    at the stairs. Their recharge gate is computable with pure vanilla
    ///    API, so on a null result we re-run it and reuse the verified
    ///    cross-level charger probe from the vanilla patch.
    /// 2. Smart escort: their followee resolver (assigned escort target comp,
    ///    falling back to the overseer) succeeds column-wide thanks to our
    ///    IsPlayerHome extension, but the base follow giver dies on the
    ///    cross-map CanReach. Null result + followee on a linked level =>
    ///    stairs toward the followee.
    /// 3. Shutdown zones are map-scoped ZoneManager entries. When the giver
    ///    finds nothing locally AND the mech is not already parked in a
    ///    qualifying zone/room, probe linked levels with THEIR OWN finder
    ///    under a virtual position swap (needs-bridge doctrine), then hop.
    /// 4. Scavenge zones: same shape, plus their branch gate
    ///    (ConditionalAnyScavengeZone) is map-scoped, so it must go
    ///    column-aware or the giver never runs. The conditional only flips
    ///    when a route can actually be armed, because their wander giver
    ///    NREs when a mech wanders outside any zone (GetScavengeWanderRoot
    ///    dereferences GetZone(pos) unguarded - hardened with a prefix too).
    /// 5. Combat column-awareness (user-approved): ConditionalEnemyOnMap goes
    ///    column-wide, and vanilla JobGiver_AIGotoNearestHostile - which their
    ///    combat branches reuse - gets a postfix gated to player mechs in WVC
    ///    seek modes so "find and destroy" / "wait enemy" mechs climb stairs
    ///    toward a raided level. One-big-map parity: a raid on the surface
    ///    wakes basement combat mechs; "work if safe" mechs treat it as
    ///    danger anywhere in the column. Walking via stairs respects the
    ///    basement routing-only doctrine (no cross-gap gameplay is added).
    ///
    /// Everything resolves by name at startup, foreign types never appear in
    /// member signatures (boxed enum + object-typed instances only), their
    /// own mod settings gates are read live and respected, and the module
    /// fails open per seam.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABWVCWorkModesCompat
    {
        private const int ChargeCooldownTicks = 600;
        private const int FollowCooldownTicks = 250;
        private const int ZoneCooldownTicks = 600;
        private const int ScavengeCooldownTicks = 450;
        private const int HostileCooldownTicks = 250;

        private static readonly ABPawnCooldown chargeCooldown = new ABPawnCooldown();
        private static readonly ABPawnCooldown followCooldown = new ABPawnCooldown();
        private static readonly ABPawnCooldown zoneCooldown = new ABPawnCooldown();
        private static readonly ABPawnCooldown scavengeCooldown = new ABPawnCooldown();
        private static readonly ABPawnCooldown hostileCooldown = new ABPawnCooldown();

        private static bool active;

        // WVC settings (live reads; their toggles keep working mid-session).
        private static FieldInfo settingsField;
        private static FieldInfo enableShutdownZoneField;
        private static FieldInfo enableEnemySearchingField;

        // Smart escort followee resolver (their comp assignment + overseer fallback).
        private static MethodInfo getAssignedPawnMethod;

        // Shutdown zone internals.
        private static FieldInfo workModeTypeField;          // enum field on the giver
        private static FieldInfo possibleRoomsField;         // List<string> on the giver
        private static PropertyInfo possibleRoomsProperty;   // List<RoomRoleDef> cache
        private static MethodInfo mechInShutdownZoneMethod;  // (Pawn, IntVec3, enum)
        private static MethodInfo mechInShutdownRoomMethod;  // (Pawn, IntVec3, List<RoomRoleDef>)
        private static MethodInfo findShutdownZoneMethod;    // (List<Zone>, Pawn, Map, enum, out IntVec3)
        private static MethodInfo anyMechanoidZoneMethod;    // (List<Zone>)

        // Scavenge internals.
        private static MethodInfo anyScavengeZoneMethod;         // (List<Zone>)
        private static MethodInfo findScavengeZoneMethod;        // (List<Zone>, Pawn, out IntVec3)
        private static MethodInfo mechInScavengeZoneMethod;      // (Pawn, IntVec3)

        // Combat-seek work modes: these WANT to travel to enemies. Defensive
        // (DefendYourself), stationary (Ambush) and escort modes stay local.
        private static readonly HashSet<MechWorkModeDef> seekModes = new HashSet<MechWorkModeDef>();

        static ABWVCWorkModesCompat()
        {
            try
            {
                if (!ABDetect.Active("wvc.sergkart.biotech.MoreMechanoidsWorkModes")
                    || !ModsConfig.BiotechActive)
                {
                    return;
                }
                Type modType = AccessTools.TypeByName("WVC_WorkModes.WVC_MMWM");
                Type settingsType = AccessTools.TypeByName("WVC_WorkModes.WVC_MMWM_Settings");
                settingsField = modType != null ? AccessTools.Field(modType, "settings") : null;
                if (settingsType != null)
                {
                    enableShutdownZoneField = AccessTools.Field(settingsType, "enable_GoToShutdownZoneJob");
                    enableEnemySearchingField = AccessTools.Field(settingsType, "enableEnemySearching");
                }

                int patched = 0;
                patched += PatchCharger() ? 1 : 0;
                patched += PatchEscort() ? 1 : 0;
                patched += PatchShutdownZone() ? 1 : 0;
                patched += PatchScavenge() ? 1 : 0;
                patched += PatchCombat() ? 1 : 0;
                active = patched > 0;
                if (active)
                {
                    ABLog.Dev("WVC Work Modes detected, cross-level mech bridging active ("
                        + patched + "/5 seams patched).");
                }
                else
                {
                    Log.Warning(ABLog.Tag + " WVC Work Modes is active but none of its internals resolved; cross-level work-mode bridging is off.");
                }
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " WVC Work Modes compat setup failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------------

        private static bool PatchCharger()
        {
            Type giver = AccessTools.TypeByName("WVC_WorkModes.JobGiver_GetEnergy_Charger");
            MethodInfo method = giver != null ? AccessTools.DeclaredMethod(giver, "TryGiveJob") : null;
            if (method == null)
            {
                return false;
            }
            HarmonyBoot.Harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(ChargerPostfix)));
            return true;
        }

        private static bool PatchEscort()
        {
            Type util = AccessTools.TypeByName("WVC_WorkModes.SmartEscortUtility");
            getAssignedPawnMethod = util != null
                ? AccessTools.Method(util, "GetAssignedPawnOnMap")
                : null;
            Type giver = AccessTools.TypeByName("WVC_WorkModes.JobGiver_SmartAIFollowOverseer");
            MethodInfo method = giver != null ? AccessTools.DeclaredMethod(giver, "TryGiveJob") : null;
            if (method == null || getAssignedPawnMethod == null)
            {
                return false;
            }
            HarmonyBoot.Harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(FollowPostfix)));
            return true;
        }

        private static bool PatchShutdownZone()
        {
            Type giver = AccessTools.TypeByName("WVC_WorkModes.JobGiver_GoToShutdownZone");
            Type util = AccessTools.TypeByName("WVC_WorkModes.ShutdownUtility");
            Type workTypeEnum = AccessTools.TypeByName("WVC_WorkModes.MechanoidWorkType");
            if (giver == null || util == null || workTypeEnum == null)
            {
                return false;
            }
            workModeTypeField = AccessTools.Field(giver, "workModeType");
            possibleRoomsField = AccessTools.Field(giver, "possibleRooms");
            possibleRoomsProperty = AccessTools.Property(giver, "PossibleRooms");
            mechInShutdownZoneMethod = AccessTools.Method(util, "MechInShutdownZone",
                new[] { typeof(Pawn), typeof(IntVec3), workTypeEnum });
            mechInShutdownRoomMethod = AccessTools.Method(util, "MechInShutdownZone",
                new[] { typeof(Pawn), typeof(IntVec3), typeof(List<RoomRoleDef>) });
            findShutdownZoneMethod = AccessTools.Method(util, "TryFindRandomMechShutdownZone",
                new[] { typeof(List<Zone>), typeof(Pawn), typeof(Map), workTypeEnum, typeof(IntVec3).MakeByRefType() });
            anyMechanoidZoneMethod = AccessTools.Method(util, "AnyMechanoidZone",
                new[] { typeof(List<Zone>) });
            MethodInfo method = AccessTools.DeclaredMethod(giver, "TryGiveJob");
            if (method == null || workModeTypeField == null || mechInShutdownZoneMethod == null
                || findShutdownZoneMethod == null || anyMechanoidZoneMethod == null)
            {
                return false;
            }
            HarmonyBoot.Harmony.Patch(method,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(ShutdownZonePostfix)));
            return true;
        }

        private static bool PatchScavenge()
        {
            Type giver = AccessTools.TypeByName("WVC_WorkModes.JobGiver_GoToScavengeZone");
            Type cond = AccessTools.TypeByName("WVC_WorkModes.ThinkNode_ConditionalAnyScavengeZone");
            Type util = AccessTools.TypeByName("WVC_WorkModes.ScavengeUtility");
            if (giver == null || cond == null || util == null)
            {
                return false;
            }
            anyScavengeZoneMethod = AccessTools.Method(util, "AnyScavengeZone",
                new[] { typeof(List<Zone>) });
            findScavengeZoneMethod = AccessTools.Method(util, "TryFindFirstMechScavengeZone",
                new[] { typeof(List<Zone>), typeof(Pawn), typeof(IntVec3).MakeByRefType() });
            mechInScavengeZoneMethod = AccessTools.Method(util, "MechInScavengeZone",
                new[] { typeof(Pawn), typeof(IntVec3) });
            MethodInfo giverMethod = AccessTools.DeclaredMethod(giver, "TryGiveJob");
            MethodInfo condMethod = AccessTools.DeclaredMethod(cond, "Satisfied");
            if (giverMethod == null || condMethod == null || anyScavengeZoneMethod == null
                || findScavengeZoneMethod == null || mechInScavengeZoneMethod == null)
            {
                return false;
            }
            HarmonyBoot.Harmony.Patch(giverMethod,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(ScavengeGiverPostfix)));
            HarmonyBoot.Harmony.Patch(condMethod,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(ScavengeCondPostfix)));
            // Hardening: their wander root dereferences GetZone(pos) unguarded.
            // Stock trees only reach it inside a zone, but any null-result path
            // out of the routed goto (stairs destroyed mid-walk etc.) would NRE
            // the think tree. Fall back to wandering in place.
            MethodInfo wanderRoot = AccessTools.Method(util, "GetScavengeWanderRoot");
            if (wanderRoot != null)
            {
                HarmonyBoot.Harmony.Patch(wanderRoot,
                    prefix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(ScavengeWanderRootPrefix)));
            }
            return true;
        }

        private static bool PatchCombat()
        {
            Type cond = AccessTools.TypeByName("WVC_WorkModes.ThinkNode_ConditionalEnemyOnMap");
            MethodInfo condMethod = cond != null ? AccessTools.DeclaredMethod(cond, "Satisfied") : null;
            if (condMethod == null)
            {
                return false;
            }
            foreach (string name in new[] { "WVC_FindAndDestroy", "WVC_WaitEnemy", "WVC_WorkAndWaitEnemy" })
            {
                MechWorkModeDef def = DefDatabase<MechWorkModeDef>.GetNamedSilentFail(name);
                if (def != null)
                {
                    seekModes.Add(def);
                }
            }
            HarmonyBoot.Harmony.Patch(condMethod,
                postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(EnemyCondPostfix)));
            // Vanilla giver, patched ONLY while WVC is active, and the postfix
            // is further gated to player mechs in WVC seek modes - raiders and
            // every other user of the giver are untouched.
            MethodInfo gotoHostile = AccessTools.DeclaredMethod(
                typeof(JobGiver_AIGotoNearestHostile), "TryGiveJob");
            if (gotoHostile != null && seekModes.Count > 0)
            {
                HarmonyBoot.Harmony.Patch(gotoHostile,
                    postfix: new HarmonyMethod(typeof(ABWVCWorkModesCompat), nameof(GotoHostilePostfix)));
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // Shared gates
        // ---------------------------------------------------------------------

        private static bool EligibleMech(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && !pawn.Dead && !pawn.Downed
                && pawn.Map != null && pawn.RaceProps != null && pawn.RaceProps.IsMechanoid
                && pawn.Faction == Faction.OfPlayer && !pawn.Drafted
                && pawn.GetLord() == null;
        }

        /// <summary>Live read of a bool on their ModSettings; defaults to true
        /// (their own defaults) when the field did not resolve.</summary>
        private static bool WvcSetting(FieldInfo field)
        {
            if (settingsField == null || field == null)
            {
                return true;
            }
            object settings = settingsField.GetValue(null);
            return settings == null || !(field.GetValue(settings) is bool b) || b;
        }

        /// <summary>Next hop from the pawn's level toward a target map in the
        /// same column, or null (two hops max, cap 3).</summary>
        private static Map NextHopToward(LevelComp comp, Map targetMap)
        {
            if (comp.upperMap == targetMap || comp.lowerMap == targetMap)
            {
                return targetMap;
            }
            if (comp.upperMap != null && comp.upperMap.Levels()?.upperMap == targetMap)
            {
                return comp.upperMap;
            }
            if (comp.lowerMap != null && comp.lowerMap.Levels()?.lowerMap == targetMap)
            {
                return comp.lowerMap;
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // 1. Charger
        // ---------------------------------------------------------------------

        private static void ChargerPostfix(Pawn pawn, ref Job __result)
        {
            if (!active || __result != null || !ABGuard.On(ABGuard.Logistics))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelNeeds)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn) || pawn.needs?.energy == null)
                {
                    return;
                }
                // Mirror their gate exactly (pure vanilla API): only mechs their
                // own giver would send to a charger get routed. Their +2f trickle
                // branch stays theirs.
                Need_MechEnergy energy = pawn.needs.energy;
                float limit = JobGiver_GetEnergy.GetMaxRechargeLimit(pawn);
                if (energy.CurLevel + 0.1f >= limit - 5f)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!chargeCooldown.Ready(pawn, now))
                {
                    return;
                }
                chargeCooldown.ChargeUntil(pawn, now + ChargeCooldownTicks);
                __result = Patch_MechCharge_CrossLevel.TryToward(pawn, comp.upperMap)
                    ?? Patch_MechCharge_CrossLevel.TryToward(pawn, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "WVC mech charge routing", pawn);
            }
        }

        // ---------------------------------------------------------------------
        // 2. Smart escort
        // ---------------------------------------------------------------------

        private static void FollowPostfix(Pawn pawn, ref Job __result)
        {
            if (!active || __result != null || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn))
                {
                    return;
                }
                Pawn followee = getAssignedPawnMethod.Invoke(null, new object[] { pawn }) as Pawn;
                if (followee == null)
                {
                    return;
                }
                Map followeeMap = followee.MapHeld;
                if (followeeMap == null || followeeMap == pawn.Map)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                Map next = NextHopToward(comp, followeeMap);
                if (next == null)
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!followCooldown.Ready(pawn, now))
                {
                    return;
                }
                followCooldown.ChargeUntil(pawn, now + FollowCooldownTicks);
                IntVec3 dest = next == followeeMap ? followee.PositionHeld : IntVec3.Invalid;
                if (!CrossLevelWork.TryStairsJobToward(pawn, next, dest, out Job job))
                {
                    return;
                }
                ABLog.Dev("WVC escort mech " + pawn.LabelShort + " following " + followee.LabelShort
                    + " toward level " + followeeMap.Level() + ".");
                __result = job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "WVC smart escort routing");
            }
        }

        // ---------------------------------------------------------------------
        // 3. Shutdown zones
        // ---------------------------------------------------------------------

        private static void ShutdownZonePostfix(object __instance, Pawn pawn, ref Job __result)
        {
            if (!active || __result != null || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn) || !WvcSetting(enableShutdownZoneField))
                {
                    return;
                }
                object workModeType = workModeTypeField.GetValue(__instance);
                // A null result also means "already parked where it should be":
                // standing in a qualifying zone, or in one of the giver's listed
                // shutdown rooms. Never route a settled mech away.
                if (InShutdownZoneHere(__instance, pawn, workModeType))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!zoneCooldown.Ready(pawn, now))
                {
                    return;
                }
                zoneCooldown.ChargeUntil(pawn, now + ZoneCooldownTicks);
                __result = TryTowardShutdownZone(pawn, comp.upperMap, workModeType)
                    ?? TryTowardShutdownZone(pawn, comp.lowerMap, workModeType);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "WVC shutdown zone routing");
            }
        }

        private static bool InShutdownZoneHere(object giver, Pawn pawn, object workModeType)
        {
            if (mechInShutdownZoneMethod.Invoke(null,
                new[] { pawn, (object)pawn.Position, workModeType }) is bool inZone && inZone)
            {
                return true;
            }
            // Room variant only applies when the giver carries a room list.
            if (mechInShutdownRoomMethod != null && possibleRoomsField != null
                && possibleRoomsProperty != null
                && possibleRoomsField.GetValue(giver) != null
                && possibleRoomsProperty.GetValue(giver, null) is List<RoomRoleDef> rooms
                && rooms.Count > 0)
            {
                Room room = pawn.Position.GetRoom(pawn.Map);
                if (room != null
                    && mechInShutdownRoomMethod.Invoke(null,
                        new object[] { pawn, pawn.Position, rooms }) is bool inRoom && inRoom)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Probes one linked level for a qualifying shutdown zone with
        /// WVC's OWN finder (owner/group checks, standability, reservation) run
        /// under a virtual position swap at the stair exit, then issues the
        /// destination-aware stairs job. Room-based shutdown stays level-local
        /// (documented limitation).</summary>
        private static Job TryTowardShutdownZone(Pawn pawn, Map target, object workModeType)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            List<Zone> zones = target.zoneManager?.AllZones;
            if (zones == null || zones.Count == 0
                || !(anyMechanoidZoneMethod.Invoke(null, new object[] { zones }) is bool any) || !any)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs,
                out Building_ABStairs exit))
            {
                return null;
            }
            IntVec3 dest = IntVec3.Invalid;
            bool found = ABVirtualPosition.WithPawnAt(pawn, target, exit.Position, delegate
            {
                object[] args = { zones, pawn, target, workModeType, IntVec3.Invalid };
                if (findShutdownZoneMethod.Invoke(null, args) is bool ok && ok)
                {
                    dest = (IntVec3)args[4];
                    return true;
                }
                return false;
            });
            if (!found || !dest.IsValid)
            {
                return null;
            }
            StairRouter.Reroute(pawn, target, dest, ref stairs, ref exit);
            ABLog.Dev("Routing WVC mech " + pawn.LabelShort + " toward a shutdown zone on level "
                + target.Level() + ".");
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }

        // ---------------------------------------------------------------------
        // 4. Scavenge zones
        // ---------------------------------------------------------------------

        private static void ScavengeCondPostfix(Pawn pawn, ref bool __result)
        {
            if (!active || __result || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn))
                {
                    return;
                }
                // Already inside a zone: their conditional being false here means
                // the zone system is off; leave it.
                if (mechInScavengeZoneMethod.Invoke(null,
                    new object[] { pawn, pawn.Position }) is bool inZone && inZone)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                // Only open the branch when a route can actually be armed: a
                // linked level with an active scavenge zone AND resolvable
                // stairs. Opening it blindly would let their wander giver run
                // outside any zone.
                __result = ScavengeRouteExists(pawn, comp.upperMap)
                    || ScavengeRouteExists(pawn, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "WVC scavenge conditional");
            }
        }

        private static bool ScavengeRouteExists(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed || !target.IsPlayerHome)
            {
                return false;
            }
            List<Zone> zones = target.zoneManager?.AllZones;
            if (zones == null || zones.Count == 0
                || !(anyScavengeZoneMethod.Invoke(null, new object[] { zones }) is bool any) || !any)
            {
                return false;
            }
            return CrossLevelWork.TryResolveStairs(pawn, target, out _, out _);
        }

        private static void ScavengeGiverPostfix(Pawn pawn, ref Job __result)
        {
            if (!active || __result != null || !ABGuard.On(ABGuard.Movement))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn))
                {
                    return;
                }
                if (mechInScavengeZoneMethod.Invoke(null,
                    new object[] { pawn, pawn.Position }) is bool inZone && inZone)
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!scavengeCooldown.Ready(pawn, now))
                {
                    return;
                }
                scavengeCooldown.ChargeUntil(pawn, now + ScavengeCooldownTicks);
                __result = TryTowardScavengeZone(pawn, comp.upperMap)
                    ?? TryTowardScavengeZone(pawn, comp.lowerMap);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Movement, e, "WVC scavenge zone routing");
            }
        }

        private static Job TryTowardScavengeZone(Pawn pawn, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            List<Zone> zones = target.zoneManager?.AllZones;
            if (zones == null || zones.Count == 0
                || !(anyScavengeZoneMethod.Invoke(null, new object[] { zones }) is bool any) || !any)
            {
                return null;
            }
            if (!CrossLevelWork.TryResolveStairs(pawn, target, out Building_ABStairs stairs,
                out Building_ABStairs exit))
            {
                return null;
            }
            IntVec3 dest = IntVec3.Invalid;
            bool found = ABVirtualPosition.WithPawnAt(pawn, target, exit.Position, delegate
            {
                object[] args = { zones, pawn, IntVec3.Invalid };
                if (findScavengeZoneMethod.Invoke(null, args) is bool ok && ok)
                {
                    dest = (IntVec3)args[2];
                    return true;
                }
                return false;
            });
            if (!found || !dest.IsValid)
            {
                return null;
            }
            StairRouter.Reroute(pawn, target, dest, ref stairs, ref exit);
            ABLog.Dev("Routing WVC mech " + pawn.LabelShort + " toward a scavenge zone on level "
                + target.Level() + ".");
            return CrossLevelWork.MakeStairsJob(stairs, exit);
        }

        /// <summary>Hardening only: wander root falls back to the mech's own
        /// position when it is not standing in any zone (their code would NRE).</summary>
        private static bool ScavengeWanderRootPrefix(Pawn pawn, ref IntVec3 __result)
        {
            try
            {
                if (pawn?.Map != null && pawn.Position.GetZone(pawn.Map) == null)
                {
                    __result = pawn.Position;
                    return false;
                }
            }
            catch
            {
                // Never let the guard itself break the giver.
            }
            return true;
        }

        // ---------------------------------------------------------------------
        // 5. Combat column-awareness
        // ---------------------------------------------------------------------

        private static void EnemyCondPostfix(Pawn pawn, ref bool __result)
        {
            if (!active || __result || !ABGuard.On(ABGuard.Threats))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn) || !WvcSetting(enableEnemySearchingField))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                __result = HostilesOn(comp.upperMap, pawn)
                    || HostilesOn(comp.lowerMap, pawn)
                    || HostilesOn(comp.upperMap?.Levels()?.upperMap, pawn)
                    || HostilesOn(comp.lowerMap?.Levels()?.lowerMap, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "WVC enemy conditional");
            }
        }

        private static bool HostilesOn(Map map, Pawn pawn)
        {
            return map != null && !map.Disposed
                && GenHostility.AnyHostileActiveThreatTo(map, pawn.Faction);
        }

        private static void GotoHostilePostfix(Pawn pawn, ref Job __result)
        {
            if (!active || __result != null || !ABGuard.On(ABGuard.Threats))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || !settings.crossLevelOrders)
            {
                return;
            }
            try
            {
                if (!EligibleMech(pawn))
                {
                    return;
                }
                MechWorkModeDef mode = pawn.GetMechWorkMode();
                if (mode == null || !seekModes.Contains(mode))
                {
                    return;
                }
                if (!pawn.Map.TryLinkedLevels(out LevelComp comp))
                {
                    return;
                }
                // Nearest raided level first: adjacent levels, then two hops.
                Map targetMap = null;
                if (HostilesOn(comp.upperMap, pawn)) targetMap = comp.upperMap;
                else if (HostilesOn(comp.lowerMap, pawn)) targetMap = comp.lowerMap;
                else if (HostilesOn(comp.upperMap?.Levels()?.upperMap, pawn)) targetMap = comp.upperMap.Levels().upperMap;
                else if (HostilesOn(comp.lowerMap?.Levels()?.lowerMap, pawn)) targetMap = comp.lowerMap.Levels().lowerMap;
                if (targetMap == null)
                {
                    return;
                }
                Map next = NextHopToward(comp, targetMap);
                if (next == null)
                {
                    return;
                }
                int now = Find.TickManager.TicksGame;
                if (!hostileCooldown.Ready(pawn, now))
                {
                    return;
                }
                hostileCooldown.ChargeUntil(pawn, now + HostileCooldownTicks);
                if (!CrossLevelWork.TryStairsJobToward(pawn, next, IntVec3.Invalid, out Job job))
                {
                    return;
                }
                ABLog.Dev("WVC combat mech " + pawn.LabelShort + " heading toward hostiles on level "
                    + targetMap.Level() + ".");
                __result = job;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Threats, e, "WVC combat routing");
            }
        }
    }
}
