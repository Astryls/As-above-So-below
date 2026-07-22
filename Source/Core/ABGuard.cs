using System;
using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>A single kill switch. A plain object with a bool so the hot-path
    /// gate is one field read; the old string constants hashed into a HashSet on
    /// every ABGuard.On call, which added a string-hash probe to every patched
    /// vanilla getter.</summary>
    public sealed class ABGuardSwitch
    {
        internal readonly string name;
        internal bool on = true;

        internal ABGuardSwitch(string name)
        {
            this.name = name;
        }
    }

    /// <summary>
    /// Per-subsystem kill switches. When a subsystem throws, we log ONCE with context,
    /// then shut that subsystem down so it cannot error-spam or break vanilla.
    /// Harmony prefixes gated on these must fail open (return true, vanilla runs).
    /// Call sites are unchanged from the string era: ABGuard.On(ABGuard.Ui) etc.
    /// </summary>
    public static class ABGuard
    {
        public static readonly ABGuardSwitch Ui = new ABGuardSwitch("ui");
        public static readonly ABGuardSwitch LevelGen = new ABGuardSwitch("levelGen");
        public static readonly ABGuardSwitch Rendering = new ABGuardSwitch("rendering");
        public static readonly ABGuardSwitch Movement = new ABGuardSwitch("movement");
        public static readonly ABGuardSwitch Combat = new ABGuardSwitch("combat");
        public static readonly ABGuardSwitch Logistics = new ABGuardSwitch("logistics");
        public static readonly ABGuardSwitch RoofSync = new ABGuardSwitch("roofSync");
        public static readonly ABGuardSwitch Weather = new ABGuardSwitch("weather");
        public static readonly ABGuardSwitch Power = new ABGuardSwitch("power");
        public static readonly ABGuardSwitch Pipes = new ABGuardSwitch("pipes");
        public static readonly ABGuardSwitch Climate = new ABGuardSwitch("climate");
        public static readonly ABGuardSwitch Threats = new ABGuardSwitch("threats");
        public static readonly ABGuardSwitch HostileMove = new ABGuardSwitch("hostileMove");
        public static readonly ABGuardSwitch World = new ABGuardSwitch("world");
        public static readonly ABGuardSwitch Social = new ABGuardSwitch("social");
        public static readonly ABGuardSwitch Transit = new ABGuardSwitch("transit");

        /// <summary>Background-thread compute lanes (see-below mask build).
        /// Tripping this falls back to synchronous rebuilds, never off.</summary>
        public static readonly ABGuardSwitch Async = new ABGuardSwitch("async");

        private static readonly ABGuardSwitch[] All =
        {
            Ui, LevelGen, Rendering, Movement, Combat, Logistics, RoofSync, Weather, Power, Pipes, Climate,
            Threats, HostileMove, World, Social, Transit, Async
        };

        public static bool On(ABGuardSwitch subsystem) => subsystem.on;

        public static void Disable(ABGuardSwitch subsystem, Exception e, string context)
        {
            if (subsystem.on)
            {
                subsystem.on = false;
                Log.Error(ABLog.Tag + " Subsystem '" + subsystem.name + "' hit an error in " + context
                    + " and shut itself down to protect your game. Other features keep running. Details: " + e);
            }
        }

        /// <summary>Reset all kill switches. Called when a game is loaded or started.</summary>
        public static void Reset()
        {
            int cleared = 0;
            for (int i = 0; i < All.Length; i++)
            {
                if (!All[i].on)
                {
                    All[i].on = true;
                    cleared++;
                }
            }
            if (cleared > 0)
            {
                ABLog.Dev("Cleared " + cleared + " tripped kill switch(es).");
            }
        }
    }
}
