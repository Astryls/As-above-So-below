using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
    /// ⚠ THE SWITCH ALREADY WORKS AND NEEDS NOTHING FROM US. `CompPipe.ReceiveCompSignal`
    /// calls `DirtyPipeGrid(mode)` then `RegenPipeGrids()` on FlickedOn/FlickedOff, and it
    /// does so unconditionally - unlike `CompPipe.closed`, which only ever reads a flicker
    /// that Dubwise wires up for `Building_Valve` alone. So flicking a breaker re-runs
    /// `RebuildPipeGrid`, which re-runs this postfix, which re-evaluates the link.
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
            public string prefix;           // our network-id prefix, e.g. "DBH."
        }

        private static readonly Dictionary<Type, Family> families = new Dictionary<Type, Family>();

        private static readonly (string comp, string net, string prefix)[] Hosts =
        {
            ("DubsBadHygiene.HygienePipeMapComp", "DubsBadHygiene.PlumbingNet",  "DBH."),
            ("Rimefeller.MapComponent_Rimefeller", "Rimefeller.PipelineNet",     "Rimefeller."),
            ("Rimatomics.MapComponent_Rimatomics", "Rimatomics.BasePipeNet",     "Rimatomics.")
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
            foreach ((string compName, string netName, string prefix) in Hosts)
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
                    pipedThings = things, initNet = init, prefix = prefix
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
                if (fam == null || map == null || !ABBands.Banded(map))
                {
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
            List<ThingDef> risers = ABRiserDefs.All;
            for (int i = 0; i < risers.Count; i++)
            {
                ABRiserExt ext = risers[i].GetModExtension<ABRiserExt>();
                // Junctions only: the pair is symmetric, so walking both ends would do every
                // merge twice.
                if (ext == null || ext.role != ABRiserRole.Junction
                    || ext.network == null || !ext.network.StartsWith(fam.prefix))
                {
                    continue;
                }
                List<Thing> here = map.listerThings.ThingsOfDef(risers[i]);
                for (int j = 0; j < here.Count; j++)
                {
                    partners.Clear();
                    ABRiserLink.AppendPartners(here[j], partners);
                    for (int k = 0; k < partners.Count; k++)
                    {
                        Join(fam, holder, here[j], partners[k]);
                    }
                }
            }
        }

        /// <summary>Fold the partner's net into the junction's, then delete the loser.
        ///
        /// ⚠ THE HOLDER IS `__instance`, NOT A LOOKUP. An earlier version re-found the map
        /// component from `pipeNets.DeclaringType` - which is the ABSTRACT
        /// `UniversalPipeMapComp` base for Rimefeller and Rimatomics, so `GetComponent` on it
        /// could miss and the losing net was never removed from the array. The component that
        /// just rebuilt its own grid is sitting right there in `__instance`; use it.</summary>
        private static void Join(Family fam, object holder, Thing junction, Thing breaker)
        {
            object keep = NetOf(fam, junction);
            object drop = NetOf(fam, breaker);
            if (keep == null || drop == null || ReferenceEquals(keep, drop))
            {
                return;
            }
            if (!(fam.pipedThings.GetValue(keep) is ICollection keepThings)
                || !(fam.pipedThings.GetValue(drop) is IEnumerable dropThings))
            {
                return;
            }
            // HashSet<ThingWithComps>.Add via reflection - the collection type differs per
            // family, so go through the interface rather than naming it.
            MethodInfo add = keepThings.GetType().GetMethod("Add");
            if (add == null)
            {
                return;
            }
            foreach (object t in dropThings)
            {
                add.Invoke(keepThings, new[] { t });
                RepointPipes(fam, t, keep);
            }
            RemoveNet(fam, holder, drop);
            fam.initNet.Invoke(keep, null);
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
            ABLog.Dev("Dubs pipe merge: dropped a net, " + arr.Length + " -> " + kept.Count);
        }
    }
}
