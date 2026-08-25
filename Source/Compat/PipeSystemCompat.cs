using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
            ABColumnLink.AppendPartners(thing, __result);
        }
    }

    /// <summary>
    /// §62.N - PIPESYSTEM RESOLVES PIPE ART THROUGH A STATIC def-&gt;Graphic REGISTRY, AND A
    /// DEF WE INVENTED AT RUNTIME IS NOT IN IT.
    ///
    /// `Building_Pipe.Graphic` is nothing but `LinkedPipes.GetPipeFor(def)`, an UNGUARDED
    /// `Dictionary&lt;ThingDef, Graphic_LinkedPipe&gt;` lookup. That dictionary is built once,
    /// in a [StaticConstructorOnStartup] class constructor, by walking every
    /// `PipeNetDef.pipeDefs` - the AUTHORED list of pipe defs. Our carriers are generated
    /// clones and appear in no authored list, so a carrier cloned from a VE pipe threw the
    /// moment it drew:
    ///     Exception printing AB_Carrier_VEF_VNPE_NutrientPasteNet2459388 at (90, 0, 293):
    ///     KeyNotFoundException: The given key 'AB_Carrier_VEF_VNPE_NutrientPasteNet' was
    ///     not present in the dictionary
    ///       at PipeSystem.LinkedPipes.GetPipeFor -&gt; PipeSystem.Building_Pipe.get_Graphic
    /// once per section regeneration, per enabled VE network, forever.
    ///
    /// ⚠ THE BUG WAS DORMANT UNTIL §62.J. While carriers were `DrawerType.None` nothing
    /// ever asked one for a Graphic. Making them visible - so a column reads as part of the
    /// grid in overlays, and the up-cell carrier IS the conduit on the floor above - turned
    /// every PipeSystem carrier into a draw call the host had no entry for. The UP-CELL
    /// carrier is the one that spams: §62.M suppresses the one under the column, and the
    /// cell above has no column standing in it.
    ///
    /// THE FIX IS ONE DICTIONARY WRITE PER CARRIER: point our carrier at the graphic the
    /// host ALREADY built for the template we cloned. That is not an approximation, it is
    /// literally the same object - RimWorld shares one Graphic across every thing of a def,
    /// and both behaviours of a pipe graphic take the thing as an argument rather than
    /// reading the def: `ShouldLinkWith` asks the map's PipeNetManager whether a net of
    /// that resource exists at the neighbouring cell (so a carrier links into the player's
    /// real pipe run, on either band), and `Print` is handed the thing to position from.
    ///
    /// ⚠ AND NOT THE OBVIOUS FIX. Adding the carrier to `PipeNetDef.pipeDefs` and letting
    /// the framework build the entry itself looks tidier and is wrong twice. That list is
    /// also the authority for `Designator_DeconstructPipe.CanDesignateThing`, so a carrier
    /// listed there becomes drag-deconstructible - on a thing that is `destroyable=false`,
    /// where `Destroy()` logs an error (§62.C). And their constructor reads
    /// `def.graphic.data.texPath` off every registered def from inside a foreign cctor we
    /// do not control: one implied def whose graphic is not resolved at that instant throws
    /// a TypeInitializationException that kills pipe art for EVERY net in the game, not
    /// just ours. Rule 14 - ask what is already at the destination before putting data
    /// there.
    ///
    /// ⚠ THE TIMING IS THE OTHER HALF, AND IT RULES OUT THE OBVIOUS HOOK TOO. The registry
    /// is only guaranteed full after `StaticConstructorOnStartupUtility.CallAll`, and we
    /// cannot postfix CallAll: HarmonyBoot is itself [StaticConstructorOnStartup], so our
    /// patches are installed BY that very call, already mid-flight, and a patch applied to
    /// a method on the stack does not affect the running invocation. `RunClassConstructor`
    /// is the answer - idempotent, and forcing it from inside CallAll runs it in exactly
    /// the phase it would have run in anyway (checked against PlayDataLoader.DoPlayLoad:
    /// def graphics resolve in an ExecuteWhenFinished callback queued well before the one
    /// that calls CallAll).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABPipeGraphics
    {
        private static int bound;

        private static int notPipeSystem;

        private static int already;

        private static bool hostPresent;

        private static string skip = "(not run)";

        static ABPipeGraphics()
        {
            Bind();
        }

        /// <summary>Re-checked once per game start as well as at startup. Idempotent by
        /// construction: a carrier already present in the host's registry is counted and
        /// left alone, never rewritten.</summary>
        [ABGameReset(60)]
        public static void Bind()
        {
            bound = 0;
            notPipeSystem = 0;
            already = 0;
            hostPresent = false;
            skip = "(none)";

            Type linked = AccessTools.TypeByName("PipeSystem.LinkedPipes");
            if (linked == null)
            {
                skip = "no VE pipe mod installed";
                return; // the normal case
            }
            hostPresent = true;

            try
            {
                // Idempotent, and a no-op if CallAll reached them before us.
                RuntimeHelpers.RunClassConstructor(linked.TypeHandle);
            }
            catch (Exception e)
            {
                skip = "PipeSystem's OWN registry build threw while we forced it (this is "
                    + "their static constructor, not our write): " + e.Message;
                Log.Warning(ABLog.Tag + " " + skip);
                return;
            }

            IDictionary dict = AccessTools.Field(linked, "pipesLinked")?.GetValue(null) as IDictionary;
            if (dict == null)
            {
                skip = "PipeSystem.LinkedPipes has no readable 'pipesLinked' dictionary; the "
                    + "framework's internals have changed. VE pipe carriers will draw nothing "
                    + "and log KeyNotFoundException once per section regeneration.";
                Log.Warning(ABLog.Tag + " " + skip);
                return;
            }

            List<ABNetwork> nets = ABColumnNetworks.All;
            int psNets = 0;
            for (int i = 0; i < nets.Count; i++)
            {
                ABNetwork n = nets[i];
                if (n.probe == "PipeSystem")
                {
                    psNets++;
                }
                if (n.carrier == null || n.template == null)
                {
                    continue;
                }
                // ⚠ SELF-SELECTING, DELIBERATELY. "Is the template a key in THEIR registry"
                // is a better test than "did the PipeSystem probe find this network": it is
                // the host's own answer, and it picks up an adapter-def network whose
                // template happens to be a PipeSystem pipe without knowing that it is one.
                // ⚠ The object-keyed indexer on Dictionary<K,V> RETURNS NULL for a missing
                // or wrong-typed key rather than throwing - unlike the generic one that
                // produced this bug in the first place.
                if (dict[n.carrier] != null)
                {
                    already++;
                    continue;
                }
                object hostGraphic = dict[n.template];
                if (hostGraphic == null)
                {
                    notPipeSystem++; // vanilla power, Dubwise, RimIOT, Eccentric
                    continue;
                }
                try
                {
                    dict[n.carrier] = hostGraphic;
                    bound++;
                }
                catch (Exception e)
                {
                    skip = "writing " + n.carrier.defName + " into the host registry threw: " + e.Message;
                }
            }

            // ⚠ RULE 33. This filter can reject everything, so it has to say so. "0 bound"
            // with PipeSystem loaded and VE networks detected is a CONTRADICTION, not a
            // clean run, and the symptom it hides is exactly the KeyNotFound spam this
            // class exists to stop.
            if (bound == 0 && already == 0 && psNets > 0)
            {
                skip = psNets + " PipeSystem network(s) were detected, but not one of their "
                    + "templates is a key in the host's own registry - carriers will draw "
                    + "nothing and log KeyNotFoundException. Template pick or registry shape "
                    + "has changed.";
                Log.Warning(ABLog.Tag + " " + skip);
            }
            else if (psNets == 0)
            {
                skip = "PipeSystem is loaded but no VE network reached carrier generation";
            }

            ABLog.Dev("PipeSystem art binding: " + bound + " carrier(s) bound to host pipe "
                + "art, " + already + " already bound, " + notPipeSystem + " non-PipeSystem "
                + "carrier(s) skipped.");
        }

        /// <summary>Rule 15: assert always, narrate on request.</summary>
        public static string CounterReport()
        {
            if (!hostPresent)
            {
                return "    (no VE pipe mod installed)";
            }
            StringBuilder sb = new StringBuilder();
            sb.Append("    PipeSystem: bound=" + bound + " already=" + already
                + " skippedNonVE=" + notPipeSystem + " | skip: " + skip);
            return sb.ToString();
        }
    }
}
