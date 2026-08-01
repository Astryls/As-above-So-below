using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Which half of a cross-level link a building is.
    ///
    /// The split is presentational and topological, NOT a flow direction: once a junction
    /// and a breaker pair up, their two networks MERGE into one and resources pool exactly
    /// as they would on a single level. RimWorld's PowerNet has no concept of a one-way
    /// edge and neither do any of the three foreign pipe systems, so a genuinely
    /// directional link would mean running two nets and transferring between them - a much
    /// larger system with its own battery, priority and overflow rules.
    ///
    /// What the split buys instead is legibility: you can see which level owns the machinery
    /// and which is being fed, and the switch has an obvious home on the receiving end.</summary>
    public enum ABRiserRole
    {
        /// <summary>The machinery. Bridges to a breaker one level UP and one level DOWN, so
        /// a single junction can serve three levels.</summary>
        Junction,

        /// <summary>The cheap receiving end, carrying the switch. Bridges to whichever
        /// junction sits in the matching cell one level above or below.</summary>
        Breaker
    }

    /// <summary>
    /// Marks a ThingDef as a cross-level utility riser and names the network it carries.
    ///
    /// This is DATA ONLY and deliberately so. The riser defs exist before the linking
    /// mechanic (§30b-d) does, because the art had to land first and because a def that
    /// merely joins its own level's network is already a legal, buildable, non-broken
    /// object - it just does not reach the next band yet. Shipping the extension now means
    /// the mechanic can find its buildings by asking the def database rather than by
    /// hard-coding fifteen defNames in C#.
    ///
    /// ⚠ `network` IS OUR OWN IDENTIFIER, NOT THE HOST MOD'S. It has to be, because the
    /// three families name the same idea three different ways: Dubwise uses a `PipeType`
    /// enum member, VEF uses a `PipeNetDef` reference, and vanilla power has no name for it
    /// at all. A plain string keyed by us is the only thing all three can agree on, and it
    /// keeps foreign types out of a def that is parsed whether or not the host is loaded.
    /// </summary>
    public class ABRiserExt : DefModExtension
    {
        /// <summary>Which network this riser bridges. Values are ours: "Power",
        /// "DBH.Sewage", "DBH.Air", "Rimefeller.Oil", "Rimatomics.ColdWater",
        /// "Rimatomics.Steam", "Rimatomics.Cooling", "Rimatomics.HighVoltage",
        /// "Rimatomics.Loom", "VEF.&lt;PipeNetDef defName&gt;".</summary>
        public string network;

        /// <summary>Junction (sender-side machinery) or Breaker (receiving end with the
        /// switch). See ABRiserRole - this is about which building goes where, not about
        /// which way resources flow.</summary>
        public ABRiserRole role = ABRiserRole.Junction;

        /// <summary>Wall-mounted cabinet rather than a floor stub. Every riser is wall
        /// mounted today; the flag stays because the placement rule for a wall attachment
        /// differs from a free-standing one.</summary>
        public bool wallMounted = true;
    }

    /// <summary>
    /// THE ONE PLACE THAT ANSWERS "what is this riser joined to".
    ///
    /// Every family's merge patch funnels through <see cref="AppendPartners"/>, because the
    /// three network systems ask the same underlying question in three different shapes:
    /// VEF walks a neighbour list, vanilla power floods over cardinal cells, and Dubwise
    /// flood-fills a cell dictionary. Only the ANSWER is shared, so only the answer lives
    /// here - each patch adapts it to its own caller.
    ///
    /// ⚠ THE PARTNER CELL IS EXACTLY ONE SLOT AWAY IN Z, NOT ONE BAND "UP". Bands are
    /// aligned 1:1 in x/z and stack by Slot (band height PLUS gutter), so the counterpart of
    /// (x, z) is (x, z ± Slot). Stepping by bandHeight instead skews the offset by a growing
    /// multiple of the gutter - the same arithmetic slip that has produced the single-step
    /// descent bug nine times elsewhere in this mod.
    /// </summary>
    public static class ABRiserLink
    {
        /// <summary>An end conducts only when nothing has switched it off. Absent
        /// CompFlickable means "always on", which is what a junction has - the switch lives
        /// on the breaker so there is exactly one place to look when a link is dead.</summary>
        public static bool EndIsLive(Thing t)
        {
            if (t == null || !t.Spawned)
            {
                return false;
            }
            CompFlickable f = (t as ThingWithComps)?.GetComp<CompFlickable>();
            return f == null || f.SwitchIsOn;
        }

        /// <summary>The two cells a riser at <paramref name="cell"/> could pair with.
        /// Invalid where the band does not exist, the cell is off-map, or it lands in a
        /// gutter.</summary>
        public static bool TryPartnerCells(Map map, IntVec3 cell, out IntVec3 up, out IntVec3 down)
        {
            up = IntVec3.Invalid;
            down = IntVec3.Invalid;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            int slot = bands.Slot;
            int band = bands.BandOf(cell);
            IntVec3 u = new IntVec3(cell.x, 0, cell.z + slot);
            IntVec3 d = new IntVec3(cell.x, 0, cell.z - slot);
            if (u.InBounds(map) && !bands.InGutter(u) && bands.BandOf(u) == band + 1)
            {
                up = u;
            }
            if (d.InBounds(map) && !bands.InGutter(d) && bands.BandOf(d) == band - 1)
            {
                down = d;
            }
            return up.IsValid || down.IsValid;
        }

        /// <summary>Append every building this one is cross-level joined to.
        ///
        /// Symmetric by construction: a junction looks for breakers and a breaker looks for
        /// junctions, so whichever end a network rebuild starts from finds the other. That
        /// symmetry is what makes the merge model work without any patch having to know
        /// which side it is standing on.</summary>
        public static void AppendPartners(Thing t, List<Thing> into)
        {
            if (into == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                ABRiserExt ext = t?.def?.GetModExtension<ABRiserExt>();
                if (ext == null || t.Map == null || ext.network.NullOrEmpty() || !EndIsLive(t))
                {
                    return;
                }
                if (!TryPartnerCells(t.Map, t.Position, out IntVec3 up, out IntVec3 down))
                {
                    return;
                }
                ABRiserRole want = ext.role == ABRiserRole.Junction
                    ? ABRiserRole.Breaker
                    : ABRiserRole.Junction;
                AddAt(t.Map, up, ext.network, want, into);
                AddAt(t.Map, down, ext.network, want, into);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "riser link", t);
            }
        }

        private static void AddAt(Map map, IntVec3 cell, string network, ABRiserRole want,
            List<Thing> into)
        {
            if (!cell.IsValid)
            {
                return;
            }
            List<Thing> here = cell.GetThingList(map);
            for (int i = 0; i < here.Count; i++)
            {
                Thing candidate = here[i];
                ABRiserExt ext = candidate.def?.GetModExtension<ABRiserExt>();
                if (ext == null || ext.role != want || ext.network != network)
                {
                    continue;
                }
                if (!EndIsLive(candidate) || into.Contains(candidate))
                {
                    continue;
                }
                into.Add(candidate);
            }
        }
    }

    /// <summary>Lookup for riser defs, built once on first use.
    ///
    /// Kept separate from ABRiserExt so the extension stays a pure data class - a
    /// DefModExtension with behaviour on it is awkward to reason about when the def is
    /// loaded but its host mod is not.</summary>
    public static class ABRiserDefs
    {
        private static List<ThingDef> all;

        private static Dictionary<string, ThingDef> byNetwork;

        /// <summary>Every riser def present in this game, whatever its host mod.
        ///
        /// Resolved lazily rather than in a static constructor: the def database is not
        /// populated when static constructors run, and the compat folders mean the set
        /// genuinely differs between installs.</summary>
        public static List<ThingDef> All
        {
            get
            {
                if (all == null)
                {
                    Build();
                }
                return all;
            }
        }

        public static ThingDef ForNetwork(string network, ABRiserRole role)
        {
            if (byNetwork == null)
            {
                Build();
            }
            return network != null && byNetwork.TryGetValue(Key(network, role), out ThingDef d) ? d : null;
        }

        public static ABRiserRole? RoleOf(Thing t)
        {
            return t?.def?.GetModExtension<ABRiserExt>()?.role;
        }

        public static bool IsRiser(ThingDef def)
        {
            return def != null && def.HasModExtension<ABRiserExt>();
        }

        /// <summary>The network a spawned riser carries, or null when it is not one.</summary>
        public static string NetworkOf(Thing t)
        {
            return t?.def?.GetModExtension<ABRiserExt>()?.network;
        }

        /// <summary>Both halves of a network share one entry, so the lookup is keyed by
        /// network AND role. Two junctions on one network is a content error worth naming.</summary>
        private static string Key(string network, ABRiserRole role)
        {
            return network + "/" + role;
        }

        private static void Build()
        {
            all = new List<ThingDef>();
            byNetwork = new Dictionary<string, ThingDef>();
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ABRiserExt ext = defs[i].GetModExtension<ABRiserExt>();
                if (ext == null)
                {
                    continue;
                }
                all.Add(defs[i]);
                if (ext.network.NullOrEmpty())
                {
                    Log.Warning(ABLog.Tag + " riser " + defs[i].defName + " has no <network>; it cannot be linked.");
                    continue;
                }
                string key = Key(ext.network, ext.role);
                if (byNetwork.ContainsKey(key))
                {
                    Log.Warning(ABLog.Tag + " two risers claim " + key + ": "
                        + byNetwork[key].defName + " and " + defs[i].defName
                        + ". Keeping the first.");
                    continue;
                }
                byNetwork.Add(key, defs[i]);
            }
            ABLog.Dev("Risers discovered: " + all.Count);
        }
    }
}
