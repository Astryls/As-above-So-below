using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// SOME THINGS BELONG TO THE GROUND LEVEL AND NOWHERE ELSE.
    ///
    /// §99's dressing pass makes bands generous by default, so the exceptions have to be
    /// stated explicitly rather than relied upon as side effects. Two categories, from the
    /// user, and they fail for completely different reasons - which is why they get two
    /// enforcement points instead of one shared "banned defs" list (rule 37: name the
    /// enforcement point).
    ///
    /// ⚠ (A) SPECIAL TREES - ANIMA AND ITS FAMILY - ARE A CELL-VALIDITY PROBLEM.
    /// An anima tree is not scattered scenery; it is a meditation focus with a psyfocus
    /// radius, a linking ritual and a grass ring, and every one of those systems assumes the
    /// colony can walk to it. <c>GenStep_SpecialTrees.CanSpawnAt</c> is the single predicate
    /// every path funnels through - map generation AND the <c>AnimaTreeSpawn</c> incident,
    /// which re-runs the genstep months into a colony on a fully live map and which NOTHING
    /// in this mod covered before now. Gating the predicate covers both with one postfix,
    /// and covers Polux trees and any modded special tree by construction.
    ///
    /// ⚠ THIS GATE IS ALWAYS ON, INCLUDING DURING GENERATION. The carve would have destroyed
    /// a generation-time anima tree in a doomed band anyway, so during generation the gate is
    /// mostly a waste-saver; it is the INCIDENT that made it necessary, because by then the
    /// carve is long finished and a tree placed in a cavern is a tree that stays there.
    /// Rule 20: earlier work turns on every readiness gate - the incident path only became
    /// reachable at all once bands were a thing.
    ///
    /// ⚠ (B) ANCIENT WRECKAGE IS A PROVENANCE PROBLEM, NOT A CELL PROBLEM.
    /// A basement cave floor is ordinary standable rock. A wreck sitting on it passes every
    /// test the cell can answer - it is not floating, not in a wall, not in the void. What is
    /// wrong is the LEVEL, and as ABAirSpawnGuard already puts it: no property of the cell can
    /// tell you that, only knowing who put it there can. So wreckage is handled at the three
    /// places that can actually place it, and each was already covered or is covered now:
    ///
    ///   1. MAP-GEN SCATTERERS - vanilla flags every one of them <c>isJunk</c>, and
    ///      ABBandDressing refuses eligibility on that flag. This is the primary control.
    ///   2. VVE / VEHICLE FRAMEWORK PROPS - these are not gensteps at all. They are VEF
    ///      <c>ObjectSpawnsDef</c> content driven from a <c>LongEventHandler</c> callback
    ///      after the whole generation event drains, and ABAirSpawnGuard's decoration window
    ///      already pins every unfactioned ground-resting thing spawned in that window to the
    ///      surface band. That rule predates this file and needed no change.
    ///   3. ANYTHING ELSE - caught by the audit below, which is the point of it.
    ///
    /// ⚠ THE AUDIT IS AN ASSERTION, NOT A CLEANUP (rule 15: assert always, narrate on
    /// request). It does not move or delete anything. If wreckage ever does reach a band we
    /// want the log to name the def, because that means a FOURTH placement path exists and
    /// the right response is to find it, not to sweep up after it every colony (rule 65: the
    /// fourth failure is a defective input).
    /// </summary>
    internal static class ABGroundOnly
    {
        /// <summary>defName fragments that mark ancient wreckage props. Rebuilt into a def set
        /// once per game so the audit is a hash lookup rather than a substring scan per
        /// thing.</summary>
        private static readonly string[] WreckFragments =
        {
            "Wreck", "Wreckage", "Exostrider", "Tunneler", "Tuneller", "Crashed",
            "AncientDrillPlatform", "ChunkMechanoidSlag", "AncientTunnelerClaw"
        };

        private static HashSet<ThingDef> wreckDefs;

        private static HashSet<ThingDef> WreckDefs
        {
            get
            {
                if (wreckDefs == null)
                {
                    wreckDefs = new HashSet<ThingDef>();
                    List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
                    for (int i = 0; i < all.Count; i++)
                    {
                        ThingDef d = all[i];
                        if (d?.defName == null)
                        {
                            continue;
                        }
                        for (int f = 0; f < WreckFragments.Length; f++)
                        {
                            if (d.defName.IndexOf(WreckFragments[f],
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                wreckDefs.Add(d);
                                break;
                            }
                        }
                    }
                }
                return wreckDefs;
            }
        }

        internal static bool IsWreckage(ThingDef def)
        {
            return def != null && WreckDefs.Contains(def);
        }

        /// <summary>Post-dressing census. Dev-gated because it walks every band, and silent
        /// when clean - a passing assertion should cost nothing to read.</summary>
        internal static void AuditBands(Map map, ABBandMap bands)
        {
            if (map == null || bands == null || !bands.Banded || !ABLog.DevEnabled)
            {
                return;
            }
            try
            {
                Dictionary<string, int> found = null;
                List<Thing> all = map.listerThings.AllThings;
                for (int i = 0; i < all.Count; i++)
                {
                    Thing t = all[i];
                    if (t == null || !t.Spawned || !IsWreckage(t.def))
                    {
                        continue;
                    }
                    if (bands.BandOf(t.Position) == bands.surfaceBand)
                    {
                        continue;
                    }
                    if (found == null)
                    {
                        found = new Dictionary<string, int>();
                    }
                    found.TryGetValue(t.def.defName, out int n);
                    found[t.def.defName] = n + 1;
                }
                if (found == null)
                {
                    return;
                }
                string report = string.Empty;
                foreach (KeyValuePair<string, int> kv in found)
                {
                    report += (report.Length > 0 ? ", " : "") + kv.Key + " x" + kv.Value;
                }
                Log.Warning(ABLog.Tag + " ancient wreckage reached a non-surface band: "
                    + report + ". Neither the isJunk blocklist nor the decoration window"
                    + " caught it, so a placement path exists that this mod does not know"
                    + " about - find the placer rather than deleting the thing.");
            }
            catch
            {
                // An audit must never be the thing that breaks a colony.
            }
        }
    }

    /// <summary>
    /// Anima (and every other special tree) is surface-band only.
    ///
    /// A postfix on the predicate rather than on <c>Generate</c>, because the predicate is
    /// what BOTH callers share: the map-gen genstep and <c>IncidentWorker_AnimaTreeSpawn</c>,
    /// which drives the very same genstep on a live map long after generation. Patching
    /// Generate would have fixed the half that the carve already handled and missed the half
    /// that actually needed fixing (rule 25's cousin: patch what everything funnels through,
    /// not what you happened to be looking at).
    ///
    /// ⚠ IT NEVER STARVES THE SEARCH. <c>GenStep_SpecialTrees.Generate</c> gives up after
    /// 1000 iterations with <c>Log.Error("Could not place ...")</c>, so a gate that can reject
    /// everything would turn into an engine error attributed to us. It cannot here: the
    /// surface band is a full 126x126 level of ordinary biome terrain, i.e. exactly the map an
    /// unbanded colony would have had, so any tile that could ever host an anima tree still
    /// can. The gate removes candidates the tree could not have kept anyway.
    /// </summary>
    [HarmonyPatch(typeof(GenStep_SpecialTrees), nameof(GenStep_SpecialTrees.CanSpawnAt))]
    public static class Patch_GenStep_SpecialTrees_ABGroundOnly
    {
        private static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (!__result || map == null)
            {
                return;
            }
            try
            {
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    // Mid-generation the component is set up but Banded may still be false;
                    // the pending layout is the authority in that window (§14 engine facts).
                    if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect pending, out _))
                    {
                        return;
                    }
                    if (!pending.Contains(c))
                    {
                        __result = false;
                    }
                    return;
                }
                if (bands.BandOf(c) != bands.surfaceBand)
                {
                    __result = false;
                }
            }
            catch
            {
                // Leave vanilla's verdict standing rather than losing the tree entirely.
            }
        }
    }
}
