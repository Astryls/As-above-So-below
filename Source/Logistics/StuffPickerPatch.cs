using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// The build designator's stuff picker requires each material to be
    /// physically spawned on the CURRENT map (the ListerThings.ThingsOfDef gate
    /// in Designator_Build.ProcessInput), so a fresh pocket level with all
    /// materials stored on the surface reports "no usable materials to build
    /// this from" even though cross-level hauling would deliver them. Swap that
    /// one call for a column-aware version: local things win unchanged,
    /// otherwise a directly linked level with the material satisfies the gate.
    /// The ResourceCounter augmentation already feeds the picker's def source,
    /// so together the picker now sees the whole column. Pattern-guarded: when
    /// the call is not found the original body is returned untouched with a
    /// warning, and HarmonyBoot catches a failed patch class independently.
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.ProcessInput))]
    internal static class Patch_DesignatorBuild_ColumnStuff
    {
        private static readonly MethodInfo ThingsOfDefMethod =
            AccessTools.Method(typeof(ListerThings), nameof(ListerThings.ThingsOfDef));

        private static readonly MethodInfo ReplacementMethod =
            AccessTools.Method(typeof(Patch_DesignatorBuild_ColumnStuff), nameof(ColumnThingsOfDef));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Calls(ThingsOfDefMethod))
                {
                    // Instance callvirt (lister, def) becomes a static call with
                    // the identical stack shape; labels and blocks are preserved
                    // by mutating in place.
                    list[i].opcode = OpCodes.Call;
                    list[i].operand = ReplacementMethod;
                    replaced++;
                }
            }
            if (replaced == 0)
            {
                Log.Warning(ABLog.Tag + " Stuff picker patch found no ThingsOfDef call; cross level materials will not show in the build menu.");
            }
            return list;
        }

        /// <summary>Drop-in for ListerThings.ThingsOfDef at the picker call site
        /// only. Returns the local list whenever it has entries; otherwise a
        /// directly linked level's non-empty list stands in (the call site only
        /// reads Count).</summary>
        public static List<Thing> ColumnThingsOfDef(ListerThings lister, ThingDef def)
        {
            List<Thing> local = lister.ThingsOfDef(def);
            if (local.Count > 0 || !ABGuard.On(ABGuard.Logistics))
            {
                return local;
            }
            try
            {
                Map cur = Find.CurrentMap;
                if (cur == null || cur.listerThings != lister)
                {
                    // Some other map's lister: stay strictly vanilla.
                    return local;
                }
                LevelComp comp = cur.Levels();
                if (comp == null)
                {
                    return local;
                }
                Map up = comp.upperMap;
                if (up != null && !up.Disposed)
                {
                    List<Thing> other = up.listerThings.ThingsOfDef(def);
                    if (other.Count > 0)
                    {
                        return other;
                    }
                }
                Map down = comp.lowerMap;
                if (down != null && !down.Disposed)
                {
                    List<Thing> other = down.listerThings.ThingsOfDef(def);
                    if (other.Count > 0)
                    {
                        return other;
                    }
                }
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Logistics, e, "column stuff lookup");
            }
            return local;
        }
    }
}
