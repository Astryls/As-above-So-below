using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// A TRAP FOR THE "LINE GOES STRAIGHT DOWN" BUG. Diagnostic only, OFF by default.
    ///
    /// ⚠ BUILT BECAUSE THE SECOND GUESS WAS ALSO WRONG. "The line stops just above the stairs"
    /// pointed at the route preview; `AB2: route line dump` then showed that preview to be
    /// perfectly clean (one hop node on band 1, seven walk nodes on band 2, no vertical). Two
    /// inferences from a screenshot, two misses. Every line the mod draws goes through
    /// `GenDraw.DrawLineBetween`, so the honest move is to watch that funnel and let it name
    /// the caller.
    ///
    /// A segment whose |Δz| is close to a whole Slot cannot be a real walk - the gutter is
    /// impassable, so nothing legitimately steps a Slot in one segment. Any such segment is an
    /// endpoint that missed `ABUIGeometry.LiftToView`. The stack trace says whose.
    ///
    /// ⚠ OFF BY DEFAULT AND IT MUST STAY THAT WAY. This prefixes one of the hottest draw
    /// helpers in the game and captures a managed stack trace when it fires. `Log.WarningOnce`
    /// keyed on the caller signature keeps a real hit to one line per distinct origin rather
    /// than one per frame.
    /// </summary>
    public static class ABLineTrap
    {
        public static bool Enabled;

        public static int hits;

        /// <summary>How much of a Slot a vertical run has to cover before it is suspicious.
        /// Deliberately loose: a genuine long diagonal on a tall map could reach a fair z
        /// delta, but only an unlifted endpoint lands near a whole Slot.</summary>
        private const float SuspectFraction = 0.85f;

        internal static void Inspect(Vector3 a, Vector3 b)
        {
            if (!Enabled)
            {
                return;
            }
            Map map = Find.CurrentMap;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return;
            }
            float dz = Mathf.Abs(a.z - b.z);
            if (dz < bands.Slot * SuspectFraction)
            {
                return;
            }
            hits++;
            string trace = new System.Diagnostics.StackTrace(2, false).ToString();
            // Trim to the first few frames: the interesting caller is always near the top and
            // the full trace is enormous inside a draw stack.
            string[] lines = trace.Split('\n');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(ABLog.Tag + " VERTICAL LINE TRAPPED");
            sb.AppendLine("  a=" + a + " (band " + BandOfPos(map, a) + ")");
            sb.AppendLine("  b=" + b + " (band " + BandOfPos(map, b) + ")");
            sb.AppendLine("  dz=" + dz.ToString("0.0") + "  slot=" + bands.Slot
                + "  viewBand=" + ABBandView.CurrentBand(map));
            for (int i = 0; i < Math.Min(8, lines.Length); i++)
            {
                sb.AppendLine("    " + lines[i].TrimEnd());
            }
            // Keyed on the top frame so each distinct origin reports once, not once a frame.
            Log.WarningOnce(sb.ToString(),
                (lines.Length > 0 ? lines[0] : "none").GetHashCode() ^ 762195951);
        }

        private static int BandOfPos(Map map, Vector3 v)
        {
            IntVec3 c = v.ToIntVec3();
            return c.InBounds(map) ? ABBands.BandOf(map, c) : -1;
        }
    }

    [HarmonyPatch]
    public static class Patch_GenDraw_ABLineTrap
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo m in typeof(GenDraw).GetMethods(
                BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != nameof(GenDraw.DrawLineBetween))
                {
                    continue;
                }
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length >= 2 && ps[0].ParameterType == typeof(Vector3)
                    && ps[1].ParameterType == typeof(Vector3))
                {
                    yield return m;
                }
            }
        }

        private static void Prefix(Vector3 A, Vector3 B)
        {
            if (!ABLineTrap.Enabled)
            {
                return; // one static bool read on the hot path when disabled
            }
            try
            {
                ABLineTrap.Inspect(A, B);
            }
            catch
            {
                // A diagnostic must never break rendering.
            }
        }
    }

    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: trap vertical lines (toggle)",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ToggleLineTrap()
        {
            ABLineTrap.Enabled = !ABLineTrap.Enabled;
            Messages.Message("AB2: vertical line trap "
                + (ABLineTrap.Enabled ? "ON - reproduce the bad line now" : "OFF")
                + " (hits so far: " + ABLineTrap.hits + ")",
                MessageTypeDefOf.TaskCompletion, false);
        }
    }
}
