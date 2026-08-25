using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// ECCENTRIC DEFENSE GRID, MADE CROSS-LEVEL (§62 Data column).
    ///
    /// The grid is incremental, not rebuilt: `DefenseGridMapComponent.InitConduit` looks at
    /// a spawning conduit's cardinal neighbours, joins the network it touches, and calls a
    /// private `MergeNetworks(a, b)` when it touches two. That merge helper is CHEAP - a
    /// Things list append, a Tiles union and a cell-grid repoint - so the cross-level
    /// adapter simply calls it one more time: after OUR carrier initialises, if its partner
    /// one Slot away already has a network and it differs, the two merge exactly as if the
    /// carriers were adjacent.
    ///
    /// ⚠ ORDER COVERS ITSELF. On the mass path (map load: RegisterConduit(mass) then a
    /// dirty RegenGrid inits every conduit) and on the live path (toggle spawns own-cell
    /// then up-cell), one carrier of the pair always initialises second, sees the first's
    /// network, and performs the merge. A partner with a null network just means we are the
    /// first of the pair - skip, the second visit does the work.
    ///
    /// ⚠ DISCONNECT FORCES THE HOST'S OWN FULL REBUILD. `RemoveConduit`'s split logic
    /// refloods from CARDINAL neighbours only, so it cannot see the cross-band union it is
    /// standing in: despawning one carrier would strand band A and band B in one network
    /// object with no physical link - turrets upstairs would stay "connected" to a console
    /// downstairs until something else rebuilt the grid. The host already owns the answer:
    /// `dirty = true` triggers RegenGrid next tick, which rebuilds per-band components and
    /// re-merges only what still has carrier pairs. Deregister of one of OUR carriers sets
    /// exactly that flag.
    ///
    /// ⚠ NOT ONE FOREIGN TYPE IN ANY SIGNATURE. Everything resolves by name in Prepare and
    /// travels as `object` - the dev-palette scanner resolves every method signature in the
    /// assembly whether or not it is ever called.
    /// </summary>
    public static class EccentricGridCompat
    {
        internal static Type mapComp;
        internal static Type conduitComp;
        internal static FieldInfo fNetwork;   // AbstractDefenseComp/CompDefenseConduit.network
        internal static FieldInfo fDirty;     // DefenseGridMapComponent.dirty (public bool)
        internal static MethodInfo mergeNetworks; // private MergeNetworks(a, b)

        internal static int inits;
        internal static int merges;
        internal static int rebuildsForced;
        internal static string skip = "(none)";

        public static string CounterReport()
        {
            if (mapComp == null)
            {
                return "    EccentricGrid: (not installed)";
            }
            return "    EccentricGrid: inits=" + inits + " merges=" + merges
                + " forcedRebuilds=" + rebuildsForced + " | skip: " + skip;
        }

        internal static bool Resolve()
        {
            if (mapComp != null)
            {
                return true;
            }
            mapComp = AccessTools.TypeByName("EccentricDefenseGrid.DefenseGridMapComponent");
            conduitComp = AccessTools.TypeByName("EccentricDefenseGrid.CompDefenseConduit");
            if (mapComp == null || conduitComp == null)
            {
                return false;
            }
            fNetwork = AccessTools.Field(conduitComp, "network");
            fDirty = AccessTools.Field(mapComp, "dirty");
            mergeNetworks = AccessTools.Method(mapComp, "MergeNetworks");
            if (fNetwork == null || fDirty == null || mergeNetworks == null)
            {
                Log.Warning(ABLog.Tag + " Eccentric Defense Grid is present but its internals "
                    + "did not resolve; cross-level defense grid is disabled.");
                mapComp = null;
                return false;
            }
            return true;
        }

        /// <summary>Is this thing one of OUR generated Defense Grid carriers?</summary>
        internal static bool IsOurCarrier(Thing t)
        {
            ABCarrierExt ext = t?.def?.GetModExtension<ABCarrierExt>();
            return ext != null && ext.network == "Eccentric.DefenseGrid";
        }

        /// <summary>The conduit comp on a thing, found by runtime type - carriers clone the
        /// host's own comp list, so the comp is really there, just unnameable here.</summary>
        internal static object ConduitCompOf(Thing t)
        {
            if (!(t is ThingWithComps twc))
            {
                return null;
            }
            for (int i = 0; i < twc.AllComps.Count; i++)
            {
                if (conduitComp.IsInstanceOfType(twc.AllComps[i]))
                {
                    return twc.AllComps[i];
                }
            }
            return null;
        }
    }

    /// <summary>After a conduit joins the grid, bridge it to its cross-band partner.</summary>
    [HarmonyPatch]
    public static class Patch_EccentricGrid_ABInitConduit
    {
        private static bool Prepare()
        {
            return EccentricGridCompat.Resolve();
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(EccentricGridCompat.mapComp, "InitConduit");
        }

        /// <summary>⚠ The parameter MUST be named `conduit` - Harmony binds by name, and
        /// `InitConduit(CompDefenseConduit conduit)` is what the host called it.</summary>
        private static void Postfix(object __instance, object conduit)
        {
            if (!ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                Thing parent = (conduit as ThingComp)?.parent;
                if (parent == null || !EccentricGridCompat.IsOurCarrier(parent))
                {
                    return;
                }
                EccentricGridCompat.inits++;
                Map map = parent.Map;
                if (map == null || !ABBands.Banded(map))
                {
                    EccentricGridCompat.skip = "map null or unbanded";
                    return;
                }
                List<Thing> partners = new List<Thing>();
                ABColumnLink.AppendPartners(parent, partners);
                object mine = EccentricGridCompat.fNetwork.GetValue(conduit);
                if (mine == null)
                {
                    EccentricGridCompat.skip = "own network null after init - host changed?";
                    return;
                }
                for (int i = 0; i < partners.Count; i++)
                {
                    object partnerComp = EccentricGridCompat.ConduitCompOf(partners[i]);
                    if (partnerComp == null)
                    {
                        continue;
                    }
                    object theirs = EccentricGridCompat.fNetwork.GetValue(partnerComp);
                    if (theirs == null)
                    {
                        // We are the first of the pair to initialise; the partner's own
                        // InitConduit performs the merge when its turn comes.
                        EccentricGridCompat.skip = "partner not initialised yet - second visit merges";
                        continue;
                    }
                    if (ReferenceEquals(mine, theirs))
                    {
                        EccentricGridCompat.skip = "already the same network - nothing to merge";
                        continue;
                    }
                    EccentricGridCompat.mergeNetworks.Invoke(__instance, new[] { mine, theirs });
                    EccentricGridCompat.merges++;
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "Eccentric grid merge");
            }
        }
    }

    /// <summary>When one of OUR carriers leaves the grid, force the host's full rebuild -
    /// its cardinal-only split cannot dissolve a cross-band union on its own.</summary>
    [HarmonyPatch]
    public static class Patch_EccentricGrid_ABDeregister
    {
        private static bool Prepare()
        {
            return EccentricGridCompat.Resolve();
        }

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(EccentricGridCompat.mapComp, "DeregisterConduit");
        }

        private static void Postfix(object __instance, object conduit)
        {
            if (!ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                Thing parent = (conduit as ThingComp)?.parent;
                if (parent == null || !EccentricGridCompat.IsOurCarrier(parent))
                {
                    return;
                }
                EccentricGridCompat.fDirty.SetValue(__instance, true);
                EccentricGridCompat.rebuildsForced++;
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "Eccentric grid deregister");
            }
        }
    }
}
