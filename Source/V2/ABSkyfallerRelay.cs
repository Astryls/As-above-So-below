using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// DROP PODS FALL THROUGH THE LEVELS.
    ///
    /// TWO SEPARATE FAULTS, one visible symptom ("pods pop into existence just above the
    /// ground, and a pod bound for another level is never seen at all").
    ///
    /// 1. THE CURTAIN EATS THE FLIGHT. A skyfaller's DrawPos is its landing cell plus an
    ///    animation offset that starts tens of cells to the north and shrinks to zero at
    ///    impact - so for most of its flight the pod is drawn OUTSIDE its own band, over the
    ///    region the band curtain paints. AltitudeLayer.WorldClipper (the curtain, matching
    ///    vanilla's map-edge clipper) sorts ABOVE AltitudeLayer.Skyfaller, so the curtain
    ///    covers the pod until it drops inside the band. Nothing was broken about the pod;
    ///    it was being painted over.
    ///
    /// 2. A POD BOUND FOR ANOTHER LEVEL IS CULLED. DynamicDrawManager culls by the view
    ///    rect, which Patch_CameraDriver_ABClipViewToBand clips to the viewed band, so a pod
    ///    landing two levels down simply never draws. But a pod descending from orbit
    ///    physically PASSES THROUGH every level above its target, and that is what the
    ///    player expects to see.
    ///
    /// THE FIX IS ONE MECHANISM FOR BOTH: take the skyfaller's draw away from vanilla's
    /// per-thing pass and re-issue it ourselves, once, into the band the player is looking
    /// at. Because bands are aligned 1:1 the translation is a single z offset of
    /// (viewedBand - itsBand) * Slot - the same "one linear remapping instead of N patches"
    /// shape that replaced seven Dubs Mint Minimap patches with one rect.
    ///
    /// ALTITUDE IS CHOSEN PER FRAME, not once. While the pod is over its own level it keeps
    /// its normal altitude so the lighting overlay, weather and fog still sit on top of it
    /// exactly as vanilla intends; only when it is out over the curtain is it lifted above
    /// WorldClipper. Lifting it unconditionally would have made every landing render at full
    /// brightness through the night-time lighting overlay - a worse bug than the one being
    /// fixed, and one that only shows up after dark.
    ///
    /// PASS-THROUGH TIMING. A pod aimed n levels below the viewed one is shown for all but
    /// the last n * PassThroughTicksPerLevel ticks of its flight, so it visibly sinks out of
    /// the level instead of appearing to land on our floor and vanish. Outgoing skyfallers
    /// (reversed) get the mirror rule off ageTicks: they become visible on the levels above
    /// only once they have had time to rise past them.
    /// </summary>
    public static class ABSkyfallerRelay
    {
        /// <summary>How long a pod appears to spend crossing one level. Purely a feel
        /// number: large enough that the pod clears the screen edge before it would have
        /// touched our floor, small enough that a pod one level down is on screen for most
        /// of its descent.</summary>
        private const int PassThroughTicksPerLevel = 18;

        /// <summary>One layer above WorldClipper, which is where the band curtain and
        /// vanilla's map-edge clipper both draw. Anything at this altitude is over the
        /// curtain and under the meta overlays.</summary>
        private static readonly float OverCurtainAltitude = AltitudeLayer.Silhouettes.AltitudeFor();

        /// <summary>Live skyfallers per map. A ConditionalWeakTable keyed by the Map OBJECT,
        /// not by uniqueID: ids restart at zero every new game, and a static dictionary keyed
        /// by one is the exact leak that made wormholes and the viewed band bleed between
        /// colonies.</summary>
        private static readonly ConditionalWeakTable<Map, List<Skyfaller>> live =
            new ConditionalWeakTable<Map, List<Skyfaller>>();

        /// <summary>Set while the relay is issuing its own draw, so the suppression prefix
        /// lets that one call through. Without it the prefix would swallow our re-issue too
        /// and skyfallers would vanish entirely.</summary>
        internal static bool Relaying;

        /// <summary>True for skyfallers this file has taken responsibility for drawing.
        ///
        /// Skyfaller_FlyingPawn is excluded deliberately: it OVERRIDES DrawAt, so the
        /// suppression prefix on Skyfaller.DrawAt never runs for it, and relaying it too
        /// would draw it twice. Leaving it entirely to vanilla is consistent - it is a
        /// short-range hop within one level, not an arrival from orbit.</summary>
        internal static bool Handles(Skyfaller s)
        {
            return s != null && s.Spawned && !(s is Skyfaller_FlyingPawn)
                && ABBands.Banded(s.Map);
        }

        internal static void Register(Skyfaller s)
        {
            if (s == null || s.Map == null)
            {
                return;
            }
            List<Skyfaller> list = live.GetValue(s.Map, _ => new List<Skyfaller>());
            if (!list.Contains(s))
            {
                list.Add(s);
            }
        }

        public static void Draw(Map map)
        {
            if (map == null || !ABGuard.On(ABGuard.Rendering))
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            if (!live.TryGetValue(map, out List<Skyfaller> list) || list.Count == 0)
            {
                return;
            }
            int viewBand = ABBandView.CurrentBand(map);
            CellRect view = bands.RectOfBand(viewBand);
            int slot = bands.Slot;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                Skyfaller s = list[i];
                // Pruned lazily on the draw pass rather than hooked on despawn: a skyfaller
                // leaves by Destroy, DeSpawn, impact, or the map going away, and one liveness
                // test here covers all four without four more patches.
                if (s == null || s.Destroyed || !s.Spawned || s.Map != map)
                {
                    list.RemoveAt(i);
                    continue;
                }
                int levelsAbove = viewBand - bands.BandOf(s.Position);
                if (levelsAbove < 0)
                {
                    continue; // it lands above us; its flight never reaches this level
                }
                if (!PassingThrough(s, levelsAbove))
                {
                    continue;
                }
                try
                {
                    Vector3 pos = s.DrawPos;
                    pos.z += levelsAbove * slot;
                    if (pos.z < view.minZ || pos.z > view.maxZ + 1)
                    {
                        pos.y = OverCurtainAltitude;
                    }
                    Relaying = true;
                    s.DrawNowAt(pos);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(ABLog.Tag + " V2: skyfaller relay draw threw: " + e, 331880417);
                }
                finally
                {
                    Relaying = false;
                }
            }
        }

        /// <summary>Is this skyfaller still crossing the level the player is watching?
        /// Always true on its own level, where the answer must reduce exactly to vanilla's
        /// behaviour.</summary>
        private static bool PassingThrough(Skyfaller s, int levelsAbove)
        {
            if (levelsAbove == 0)
            {
                return true;
            }
            int budget = levelsAbove * PassThroughTicksPerLevel;
            if (s.def.skyfaller != null && s.def.skyfaller.reversed)
            {
                return s.ageTicks >= budget; // leaving: it has to climb to us first
            }
            return s.ticksToImpact > budget; // arriving: it sinks past us before it lands
        }
    }

    /// <summary>Registers every skyfaller as it arrives, including on load
    /// (respawningAfterLoad still runs SpawnSetup, so a pod saved mid-flight is picked up).
    /// Subclasses that override SpawnSetup all chain to base, so patching the base reaches
    /// the whole family.</summary>
    [HarmonyPatch(typeof(Skyfaller), nameof(Skyfaller.SpawnSetup))]
    public static class Patch_Skyfaller_ABRegister
    {
        private static void Postfix(Skyfaller __instance)
        {
            try
            {
                if (ABSkyfallerRelay.Handles(__instance))
                {
                    ABSkyfallerRelay.Register(__instance);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Takes the skyfaller's draw away from vanilla's per-thing pass on a banded map.
    ///
    /// Patched on Skyfaller.DrawAt rather than Thing.DynamicDrawPhaseAt on purpose: the
    /// latter is called for every dynamic thing on the map every frame, and this needs to
    /// cost nothing on a colony with three hundred pawns. DrawAt is reached only by
    /// skyfallers.
    ///
    /// Suppressing the whole body (not just the sprite) also parks the drop-spot shadow,
    /// which is correct - the relay's own call re-runs the same body, so the shadow is drawn
    /// exactly once, at the real landing cell, at its own low altitude. It therefore shows
    /// when the target level is the one on screen and stays hidden behind the curtain when
    /// it is not, which is precisely the cue wanted.
    ///
    /// The registration fallback matters: if a skyfaller somehow reached the map without
    /// passing SpawnSetup, suppressing its draw without the relay knowing about it would
    /// make it invisible. Anything vanilla tries to draw gets enrolled here first.
    /// </summary>
    [HarmonyPatch(typeof(Skyfaller), "DrawAt")]
    public static class Patch_Skyfaller_ABBandRelayDraw
    {
        private static bool Prefix(Skyfaller __instance)
        {
            try
            {
                if (ABSkyfallerRelay.Relaying)
                {
                    return true; // our own re-issue
                }
                if (!ABSkyfallerRelay.Handles(__instance))
                {
                    return true; // ordinary map: vanilla draws it as always
                }
                ABSkyfallerRelay.Register(__instance);
                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
