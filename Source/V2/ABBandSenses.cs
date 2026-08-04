using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AsAboveSoBelow
{
    /// <summary>
    /// WHAT YOU HEAR AND FEEL FROM ANOTHER LEVEL.
    ///
    /// After window 4c the SPRITES of a cross-level fight are real time: pawns, projectiles,
    /// flecks, shadows. What still made it feel like watching through glass was the other two
    /// senses:
    ///
    /// ⚠ SOUND WAS THE BIG ONE. Every one-shot - gunfire, impacts, deaths - plays anchored at
    /// its REAL map position, a whole Slot (128+) from the camera, and RimWorld attenuates by
    /// camera distance. A firefight one level down was therefore effectively SILENT, and
    /// silent combat never feels live regardless of what the pixels do.
    ///
    /// The fix is the same shape as every visual mirror: re-anchor at the PRODUCER, gated on
    /// the view. One prefix on SoundStarter.PlayOneShot; when the sound's cell sits on
    /// another band and its column is open to the view (the descent rule looking down,
    /// ABShaft.ColumnOpen looking up - the exact predicates the eyes already use), rebuild
    /// the SoundInfo anchored at the translated cell. What you can see, you can hear.
    ///
    /// ⚠ DELIBERATELY NOT COVERED, v1: SUSTAINERS (fire crackle, mortar hums) - they hold a
    /// live reference to their anchor and re-anchoring one mid-life is a different, stateful
    /// problem. And sounds through a SOLID floor stay vanilla-distant rather than "muffled":
    /// mirroring visible-only keeps one rule for every sense, and a muffle pass would want
    /// volume design, not just geometry.
    ///
    /// ⚠ THIS SITS ON A HOT PATH (every sound in the game). The common case must exit on
    /// int compares: not-on-camera check, current map check, band equality - before any
    /// terrain is touched. Same discipline as the fleck mirror (§39), same reason.
    /// </summary>
    public static class ABBandSenses
    {
        public static int soundsMoved;

        public static int shakesAdded;

        public static void ResetCounters()
        {
            soundsMoved = 0;
            shakesAdded = 0;
        }

        public static string CounterReport()
        {
            return "senses: soundsMoved=" + soundsMoved + " shakesAdded=" + shakesAdded;
        }

        /// <summary>Is a thing at <paramref name="cell"/> (band <paramref name="srcBand"/>)
        /// perceivable from <paramref name="viewBand"/>? The shared pair of rules: descent
        /// rule looking down, strict open column looking up.</summary>
        internal static bool PerceivableFrom(Map map, ABBandMap bands, IntVec3 cell,
            int srcBand, int viewBand, out int dz)
        {
            dz = (viewBand - srcBand) * bands.Slot;
            IntVec3 shown = new IntVec3(cell.x, cell.y, cell.z + dz);
            if (!shown.InBounds(map))
            {
                return false;
            }
            if (viewBand > srcBand)
            {
                return ABBands.TryResolveVisibleBelow(map, bands, shown, out IntVec3 below,
                    out int _) && below.x == cell.x && below.z == cell.z;
            }
            return !bands.InGutter(cell)
                && ABShaft.ColumnOpen(map, bands, cell, srcBand, viewBand);
        }
    }

    /// <summary>
    /// One-shot sounds, re-anchored into the viewed band when their source is perceivable
    /// through it. PlayOneShot is THE funnel: PlayOneShotOnCamera builds an on-camera
    /// SoundInfo (skipped by the first guard), and sustainers go through their own maker.
    /// </summary>
    [HarmonyPatch(typeof(SoundStarter), nameof(SoundStarter.PlayOneShot))]
    public static class Patch_SoundStarter_ABCrossBandAudio
    {
        private static void Prefix(SoundDef soundDef, ref SoundInfo info)
        {
            try
            {
                if (info.IsOnCamera || soundDef == null)
                {
                    return;
                }
                TargetInfo maker = info.Maker;
                if (!maker.IsValid)
                {
                    return;
                }
                Map map = maker.Map;
                if (map == null || map != Find.CurrentMap)
                {
                    return; // other maps are somebody else's soundscape
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                IntVec3 cell = maker.Cell;
                if (!cell.InBounds(map))
                {
                    return;
                }
                int srcBand = bands.BandOf(cell);
                int viewBand = ABBandView.CurrentBand(map);
                if (srcBand == viewBand)
                {
                    return; // the whole common case ends here
                }
                if (!ABBandSenses.PerceivableFrom(map, bands, cell, srcBand, viewBand,
                        out int dz))
                {
                    return; // behind a solid floor: stays vanilla-distant, not our problem
                }
                // Rebuild rather than mutate: SoundInfo's anchor is a get-only property, and
                // the struct carries its custom parameters in a private dictionary that only
                // SetParameter can reach.
                SoundInfo moved = SoundInfo.InMap(
                    new TargetInfo(new IntVec3(cell.x, cell.y, cell.z + dz), map),
                    info.Maintenance);
                moved.volumeFactor = info.volumeFactor;
                moved.pitchFactor = info.pitchFactor;
                moved.testPlay = info.testPlay;
                moved.forcedPlayOnCamera = info.forcedPlayOnCamera;
                foreach (KeyValuePair<string, float> p in info.DefinedParameters)
                {
                    moved.SetParameter(p.Key, p.Value);
                }
                info = moved;
                ABBandSenses.soundsMoved++;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 cross-band audio");
            }
        }
    }

    /// <summary>
    /// EXPLOSION SCREEN SHAKE. Vanilla computes it inline in DamageWorker.ExplosionStart as
    /// <c>4f * radius * screenShakeFactor / |explosionPos - cameraPos|</c> - a raw camera
    /// distance, so a boomalope detonating one level down (a Slot from the camera in world
    /// space) shakes nothing. §1's map-wide-scalar shape, in the vestibular system.
    ///
    /// A POSTFIX THAT ADDS rather than a rewrite: vanilla's own contribution at Slot range is
    /// ~0.1 and fades with distance exactly as it should for genuinely far explosions; ours
    /// adds the shake the TRANSLATED distance deserves when the blast is perceivable through
    /// the floor. No double-counting worth caring about, no transpiler.
    /// </summary>
    [HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionStart))]
    public static class Patch_DamageWorker_ABCrossBandShake
    {
        private static void Postfix(Explosion explosion)
        {
            try
            {
                if (explosion == null || !explosion.Spawned || !explosion.doVisualEffects)
                {
                    return;
                }
                Map map = explosion.Map;
                if (map == null || map != Find.CurrentMap)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded || !ABGuard.On(ABGuard.Rendering))
                {
                    return;
                }
                IntVec3 cell = explosion.Position;
                int srcBand = bands.BandOf(cell);
                int viewBand = ABBandView.CurrentBand(map);
                if (srcBand == viewBand)
                {
                    return; // vanilla already shook correctly
                }
                if (!ABBandSenses.PerceivableFrom(map, bands, cell, srcBand, viewBand,
                        out int dz))
                {
                    return;
                }
                Vector3 shown = new Vector3(cell.x + 0.5f, 0f, cell.z + dz + 0.5f);
                float magnitude = (shown - Find.Camera.transform.position).magnitude;
                if (magnitude < 1f)
                {
                    magnitude = 1f;
                }
                Find.CameraDriver.shaker.DoShake(
                    4f * explosion.radius * explosion.screenShakeFactor / magnitude);
                ABBandSenses.shakesAdded++;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Rendering, e, "V2 cross-band shake");
            }
        }
    }
}
