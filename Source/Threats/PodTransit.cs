using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Pod transit: descending skyfallers bound for the surface pass through the
    /// sky level's airspace when the sky cell above their landing spot is an open
    /// gap. During the upper half of the descent the skyfaller genuinely lives on
    /// the sky map - so anti-air defenses built up there (e.g. Anti-Air Artillery,
    /// which scans its own map's thing grid for DropPodIncoming / named skyfaller
    /// defs) engage it with zero compat code on either side. At the halfway mark
    /// the same Thing instance is handed to the ground map with its remaining
    /// ticks intact: landing spot, total descent time, roof punch and impact side
    /// effects are all vanilla.
    ///
    /// Scope: DropPodIncoming (class check, covers faction/mod pod defs), ship
    /// chunks, meteorites (+ crater), crashed ship parts and VFEI's insect
    /// meteorite. Shuttles and quest skyfallers are excluded - anything landing
    /// ON the sky level (standable rooftop cells) is untouched by construction,
    /// because transit only triggers over open-air gap cells. Skyfallers spawned
    /// directly on a sky map over open air (dev spawns, odd mod drops) take the
    /// downward leg only, instead of "landing" on an impassable gap.
    ///
    /// Modder API: ABSkyfallerTransit def extension forces a skyfaller in or out.
    /// Setting podTransit (default ON), kill switch: Transit (fail open = pods
    /// drop straight to their target map, pure vanilla).
    /// </summary>
    internal static class PodTransit
    {
        /// <summary>Below this many ticksToImpact at spawn, the descent is too
        /// short to split into two legs; the skyfaller lands vanilla.</summary>
        internal const int MinTransitTicks = 40;

        /// <summary>ticksToImpact at which a transiting skyfaller hands off to
        /// the ground map. Late by design: the pod is falling from orbit, so it
        /// spends nearly the whole descent in the sky level's airspace (giving
        /// upper-level anti-air the full vanilla engagement window - the run-35
        /// halfway handoff left AA only ~60-100 ticks and 2-3 shots) and only
        /// crosses the slab at the very end. 22 keeps the ground leg's vanilla
        /// roof punch (fires at 15) and impact intact.</summary>
        internal const int HandoffTicksToImpact = 22;

        private static readonly HashSet<string> transitDefNames = new HashSet<string>
        {
            "CrashedShipPartIncoming",
            "ShipChunkIncoming",
            "ShipChunkIncoming_SmallExplosion",
            "MeteoriteIncoming",
            "MeteoriteCraterIncoming",
            "VFEI_InsectMeteoriteIncoming"
        };

        private static readonly AccessTools.FieldRef<Skyfaller, int> ticksToImpactMaxRef =
            AccessTools.FieldRefAccess<Skyfaller, int>("ticksToImpactMax");

        /// <summary>Reentrancy gate: our own despawn/respawn passes through
        /// Skyfaller.SpawnSetup and must not re-register.</summary>
        internal static bool InTransfer { get; private set; }

        /// <summary>Dev-only observation hook for the self-test harness.</summary>
        internal static event Action<Skyfaller, Map, Map> DevTransferred;

        /// <summary>Def-level eligibility: non-reversed skyfaller that is a drop
        /// pod by class or a whitelisted falling hazard by defName. An
        /// ABSkyfallerTransit extension overrides both ways.</summary>
        internal static bool IsTransitDef(ThingDef def)
        {
            if (def?.skyfaller == null || def.skyfaller.reversed)
            {
                return false;
            }
            ABSkyfallerTransit ext = def.GetModExtension<ABSkyfallerTransit>();
            if (ext != null)
            {
                return ext.transit;
            }
            if (def.thingClass != null && typeof(DropPodIncoming).IsAssignableFrom(def.thingClass))
            {
                return true;
            }
            return transitDefNames.Contains(def.defName);
        }

        /// <summary>True when every cell of the footprint is an unroofed open-air
        /// gap on the sky map - the only geometry a skyfaller can fall through.
        /// Partial ledge overlap means the fiction puts it under an overhang;
        /// those land vanilla.</summary>
        internal static bool GapOpen(Map sky, CellRect rect)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(sky) || sky.terrainGrid.TerrainAt(c) != air || sky.roofGrid.Roofed(c))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Move a live skyfaller to another level of the same column,
        /// preserving its descent clock. Skyfaller.SpawnSetup rerolls
        /// ticksToImpact/Max/Discard and angle on every fresh spawn, so they are
        /// captured and restored around the respawn. On any failure the thing is
        /// put back where it was - a skyfaller must never be leaked despawned.</summary>
        internal static bool Transfer(Skyfaller sf, Map to)
        {
            Map from = sf.Map;
            IntVec3 pos = sf.Position;
            Rot4 rot = sf.Rotation;
            int ticks = sf.ticksToImpact;
            int max = ticksToImpactMaxRef(sf);
            int discard = sf.ticksToDiscard;
            float angle = sf.angle;
            int age = sf.ageTicks;
            InTransfer = true;
            try
            {
                sf.DeSpawn(DestroyMode.Vanish);
                GenSpawn.Spawn(sf, pos, to, rot, WipeMode.Vanish);
                sf.ticksToImpact = ticks;
                ticksToImpactMaxRef(sf) = max;
                sf.ticksToDiscard = discard;
                sf.angle = angle;
                sf.ageTicks = age;
                DevTransferred?.Invoke(sf, from, to);
                ABLog.Dev("Pod transit: " + sf.def.defName + " moved from map " + from.uniqueID
                    + " to map " + to.uniqueID + " with " + ticks + " ticks to impact.");
                return true;
            }
            catch (Exception e)
            {
                if (!sf.Destroyed && !sf.Spawned)
                {
                    try
                    {
                        GenSpawn.Spawn(sf, pos, from, rot, WipeMode.Vanish);
                        sf.ticksToImpact = ticks;
                        ticksToImpactMaxRef(sf) = max;
                        sf.ticksToDiscard = discard;
                        sf.angle = angle;
                        sf.ageTicks = age;
                    }
                    catch (Exception)
                    {
                        // Both spawns failed; the guard below reports the original.
                    }
                }
                ABGuard.Disable(ABGuard.Transit, e, "skyfaller level transfer");
                return false;
            }
            finally
            {
                InTransfer = false;
            }
        }
    }

    /// <summary>
    /// Per-map transit state. Surface maps hold the one-tick lift queue (spawn
    /// hooks must never despawn a thing mid-spawn, so the lift defers to the next
    /// component tick); sky maps hold the descending set with each skyfaller's
    /// handoff mark. Zero idle cost: two count checks per map per tick.
    /// </summary>
    public class PodTransitComp : MapComponent
    {
        private List<Skyfaller> lift;
        private List<Skyfaller> descending;
        private List<int> transferAt;

        public PodTransitComp(Map map) : base(map)
        {
        }

        internal void RegisterLift(Skyfaller sf)
        {
            if (lift == null)
            {
                lift = new List<Skyfaller>();
            }
            if (!lift.Contains(sf))
            {
                lift.Add(sf);
            }
        }

        internal void RegisterDescent(Skyfaller sf, int at)
        {
            if (descending == null)
            {
                descending = new List<Skyfaller>();
                transferAt = new List<int>();
            }
            if (!descending.Contains(sf))
            {
                descending.Add(sf);
                transferAt.Add(at);
            }
        }

        internal bool DevQueuedForLift(Skyfaller sf) => lift != null && lift.Contains(sf);

        internal int DevTransferAt(Skyfaller sf)
        {
            int idx = descending?.IndexOf(sf) ?? -1;
            return idx < 0 ? -1 : transferAt[idx];
        }

        public override void MapComponentTick()
        {
            bool anyLift = lift != null && lift.Count > 0;
            bool anyDescent = descending != null && descending.Count > 0;
            if (!anyLift && !anyDescent)
            {
                return;
            }
            if (!ABGuard.On(ABGuard.Transit))
            {
                lift?.Clear();
                descending?.Clear();
                transferAt?.Clear();
                return;
            }
            try
            {
                if (anyLift)
                {
                    ProcessLift();
                }
                if (anyDescent)
                {
                    ProcessDescent();
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Transit, e, "pod transit tick");
            }
        }

        /// <summary>Surface leg start: raise the freshly spawned skyfaller to the
        /// sky map. Eligibility is rechecked - a tick has passed and AA or roof
        /// changes may have intervened. The descent clock keeps its full roll, so
        /// total time to the ground is unchanged.</summary>
        private void ProcessLift()
        {
            for (int i = lift.Count - 1; i >= 0; i--)
            {
                Skyfaller sf = lift[i];
                lift.RemoveAt(i);
                if (sf == null || sf.Destroyed || !sf.Spawned || sf.Map != map)
                {
                    continue;
                }
                Map sky = map.Levels()?.upperMap;
                if (sky == null || sky.Disposed)
                {
                    continue;
                }
                if (!PodTransit.GapOpen(sky, sf.OccupiedRect()))
                {
                    continue;
                }
                int at = Math.Min(PodTransit.HandoffTicksToImpact, sf.ticksToImpact / 2);
                if (at < 16)
                {
                    // Not enough time left for a sane lower leg; land vanilla.
                    continue;
                }
                if (PodTransit.Transfer(sf, sky))
                {
                    sky.GetComponent<PodTransitComp>()?.RegisterDescent(sf, at);
                }
            }
        }

        /// <summary>Sky leg end: when the descent clock crosses the handoff mark,
        /// pass the skyfaller down to the ground map for the vanilla impact. If
        /// the gap has closed mid-descent (player floored it over), the entry is
        /// dropped and the skyfaller impacts on the sky level instead - it
        /// physically cannot fall through a floor.</summary>
        private void ProcessDescent()
        {
            for (int i = descending.Count - 1; i >= 0; i--)
            {
                Skyfaller sf = descending[i];
                if (sf == null || sf.Destroyed || !sf.Spawned || sf.Map != map)
                {
                    // Shot down mid-transit (anti-air kills call Destroy on the
                    // skyfaller directly): burst at its gap cell so the intercept
                    // reads from the sky view - the AA mod's own flash sits at
                    // the gun, and a silently vanishing pod looks like a miss.
                    if (sf != null && sf.Destroyed && sf.Position.IsValid
                        && sf.Position.InBounds(map))
                    {
                        FleckMaker.Static(sf.Position, map, FleckDefOf.ExplosionFlash, 3f);
                        FleckMaker.ThrowSmoke(sf.Position.ToVector3Shifted(), map, 2.5f);
                        FleckMaker.ThrowMicroSparks(sf.Position.ToVector3Shifted(), map);
                    }
                    descending.RemoveAt(i);
                    transferAt.RemoveAt(i);
                    continue;
                }
                if (sf.ticksToImpact > transferAt[i])
                {
                    continue;
                }
                descending.RemoveAt(i);
                transferAt.RemoveAt(i);
                if (!PodTransit.GapOpen(map, sf.OccupiedRect()))
                {
                    continue;
                }
                Map ground = map.Levels()?.lowerMap;
                if (ground == null || ground.Disposed || !sf.Position.InBounds(ground))
                {
                    continue;
                }
                PodTransit.Transfer(sf, ground);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref lift, "abTransitLift", LookMode.Reference);
            Scribe_Collections.Look(ref descending, "abTransitDescending", LookMode.Reference);
            Scribe_Collections.Look(ref transferAt, "abTransitTransferAt", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                lift?.RemoveAll(x => x == null || x.Destroyed);
                if (descending != null)
                {
                    if (transferAt == null || transferAt.Count != descending.Count)
                    {
                        // Defensive: parallel lists drifted; drop the tracking,
                        // the pods just land where they are (vanilla impact).
                        descending.Clear();
                        transferAt = new List<int>();
                    }
                    else
                    {
                        for (int i = descending.Count - 1; i >= 0; i--)
                        {
                            if (descending[i] == null || descending[i].Destroyed)
                            {
                                descending.RemoveAt(i);
                                transferAt.RemoveAt(i);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The single entry point: every skyfaller spawn passes through
    /// Skyfaller.SpawnSetup regardless of which maker or mod produced it. Fresh
    /// spawns on a surface with an open sky gap above queue a lift; fresh spawns
    /// directly on a sky map over open air queue the downward leg. Respawns
    /// (load, our own transfers) and everything on non-column maps early out on
    /// static reads.
    /// </summary>
    [HarmonyPatch(typeof(Skyfaller), nameof(Skyfaller.SpawnSetup))]
    internal static class Patch_Skyfaller_Transit
    {
        private static void Postfix(Skyfaller __instance, Map map, bool respawningAfterLoad)
        {
            try
            {
                if (respawningAfterLoad || PodTransit.InTransfer || !LevelCensus.AnySkyLevels)
                {
                    return;
                }
                if (!ABGuard.On(ABGuard.Transit))
                {
                    return;
                }
                ABSettings settings = ABMod.Settings;
                if (settings == null || !settings.podTransit)
                {
                    return;
                }
                if (!PodTransit.IsTransitDef(__instance.def))
                {
                    return;
                }
                LevelComp comp = map.Levels();
                if (comp == null)
                {
                    return;
                }
                if (comp.level == 0)
                {
                    Map sky = comp.upperMap;
                    if (sky == null || sky.Disposed)
                    {
                        return;
                    }
                    if (__instance.ticksToImpact < PodTransit.MinTransitTicks)
                    {
                        return;
                    }
                    if (!PodTransit.GapOpen(sky, __instance.OccupiedRect()))
                    {
                        return;
                    }
                    map.GetComponent<PodTransitComp>()?.RegisterLift(__instance);
                }
                else if (comp.level == 1)
                {
                    if (comp.lowerMap == null || comp.lowerMap.Disposed)
                    {
                        return;
                    }
                    if (!PodTransit.GapOpen(map, __instance.OccupiedRect()))
                    {
                        return;
                    }
                    int at = Math.Max(1,
                        Math.Min(PodTransit.HandoffTicksToImpact, __instance.ticksToImpact / 2));
                    map.GetComponent<PodTransitComp>()?.RegisterDescent(__instance, at);
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Transit, e, "skyfaller transit spawn hook");
            }
        }
    }
}
