using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE DUBWISE PIPE FAMILY, MADE CROSS-LEVEL: Bad Hygiene, Rimefeller and Rimatomics.
    ///
    /// All three ship the SAME code in three assemblies - `RebuildPipeGrid(int P)` clears
    /// the grid, collects every `CompPipe` of that PipeType into a cell dictionary, and
    /// flood-fills one net per connected component. Only the TYPES differ
    /// (`PlumbingNet` / `PipelineNet` / Rimatomics' `BasePipeNet` subclasses), so this is
    /// one patch driven entirely by reflection with `object` in every signature.
    ///
    /// ⚠ THE FLOOD ITSELF CANNOT BE PATCHED. `PassCheck` is a local-function closure inside
    /// `RebuildPipeGrid`; there is no method to hook and no way to widen its notion of
    /// adjacency. So we let the rebuild finish and MERGE the resulting nets afterwards,
    /// which is exactly what Dubwise themselves do across maps in
    /// `HygienePipeMapComp.RefreshInternetsOnTile`. Following the author's own recipe beats
    /// inventing one.
    ///
    /// ⚠ MERGE BY REMOVAL, NOT BY SHARING. RefreshInternetsOnTile gives every net in a group
    /// the same `PipedThings` and then relies on a `slave` flag so only one of them runs its
    /// tick logic - but `slave` exists only on Bad Hygiene's `PlumbingNet`. Rimefeller and
    /// Rimatomics have no such flag, so two nets sharing one member list would BOTH tick and
    /// silently double every pull, push and production rate. Instead the loser is deleted
    /// from the map component's `PipeNets` array and its pipes are repointed at the winner,
    /// which leaves exactly one net ticking on every family.
    ///
    /// ⚠ TOGGLING NEEDS NOTHING FROM US (rule 27). A column connects or disconnects a
    /// network by SPAWNING or DESPAWNING its carrier, and every Dubwise `CompPipe` dirties
    /// and regenerates its grid from PostSpawnSetup and PostDeSpawn unconditionally. The
    /// riser era's flick plumbing (CompABRiserSwitch + PokeRebuild) existed because
    /// Rimefeller and Rimatomics gate their flick handling on `Building_Valve`; spawn and
    /// despawn have no such gate, so that whole seam is deleted.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_DubsPipes_ABRiserLink
    {
        /// <summary>Reflection handles for one host mod, resolved once per map-component
        /// type. Null entries mean "this family is not installed", which is the normal case
        /// for two of the three.</summary>
        private sealed class Family
        {
            public FieldInfo pipeNets;      // Net[] on the map component
            public Type compPipe;           // <Mod>.CompPipe
            public FieldInfo pipeNetRef;    // Net on CompPipe
            public FieldInfo pipedThings;   // ICollection on Net
            public MethodInfo initNet;      // Net.InitNet()
            public MethodInfo addThing;     // PipedThings.Add(T), resolved on first use
            public MethodInfo dirtyAll;     // <MapComp>.DirtyAllPipeGrids()
            public MethodInfo regen;        // <MapComp>.RegenPipeGrids()
            public string prefix;           // our network-id prefix, e.g. "DBH."
            public string name;             // for the report
            public int rebuilds, joins, drops;
            public string skip = "(none)";
        }

        private static readonly Dictionary<Type, Family> families = new Dictionary<Type, Family>();

        /// <summary>
        /// Instrumentation, PER HOST rather than global.
        ///
        /// ⚠ ONE SHARED COUNTER HIDES A BROKEN FAMILY. A working Bad Hygiene merge makes the
        /// totals look healthy while a Rimefeller one that never fires is invisible - which
        /// is exactly the state that produced "water works, oil does not". Three hosts,
        /// three rows.
        /// </summary>
        public static string CounterReport()
        {
            if (families.Count == 0)
            {
                return "    (no Dubwise host installed)";
            }
            StringBuilder sb = new StringBuilder();
            foreach (Family f in families.Values)
            {
                sb.AppendLine("    " + f.name + ": rebuilds=" + f.rebuilds
                    + " joins=" + f.joins + " dropped=" + f.drops + " | skip: " + f.skip);
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>A short identity for the net a riser currently sits on, for the report.
        /// Two ends showing the SAME id means the merge worked and the fault is downstream;
        /// different ids means the merge did not take.</summary>
        public static string DescribeNet(Thing t)
        {
            try
            {
                foreach (Family fam in families.Values)
                {
                    object net = NetOf(fam, t);
                    if (net == null)
                    {
                        continue;
                    }
                    return net.GetType().Name + "#" + net.GetHashCode()
                        + " things=" + CountOf(fam.pipedThings.GetValue(net));
                }
            }
            catch
            {
                // diagnostics must never be the thing that throws
            }
            return null;
        }

        /// <summary>
        /// ⚠ THE NETWORK-ID PREFIX IS DERIVED, NEVER HAND-WRITTEN. The riser era authored
        /// ids in XML as "DBH.Sewage", so this table carried the literal "DBH." - and the
        /// moment §62's probe started BUILDING ids from the host namespace
        /// ("DubsBadHygiene.Sewage"), that literal matched nothing at all. Every Bad
        /// Hygiene merge was skipped by the filter before it could record a decline, so the
        /// field report read `joins=0 | skip: (none)`: no work done and no reason given.
        /// Deriving the prefix from the same namespace the probe uses makes the two
        /// physically incapable of drifting apart again.
        /// </summary>
        private static readonly (string comp, string net)[] Hosts =
        {
            ("DubsBadHygiene.HygienePipeMapComp", "DubsBadHygiene.PlumbingNet"),
            ("Rimefeller.MapComponent_Rimefeller", "Rimefeller.PipelineNet"),
            ("Rimatomics.MapComponent_Rimatomics", "Rimatomics.BasePipeNet")
        };

        private static bool Prepare()
        {
            return TargetsInternal().Count > 0;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            return TargetsInternal();
        }

        /// <summary>One target per installed host. `RebuildPipeGrid` is declared on the
        /// shared `UniversalPipeMapComp` base for Rimefeller and Rimatomics and directly on
        /// `HygienePipeMapComp` for Bad Hygiene, so it is resolved by walking up rather than
        /// with DeclaredMethod.</summary>
        private static List<MethodBase> TargetsInternal()
        {
            List<MethodBase> found = new List<MethodBase>();
            foreach ((string compName, string netName) in Hosts)
            {
                Type mapComp = AccessTools.TypeByName(compName);
                Type net = AccessTools.TypeByName(netName);
                if (mapComp == null || net == null)
                {
                    continue;
                }
                MethodInfo rebuild = AccessTools.Method(mapComp, "RebuildPipeGrid", new[] { typeof(int) });
                Type comp = AccessTools.TypeByName(compName.Split('.')[0] + ".CompPipe");
                FieldInfo nets = AccessTools.Field(mapComp, "PipeNets");
                FieldInfo netRef = comp != null ? AccessTools.Field(comp, "pipeNetRef") : null;
                FieldInfo things = AccessTools.Field(net, "PipedThings");
                MethodInfo init = AccessTools.Method(net, "InitNet");
                if (rebuild == null || nets == null || netRef == null || things == null || init == null)
                {
                    Log.Warning(ABLog.Tag + " " + compName + " is present but its pipe internals did not "
                        + "resolve; cross-level pipes are disabled for it.");
                    continue;
                }
                families[mapComp] = new Family
                {
                    pipeNets = nets, compPipe = comp, pipeNetRef = netRef,
                    pipedThings = things, initNet = init,
                    prefix = compName.Split('.')[0] + ".",
                    name = compName.Split('.')[0],
                    dirtyAll = AccessTools.Method(mapComp, "DirtyAllPipeGrids"),
                    regen = AccessTools.Method(mapComp, "RegenPipeGrids")
                };
                found.Add(rebuild);
            }
            return found;
        }

        /// <summary>⚠ The parameter MUST be named `P` - Harmony binds by name, and
        /// `RebuildPipeGrid(int P)` is what Dubwise called it.</summary>
        private static void Postfix(object __instance, int P)
        {
            if (__instance == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                Family fam = null;
                for (Type t = __instance.GetType(); t != null && fam == null; t = t.BaseType)
                {
                    families.TryGetValue(t, out fam);
                }
                Map map = (__instance as MapComponent)?.map;
                if (fam == null)
                {
                    return;
                }
                fam.rebuilds++;
                if (map == null || !ABBands.Banded(map))
                {
                    fam.skip = "map null or unbanded";
                    return;
                }
                MergeFor(map, fam, __instance);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "Dubwise pipe riser merge");
            }
        }

        private static readonly List<Thing> partners = new List<Thing>();

        private static void MergeFor(Map map, Family fam, object holder)
        {
            // Both ends of a link are the SAME carrier def now, so this walks every pair
            // twice - once from each end. Harmless: the second visit lands in Join's
            // "already the same net" early-out.
            List<ABNetwork> nets = ABColumnNetworks.All;
            int matched = 0;
            int carriers = 0;
            for (int i = 0; i < nets.Count; i++)
            {
                ABNetwork n = nets[i];
                if (n.carrier == null || n.id == null || !n.id.StartsWith(fam.prefix))
                {
                    continue;
                }
                matched++;
                List<Thing> here = map.listerThings.ThingsOfDef(n.carrier);
                carriers += here.Count;
                for (int j = 0; j < here.Count; j++)
                {
                    partners.Clear();
                    ABColumnLink.AppendPartners(here[j], partners);
                    for (int k = 0; k < partners.Count; k++)
                    {
                        Join(fam, holder, here[j], partners[k]);
                    }
                }
            }
            // ⚠ RULE 31, ADDED THE HARD WAY. Without these two lines the prefix mismatch
            // above was INVISIBLE: the filter rejected every network, the loop never
            // reached a decline point, and the report showed `joins=0 | skip: (none)` -
            // no work and no reason. A filter that can reject everything must say so.
            if (matched == 0)
            {
                fam.skip = "NO NETWORK ID STARTS WITH '" + fam.prefix
                    + "' - prefix/probe-id mismatch, nothing was even considered";
            }
            else if (carriers == 0)
            {
                fam.skip = matched + " network(s) matched the prefix, but no carrier of "
                    + "theirs is spawned on this map (no column, or its toggle is off)";
            }
        }

        /// <summary>Fold the partner's net into the anchor's, then delete the loser.
        ///
        /// ⚠ THE HOLDER IS `__instance`, NOT A LOOKUP. An earlier version re-found the map
        /// component from `pipeNets.DeclaringType` - which is the ABSTRACT
        /// `UniversalPipeMapComp` base for Rimefeller and Rimatomics, so `GetComponent` on it
        /// could miss and the losing net was never removed from the array. The component that
        /// just rebuilt its own grid is sitting right there in `__instance`; use it.</summary>
        private static void Join(Family fam, object holder, Thing anchor, Thing partner)
        {
            fam.joins++;
            object keep = NetOf(fam, anchor);
            object drop = NetOf(fam, partner);
            if (keep == null || drop == null)
            {
                fam.skip = "net was null (keep=" + (keep != null) + " drop=" + (drop != null)
                    + ") - the riser's CompPipe has no pipeNetRef yet";
                return;
            }
            if (ReferenceEquals(keep, drop))
            {
                fam.skip = "already the same net - nothing to merge";
                return;
            }
            object keepThings = fam.pipedThings.GetValue(keep);
            // ⚠ DO NOT CAST TO NON-GENERIC ICollection. `PipedThings` is a
            // HashSet<ThingWithComps>, and HashSet<T> implements ICollection<T> but NOT the
            // non-generic System.Collections.ICollection - unlike List<T>, which does both.
            // An `is ICollection` guard therefore fails on every single call and the merge
            // silently never runs. Only IEnumerable is safe to lean on here.
            if (keepThings == null || !(fam.pipedThings.GetValue(drop) is IEnumerable dropThings))
            {
                fam.skip = "PipedThings was null or not enumerable";
                return;
            }
            MethodInfo add = AddMethodFor(fam, keepThings);
            if (add == null)
            {
                fam.skip = "no single-argument Add on " + keepThings.GetType().Name;
                return;
            }
            // Snapshot: the loser's set is read while the winner's is written. They are
            // different objects today, but copying first makes that independent of whether a
            // future host ever shares one instance between nets.
            List<object> moving = new List<object>();
            foreach (object t in dropThings)
            {
                moving.Add(t);
            }
            for (int i = 0; i < moving.Count; i++)
            {
                add.Invoke(keepThings, new[] { moving[i] });
                RepointPipes(fam, moving[i], keep);
            }
            RemoveNet(fam, holder, drop);
            fam.initNet.Invoke(keep, null);
        }

        /// <summary>`Add` resolved by shape rather than by name-plus-signature, so it works
        /// whatever element type a host used. Cached per family - this runs inside a network
        /// rebuild.</summary>
        private static MethodInfo AddMethodFor(Family fam, object collection)
        {
            if (fam.addThing != null)
            {
                return fam.addThing;
            }
            MethodInfo[] all = collection.GetType().GetMethods();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].Name == "Add" && all[i].GetParameters().Length == 1)
                {
                    fam.addThing = all[i];
                    break;
                }
            }
            return fam.addThing;
        }

        /// <summary>Count without assuming a non-generic interface. See the warning in Join.</summary>
        private static int CountOf(object collection)
        {
            if (collection == null)
            {
                return -1;
            }
            PropertyInfo p = collection.GetType().GetProperty("Count");
            return p != null ? (int)p.GetValue(collection) : -1;
        }

        private static object NetOf(Family fam, Thing t)
        {
            if (!(t is ThingWithComps twc))
            {
                return null;
            }
            for (int i = 0; i < twc.AllComps.Count; i++)
            {
                if (fam.compPipe.IsInstanceOfType(twc.AllComps[i]))
                {
                    return fam.pipeNetRef.GetValue(twc.AllComps[i]);
                }
            }
            return null;
        }

        private static void RepointPipes(Family fam, object thing, object net)
        {
            if (!(thing is ThingWithComps twc))
            {
                return;
            }
            for (int i = 0; i < twc.AllComps.Count; i++)
            {
                if (fam.compPipe.IsInstanceOfType(twc.AllComps[i]))
                {
                    fam.pipeNetRef.SetValue(twc.AllComps[i], net);
                }
            }
        }

        /// <summary>Drop a net from the map component's array so it stops ticking. Rebuilt
        /// as a new array because the field is a plain `Net[]`, not a List.</summary>
        private static void RemoveNet(Family fam, object holder, object drop)
        {
            if (holder == null)
            {
                return;
            }
            if (!(fam.pipeNets.GetValue(holder) is Array arr))
            {
                return;
            }
            List<object> kept = new List<object>(arr.Length);
            foreach (object n in arr)
            {
                if (!ReferenceEquals(n, drop))
                {
                    kept.Add(n);
                }
            }
            if (kept.Count == arr.Length)
            {
                return;
            }
            Array rebuilt = Array.CreateInstance(arr.GetType().GetElementType(), kept.Count);
            for (int i = 0; i < kept.Count; i++)
            {
                rebuilt.SetValue(kept[i], i);
            }
            fam.pipeNets.SetValue(holder, rebuilt);
            fam.drops++;
        }
    }
}
