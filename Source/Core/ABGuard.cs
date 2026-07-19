using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Per-subsystem kill switches. When a subsystem throws, we log ONCE with context,
    /// then shut that subsystem down so it cannot error-spam or break vanilla.
    /// Harmony prefixes gated on these must fail open (return true, vanilla runs).
    /// </summary>
    public static class ABGuard
    {
        public const string Ui = "ui";
        public const string LevelGen = "levelGen";
        public const string Rendering = "rendering";
        public const string Movement = "movement";
        public const string Logistics = "logistics";
        public const string RoofSync = "roofSync";
        public const string Weather = "weather";
        public const string Power = "power";
        public const string Pipes = "pipes";

        private static readonly HashSet<string> disabled = new HashSet<string>();

        public static bool On(string subsystem) => !disabled.Contains(subsystem);

        public static void Disable(string subsystem, Exception e, string context)
        {
            if (disabled.Add(subsystem))
            {
                Log.Error(ABLog.Tag + " Subsystem '" + subsystem + "' hit an error in " + context
                    + " and shut itself down to protect your game. Other features keep running. Details: " + e);
            }
        }

        /// <summary>Reset all kill switches. Called when a game is loaded or started.</summary>
        public static void Reset()
        {
            if (disabled.Count > 0)
            {
                ABLog.Dev("Cleared " + disabled.Count + " tripped kill switch(es).");
                disabled.Clear();
            }
        }

        /// <summary>Cold-path helper. Hot paths should hand-write try/catch to avoid closures.</summary>
        public static void Run(string subsystem, string context, Action action)
        {
            if (!On(subsystem))
            {
                return;
            }
            try
            {
                action();
            }
            catch (Exception e)
            {
                Disable(subsystem, e, context);
            }
        }
    }
}
