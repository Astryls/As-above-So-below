using System;
using System.Collections.Generic;
using RimWorld;
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
        internal string lastContext;
        internal string lastCulprit;

        internal ABGuardSwitch(string name)
        {
            this.name = name;
        }

        // Settings-panel readouts (visibility of system status): the guards
        // were invisible without log diving before the 2026-07-22 rework.
        public string Name => name;

        public bool IsOn => on;

        public string LastContext => lastContext;

        /// <summary>What tripped the switch - the failing item/mod/pawn named
        /// by ABBlame, or null when nothing specific could be attributed.</summary>
        public string LastCulprit => lastCulprit;
    }

    /// <summary>
    /// Per-subsystem kill switches. When a subsystem throws, we log ONCE with context,
    /// then shut that subsystem down so it cannot error-spam or break vanilla.
    /// Harmony prefixes gated on these must fail open (return true, vanilla runs).
    /// Call sites are unchanged from the string era: ABGuard.On(ABGuard.Ui) etc.
    /// </summary>
    public static class ABGuard
    {
        // ⚠ EVERY SWITCH IN THIS LIST IS REFERENCED BY SOMETHING. Twelve more used to live
        // here - logistics, power, pipes, climate, threats, hostileMove, world, social,
        // areas, combatAI, async and an unused RoofSync-era duplicate - carried over from V1
        // and referenced by nothing in V2. They were not harmless: AllSwitches feeds the
        // settings panel's status readout, so the player was shown twelve subsystems that
        // could never change state, which makes the readout useless as a diagnostic exactly
        // when it matters. A kill switch with no call site is not a safety net, it is noise.
        // Do not add one speculatively; add it in the same commit as its guard.
        public static readonly ABGuardSwitch Ui = new ABGuardSwitch("ui");
        public static readonly ABGuardSwitch LevelGen = new ABGuardSwitch("levelGen");
        public static readonly ABGuardSwitch Rendering = new ABGuardSwitch("rendering");
        public static readonly ABGuardSwitch Movement = new ABGuardSwitch("movement");
        public static readonly ABGuardSwitch Combat = new ABGuardSwitch("combat");
        public static readonly ABGuardSwitch RoofSync = new ABGuardSwitch("roofSync");
        public static readonly ABGuardSwitch Weather = new ABGuardSwitch("weather");

        /// <summary>Cross-band transit: the synthetic RegionLink re-arm and the segmented
        /// pather. Re-arm runs from MapEvents.RegionsRoomsChanged - i.e. after every region
        /// rebuild - so an unguarded fault there is an error PER REBUILD. Tripping it stops
        /// the stairs conducting and says so once, which beats a log full of the same line.</summary>
        public static readonly ABGuardSwitch Transit = new ABGuardSwitch("transit");

        /// <summary>Band camera: the per-frame clamp and the level-switch input. Both sit on
        /// paths that run every frame / every GUI event, which is precisely where an
        /// unthrottled error handler turns one bug into an unplayable game.</summary>
        public static readonly ABGuardSwitch Camera = new ABGuardSwitch("camera");

        private static readonly ABGuardSwitch[] All =
        {
            Ui, LevelGen, Rendering, Movement, Combat, RoofSync, Weather, Transit, Camera
        };

        public static bool On(ABGuardSwitch subsystem) => subsystem.on;

        /// <summary>Trip a subsystem's kill switch after an error. Pass a
        /// <paramref name="subject"/> (the Thing/Def/Pawn the failing code was
        /// working on) whenever the call site knows it, so the log line AND the
        /// in-game message name the culprit - a modded mech that could not
        /// charge, an item that would not store, etc. When no subject is given,
        /// the exception stack is mined for a third-party mod instead.</summary>
        public static void Disable(ABGuardSwitch subsystem, Exception e, string context, object subject = null)
        {
            if (subsystem.on)
            {
                subsystem.on = false;
                subsystem.lastContext = context;
                // Attribution runs inside the handler and never throws (ABBlame
                // swallows its own faults), so a failed blame can't compound the
                // original error.
                string culprit = ABBlame.Cause(subject, e);
                subsystem.lastCulprit = culprit;
                string blame = culprit != null ? " Likely cause: " + culprit + "." : string.Empty;
                Log.Error(ABLog.Tag + " Subsystem '" + subsystem.name + "' hit an error in " + context
                    + " and shut itself down to protect your game. Other features keep running." + blame
                    + " Details: " + e);
                // A tripped switch used to be invisible outside the dev log,
                // and players read the resulting silence (no cross-level
                // hauling, no column stuff picker) as separate bugs. One
                // non-historical message makes the shutdown visible in-game,
                // now naming what tripped it when we can.
                try
                {
                    if (Current.ProgramState == ProgramState.Playing)
                    {
                        TaggedString msg = culprit != null
                            ? "AB_SubsystemDownBecause".Translate(subsystem.name, culprit)
                            : "AB_SubsystemDown".Translate(subsystem.name);
                        Messages.Message(msg, MessageTypeDefOf.CautionInput, historical: false);
                    }
                }
                catch
                {
                    // Messaging must never compound the original failure.
                }
            }
        }

        /// <summary>Every switch, for the settings panel's status readout.</summary>
        public static ABGuardSwitch[] AllSwitches => All;

        /// <summary>Re-arm one tripped switch from the settings panel. The
        /// subsystem gets another chance; if the fault persists it trips
        /// again on the next error with a fresh log line.</summary>
        public static void ReArm(ABGuardSwitch subsystem)
        {
            if (!subsystem.on)
            {
                subsystem.on = true;
                subsystem.lastContext = null;
                subsystem.lastCulprit = null;
                ABLog.Dev("Kill switch '" + subsystem.name + "' re-armed from settings.");
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
                    All[i].lastContext = null;
                    All[i].lastCulprit = null;
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
