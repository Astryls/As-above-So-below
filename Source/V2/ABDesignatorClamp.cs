using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Rule 1 for the ORDER designators: a designator only accepts cells on the band the
    /// player is LOOKING at.
    ///
    /// THE FIELD REPORT THIS FIXES (§46). Everything past a level's north/south edge is
    /// real map - two gutter rows, then the neighbouring level. §29e clamped PLACEMENT and
    /// the click-through system translates SELECTION, but not one Designator was clamped:
    /// dragging Mine across the seam designated (and, in dev mode, instantly destroyed)
    /// the next level's rock at its true coordinates. The reporter mined "ghosts" in the
    /// black area and found the walls gone one level down.
    ///
    /// WHY EVERY OVERRIDE IS PATCHED INSTEAD OF THE BASE. Designator.CanDesignateCell is
    /// ABSTRACT - there is no body to patch, and virtual dispatch goes straight to each
    /// override. So this walks every Designator subclass (vanilla, DLC and other mods';
    /// [StaticConstructorOnStartup] runs after all of them are loaded) and patches each
    /// DECLARED CanDesignateCell(IntVec3) with one shared postfix. Abstract intermediates
    /// are included: AllSubclasses, not AllSubclassesNonAbstract, because an override
    /// declared on an abstract mid-class (and merely inherited by the leaves) is a real
    /// method body that the leaves dispatch to - enumerating only leaves would miss it.
    ///
    /// The postfix binds the cell POSITIONALLY (__0), never by name: the base names it
    /// "loc" but overrides rename it freely ("c", "cell"), and Harmony binds by name -
    /// one mismatched override would kill the whole clamp at patch time.
    ///
    /// SCOPE, deliberately narrow:
    ///   * CELL designators only. CanDesignateThing is left alone - selecting a THING on
    ///     another level (through open air, via click-through) and ordering it hauled is
    ///     legitimate cross-level work, which vanilla then routes over the wormholes.
    ///   * Only flips Accepted to denied, never the reverse - stacking under §29e's
    ///     placement clamp is harmless (first refusal wins).
    ///   * DEBUG TOOLS are not clamped. They are god instruments and a dev may genuinely
    ///     want to edit a band they are not looking at; the palette is dev-mode only.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABDesignatorClamp
    {
        static ABDesignatorClamp()
        {
            try
            {
                InstallOn(new Harmony("astryl.AsAboveSoBelow2.designatorClamp"));
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: designator band clamp failed to install: " + e);
            }
        }

        /// <summary>Vanilla stubs whose entire body is `throw new NotImplementedException()`.
        /// Harmony cannot re-emit a bare-throw body together with a postfix that reads
        /// __result - the generated wrapper fails to JIT ("Invalid IL code ... IL_001c:
        /// ret", run #414) because no return value ever reaches the ret the postfix needs
        /// to load. Nothing is lost by skipping: a method that only throws can never
        /// ACCEPT a cell, so there is no verdict to clamp. Skipped silently at dev level -
        /// a startup Warning for a known engine stub reads as a broken mod to players and
        /// log-triage tools alike. Unexpected failures on OTHER types still warn.</summary>
        private static readonly HashSet<string> KnownUnpatchable = new HashSet<string>
        {
            "RimWorld.Designator_EmptySpace",
        };

        private static void InstallOn(Harmony harmony)
        {
            HarmonyMethod postfix = new HarmonyMethod(
                typeof(ABDesignatorClamp), nameof(ClampPostfix));
            int patched = 0;
            foreach (Type type in typeof(Designator).AllSubclasses())
            {
                if (type.IsGenericTypeDefinition || type.ContainsGenericParameters)
                {
                    continue; // shared JIT codegen makes generic patches unsafe
                }
                MethodInfo m;
                try
                {
                    m = type.GetMethod("CanDesignateCell",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly,
                        null, new[] { typeof(IntVec3) }, null);
                }
                catch (Exception)
                {
                    continue; // a foreign type whose method table will not even resolve
                }
                if (m == null || m.IsAbstract || m.ReturnType != typeof(AcceptanceReport))
                {
                    continue;
                }
                if (KnownUnpatchable.Contains(type.FullName))
                {
                    ABLog.Dev("Designator clamp: skipped known bare-throw stub "
                        + type.FullName + ".");
                    continue;
                }
                try
                {
                    harmony.Patch(m, postfix: postfix);
                    patched++;
                }
                catch (Exception e)
                {
                    // One unpatched foreign designator must not cost the other hundred.
                    Log.Warning(ABLog.Tag + " V2: designator clamp skipped "
                        + type.FullName + ": " + e.Message);
                }
            }
            ABLog.Dev("Designator band clamp installed on " + patched
                + " CanDesignateCell override(s).");
        }

        // Per-frame memo, same shape as the view clip's: CanDesignateCell runs per cell of
        // a drag rect per mouse move, and resolving the band comp is a ConditionalWeakTable
        // probe - measured and removed from per-frame paths in this codebase before.
        private static int cachedFrame = -1;

        private static bool cachedActive;

        private static Map cachedMap;

        private static ABBandMap cachedBands;

        private static int cachedViewBand;

        private static void ClampPostfix(Designator __instance, IntVec3 __0,
            ref AcceptanceReport __result)
        {
            try
            {
                if (!__result.Accepted || !ABGuard.On(ABGuard.Ui)
                    || Current.ProgramState != ProgramState.Playing)
                {
                    return;
                }
                if (cachedFrame != Time.frameCount)
                {
                    cachedFrame = Time.frameCount;
                    cachedMap = Find.CurrentMap;
                    cachedBands = cachedMap == null ? null : ABBands.CompOf(cachedMap);
                    cachedActive = cachedBands != null && cachedBands.Banded;
                    cachedViewBand = cachedActive ? ABBandView.CurrentBand(cachedMap) : 0;
                }
                if (!cachedActive || __instance == null || __instance.Map != cachedMap)
                {
                    return;
                }
                IntVec3 c = __0;
                if (!c.InBounds(cachedMap))
                {
                    return; // vanilla's own InBounds refusals already read correctly
                }
                if (!cachedBands.InGutter(c) && cachedBands.BandOf(c) == cachedViewBand)
                {
                    return; // on the viewed level: vanilla's verdict stands
                }
                __result = new AcceptanceReport("AB_WrongLevel".Translate());
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Ui, e, "V2 designator band clamp");
            }
        }
    }
}
