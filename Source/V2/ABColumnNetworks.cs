using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>Marks a generated carrier and names the network it carries. Our id, not the
    /// host's - see ABColumnNetworks for why a plain string is the only thing all the
    /// families can agree on.</summary>
    public class ABCarrierExt : DefModExtension
    {
        public string network;
    }

    /// <summary>The four utility families a column can carry. One buildable per type; every
    /// detected network is sorted into exactly one bucket by <see cref="ABColumnNetworks.Classify"/>.
    /// GAS IS DELIBERATELY NOT A TYPE - fluids and gases share the Pipe column (user call,
    /// window 7): to a pipe network the phase of the contents is flavor, not topology.</summary>
    public enum ABColumnType
    {
        Power,
        Pipe,
        Climate,
        Data
    }

    /// <summary>Stamped on each column ThingDef to name which bucket of networks it offers.</summary>
    public class ABColumnTypeExt : DefModExtension
    {
        public ABColumnType columnType = ABColumnType.Pipe;
    }

    /// <summary>
    /// THE DATA ESCAPE HATCH. Anything the three probes miss gets support from XML alone.
    ///
    /// A third party (or we) can add a network without touching this assembly: name a
    /// template ThingDef to clone and an id to key it by. That is the whole contract.
    ///
    /// ⚠ REFERENCED IN XML AS &lt;AsAboveSoBelow.ABColumnAdapterDef&gt;. A custom Def
    /// subclass in a non-vanilla namespace is invisible by short name - GenTypes only
    /// bare-name-resolves inside the vanilla namespace whitelist - so the node MUST be
    /// fully qualified or the def silently never loads.
    /// </summary>
    public class ABColumnAdapterDef : Def
    {
        /// <summary>Our network id. Free-form; only has to be unique.</summary>
        public string network;

        /// <summary>defName of the host ThingDef to clone as the carrier.</summary>
        public string templateDef;

        /// <summary>Optional column bucket override: Power, Pipe, Climate or Data. A plain
        /// string rather than the enum so a typo degrades to a scan note instead of a def
        /// parse error.</summary>
        public string columnType;
    }

    /// <summary>One detected network, and everything the column UI needs to present it.</summary>
    public sealed class ABNetwork
    {
        /// <summary>Our key. Stable across saves, so it is what the column scribes.</summary>
        public string id;

        /// <summary>Which probe found it. Report-only, but it is the first thing you want
        /// when a network is missing or duplicated.</summary>
        public string probe;

        /// <summary>Which column offers this network. Computed by the classifier, or forced
        /// by an adapter def.</summary>
        public ABColumnType type = ABColumnType.Pipe;

        /// <summary>Adapter-supplied override, applied by Classify. Null means classify.</summary>
        public string forcedType;

        /// <summary>The host's own conduit, cloned into the carrier and used as the source
        /// of the toggle's icon, label, research gate and price.</summary>
        public ThingDef template;

        /// <summary>The generated hidden carrier.</summary>
        public ThingDef carrier;

        public string LabelCap => template != null ? template.LabelCap.ToString() : id;

        public List<ResearchProjectDef> Research => template?.researchPrerequisites;

        /// <summary>Unlocked when every research the host demands of its own conduit is
        /// done. Gating the TOGGLE rather than the column keeps one buildable that grows
        /// capability as the colony researches, instead of fifteen buildables.</summary>
        public bool ResearchDone
        {
            get
            {
                List<ResearchProjectDef> req = Research;
                if (req == null)
                {
                    return true;
                }
                for (int i = 0; i < req.Count; i++)
                {
                    if (req[i] != null && !req[i].IsFinished)
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>
    /// AUTOMATIC NETWORK DETECTION, AND THE GENERATED CARRIERS.
    ///
    /// THE PROBLEM THIS REPLACES. The riser system covered networks with a WHITELIST: 30
    /// hand-generated ThingDefs across six Compat/ folders, gated by six hardcoded
    /// packageIds in LoadFolders, maintained by a python script. Any PipeSystem mod not on
    /// that list got nothing, and there are dozens. A new Rimatomics pipe mode got nothing.
    /// The ceiling was not a bug, it was the design.
    ///
    /// ⚠ THE FIX IS TO CLONE THE HOST'S OWN CONDUIT RATHER THAN AUTHOR A DEF PER NETWORK.
    /// Every field the player sees on a column's toggle row - icon, label, research gate,
    /// price - is taken straight from the template. That means it is already localized by
    /// the host, already recognizable, already balanced against the pipe it extends, and
    /// already correct for a mod written after this file. ZERO AUTHORED CONTENT PER
    /// SUPPORTED MOD is the entire point; the moment we hand-write a label per network we
    /// are back to the whitelist with extra steps.
    ///
    /// ⚠ THE HOOK IS `GenerateImpliedDefs_PreResolve`, AND BOTH HALVES OF THAT MATTER.
    /// Checked against 1.6's PlayDataLoader.DoPlayLoad:
    ///   * `DirectXmlCrossRefLoader.ResolveAllWantedCrossReferences` has ALREADY RUN, so
    ///     `CompProperties_Resource.pipeNet`, `researchPrerequisites`, `costList` and
    ///     `designationCategory` are live objects rather than pending name strings. Scanning
    ///     any earlier reads nulls.
    ///   * `DefDatabase&lt;ThingDef&gt;.ResolveAllReferences()` has NOT yet run, so our clones
    ///     get resolved with everything else. A def injected post-resolve is half-built.
    ///   * `ShortHashGiver.GiveAllShortHashes()` runs at the very end, so hashes come free.
    ///
    /// ⚠ AND `AddImpliedDef` MUST BE PINNED TO `ThingDef`. It infers its type parameter from
    /// the argument's COMPILE-TIME type and calls `DefDatabase&lt;T&gt;.Add`. Rimatomics
    /// templates are `RimatomicsThingDef`, a ThingDef SUBCLASS - clone one, let inference
    /// run, and it lands in `DefDatabase&lt;RimatomicsThingDef&gt;`, a different database that
    /// nothing enumerating ThingDef will ever see. No error, no log line, the carrier simply
    /// does not exist. Pinning keeps the runtime type intact while registering it where the
    /// game looks.
    /// </summary>
    public static class ABColumnNetworks
    {
        private static List<ABNetwork> all;

        private static readonly List<string> log = new List<string>();

        private static int generated;

        private static int skipped;

        /// <summary>Every network this install has, in scan order. Empty on a vanilla-only
        /// game except for power, which is always present.</summary>
        public static List<ABNetwork> All
        {
            get
            {
                if (all == null)
                {
                    Scan();
                }
                return all;
            }
        }

        /// <summary>Every generated carrier def, for O(1) "is this one of ours" tests in
        /// the render hot path. A `HasModExtension` walk per printed thing would be far
        /// too slow to sit in a section-layer prefix.</summary>
        private static readonly HashSet<ThingDef> carrierDefs = new HashSet<ThingDef>();

        public static bool IsCarrier(ThingDef d)
        {
            return d != null && carrierDefs.Contains(d);
        }

        public static ABNetwork ById(string id)
        {
            List<ABNetwork> list = All;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].id == id)
                {
                    return list[i];
                }
            }
            return null;
        }

        /// <summary>Rule 15: assert always, narrate on request. A wrong template pick is the
        /// most likely failure here and it is invisible in game - the column offers a toggle
        /// that links the wrong thing, or a network is silently absent. One line of fact per
        /// network beats a round of guessing.</summary>
        public static string Report()
        {
            StringBuilder sb = new StringBuilder();
            List<ABNetwork> list = All;
            sb.AppendLine(ABLog.Tag + " NETWORK SCAN: " + list.Count + " network(s), "
                + generated + " carrier(s) generated, " + skipped + " skipped.");
            for (int i = 0; i < list.Count; i++)
            {
                ABNetwork n = list[i];
                sb.AppendLine("  " + n.id);
                sb.AppendLine("      type:     " + n.type + (n.forcedType != null ? " (forced)" : ""));
                sb.AppendLine("      probe:    " + n.probe);
                sb.AppendLine("      template: " + (n.template != null ? n.template.defName : "NONE")
                    + "  label=\"" + n.LabelCap + "\""
                    + "  from=" + (n.template?.modContentPack?.Name ?? "?"));
                sb.AppendLine("      carrier:  " + (n.carrier != null ? n.carrier.defName : "NOT GENERATED"));
                sb.AppendLine("      research: " + DescribeResearch(n) + "  done=" + n.ResearchDone);
            }
            if (log.Count > 0)
            {
                sb.AppendLine("  SCAN NOTES:");
                for (int i = 0; i < log.Count; i++)
                {
                    sb.AppendLine("    " + log[i]);
                }
            }
            return sb.ToString();
        }

        private static string DescribeResearch(ABNetwork n)
        {
            List<ResearchProjectDef> req = n.Research;
            if (req == null || req.Count == 0)
            {
                return "(none)";
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < req.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(req[i]?.defName ?? "null");
            }
            return sb.ToString();
        }

        /// <summary>Idempotent. Called from the def-generation postfix, and lazily by All so
        /// a dev tool can still report on a game where the postfix somehow did not run.</summary>
        public static void Scan()
        {
            if (all != null)
            {
                return;
            }
            all = new List<ABNetwork>();
            try
            {
                ProbeVanillaPower();
                ProbePipeSystem();
                ProbeDubwise();
                ProbeRimIOT();
                ProbeEccentricGrid();
                ProbeAdapters();
                for (int i = 0; i < all.Count; i++)
                {
                    Classify(all[i]);
                }
                for (int i = 0; i < all.Count; i++)
                {
                    BuildCarrier(all[i]);
                }
            }
            catch (Exception e)
            {
                Log.Error(ABLog.Tag + " network scan failed; cross-level utilities are "
                    + "disabled for this session. " + e);
            }
            ABLog.Dev("Network scan: " + all.Count + " network(s), " + generated + " carrier(s).");
        }

        // ------------------------------------------------------------------ probes

        /// <summary>Always present. Vanilla power needs no detection, only a template.</summary>
        private static void ProbeVanillaPower()
        {
            ThingDef conduit = DefDatabase<ThingDef>.GetNamedSilentFail("PowerConduit");
            if (conduit == null)
            {
                Note("PowerConduit not found; vanilla power columns are unavailable.");
                return;
            }
            Add("Power", "vanilla", conduit);
        }

        /// <summary>
        /// VEF PipeSystem: every ThingDef carrying `CompProperties_Resource`, grouped by the
        /// `pipeNet` it names.
        ///
        /// ⚠ THIS IS THE PROBE THAT LIFTS THE CEILING. Vanilla Chemfuel Expanded, Vanilla
        /// Temperature Expanded, Ushanka's Luciferium Expansion and every other VE pipe mod
        /// ship thin content over this one framework and add nothing of their own to the
        /// network layer. Grouping by PipeNetDef therefore covers all of them, plus every VE
        /// pipe mod released after this file was written, without naming a single one.
        /// </summary>
        private static void ProbePipeSystem()
        {
            Type props = AccessTools.TypeByName("PipeSystem.CompProperties_Resource");
            if (props == null)
            {
                return; // no VE pipe mod installed - the normal case
            }
            FieldInfo pipeNet = AccessTools.Field(props, "pipeNet");
            if (pipeNet == null)
            {
                Note("PipeSystem.CompProperties_Resource has no 'pipeNet' field; the "
                    + "framework's internals may have changed. VE pipe columns disabled.");
                return;
            }
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                List<CompProperties> comps = defs[i].comps;
                if (comps == null)
                {
                    continue;
                }
                for (int j = 0; j < comps.Count; j++)
                {
                    // Exact type, not IsInstanceOfType: CompProperties_PipeValve DERIVES from
                    // CompProperties_Resource, and a valve is a switch rather than a conduit.
                    // Accepting subclasses would let a valve win the cheapest-template
                    // contest and make every carrier a switchable valve.
                    if (comps[j] == null || comps[j].GetType() != props)
                    {
                        continue;
                    }
                    Def net = pipeNet.GetValue(comps[j]) as Def;
                    if (net == null)
                    {
                        continue;
                    }
                    Consider("VEF." + net.defName, "PipeSystem", defs[i]);
                }
            }
        }

        /// <summary>
        /// The Dubwise family: Bad Hygiene, Rimefeller and Rimatomics.
        ///
        /// ⚠ MATCHED BY SHAPE, NOT BY NAME. All three ship their own
        /// `&lt;Host&gt;.CompProperties_Pipe` with a `mode` enum, in three separate
        /// assemblies with no shared base we can name. Matching on the type's SHORT name
        /// plus the presence of a `mode` field covers all three with one probe, covers a
        /// mode added by a future update, and covers a fourth Dubwise-shaped mod we have
        /// never heard of. The namespace goes into the id so two hosts cannot collide on a
        /// mode name.
        /// </summary>
        private static void ProbeDubwise()
        {
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                List<CompProperties> comps = defs[i].comps;
                if (comps == null)
                {
                    continue;
                }
                for (int j = 0; j < comps.Count; j++)
                {
                    Type t = comps[j]?.GetType();
                    if (t == null || t.Name != "CompProperties_Pipe")
                    {
                        continue;
                    }
                    FieldInfo mode = AccessTools.Field(t, "mode");
                    if (mode == null)
                    {
                        continue;
                    }
                    object v = mode.GetValue(comps[j]);
                    if (v == null)
                    {
                        continue;
                    }
                    string host = t.Namespace ?? "Pipe";
                    Consider(host + "." + v, "Dubwise", defs[i]);
                }
            }
        }

        /// <summary>
        /// RimIOT Logistic Matrix: item logistics over cables. One network, always Data.
        ///
        /// The template is the def whose CompProperties_NetworkNode declares role Cable -
        /// interfaces and input connectors carry the SAME comp type with other roles, and a
        /// carrier cloned from one of those would drag interface power draw and placement
        /// rules along with it. Role is an enum in a foreign assembly, so it is compared by
        /// name string, never by value.
        /// </summary>
        private static void ProbeRimIOT()
        {
            Type props = AccessTools.TypeByName("RimIOT.CompProperties_NetworkNode");
            if (props == null)
            {
                return; // RimIOT not installed - the normal case
            }
            FieldInfo role = AccessTools.Field(props, "role");
            if (role == null)
            {
                Note("RimIOT.CompProperties_NetworkNode has no 'role' field; the mod's "
                    + "internals may have changed. RimIOT columns disabled.");
                return;
            }
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                List<CompProperties> comps = defs[i].comps;
                if (comps == null)
                {
                    continue;
                }
                for (int j = 0; j < comps.Count; j++)
                {
                    if (comps[j] == null || comps[j].GetType() != props)
                    {
                        continue;
                    }
                    object v = role.GetValue(comps[j]);
                    if (v == null || v.ToString() != "Cable")
                    {
                        continue;
                    }
                    Consider("RimIOT.Network", "RimIOT", defs[i]);
                }
            }
            ABNetwork n = Find("RimIOT.Network");
            if (n != null && n.forcedType == null)
            {
                n.forcedType = "Data";
            }
        }

        /// <summary>
        /// Eccentric Defense Grid: remote-turret control conduits. One network, always Data.
        ///
        /// Every grid participant - turrets, consoles, capacitors, the conduit itself -
        /// carries CompProperties_DefenseConduit, so the probe accepts them all as
        /// candidates and lets Score() do the picking: the plain 1x1 conduit beats a turret
        /// on every axis Score punishes (size, cost, machinery).
        /// </summary>
        private static void ProbeEccentricGrid()
        {
            Type props = AccessTools.TypeByName("EccentricDefenseGrid.CompProperties_DefenseConduit");
            if (props == null)
            {
                return; // Defense Grid not installed - the normal case
            }
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                List<CompProperties> comps = defs[i].comps;
                if (comps == null)
                {
                    continue;
                }
                for (int j = 0; j < comps.Count; j++)
                {
                    if (comps[j] != null && comps[j].GetType() == props)
                    {
                        Consider("Eccentric.DefenseGrid", "EccentricGrid", defs[i]);
                        break;
                    }
                }
            }
            ABNetwork n = Find("Eccentric.DefenseGrid");
            if (n != null && n.forcedType == null)
            {
                n.forcedType = "Data";
            }
        }

        /// <summary>The XML escape hatch. Runs last so a hand-authored adapter can override
        /// a probe's template pick for a network the heuristic got wrong.</summary>
        private static void ProbeAdapters()
        {
            List<ABColumnAdapterDef> adapters = DefDatabase<ABColumnAdapterDef>.AllDefsListForReading;
            for (int i = 0; i < adapters.Count; i++)
            {
                ABColumnAdapterDef a = adapters[i];
                if (a.network.NullOrEmpty() || a.templateDef.NullOrEmpty())
                {
                    Note("adapter " + a.defName + " needs both <network> and <templateDef>.");
                    continue;
                }
                ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(a.templateDef);
                if (t == null)
                {
                    // Not an error: an adapter for a mod the player does not own.
                    continue;
                }
                ABNetwork existing = Find(a.network);
                if (existing != null)
                {
                    existing.template = t;
                    existing.forcedType = a.columnType ?? existing.forcedType;
                    existing.probe = "adapter:" + a.defName + " (overrode " + existing.probe + ")";
                    continue;
                }
                Add(a.network, "adapter:" + a.defName, t);
                Find(a.network).forcedType = a.columnType;
            }
        }

        // ------------------------------------------------------- template selection

        /// <summary>
        /// Offer a candidate template for a network, keeping the better of the two.
        ///
        /// "Better" means CHEAPEST BUILDABLE PLAIN CONDUIT, because that is what a column
        /// extends. Storage tanks, refineries and valves all carry the same comp and would
        /// otherwise be equally valid clones - and cloning a refinery would give the carrier
        /// a work table's comps, a production graphic and a four-cell footprint.
        /// </summary>
        private static void Consider(string id, string probe, ThingDef candidate)
        {
            ABNetwork existing = Find(id);
            if (existing == null)
            {
                Add(id, probe, candidate);
                return;
            }
            if (Score(candidate) < Score(existing.template))
            {
                existing.template = candidate;
            }
        }

        /// <summary>Lower is more conduit-like. Ordered by how badly each trait disqualifies
        /// a candidate rather than by cost alone, so a free-but-huge reservoir never beats a
        /// steel conduit.</summary>
        private static int Score(ThingDef d)
        {
            if (d == null)
            {
                return int.MaxValue;
            }
            int s = 0;
            if (d.building != null && d.building.isEdifice)
            {
                s += 10000; // a wall or a tank, not a pipe
            }
            if (d.Size.x != 1 || d.Size.z != 1)
            {
                s += 10000;
            }
            if (d.designationCategory == null)
            {
                s += 5000; // not something the player builds
            }
            if (d.tickerType != TickerType.Never)
            {
                s += 2000; // machinery, not plumbing
            }
            if (d.costList != null)
            {
                for (int i = 0; i < d.costList.Count; i++)
                {
                    s += d.costList[i].count;
                }
            }
            s += Mathf_RoundToIntSafe(d.GetStatValueAbstract(StatDefOf.WorkToBuild)) / 100;
            return s;
        }

        private static int Mathf_RoundToIntSafe(float f)
        {
            return f > 0f && f < 1000000f ? (int)f : 0;
        }

        // ------------------------------------------------------------ classification

        /// <summary>
        /// Sort a network into its column bucket.
        ///
        /// ⚠ CLASSIFY ON NAMES, NEVER ON `PipeNetDef.resource.unit`. The unit field is NOT
        /// a phase signal: Helixien GAS declares `unit=l`, and so does VTE's "Efficiency",
        /// which is not a substance at all. Only Oxygen (u3) and Hemogen (packs) deviate,
        /// which is far too thin to hang a taxonomy on. Names are what authors actually
        /// write carefully.
        ///
        /// The haystack is our id + the template's defName + its label, lowercased. Keyword
        /// order matters: Power and Data are checked before Climate so "HighVoltage" can
        /// never fall into a later bucket by accident. Default is Pipe - on the evidence of
        /// this install, everything unmatched is a fluid or a gas, and a wrong Pipe guess
        /// is one &lt;columnType&gt; adapter line to fix.
        /// </summary>
        private static void Classify(ABNetwork n)
        {
            if (n.forcedType != null && Enum.TryParse(n.forcedType, true, out ABColumnType forced))
            {
                n.type = forced;
                return;
            }
            if (n.forcedType != null)
            {
                Note("adapter forced unknown columnType '" + n.forcedType + "' for " + n.id
                    + "; expected Power, Pipe, Climate or Data. Classifying instead.");
            }
            if (n.id == "Power")
            {
                n.type = ABColumnType.Power;
                return;
            }
            string hay = (n.id + " " + (n.template?.defName ?? "") + " "
                + (n.template?.label ?? "")).ToLowerInvariant();
            if (Matches(hay, PowerKeys))
            {
                n.type = ABColumnType.Power;
            }
            else if (Matches(hay, DataKeys))
            {
                n.type = ABColumnType.Data;
            }
            else if (Matches(hay, ClimateKeys))
            {
                n.type = ABColumnType.Climate;
            }
            else
            {
                n.type = ABColumnType.Pipe;
            }
        }

        private static readonly string[] PowerKeys = { "power", "voltage", "electr" };

        private static readonly string[] DataKeys = { "loom", "data", "signal", "fibre", "fiber" };

        // ⚠ "cool" not "cold": Rimatomics ColdWater is reactor PLUMBING and belongs to
        // Pipe, while Rimatomics Cooling is thermal loop and belongs here. And no "gas"
        // anywhere - gases are Pipe by design.
        private static readonly string[] ClimateKeys = { "air", "efficien", "cool", "heat", "therm", "vent", "clima", "duct" };

        private static bool Matches(string hay, string[] keys)
        {
            for (int i = 0; i < keys.Length; i++)
            {
                if (hay.Contains(keys[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static ABNetwork Find(string id)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].id == id)
                {
                    return all[i];
                }
            }
            return null;
        }

        private static void Add(string id, string probe, ThingDef template)
        {
            all.Add(new ABNetwork { id = id, probe = probe, template = template });
        }

        private static void Note(string s)
        {
            log.Add(s);
        }

        // ---------------------------------------------------------- carrier building

        /// <summary>
        /// Clone the template into a hidden carrier.
        ///
        /// ⚠ ONE COMP PER CARRIER IS THE WHOLE REASON CARRIERS EXIST. A single column
        /// building carrying N host comps would be silently broken: every host does
        /// `GetComp&lt;CompPipe&gt;()` somewhere and that returns the FIRST comp, so such a
        /// column would carry exactly one network and quietly drop the rest. Stacking N
        /// single-comp things in one cell instead means each host sees precisely the shape
        /// it already understands.
        ///
        /// ⚠ AND THAT STACKING IS LEGAL, CHECKED AGAINST `GenSpawn.SpawningWipes` IN 1.6.
        /// Two non-edifice buildings of different defs, neither transmitting power, with no
        /// `blocksAltitudes` overlap, do not wipe each other. Better: the method's early-out
        /// `if (!ignoreDestroyable &amp;&amp; !thingDef2.destroyable) return false;` means a carrier
        /// marked `destroyable=false` can never be wiped by ANYTHING - including a player
        /// dropping a conduit on the column's cell.
        ///
        /// ⚠ `destroyable=false` ALSO MEANS `Destroy()` LOGS AN ERROR. Tear carriers down
        /// with `DeSpawn()`.
        /// </summary>
        private static void BuildCarrier(ABNetwork n)
        {
            if (n.template == null)
            {
                skipped++;
                return;
            }
            try
            {
                ThingDef c = (ThingDef)Activator.CreateInstance(n.template.GetType());
                CopyFields(n.template, c, c.GetType());

                c.defName = CarrierDefName(n.id);
                c.label = n.template.label;
                c.description = n.template.description;
                c.modContentPack = n.template.modContentPack;

                // ⚠ THESE ARE SHARED REFERENCES UNTIL COPIED. A field-by-field clone copies
                // the POINTER to building/graphicData/comps, so mutating them below would
                // edit the host's own conduit def. Every object we touch has to be its own.
                c.building = CloneOf(n.template.building);
                c.graphicData = CloneOf(n.template.graphicData);
                c.comps = CloneComps(n.template.comps);
                c.statBases = n.template.statBases != null
                    ? new List<StatModifier>(n.template.statBases)
                    : null;

                // Never built, never seen, never touched.
                // ⚠ THERE IS NO `menuHidden` ON ThingDef IN 1.6. A null designationCategory
                // is what keeps a def out of the architect menu; ArchitectCategoryTab builds
                // its list from designationCategoryDef.AllResolvedDesignators, so a def that
                // belongs to no category is unreachable by construction.
                c.designationCategory = null;
                c.designatorDropdown = null;
                c.researchPrerequisites = null;
                c.costList = null;
                c.costStuffCount = 0;
                c.stuffCategories = null;
                c.selectable = false;
                c.neverMultiSelect = true;
                c.destroyable = false;
                c.useHitPoints = false;
                c.scatterableOnMapGen = false;
                c.blocksAltitudes = null;

                // ⚠ THE CARRIER KEEPS THE TEMPLATE'S DRAWER AND GRAPHIC, AND THAT IS A
                // DELIBERATE REVERSAL OF SLICE 1. `DrawerType.None` was forced back when
                // carriers were imagined living in GUTTER cells, which must never be
                // revealed. They do not: a carrier lives in the column's own cell and in
                // the cell one Slot up, both ordinary band cells. Two field requests both
                // reduce to this one field:
                //   1. `SectionLayer_Things.Regenerate` filters on
                //      `thing.def.drawerType != DrawerType.None`, so an invisible carrier
                //      is excluded from EVERY section layer - including
                //      SectionLayer_ThingsPowerGrid, the overlay RimWorld switches on
                //      while the player is placing a conduit. Keeping the template's
                //      drawer is what makes a column read as part of the grid at exactly
                //      the moment the player is deciding where to run pipe.
                //   2. The up-cell carrier then IS the "corresponding conduit on the floor
                //      above", drawn in the host's own art, with no second def to author
                //      and no auto-construction to balance.
                // ⚠ LINKING SURVIVES THE CLONE. Conduits and pipes link by
                // `graphicData.linkFlags`, never by defName, so a carrier joins the
                // player's real pipe runs visually instead of drawing an isolated stub.
                c.castEdgeShadows = false;
                c.blockWind = false;
                c.blockLight = false;
                c.fillPercent = 0f;
                c.rotatable = false;
                c.size = new IntVec2(1, 1);

                if (c.building != null)
                {
                    c.building.isEdifice = false;
                    c.building.isInert = true;
                    c.building.claimable = false;
                    c.building.ai_chillDestination = false;
                    c.building.canPlaceOverWall = false;
                }

                if (c.modExtensions == null)
                {
                    c.modExtensions = new List<DefModExtension>();
                }
                else
                {
                    c.modExtensions = new List<DefModExtension>(c.modExtensions);
                }
                c.modExtensions.Add(new ABCarrierExt { network = n.id });

                // ⚠ PINNED TO ThingDef. See the banner: inference would file a
                // RimatomicsThingDef clone under its own database and nothing would find it.
                DefGenerator.AddImpliedDef<ThingDef>(c);
                carrierDefs.Add(c);
                n.carrier = c;
                generated++;
            }
            catch (Exception e)
            {
                skipped++;
                Note("carrier generation failed for " + n.id + " (template "
                    + n.template.defName + "): " + e.Message);
            }
        }

        /// <summary>⚠ defNames may not end with a digit - RimWorld rejects that on ThingDefs
        /// specifically, and a mode enum or a net name ending in "2" is entirely plausible.</summary>
        private static string CarrierDefName(string id)
        {
            StringBuilder sb = new StringBuilder("AB_Carrier_");
            for (int i = 0; i < id.Length; i++)
            {
                char ch = id[i];
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }
            if (char.IsDigit(sb[sb.Length - 1]))
            {
                sb.Append('X');
            }
            return sb.ToString();
        }

        private static List<CompProperties> CloneComps(List<CompProperties> src)
        {
            if (src == null)
            {
                return null;
            }
            List<CompProperties> outp = new List<CompProperties>(src.Count);
            for (int i = 0; i < src.Count; i++)
            {
                if (src[i] == null)
                {
                    continue;
                }
                // ⚠ COPY THE PROPS OBJECT, DO NOT SHARE IT. ResolveReferences runs per def
                // and is handed the parent; a shared CompProperties would be resolved twice
                // against two different parents, which is a class of bug nobody wants to
                // debug inside a foreign assembly.
                CompProperties copy = CloneOf(src[i]);
                if (copy != null)
                {
                    outp.Add(copy);
                }
            }
            return outp;
        }

        /// <summary>Shallow field-for-field copy preserving the runtime type. Used for the
        /// handful of objects a carrier must own rather than share.</summary>
        private static T CloneOf<T>(T src) where T : class
        {
            if (src == null)
            {
                return null;
            }
            try
            {
                T dst = (T)Activator.CreateInstance(src.GetType());
                CopyFields(src, dst, src.GetType());
                return dst;
            }
            catch
            {
                return src; // sharing beats crashing
            }
        }

        /// <summary>Copy every instance field declared anywhere up the hierarchy. Walking
        /// BaseType is required: GetFields does not return private fields of base classes,
        /// and ThingDef inherits a good deal from BuildableDef and Def.</summary>
        private static void CopyFields(object src, object dst, Type t)
        {
            while (t != null && t != typeof(object))
            {
                FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (int i = 0; i < fields.Length; i++)
                {
                    if (fields[i].IsLiteral || fields[i].IsInitOnly)
                    {
                        continue;
                    }
                    fields[i].SetValue(dst, fields[i].GetValue(src));
                }
                t = t.BaseType;
            }
        }
    }

    /// <summary>
    /// The one hook. See ABColumnNetworks' banner for why this exact moment and no other.
    /// </summary>
    [HarmonyPatch(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve))]
    public static class Patch_DefGenerator_ABColumnCarriers
    {
        private static void Postfix()
        {
            ABColumnNetworks.Scan();
        }
    }

    /// <summary>
    /// A CARRIER SHARING A CELL WITH A COLUMN DRAWS NOTHING - BUT ONLY ON THE MAP MESH.
    ///
    /// §62.J made carriers visible so the up-cell one reads as "the conduit on the floor
    /// above" and so columns appear in network overlays. The cost was clutter: the carrier
    /// in the column's OWN cell drew pipe and wire stubs poking out from under the column
    /// art. Hiding it must not undo the overlay half of that change.
    ///
    /// ⚠ THE TWO DRAW PATHS ARE SEPARATE METHODS, WHICH IS THE WHOLE TRICK. The map mesh
    /// prints via `SectionLayer_ThingsGeneral.TakePrintFrom` -&gt; `Thing.Print`, while the
    /// power overlay prints via `SectionLayer_ThingsPowerGrid.TakePrintFrom` -&gt;
    /// `ThingWithComps.PrintForPowerGrid` -&gt; `CompPrintForPowerGrid`. Suppressing only the
    /// GENERAL layer therefore hides the art in normal play and leaves every overlay - the
    /// vanilla power grid, and each pipe mod's own layer, which is a different class again
    /// - completely untouched. Patching `Thing.Print` instead would have been wrong twice:
    /// broader than needed, and it would still have missed the power overlay entirely.
    ///
    /// ⚠ THE CONDITION IS "A COLUMN STANDS HERE", NOT "THIS IS THE LOWER CARRIER". That is
    /// self-maintaining for stacks: a column built on the cell above another column hides
    /// the shared carrier there too, which is exactly right, and removing a column
    /// re-reveals it because the despawn dirties the section.
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_ThingsGeneral), "TakePrintFrom")]
    public static class Patch_SectionLayer_ABHideCarrierUnderColumn
    {
        private static bool Prefix(Thing t)
        {
            if (t == null || !ABColumnNetworks.IsCarrier(t.def))
            {
                return true;
            }
            Map map = t.Map;
            if (map == null)
            {
                return true;
            }
            List<Thing> here = t.Position.GetThingList(map);
            for (int i = 0; i < here.Count; i++)
            {
                if (here[i] is Building_ABColumn)
                {
                    return false; // the column speaks for it
                }
            }
            return true;
        }
    }

    public static class ABDevToolsColumns
    {
        // ⚠ `AllowedGameStates` FLAGS ARE ANDed AS REQUIREMENTS, NOT ORed AS PERMISSIONS.
        // DebugActionAttribute.IsAllowedInCurrentGameState reads:
        //     (states & Entry)   == 0 || ProgramState == Entry
        //  && (states & Playing) == 0 || ProgramState == Playing
        // so naming a state means "I REQUIRE this state", and `Entry | PlayingOnMap`
        // demands ProgramState be Entry AND Playing simultaneously - the action then exists
        // but can never be listed anywhere, and a palette pin for it is silently dropped
        // with "Could not find node from path". PlayingOnMap alone, like every other tool
        // in this mod.
        [LudeonTK.DebugAction("As above", "AB2: network scan",
            allowedGameStates = LudeonTK.AllowedGameStates.PlayingOnMap)]
        private static void NetworkScan()
        {
            Log.Warning(ABColumnNetworks.Report()
                + "\n  MERGE COUNTERS (rule 15 - one row per family, a healthy family cannot hide a broken one):\n"
                + Patch_PowerNetMaker_ABRiserLink.CounterReport() + "\n"
                + Patch_DubsPipes_ABRiserLink.CounterReport() + "\n"
                + RimIOTCompat.CounterReport() + "\n"
                + EccentricGridCompat.CounterReport()
                + "\n  HOST ART BINDING (§62.N - a carrier draws with the host's own graphic):\n"
                + ABPipeGraphics.CounterReport()
                + "\n  COLUMNS ON THIS MAP (rule 31 - name the clause that declined):\n"
                + Building_ABColumn.ColumnReport(Find.CurrentMap));
        }
    }
}
