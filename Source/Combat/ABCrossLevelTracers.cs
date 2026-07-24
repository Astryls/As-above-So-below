using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Explicit cross-level shot tracers (the "option B" visual).
    ///
    /// A cross-gap bullet is a real projectile on the TARGET's map, but it is only
    /// visible from above where the sky cell directly over it is open air - so for a
    /// single-column hole it is an invisible blip and the shot never reads. Instead of
    /// leaning on that, every direct cross-level shot registers a short-lived tracer
    /// that is drawn spanning BOTH levels: from the shooter's position to the target's
    /// position, with the below endpoint pushed through the see-below transform. The
    /// bolt animates head-first along the line at a bullet-like pace, so the shot
    /// reads as a real plunging (or rising) round regardless of hole geometry.
    ///
    /// Physics/damage are unchanged - this is pure feedback. Tracers self-expire by
    /// wall-clock time (so they finish and clear even while the sim is paused), and
    /// the whole system early-outs to nothing when no cross-level shot is in flight.
    /// Direct fire only; arcing shells (mortars) lob and are shown via see-below.
    /// </summary>
    internal static class ABCrossLevelTracers
    {
        private struct Tracer
        {
            public Vector3 fromWorld;
            public Map fromMap;
            public Vector3 toWorld;
            public Map toMap;
            public float spawnTime;
            public float duration;
        }

        private const int MaxTracers = 96;
        private const float TrailFraction = 0.35f;

        private static readonly List<Tracer> tracers = new List<Tracer>();
        private static Material tracerMat;

        private static Material Mat => tracerMat != null
            ? tracerMat
            : (tracerMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(1f, 0.86f, 0.45f, 0.9f)));

        /// <summary>Register a tracer for one shot. Endpoints are frozen at fire time
        /// (a bullet's path does not move with the shooter). <paramref name="distance"/>
        /// is the gap-folded shot distance, used to pace the bolt.</summary>
        internal static void Add(Thing shooter, Thing target, float distance)
        {
            if (shooter == null || target == null || !shooter.Spawned || target.MapHeld == null)
            {
                return;
            }
            if (tracers.Count >= MaxTracers)
            {
                tracers.RemoveAt(0);
            }
            tracers.Add(new Tracer
            {
                fromWorld = shooter.DrawPos,
                fromMap = shooter.Map,
                toWorld = target.DrawPos,
                toMap = target.MapHeld,
                spawnTime = Time.realtimeSinceStartup,
                duration = Mathf.Clamp(distance / 55f, 0.05f, 0.28f)
            });
        }

        /// <summary>Per-frame draw for the viewed map. Called from
        /// LevelComp.MapComponentUpdate; empty-set early-out keeps idle cost near zero.</summary>
        internal static void Draw(Map cur)
        {
            if (tracers.Count == 0 || cur == null)
            {
                return;
            }
            Map below = cur.Levels()?.lowerMap;
            float now = Time.realtimeSinceStartup;
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            for (int i = tracers.Count - 1; i >= 0; i--)
            {
                Tracer t = tracers[i];
                float f = (now - t.spawnTime) / t.duration;
                if (f >= 1f || f < 0f)
                {
                    tracers.RemoveAt(i);
                    continue;
                }
                if (!ViewPos(t.fromMap, t.fromWorld, cur, below, out Vector3 a)
                    || !ViewPos(t.toMap, t.toWorld, cur, below, out Vector3 b))
                {
                    continue; // neither/both endpoints not visible from this level now
                }
                a.y = y;
                b.y = y;
                Vector3 head = Vector3.Lerp(a, b, f);
                Vector3 tail = Vector3.Lerp(a, b, Mathf.Max(0f, f - TrailFraction));
                if ((head - tail).sqrMagnitude < 0.0004f)
                {
                    continue; // degenerate on the very first frame(s)
                }
                GenDraw.DrawLineBetween(tail, head, Mat, 0.18f);
            }
        }

        /// <summary>Resolve a stored world position into the currently-viewed space:
        /// same level draws in place, the level below draws through the see-below
        /// transform, anything else is not visible this frame.</summary>
        private static bool ViewPos(Map map, Vector3 world, Map cur, Map below, out Vector3 pos)
        {
            if (map == cur)
            {
                pos = world;
                return true;
            }
            if (below != null && map == below)
            {
                pos = LevelRenderer.ShiftedBelowDrawPos(world);
                return true;
            }
            pos = default;
            return false;
        }

        internal static void ClearAll()
        {
            tracers.Clear();
        }
    }
}
