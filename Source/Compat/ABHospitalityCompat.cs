using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Hospitality guests roam the whole column (parity pass 2026-07-24).
    /// Architecture: lords are map-scoped, and a despawn normally drops the
    /// pawn from its lord - which is why NPC visitors never crossed levels.
    /// Three cooperating pieces make roaming safe:
    ///
    ///  1. LORD RETENTION - Lord.Notify_PawnLost is suppressed exactly when
    ///     (a) the pawn is mid stair-transfer (StairTransfer.Transferring) and
    ///     (b) the lord's job is Hospitality's LordJob_VisitColony. The guest
    ///     stays in ownedPawns, so the group's triggers and headcounts stay
    ///     correct; the visit lord never issues cross-map duties that matter
    ///     because guests run their own jobs during the visit stage.
    ///  2. ROAM - during the visit stage (CurLordToil is LordToil_VisitPoint),
    ///     idle guests occasionally ride the stairs to a linked level that has
    ///     guest beds or vending machines (the player's invitation). At most
    ///     two guests per group are away at once. While away they behave as
    ///     ordinary neutrals (Hospitality's per-map guest logic resumes on
    ///     return); full guest services stay on the lord's level - disclosed.
    ///  3. RETURN - roamed guests are walked back the moment their lord leaves
    ///     the visit stage (departure), or after a long stay. The generic
    ///     stranded-neutral sweep (ABNeutralExit) is the safety net for
    ///     anything else.
    ///
    /// Resolved by name, inert without Orion.Hospitality, setting
    /// hospitalityRoaming (default ON), kill switch: social. State cleared on
    /// load via ClearAll.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class ABHospitalityCompat
    {
        internal static bool Active;

        private static Type visitLordJobType;

        private static Type visitPointToilType;

        private static Type guestBedType;

        private static Type vendingCompType;

        private const int ScanIntervalTicks = 900;

        private const float RoamChance = 0.2f;

        private const int MaxAwayPerLord = 2;

        private const int MaxAwayTicks = 20000;

        private static int due;

        /// <summary>pawn id -> tick the roam started. Bounded, load-cleared.</summary>
        private static readonly Dictionary<int, int> roamedAt = new Dictionary<int, int>();

        static ABHospitalityCompat()
        {
            try
            {
                if (!ABCompat.Detect("Orion.Hospitality", "Hospitality"))
                {
                    return;
                }
                visitLordJobType = AccessTools.TypeByName("Hospitality.LordJob_VisitColony");
                visitPointToilType = AccessTools.TypeByName("Hospitality.LordToil_VisitPoint");
                guestBedType = AccessTools.TypeByName("Hospitality.Building_GuestBed");
                vendingCompType = AccessTools.TypeByName("Hospitality.CompVendingMachine");
                if (visitLordJobType == null || visitPointToilType == null || guestBedType == null)
                {
                    Log.Warning(ABLog.Tag + " Hospitality detected but its visit internals were not found; guest roaming is off.");
                    return;
                }
                // Hospitality overrides the visitor worker, so the vanilla-typed
                // sky-arrival prefix never fires for its groups: patch its
                // override with the same divert.
                Type hospWorker = AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup");
                System.Reflection.MethodInfo tryExec = hospWorker != null
                    ? AccessTools.DeclaredMethod(hospWorker, "TryExecuteWorker") : null;
                if (tryExec != null)
                {
                    HarmonyBoot.Harmony.Patch(tryExec,
                        prefix: new HarmonyMethod(typeof(ABHospitalityCompat), nameof(VisitorWorkerPrefix)));
                }
                Active = true;
                ABLog.Dev("Hospitality detected; cross-level guest roaming active.");
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " Hospitality compat setup failed: " + e.Message);
            }
        }

        private static void VisitorWorkerPrefix(IncidentParms parms)
        {
            SkyArrivals.TryDivert(parms);
        }

        private static bool Enabled()
        {
            if (!Active || !ABGuard.On(ABGuard.Social))
            {
                return false;
            }
            ABSettings s = ABMod.Settings;
            return s != null && s.hospitalityRoaming;
        }

        [ABGameReset]
        internal static void ClearAll()
        {
            roamedAt.Clear();
        }

        /// <summary>True when this Notify_PawnLost is a stair ride by a
        /// Hospitality guest: suppress it so the visit lord keeps the pawn.</summary>
        internal static bool RetainInLord(Lord lord, Pawn pawn)
        {
            if (!Enabled() || pawn == null || StairTransfer.Transferring != pawn)
            {
                return false;
            }
            return lord?.LordJob != null && visitLordJobType.IsInstanceOfType(lord.LordJob);
        }

        /// <summary>Cadenced roam + return scan; one counter read when idle.</summary>
        [ABGameTick(50)]
        internal static void Tick()
        {
            if (!Enabled())
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (now < due)
            {
                return;
            }
            due = now + ScanIntervalTicks;
            try
            {
                List<Map> maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                {
                    Map map = maps[i];
                    if (map == null || map.Disposed || map.Levels() == null)
                    {
                        continue;
                    }
                    ScanMap(map, now);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Social, e, "hospitality roaming");
            }
        }

        private static void ScanMap(Map map, int now)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn p = pawns[i];
                if (p == null || p.Dead || !p.Spawned || !p.RaceProps.Humanlike
                    || p.Faction == null || p.Faction == Faction.OfPlayer
                    || p.HostileTo(Faction.OfPlayer) || p.IsPrisoner
                    || p.Downed || p.InMentalState || !p.Awake()
                    || p.CurJobDef == ABDefOf.AB_UseStairs)
                {
                    continue;
                }
                Lord localLord = map.lordManager.LordOf(p);
                if (localLord != null)
                {
                    TryRoamOut(p, localLord, map);
                    continue;
                }
                // No lord on this map: a roamed guest. Its lord lives on a
                // linked level (retention kept membership). Walk home when the
                // group is leaving or the stay ran long.
                TryReturn(p, map, now);
            }
        }

        private static void TryRoamOut(Pawn p, Lord lord, Map map)
        {
            if (lord.LordJob == null || !visitLordJobType.IsInstanceOfType(lord.LordJob)
                || lord.CurLordToil == null || !visitPointToilType.IsInstanceOfType(lord.CurLordToil))
            {
                return;
            }
            if (p.CurJob != null && p.CurJobDef != JobDefOf.GotoWander
                && p.CurJobDef != JobDefOf.Wait_Wander && p.CurJobDef != JobDefOf.Goto)
            {
                return; // busy with a real guest job (shopping, eating, joy)
            }
            if (!Rand.Chance(RoamChance))
            {
                return;
            }
            int away = 0;
            List<Pawn> owned = lord.ownedPawns;
            for (int i = 0; i < owned.Count; i++)
            {
                if (owned[i] != null && owned[i].MapHeld != map)
                {
                    away++;
                }
            }
            if (away >= MaxAwayPerLord)
            {
                return;
            }
            LevelComp comp = map.Levels();
            Map target = PickRoamTarget(p, comp?.upperMap) ?? PickRoamTarget(p, comp?.lowerMap);
            if (target == null)
            {
                return;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(p, target);
            Building_ABStairs exit = entry?.CounterpartTowards(target);
            if (exit == null)
            {
                return;
            }
            Job job = CrossLevelWork.MakeStairsJob(entry, exit);
            if (job != null)
            {
                roamedAt[p.thingIDNumber] = Find.TickManager.TicksGame;
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                ABLog.Dev("Guest " + p.LabelShort + " roaming to level " + target.Level() + ".");
            }
        }

        /// <summary>A level worth touring: guest beds or vending machines.</summary>
        private static Map PickRoamTarget(Pawn p, Map target)
        {
            if (target == null || target.Disposed)
            {
                return null;
            }
            List<Building> all = target.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                if (b == null || !b.Spawned)
                {
                    continue;
                }
                if (guestBedType.IsInstanceOfType(b))
                {
                    return target;
                }
                // Manual comp scan instead of AllComps.Any(lambda): the lambda
                // captures vendingCompType, so LINQ allocates a closure + delegate
                // + enumerator per building in this cadenced guest scan.
                if (vendingCompType != null)
                {
                    List<ThingComp> comps = ((ThingWithComps)b).AllComps;
                    for (int j = 0; j < comps.Count; j++)
                    {
                        if (vendingCompType.IsInstanceOfType(comps[j]))
                        {
                            return target;
                        }
                    }
                }
            }
            return null;
        }

        private static void TryReturn(Pawn p, Map map, int now)
        {
            Lord lord = ABNeutralExit.LordMembershipInColumn(p, map);
            if (lord == null)
            {
                // Genuinely lordless: ABNeutralExit owns the recovery.
                roamedAt.Remove(p.thingIDNumber);
                return;
            }
            if (lord.Map == map)
            {
                roamedAt.Remove(p.thingIDNumber);
                return;
            }
            bool leaving = lord.CurLordToil == null || !visitPointToilType.IsInstanceOfType(lord.CurLordToil);
            bool overstayed = !roamedAt.TryGetValue(p.thingIDNumber, out int t0) || now - t0 > MaxAwayTicks;
            if (!leaving && !overstayed)
            {
                return;
            }
            if (p.CurJob != null && p.CurJobDef != JobDefOf.GotoWander
                && p.CurJobDef != JobDefOf.Wait_Wander && p.CurJobDef != JobDefOf.Goto
                && !leaving)
            {
                return;
            }
            Building_ABStairs entry = CrossLevelWork.NearestUsableStairsCached(p, lord.Map);
            Building_ABStairs exit = entry?.CounterpartTowards(lord.Map);
            if (exit == null)
            {
                return;
            }
            Job job = CrossLevelWork.MakeStairsJob(entry, exit);
            if (job != null)
            {
                roamedAt.Remove(p.thingIDNumber);
                p.jobs?.StartJob(job, JobCondition.InterruptForced);
                ABLog.Dev("Guest " + p.LabelShort + " returning to its group.");
            }
            if (roamedAt.Count > 256)
            {
                roamedAt.Clear();
            }
        }
    }

    /// <summary>Suppress the lord-loss notification for a Hospitality guest
    /// mid stair-ride, keeping the guest in its visit group across levels.</summary>
    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    internal static class Patch_Lord_PawnLost_GuestRetention
    {
        private static bool Prefix(Lord __instance, Pawn pawn)
        {
            try
            {
                return !ABHospitalityCompat.RetainInLord(__instance, pawn);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Social, e, "guest lord retention");
                return true;
            }
        }
    }
}
