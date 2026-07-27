using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    public enum ABCompatState
    {
        /// <summary>Target mod not loaded.</summary>
        Absent,
        /// <summary>Target mod loaded; bridge detection ran.</summary>
        Present,
        /// <summary>Target loaded AND our bridge confirmed it hooked in.</summary>
        Active,
        /// <summary>Target loaded but our bridge could not attach.</summary>
        Failed
    }

    public sealed class ABCompatInfo
    {
        public readonly string packageId;
        public readonly string name;
        public ABCompatState state;
        public string note;

        public ABCompatInfo(string packageId, string name, ABCompatState state)
        {
            this.packageId = packageId;
            this.name = name;
            this.state = state;
        }
    }

    /// <summary>
    /// Central soft-compat registry (refactor R4). Every bridge routes its mod
    /// detection through here, so the whole compat surface is auditable in one
    /// place (Dev action "AB: list compat modules" + settings) instead of being
    /// scattered across ~30 bespoke static ctors.
    ///
    /// Detection stays per-bridge on purpose: different targets need different
    /// probes (packageId via <see cref="Detect"/>, type-presence / sub-bridge
    /// via <see cref="Note"/>), so a single rigid boot would fight the domain.
    /// What is standardized is the DECLARATION (one registry, one shape) and,
    /// for bridges that fit the common detect -> hook -> log flow,
    /// <see cref="Setup"/> — the go-forward boot helper.
    /// </summary>
    public static class ABCompat
    {
        private static readonly List<ABCompatInfo> modules = new List<ABCompatInfo>();

        public static IReadOnlyList<ABCompatInfo> Modules => modules;

        private static ABCompatInfo Find(string packageId)
        {
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i].packageId == packageId)
                {
                    return modules[i];
                }
            }
            return null;
        }

        private static ABCompatInfo Register(string packageId, string name, ABCompatState state)
        {
            ABCompatInfo info = Find(packageId);
            if (info == null)
            {
                info = new ABCompatInfo(packageId, name, state);
                modules.Add(info);
            }
            else if (info.state == ABCompatState.Absent || info.state == ABCompatState.Present)
            {
                // Do not downgrade a confirmed Active/Failed outcome back to a
                // bare detection result (e.g. a re-detect from a second probe).
                info.state = state;
            }
            return info;
        }

        /// <summary>Detect a target mod by packageId, record it, and return
        /// whether it is loaded. Drop-in for ABDetect.Active with a human name
        /// for the audit surface.</summary>
        public static bool Detect(string packageId, string name)
        {
            bool present = ABDetect.Active(packageId);
            Register(packageId, name, present ? ABCompatState.Present : ABCompatState.Absent);
            return present;
        }

        /// <summary>Record a target detected by a non-packageId probe (type
        /// presence, sub-bridge activation, etc.).</summary>
        public static void Note(string packageId, string name, bool present, string detail = null)
        {
            ABCompatInfo info = Register(packageId, name, present ? ABCompatState.Present : ABCompatState.Absent);
            if (detail != null)
            {
                info.note = detail;
            }
        }

        /// <summary>Mark a previously-detected bridge as successfully hooked.</summary>
        public static void MarkActive(string packageId)
        {
            ABCompatInfo info = Find(packageId);
            if (info != null && info.state != ABCompatState.Absent)
            {
                info.state = ABCompatState.Active;
            }
        }

        /// <summary>Mark a detected bridge as failed to hook (target present but
        /// our patch could not attach).</summary>
        public static void MarkFailed(string packageId, string reason)
        {
            ABCompatInfo info = Find(packageId);
            if (info != null)
            {
                info.state = ABCompatState.Failed;
                info.note = reason;
            }
        }

        /// <summary>Standardized boot for the common bridge shape: detect ->
        /// activate() -> log, with uniform try/catch and registry bookkeeping.
        /// Returns true when the bridge is live. The go-forward standard for new
        /// bridges; existing bespoke bridges route detection through
        /// <see cref="Detect"/>/<see cref="Note"/> and keep their own boot.</summary>
        public static bool Setup(string packageId, string name, Func<bool> activate)
        {
            if (!Detect(packageId, name))
            {
                return false;
            }
            try
            {
                if (activate())
                {
                    MarkActive(packageId);
                    ABLog.Dev(name + " compat enabled.");
                    return true;
                }
                MarkFailed(packageId, "activation returned false");
                return false;
            }
            catch (Exception e)
            {
                MarkFailed(packageId, e.Message);
                Log.Warning(ABLog.Tag + " " + name + " compat setup failed: " + e.Message);
                return false;
            }
        }
    }
}
