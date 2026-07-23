using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Reflection bridge to the two "haul many things into inventory" mods -
    /// Pick Up And Haul (Mehni.PickUpAndHaul) and Hauler's Dream
    /// (giwaffed.HaulersDream). Both inject a per-pawn CompHauledToInventory
    /// that tracks scooped stacks and drive an unload job through a static
    /// PawnUnloadChecker. We reuse that machinery to make our cross-level haul
    /// a BULK inventory trip: we pick items up on one level, register them into
    /// whichever mod's comp the pawn carries, ride the stairs (inventory rides
    /// along through the despawn), then ask that mod's own unloader to store
    /// them on the destination level.
    ///
    /// Reflection-only (no assembly reference), so no foreign type ever reaches
    /// a member signature - the soft-compat signature-scan trap does not apply.
    /// Every lookup is cached once and every call is guarded; the first
    /// exception on a system trips its broken flag and we fall back to the
    /// single-item carryTracker haul.
    /// </summary>
    public static class ABInventoryHaulBridge
    {
        private const string PuahPackageId = "Mehni.PickUpAndHaul";
        private const string HdPackageId = "giwaffed.HaulersDream";

        /// <summary>One inventory-haul mod: its comp type, the register method,
        /// and the static unload trigger. All resolved once at first use.</summary>
        private sealed class System
        {
            public Type compType;
            public MethodInfo register;
            public int registerArity;
            public MethodInfo unload;
            public int unloadArity;
            public bool broken;
        }

        private static bool initialized;
        private static readonly List<System> systems = new List<System>();

        private static void EnsureInit()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
            try
            {
                if (ABDetect.Active(PuahPackageId))
                {
                    Add("PickUpAndHaul.CompHauledToInventory",
                        new[] { typeof(Thing) },
                        "PickUpAndHaul.PawnUnloadChecker", "CheckIfPawnShouldUnloadInventory");
                }
                if (ABDetect.Active(HdPackageId))
                {
                    Add("HaulersDream.CompHauledToInventory",
                        new[] { typeof(Thing), typeof(int) },
                        "HaulersDream.PawnUnloadChecker", "CheckIfShouldUnload");
                }
            }
            catch (Exception e)
            {
                Log.Warning("[As above, So below] inventory-haul bridge init failed, falling back to single-item haul: " + e.Message);
                systems.Clear();
            }
        }

        private static void Add(string compTypeName, Type[] registerArgs, string checkerTypeName, string checkerMethod)
        {
            Type compType = AccessTools.TypeByName(compTypeName);
            if (compType == null)
            {
                return;
            }
            MethodInfo register = AccessTools.Method(compType, "RegisterHauledItem", registerArgs)
                ?? AccessTools.Method(compType, "RegisterHauledItem", new[] { typeof(Thing) });
            Type checkerType = AccessTools.TypeByName(checkerTypeName);
            MethodInfo unload = checkerType != null ? AccessTools.Method(checkerType, checkerMethod) : null;
            if (register == null || unload == null)
            {
                return;
            }
            systems.Add(new System
            {
                compType = compType,
                register = register,
                registerArity = register.GetParameters().Length,
                unload = unload,
                unloadArity = unload.GetParameters().Length
            });
        }

        /// <summary>True when at least one inventory-haul mod is present and its
        /// reflection surface resolved. Gates the bulk giver on and the
        /// single-item fallback off.</summary>
        public static bool AnyActive
        {
            get
            {
                EnsureInit();
                return systems.Count > 0;
            }
        }

        /// <summary>The pawn actually carries one of the mods' inventory-haul
        /// comps (colonists do; robots/mechs do not - they keep the single
        /// carryTracker haul).</summary>
        public static bool HasComp(Pawn pawn)
        {
            return FindComp(pawn, out _) != null;
        }

        private static ThingComp FindComp(Pawn pawn, out System sys)
        {
            sys = null;
            EnsureInit();
            if (pawn == null || systems.Count == 0 || pawn.AllComps == null)
            {
                return null;
            }
            List<ThingComp> comps = pawn.AllComps;
            for (int s = 0; s < systems.Count; s++)
            {
                System system = systems[s];
                if (system.broken)
                {
                    continue;
                }
                for (int i = 0; i < comps.Count; i++)
                {
                    ThingComp comp = comps[i];
                    if (comp != null && system.compType.IsInstanceOfType(comp))
                    {
                        sys = system;
                        return comp;
                    }
                }
            }
            return null;
        }

        /// <summary>Record a stack the pawn just scooped into inventory so the
        /// mod's unloader will store it. Returns false (fall back) if no comp or
        /// the call threw.</summary>
        public static bool Register(Pawn pawn, Thing thing)
        {
            if (thing == null)
            {
                return false;
            }
            ThingComp comp = FindComp(pawn, out System sys);
            if (comp == null)
            {
                return false;
            }
            try
            {
                object[] args = sys.registerArity >= 2
                    ? new object[] { thing, 0 }
                    : new object[] { thing };
                sys.register.Invoke(comp, args);
                return true;
            }
            catch (Exception e)
            {
                sys.broken = true;
                Log.Warning("[As above, So below] inventory-haul register threw; disabling bulk bridge for this system: " + e.Message);
                return false;
            }
        }

        /// <summary>Ask the pawn's inventory-haul mod to unload its tracked
        /// stacks into storage on the map it now stands on. Called right after a
        /// bulk hauler steps off the stairs onto the destination level. Idle
        /// think-tree unload patches would eventually fire anyway; this makes it
        /// prompt. Forced, so it enqueues without waiting on encumbrance grace.</summary>
        public static void RequestUnload(Pawn pawn)
        {
            ThingComp comp = FindComp(pawn, out System sys);
            if (comp == null)
            {
                return;
            }
            try
            {
                // PUAH: CheckIfPawnShouldUnloadInventory(Pawn, bool forced)
                // HD:   CheckIfShouldUnload(Pawn, bool forced, bool, bool)
                object[] args;
                switch (sys.unloadArity)
                {
                    case 2:
                        args = new object[] { pawn, true };
                        break;
                    case 4:
                        args = new object[] { pawn, true, false, true };
                        break;
                    default:
                        args = BuildUnloadArgs(sys, pawn);
                        break;
                }
                sys.unload.Invoke(null, args);
            }
            catch (Exception e)
            {
                sys.broken = true;
                Log.Warning("[As above, So below] inventory-haul unload request threw; the mod's own idle unload will still fire: " + e.Message);
            }
        }

        private static object[] BuildUnloadArgs(System sys, Pawn pawn)
        {
            ParameterInfo[] ps = sys.unload.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(Pawn))
                {
                    args[i] = pawn;
                }
                else if (ps[i].ParameterType == typeof(bool))
                {
                    // First bool is "forced"; others (behindQueuedWork/immediate) default false.
                    args[i] = i == 1;
                }
                else if (ps[i].HasDefaultValue)
                {
                    args[i] = ps[i].DefaultValue;
                }
                else
                {
                    args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                }
            }
            return args;
        }
    }
}
