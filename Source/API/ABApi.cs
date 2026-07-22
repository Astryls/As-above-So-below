using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// PUBLIC API for other mods (T12). Everything here is a stable contract:
    /// signatures use only Verse/RimWorld types and primitives, every method is
    /// null-safe and fail-open, and nothing throws for a map that has no
    /// levels. Reflection-friendly: all members are public static on this one
    /// class. See Docs/API.md in the mod folder for the guide.
    ///
    /// XML surface (no C# required):
    ///  - ABStairsExtension on a ThingDef with thingClass Building_ABStairs
    ///    (or Building_ABUtilityLink) makes YOUR building a vertical link:
    ///    deltaLevel, counterpartDef, climbFactor, utilityOnly, bridgeWater,
    ///    bridgeVef. The whole lifecycle (far-end spawn, collapse, registry)
    ///    comes along for free.
    ///  - ABIncidentLevelPolicy on an IncidentDef opts that incident into
    ///    firing directly at pocket levels (the default policy redirects
    ///    every incident to the column's surface).
    /// </summary>
    public static class ABApi
    {
        /// <summary>Bumped when the API surface changes incompatibly.</summary>
        public const int ApiVersion = 1;

        // ---------- Queries ----------

        /// <summary>Level of a map: 0 surface, +1 sky, -1 basement. Maps that
        /// are not part of a column (including foreign pocket maps) return 0.</summary>
        public static int GetLevel(Map map)
        {
            return map?.Levels()?.level ?? 0;
        }

        /// <summary>True when the map is one of our pocket levels (sky or
        /// basement) with a live column.</summary>
        public static bool IsLevelMap(Map map)
        {
            LevelComp comp = map?.Levels();
            return comp != null && comp.level != 0 && comp.groundMap != null && !comp.groundMap.Disposed;
        }

        /// <summary>The column's surface map for any member map (identity for
        /// the surface itself). Null when the map has no column.</summary>
        public static Map GetGroundMap(Map map)
        {
            LevelComp comp = map?.Levels();
            if (comp == null)
            {
                return null;
            }
            Map ground = comp.level == 0 ? map : comp.groundMap;
            return ground != null && !ground.Disposed ? ground : null;
        }

        /// <summary>The map one level up from the given map, or null.</summary>
        public static Map GetUpperMap(Map map)
        {
            Map upper = map?.Levels()?.upperMap;
            return upper != null && !upper.Disposed ? upper : null;
        }

        /// <summary>The map one level down from the given map, or null.</summary>
        public static Map GetLowerMap(Map map)
        {
            Map lower = map?.Levels()?.lowerMap;
            return lower != null && !lower.Disposed ? lower : null;
        }

        /// <summary>Every live map of the given map's column, surface first,
        /// then sky, then basement. A map without a column yields itself.</summary>
        public static IEnumerable<Map> GetColumnMaps(Map map)
        {
            Map ground = GetGroundMap(map) ?? map;
            if (ground == null)
            {
                yield break;
            }
            yield return ground;
            Map upper = GetUpperMap(ground);
            if (upper != null)
            {
                yield return upper;
            }
            Map lower = GetLowerMap(ground);
            if (lower != null)
            {
                yield return lower;
            }
        }

        // ---------- Movement ----------

        /// <summary>The nearest usable stairwell on the pawn's map leading
        /// toward the target level map, with reachability checked. Returns the
        /// entry-side building (a Building subclass) or null. Utility links
        /// (conduits, pipes) are never returned.</summary>
        public static Building GetStairsToward(Pawn pawn, Map target)
        {
            if (pawn == null || !pawn.Spawned || target == null || target.Disposed)
            {
                return null;
            }
            return CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
        }

        /// <summary>The best usable stairwell toward a known destination cell on
        /// the target level map: minimizes the whole trip (walk here + climb +
        /// walk over there) instead of just the walk to the stairwell. Pass
        /// IntVec3.Invalid to mean "no destination hint".</summary>
        public static Building GetStairsToward(Pawn pawn, Map target, IntVec3 dest)
        {
            if (pawn == null || !pawn.Spawned || target == null || target.Disposed)
            {
                return null;
            }
            if (dest.IsValid && StairRouter.TryBestToward(pawn, target, dest, out Building_ABStairs s, out _))
            {
                return s;
            }
            return CrossLevelWork.NearestUsableStairs(pawn, target, checkReachability: true);
        }

        /// <summary>Order the pawn through the nearest stairs toward the target
        /// map. The pawn walks to the stairwell, climbs, and transfers; lords
        /// are handled for non-player pawns. Returns false when no usable
        /// stairs exist. forced interrupts the current job. When dest is a
        /// valid cell on the target map, the stairwell landing nearest it wins.</summary>
        public static bool TrySendPawnToward(Pawn pawn, Map target, bool forced = false)
        {
            return TrySendPawnToward(pawn, target, IntVec3.Invalid, forced);
        }

        public static bool TrySendPawnToward(Pawn pawn, Map target, IntVec3 dest, bool forced = false)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed
                || target == null || target.Disposed || pawn.Map == target)
            {
                return false;
            }
            if (!CrossLevelWork.TryStairsJobToward(pawn, target, dest, out Job job))
            {
                return false;
            }
            if (forced)
            {
                pawn.jobs?.StartJob(job, JobCondition.InterruptForced);
            }
            else
            {
                pawn.jobs?.TryTakeOrderedJob(job, JobTag.Misc);
            }
            return true;
        }

        // ---------- Events ----------

        /// <summary>Raised right after a level map finishes generating (not on
        /// save load). Argument: the new level map.</summary>
        public static event Action<Map> LevelCreated;

        /// <summary>Raised when a level map is removed (abandon, collapse).</summary>
        public static event Action<Map> LevelRemoved;

        /// <summary>Raised after a pawn transfers between levels through any
        /// link. Arguments: pawn, source map, destination map.</summary>
        public static event Action<Pawn, Map, Map> PawnTransferred;

        // ---------- Extensibility ----------

        private static readonly HashSet<string> extraExitDuties = new HashSet<string>();

        /// <summary>Register a DutyDef defName that means "this NPC is trying
        /// to leave the map". Pocket-level NPCs holding such a duty are routed
        /// down the stairs and get an exit lord on the surface. Vanilla's
        /// travel/leave/kidnap/steal set is built in; use this for custom lord
        /// systems (guest mods, quest mods).</summary>
        public static void RegisterExitDuty(string dutyDefName)
        {
            if (!dutyDefName.NullOrEmpty())
            {
                extraExitDuties.Add(dutyDefName);
            }
        }

        /// <summary>Internal: whether a duty def is a registered exit duty.</summary>
        internal static bool IsRegisteredExitDuty(DutyDef duty)
        {
            return duty != null && extraExitDuties.Count > 0 && extraExitDuties.Contains(duty.defName);
        }

        /// <summary>Register a ThinkNode_JobGiver type (full type name) whose
        /// need-satisfying scan should extend across levels. When the giver
        /// returns no job on the pawn's map, it re-runs virtually at each
        /// linked stairwell exit; on a hit the pawn takes the stairs and the
        /// giver re-rolls on arrival. Your think tree gating (need thresholds)
        /// is inherited: the hook only fires when your giver was invoked.
        /// Register partner/facility-seeking givers only - solo fallbacks
        /// (masturbate-style) satisfy locally and would mask migration; wander
        /// givers never return null and never trigger. allowInMentalState
        /// lets the giver act during mental breaks too (vanilla binge and
        /// berserk are built in that way: the binger hunts beer downstairs).
        /// Register CONCRETE runtime types, not abstract bases.
        /// Built in: vanilla BingeDrug/BingeFood/Berserk/MurderousRage
        /// (mental-safe), RJW sex+breeding family, Intimacy GetIntimacy.
        /// Note: givers that override TryIssueJobPackage itself (rare) are not
        /// covered; the hook lives on the base implementation.</summary>
        public static void RegisterNeedJobGiver(string jobGiverFullTypeName, bool allowInMentalState = false)
        {
            NeedMigration.Register(jobGiverFullTypeName, allowInMentalState);
        }

        // ---------- Internal raisers (fail-open: a broken subscriber can
        // never take our systems down) ----------

        internal static void NotifyLevelCreated(Map map)
        {
            try
            {
                LevelCreated?.Invoke(map);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " A LevelCreated subscriber threw: " + e);
            }
        }

        internal static void NotifyLevelRemoved(Map map)
        {
            try
            {
                LevelRemoved?.Invoke(map);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " A LevelRemoved subscriber threw: " + e);
            }
        }

        internal static void NotifyPawnTransferred(Pawn pawn, Map from, Map to)
        {
            try
            {
                PawnTransferred?.Invoke(pawn, from, to);
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " A PawnTransferred subscriber threw: " + e);
            }
        }
    }

    /// <summary>Put this DefModExtension on an IncidentDef to let it fire
    /// directly at pocket levels. Without it, any incident executed against a
    /// sky or basement map is redirected to the column's surface (no trader
    /// ships passing over the basement).</summary>
    public class ABIncidentLevelPolicy : DefModExtension
    {
        /// <summary>Allow this incident to execute on sky/basement maps.</summary>
        public bool allowOnPocketLevels;
    }

    /// <summary>Put this DefModExtension on a skyfaller ThingDef to control pod
    /// transit explicitly. With transit true the skyfaller passes through the
    /// sky level's airspace (and is exposed to anti-air fire up there) when it
    /// descends onto a surface cell under an open gap; with false it always
    /// drops straight to its target map. Without the extension, drop pods and
    /// the vanilla falling hazards (ship chunks, meteorites, crashed ship
    /// parts) transit; everything else - shuttles included - does not.</summary>
    public class ABSkyfallerTransit : DefModExtension
    {
        /// <summary>Whether this skyfaller falls through open sky-level gaps.</summary>
        public bool transit = true;
    }
}
