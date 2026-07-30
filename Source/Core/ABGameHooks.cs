using System;
using System.Collections.Generic;
using System.Reflection;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Marks a parameterless <c>static void</c> method to run once per
    /// game tick from ABGameComp. A ticked feature is added by annotating its own
    /// Tick() - Core never lists it (refactor R1). Lower Order runs first; ties
    /// break by full type+method name so the sequence is deterministic.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ABGameTickAttribute : Attribute
    {
        public readonly int Order;
        public ABGameTickAttribute(int order = 0) { Order = order; }
    }

    /// <summary>Marks a parameterless <c>static void</c> method to run when a
    /// game is started or loaded (from ABGameComp.FinalizeInit), to clear static
    /// session state that must not cross games.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ABGameResetAttribute : Attribute
    {
        public readonly int Order;
        public ABGameResetAttribute(int order = 0) { Order = order; }
    }

    /// <summary>Marks a parameterless <c>static void</c> method to run from
    /// ABGameComp.ExposeData (Scribe save/load hooks).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ABGameExposeAttribute : Attribute
    {
        public readonly int Order;
        public ABGameExposeAttribute(int order = 0) { Order = order; }
    }

    /// <summary>
    /// Game-lifecycle hub (refactor R1). Features self-register per-tick work,
    /// new-game state resets, and Scribe hooks by attributing their own static
    /// methods; ABGameComp just calls <see cref="RunTicks"/>/<see cref="RunResets"/>/
    /// <see cref="RunExposes"/>. This inverts the old coupling where Core hardcoded
    /// a call list into a dozen feature subsystems.
    ///
    /// Discovery is a single reflection pass at startup; the run path is a plain
    /// indexed array walk - zero per-tick allocation, and the order is stable
    /// (by [Order] then type/method name), so behavior matches the old explicit
    /// list. Each callee still self-guards, so a fault trips only its subsystem.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABGameHooks
    {
        private sealed class Entry
        {
            public int order;
            public string key;
            public Action run;
        }

        private static readonly Action[] ticks;
        private static readonly Action[] resets;
        private static readonly Action[] exposes;

        static ABGameHooks()
        {
            List<Entry> tickList = new List<Entry>();
            List<Entry> resetList = new List<Entry>();
            List<Entry> exposeList = new List<Entry>();

            Type[] types;
            try
            {
                types = typeof(ABGameHooks).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }

            foreach (Type type in types)
            {
                if (type == null)
                {
                    continue;
                }
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public
                        | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }
                foreach (MethodInfo m in methods)
                {
                    TryAdd<ABGameTickAttribute>(m, type, tickList, a => a.Order);
                    TryAdd<ABGameResetAttribute>(m, type, resetList, a => a.Order);
                    TryAdd<ABGameExposeAttribute>(m, type, exposeList, a => a.Order);
                }
            }

            ticks = Build(tickList);
            resets = Build(resetList);
            exposes = Build(exposeList);
            ABLog.Dev("Game hooks discovered: " + ticks.Length + " tick, "
                + resets.Length + " reset, " + exposes.Length + " expose.");
        }

        private static void TryAdd<T>(MethodInfo m, Type type, List<Entry> into, Func<T, int> orderOf)
            where T : Attribute
        {
            T attr = m.GetCustomAttribute<T>(false);
            if (attr == null)
            {
                return;
            }
            if (m.ReturnType != typeof(void) || m.GetParameters().Length != 0)
            {
                Log.Warning(ABLog.Tag + " " + typeof(T).Name + " on " + type.Name + "." + m.Name
                    + " ignored: must be a parameterless void method.");
                return;
            }
            try
            {
                Action run = (Action)Delegate.CreateDelegate(typeof(Action), m);
                into.Add(new Entry { order = orderOf(attr), key = type.FullName + "." + m.Name, run = run });
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " could not bind lifecycle hook " + type.Name + "." + m.Name + ": " + e.Message);
            }
        }

        private static Action[] Build(List<Entry> list)
        {
            list.Sort((x, y) => x.order != y.order
                ? x.order.CompareTo(y.order)
                : string.CompareOrdinal(x.key, y.key));
            Action[] arr = new Action[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                arr[i] = list[i].run;
            }
            return arr;
        }

        public static void RunTicks()
        {
            for (int i = 0; i < ticks.Length; i++)
            {
                ticks[i]();
            }
        }

        public static void RunResets()
        {
            for (int i = 0; i < resets.Length; i++)
            {
                resets[i]();
            }
        }

        public static void RunExposes()
        {
            for (int i = 0; i < exposes.Length; i++)
            {
                exposes[i]();
            }
        }
    }
}
