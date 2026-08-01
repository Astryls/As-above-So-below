using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
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

        /// <summary>Wall-mounted cabinet rather than a floor stub. Only affects
        /// presentation today (Graphic_Multi + rotatable); recorded because the placement
        /// rule for a wall attachment differs from a free-standing one and the mechanic
        /// will need to know which it is.</summary>
        public bool wallMounted;
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

        public static ThingDef ForNetwork(string network)
        {
            if (byNetwork == null)
            {
                Build();
            }
            return network != null && byNetwork.TryGetValue(network, out ThingDef d) ? d : null;
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
                if (byNetwork.ContainsKey(ext.network))
                {
                    Log.Warning(ABLog.Tag + " two risers claim network '" + ext.network + "': "
                        + byNetwork[ext.network].defName + " and " + defs[i].defName
                        + ". Keeping the first.");
                    continue;
                }
                byNetwork.Add(ext.network, defs[i]);
            }
            ABLog.Dev("Risers discovered: " + all.Count);
        }
    }
}
