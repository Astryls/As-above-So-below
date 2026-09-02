using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// FLECKS SEEN FROM ABOVE - AND, SINCE WINDOW 4b, FROM BELOW.
    ///
    /// ⚠ THE BELOW VIEW HAS THREE EMITTERS, NOT TWO, AND THIS IS THE THIRD.
    ///   1. the map mesh      -> SectionLayer_ABBelowV2 and friends (static things, terrain)
    ///   2. dynamic draws     -> ABBelowDynamicDraw (pawns, then realtime things, which was
    ///                           itself a late discovery - see its own banner)
    ///   3. FLECKS            -> map.flecks (FleckManager), reached by NEITHER of the above.
    /// Flecks are not Things. They are lightweight structs owned by FleckSystems and drawn by
    /// FleckManager's own batch, so they are invisible to the thing-based mirror no matter how
    /// complete that mirror becomes. Reported as "you can't see horseshoes being thrown from
    /// upper layers"; `Horseshoe` is a FleckDef, and so are sparks, dust puffs, water splashes,
    /// smoke and most of what used to be a Mote before 1.3.
    ///
    /// ⚠ MIRROR AT THE PRODUCER, NOT AT THE DRAW. A fleck's draw position is computed inside
    /// its system from state we do not own, and the systems batch their meshes - there is no
    /// seam to offset a single fleck at draw time. Duplicating the CREATION with an offset
    /// spawn position is the "patch the producer / wrap the data" shape, and it costs one
    /// extra fleck rather than a second draw pass.
    ///
    /// ⚠ AND IT IS GATED ON THE VIEW BAND, WHICH IS WHAT MAKES IT AFFORDABLE. CreateFleck is
    /// a high-frequency method, and §36e's lesson is that a hot patch's cost is DISPATCH x
    /// CALL COUNT, not its body. A fleck is only ever worth mirroring if the player is looking
    /// at a band ABOVE it right now, so the gate is an int compare that rejects the entire
    /// common case (viewing the band the fleck is on) before touching terrain. Mirroring
    /// unconditionally to every see-through band above would triple or quadruple every surface
    /// fleck on a sky stack, permanently, for something nobody is looking at.
    ///
    /// ⚠ ONE DESCENT RULE. Whether the fleck is actually visible from the view band is asked
    /// with ABBands.TryResolveVisibleBelow, never with a hand-rolled `- Slot` step - that
    /// single-step mistake is documented as having been made eight times, most recently as
    /// index arithmetic that a grep for "- Slot" could not catch.
    ///
    /// The mirror is a SHORT-LIVED COSMETIC COPY: flecks last well under a second, so a level
    /// switch mid-flight leaves at worst one stale puff, and no state needs reconciling.
    ///
    /// ⚠ THE UPWARD HALF (added with cross-level combat, at the user's request): a fleck on a
    /// band ABOVE the view - a muzzle flash at an opening, impact sparks on a sky-band raider,
    /// smoke from a shot fired overhead - mirrors DOWN through the holes in the ceiling. The
    /// predicate is ABShaft.ColumnOpen, the ballistics rule, NOT TryResolveVisibleBelow: that
    /// method looks DOWN a column and accepts AB_WallTop (seeing a wall top is legitimate),
    /// but looking UP through your own ceiling requires strict open air the whole way. Same
    /// pair of rules, same one copy each, as the projectile and skyfaller relays.
    ///
    /// ⚠ §95 SEAM MOVE (rule 16 extended): FleckManager.CreateFleck is NOT the only
    /// creation seam. Owlchemist's SimpleFX family caches the FleckSystem and calls
    /// system.CreateFleck DIRECTLY (chimney smoke was invisible cross-band). The concrete
    /// body every path funnels through is FleckSystemBase&lt;TFleck&gt;.CreateFleck - a
    /// CLOSED-GENERIC method per TFleck struct, so there is no single MethodInfo to patch.
    /// ABFleckMirrorBoot therefore enumerates every fleckSystemClass in the DefDatabase at
    /// startup, resolves each class's concrete CreateFleck (walking up to its closed base),
    /// dedupes by MethodInfo and patches each - vanilla, BloodAnimations, and any modded
    /// system alike. The manager-level patch is GONE (both seams live = double mirror);
    /// it survives only as ABFleckMirror.PostfixManager, the rule-33 fallback installed
    /// when enumeration patches NOTHING. Binding is positional (__0) because
    /// FleckSystemBase names the parameter "creationData" while the abstract declares
    /// "fleckData" - and a modded override could pick anything.
    /// </summary>
    public static class ABFleckMirror
    {
        /// <summary>The mirrored creation re-enters the patched method; without this it is
        /// unbounded recursion, and a StackOverflow in .NET is UNCATCHABLE.</summary>
        [ThreadStatic]
        private static bool mirroring;

        /// <summary>Flecks that reached the visibility test (player was looking from above).</summary>
        public static int considered;

        /// <summary>Flecks actually duplicated onto the view band.</summary>
        public static int mirrored;

        [ABGameReset]
        public static void ResetForNewGame()
        {
            considered = 0;
            mirrored = 0;
            mirroring = false;
        }

        internal static void Postfix(FleckSystem __instance, FleckCreationData __0)
        {
            MirrorCore(__instance?.parent?.parent, __0);
        }

        /// <summary>Rule-33 fallback shape: the pre-§95 manager seam, installed by
        /// ABFleckMirrorBoot ONLY when the system-level enumeration patched nothing.
        /// Misses direct system.CreateFleck callers, but a degraded mirror beats none.</summary>
        internal static void PostfixManager(FleckManager __instance, FleckCreationData __0)
        {
            MirrorCore(__instance?.parent, __0);
        }

        private static void MirrorCore(Map map, FleckCreationData fleckData)
        {
            if (mirroring)
            {
                return; // this IS the mirror
            }
            try
            {
                if (map == null || fleckData.def == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                // -1 means "not chosen yet", which the view code resolves to the surface;
                // resolving it the same way here keeps the mirror honest during the brief
                // window before the first explicit level switch.
                int viewBand = bands.viewBand;
                if (viewBand < 0)
                {
                    viewBand = bands.surfaceBand;
                }
                IntVec3 cell = fleckData.spawnPosition.ToIntVec3();
                if (!cell.InBounds(map))
                {
                    return;
                }
                int srcBand = bands.BandOf(cell);
                // The whole hot path ends here on the common case: the fleck is on the band
                // being watched, so no mirror in either direction can be needed.
                if (viewBand == srcBand)
                {
                    return;
                }
                considered++;

                int dz = (viewBand - srcBand) * bands.Slot;
                IntVec3 viewCell = new IntVec3(cell.x, cell.y, cell.z + dz);
                if (!viewCell.InBounds(map))
                {
                    return;
                }
                if (viewBand > srcBand)
                {
                    // Looking DOWN at it: is this fleck's cell the one the player actually
                    // sees through that column? The shared descent rule answers.
                    if (!ABBands.TryResolveVisibleBelow(map, bands, viewCell,
                            out IntVec3 below, out int _) || below != cell)
                    {
                        return;
                    }
                }
                else
                {
                    // Looking UP at it: visible only through a strictly open ceiling column.
                    if (bands.InGutter(cell)
                        || !ABShaft.ColumnOpen(map, bands, cell, srcBand, viewBand))
                    {
                        return;
                    }
                }

                FleckCreationData d = fleckData;
                // An attached fleck follows its target's REAL position, which would drag the
                // mirror straight back down to the source band and undo the offset. Detaching
                // makes it a plain positional fleck, which is what a mirror is.
                d.link = default(FleckAttachLink);
                d.spawnPosition = new Vector3(
                    fleckData.spawnPosition.x,
                    fleckData.spawnPosition.y,
                    fleckData.spawnPosition.z + dz);

                mirroring = true;
                try
                {
                    // Through the MANAGER, deliberately: it re-normalizes y from the def
                    // and dispatches to the right (patched) system, where the latch above
                    // stops the recursion. Works identically under the fallback seam.
                    map.flecks.CreateFleck(d);
                }
                finally
                {
                    mirroring = false;
                }
                mirrored++;
            }
            catch (Exception e)
            {
                // A cosmetic mirror must never break the real fleck, and a latch left set
                // would silently disable every later mirror.
                mirroring = false;
                Log.ErrorOnce(ABLog.Tag + " below-fleck mirror threw: " + e, 0x2B10F1);
            }
        }
    }

    /// <summary>
    /// Seats ABFleckMirror.Postfix on every concrete CreateFleck override reachable from a
    /// loaded FleckDef. [StaticConstructorOnStartup] is the timing requirement, not
    /// decoration: fleckSystemClass values come from the DefDatabase, which is only
    /// complete after defs load - HarmonyBoot's PatchAll pass runs too early to see them,
    /// which is why this class owns its own installation (same shape as ABDesignatorClamp).
    /// Per-method try/catch: one alien override that Harmony cannot chew must not take the
    /// vanilla seams down with it (PipeSystemCompat's lesson, inverted).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABFleckMirrorBoot
    {
        static ABFleckMirrorBoot()
        {
            int patched = 0;
            try
            {
                HarmonyMethod postfix = new HarmonyMethod(
                    AccessTools.Method(typeof(ABFleckMirror), nameof(ABFleckMirror.Postfix)));
                HashSet<MethodBase> seen = new HashSet<MethodBase>();
                List<FleckDef> defs = DefDatabase<FleckDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++)
                {
                    Type cls = defs[i]?.fleckSystemClass;
                    if (cls == null)
                    {
                        continue;
                    }
                    MethodInfo m = null;
                    try
                    {
                        m = AccessTools.Method(cls, "CreateFleck",
                            new[] { typeof(FleckCreationData) });
                    }
                    catch (Exception)
                    {
                        // An unresolvable foreign class; skip it, keep enumerating.
                    }
                    if (m != null && !m.IsDeclaredMember())
                    {
                        // FleckSystemStatic INHERITS CreateFleck from its closed generic
                        // base and Harmony refuses reflected members ("You can only patch
                        // implemented methods/constructors") - run #497's field catch.
                        // GetDeclaredMember re-targets the method on its DECLARING closed
                        // type, which also collapses every pure inheritor of one base onto
                        // one MethodInfo for the dedupe below.
                        m = m.GetDeclaredMember();
                    }
                    if (m == null || m.IsAbstract || !seen.Add(m))
                    {
                        continue;
                    }
                    try
                    {
                        HarmonyBoot.Harmony.Patch(m, postfix: postfix);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        Log.WarningOnce(ABLog.Tag + " fleck mirror could not seat on "
                            + cls.FullName + ".CreateFleck; flecks from that system will"
                            + " not mirror across levels. " + e.Message, 0x2B10DC);
                    }
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " fleck mirror enumeration threw: " + e, 0x2B10DD);
            }
            if (patched == 0)
            {
                // Rule 33: a filter that can reject everything must say so - and this one
                // falls back to the old manager seam rather than shipping no mirror at all.
                try
                {
                    HarmonyBoot.Harmony.Patch(
                        AccessTools.Method(typeof(FleckManager), nameof(FleckManager.CreateFleck)),
                        postfix: new HarmonyMethod(AccessTools.Method(
                            typeof(ABFleckMirror), nameof(ABFleckMirror.PostfixManager))));
                    Log.WarningOnce(ABLog.Tag + " fleck mirror fell back to the manager seam"
                        + " (system enumeration patched nothing); direct system.CreateFleck"
                        + " callers (SimpleFX Smoke) will not mirror across levels.", 0x2B10DE);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(ABLog.Tag + " fleck mirror fallback failed too: " + e,
                        0x2B10DF);
                }
            }
            else
            {
                ABLog.Dev("fleck mirror seated on " + patched + " CreateFleck override(s).");
            }
        }
    }
}
