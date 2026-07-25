using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Attributes a subsystem failure to a concrete culprit so the shutdown log
    /// line and the in-game message can name WHAT tripped it, not just WHERE.
    /// Two sources, cheapest first:
    ///  - a SUBJECT the call site knows about (the mech that could not charge,
    ///    the item that would not store, a def) - named with its source mod;
    ///  - failing that, the exception's own stack, walked frame by frame until
    ///    the first frame belonging to a third-party mod assembly (skipping
    ///    vanilla/Unity/Harmony and our own code).
    /// Everything is defensive: this runs INSIDE an error handler, so it must
    /// never throw a second exception. Any failure yields null and the caller
    /// falls back to the plain "subsystem down" wording.
    /// </summary>
    public static class ABBlame
    {
        // Assembly -> owning mod, built once from the running mod list. Only
        // MOD assemblies land in the map, so a vanilla/engine frame simply
        // misses and is skipped - no allow-list of engine assembly names to
        // maintain.
        private static Dictionary<Assembly, ModContentPack> assemblyToMod;

        private static Assembly ownAssembly;

        private static Dictionary<Assembly, ModContentPack> AssemblyMap
        {
            get
            {
                if (assemblyToMod != null)
                {
                    return assemblyToMod;
                }
                var map = new Dictionary<Assembly, ModContentPack>();
                try
                {
                    ownAssembly = typeof(ABBlame).Assembly;
                    foreach (ModContentPack pack in LoadedModManager.RunningModsListForReading)
                    {
                        List<Assembly> asms = pack?.assemblies?.loadedAssemblies;
                        if (asms == null)
                        {
                            continue;
                        }
                        for (int i = 0; i < asms.Count; i++)
                        {
                            if (asms[i] != null && !map.ContainsKey(asms[i]))
                            {
                                map[asms[i]] = pack;
                            }
                        }
                    }
                }
                catch
                {
                    // Leave whatever was collected; a partial map still helps.
                }
                assemblyToMod = map;
                return assemblyToMod;
            }
        }

        /// <summary>Best available one-line culprit for a subsystem shutdown, or
        /// null when nothing specific can be named. Subject wins (it names the
        /// exact thing plus its mod); otherwise the exception stack is mined for
        /// a third-party mod.</summary>
        public static string Cause(object subject, Exception e)
        {
            string bySubject = Describe(subject);
            if (bySubject != null)
            {
                string mod = BlameMod(e);
                // When the offending mod is not the subject's own mod (e.g. a
                // vanilla item mangled by a patch from mod X), name both.
                if (mod != null && bySubject.IndexOf(mod, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return bySubject + "; patched by " + mod;
                }
                return bySubject;
            }
            return BlameMod(e);
        }

        /// <summary>Human-readable "thing (source mod)" for a subject a call site
        /// hands us. Handles Def, Thing (incl. Pawn/Building), and falls back to
        /// ToString. Null in -> null out.</summary>
        public static string Describe(object subject)
        {
            if (subject == null)
            {
                return null;
            }
            try
            {
                switch (subject)
                {
                    case Def def:
                        return Label(def) + Source(def);
                    case Thing thing:
                    {
                        Def d = thing.def;
                        string label = SafeThingLabel(thing) ?? (d != null ? Label(d) : thing.GetType().Name);
                        return label + Source(d);
                    }
                    default:
                        return subject.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The first third-party mod on the exception's stack, or null
        /// when the trace is purely vanilla + us. Inner exceptions are walked
        /// too (reflection wraps modded throws in TargetInvocationException).</summary>
        public static string BlameMod(Exception e)
        {
            int guard = 0;
            for (Exception cur = e; cur != null && guard < 6; cur = cur.InnerException, guard++)
            {
                string mod = BlameOne(cur);
                if (mod != null)
                {
                    return mod;
                }
            }
            return null;
        }

        private static string BlameOne(Exception e)
        {
            if (e == null)
            {
                return null;
            }
            try
            {
                var trace = new StackTrace(e, fNeedFileInfo: false);
                StackFrame[] frames = trace.GetFrames();
                if (frames == null)
                {
                    return null;
                }
                Dictionary<Assembly, ModContentPack> map = AssemblyMap;
                for (int i = 0; i < frames.Length; i++)
                {
                    Assembly asm = frames[i].GetMethod()?.DeclaringType?.Assembly;
                    if (asm == null || asm == ownAssembly)
                    {
                        continue;
                    }
                    if (map.TryGetValue(asm, out ModContentPack pack) && pack != null)
                    {
                        return pack.Name;
                    }
                }
            }
            catch
            {
                // No trace available (release IL, secured stack) - give up quietly.
            }
            return null;
        }

        private static string SafeThingLabel(Thing thing)
        {
            try
            {
                // LabelShortCap can throw for transiently-bugged things (e.g. a
                // corpse mid-butchery); the def label below is the safe fallback.
                return thing.LabelShortCap;
            }
            catch
            {
                return null;
            }
        }

        private static string Label(Def def)
        {
            if (def == null)
            {
                return "?";
            }
            return !string.IsNullOrEmpty(def.label) ? def.label : def.defName;
        }

        private static string Source(Def def)
        {
            string name = def?.modContentPack?.Name;
            return string.IsNullOrEmpty(name) ? string.Empty : " (" + name + ")";
        }
    }
}
