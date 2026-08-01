using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// VANILLA EXPANDED FRAMEWORK'S PipeSystem, MADE CROSS-LEVEL BY ONE POSTFIX.
    ///
    /// ⚠ THREE MODS FOR THE PRICE OF ONE. Vanilla Chemfuel Expanded, Vanilla Temperature
    /// Expanded and Ushanka's Luciferium Expansion all ship thin content over this one
    /// framework - VCHE.dll contains four classes and no network code whatsoever. Patching
    /// the framework rather than its consumers covers all three, plus every future VE pipe
    /// mod, and means we never name a consumer's type.
    ///
    /// ⚠ AND `NeighbourThingsCardinal` IS THE ONE FUNNEL. `RegisterConnector`,
    /// `UnregisterConnector` and `CreatePipeNetFrom` are the only three things that walk the
    /// graph, and all three ask this private method what is adjacent. Appending the
    /// cross-level partner to its answer therefore reaches net creation, net merging on
    /// build, and net splitting on deconstruct, with no further patches. This is the
    /// "find the ONE method everything funnels through" rule paying off completely.
    ///
    /// ⚠ NOT ONE FOREIGN TYPE APPEARS IN THIS FILE. `PipeSystem.PipeNetManager` is resolved
    /// by name in Prepare/TargetMethod and `__instance` is not taken at all; the postfix
    /// deals only in `Thing` and `List&lt;Thing&gt;`, which are vanilla. Naming the type in a
    /// signature would make Harmony's class processor resolve it at patch time and log a
    /// scary "could not resolve type" for every player who does not own a VE pipe mod.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_PipeSystem_ABRiserLink
    {
        private static MethodBase target;

        /// <summary>Resolved once. Returning false leaves the patch uninstalled, which is
        /// the correct behaviour when no VE pipe mod is present.</summary>
        private static bool Prepare()
        {
            if (target != null)
            {
                return true;
            }
            Type manager = AccessTools.TypeByName("PipeSystem.PipeNetManager");
            if (manager == null)
            {
                return false;
            }
            target = AccessTools.Method(manager, "NeighbourThingsCardinal", new[] { typeof(Thing) });
            if (target == null)
            {
                Log.Warning(ABLog.Tag + " PipeSystem found but NeighbourThingsCardinal did not resolve; "
                    + "cross-level VE pipes are disabled. The framework's internals may have changed.");
                return false;
            }
            return true;
        }

        private static MethodBase TargetMethod()
        {
            return target;
        }

        /// <summary>
        /// ⚠ THE PARAMETER IS NAMED `thing` AND THAT IS NOT NEGOTIABLE. Harmony binds
        /// postfix parameters to the original's by NAME, so calling it `t` throws at
        /// PatchAll time and takes every other patch in the assembly down with it.
        ///
        /// `__result` is a freshly built `List&lt;Thing&gt;` that the caller then scans for
        /// CompResource, so mutating it in place is safe and is the whole patch: our
        /// junction and breaker both carry a PipeSystem resource comp, so a partner added
        /// here is discovered exactly as a physically adjacent pipe would be. Distance is
        /// never checked by the framework, which is why a partner three hundred cells away
        /// in +z is accepted without complaint.
        /// </summary>
        private static void Postfix(Thing thing, List<Thing> __result)
        {
            ABRiserLink.AppendPartners(thing, __result);
        }
    }
}
