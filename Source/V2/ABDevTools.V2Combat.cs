using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Dev tooling for cross-level combat.
    ///
    /// ⚠ THE ORDER THESE ARE MEANT TO BE USED IN, because §14's rule is "run the diagnostic
    /// before the first theory" and these two are not interchangeable:
    ///   1. `AB2: band info` FIRST, always. It prints NO WORMHOLE PAIRS REGISTERED, which is
    ///      the fastest way to learn that a test map has no stairs yet - and while stairs are
    ///      irrelevant to shooting, their absence usually means the map has no OPENINGS
    ///      either, and an opening is the one thing cross-level fire cannot do without.
    ///   2. `AB2: why can't this pawn shoot that` for a specific failure. It runs the real
    ///      solver with tracing on and prints the intermediate values - the drift, the blocked
    ///      column cell, the band-local distance - not a verdict.
    ///   3. `AB2: combat report` for counters after a firefight.
    /// </summary>
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB2: combat report",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2CombatReport()
        {
            Map map = Find.CurrentMap;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("V2 combat report:");
            sb.AppendLine("  guard: combat=" + ABGuard.On(ABGuard.Combat)
                + " rendering=" + ABGuard.On(ABGuard.Rendering)
                + " clickThrough=" + ABBelowClickThrough.Enabled);
            sb.AppendLine("  geometry rule: driftPerLevel=" + ABShaft.MaxDriftPerLevel
                + " verticalCostPerLevel=" + ABShaft.VerticalCostPerLevel);
            sb.AppendLine("  shaft: " + ABShaft.CounterReport());
            sb.AppendLine("  " + ABCombatAcquisition.CounterReport());
            sb.AppendLine("  " + ABCombatPosition.CounterReport());
            sb.AppendLine("  " + ABCombatTargeting.CounterReport());
            sb.AppendLine("  " + ABRangeOverlay.CounterReport());
            sb.AppendLine("  " + ABCombatRelay.CounterReport());
            sb.AppendLine("  " + ABBandLeap.CounterReport());
            sb.AppendLine("  " + ABCombatAbilities.CounterReport());
            sb.AppendLine("  " + ABBandSenses.CounterReport());
            sb.AppendLine("  " + ABBandArrivals.CounterReport());
            sb.AppendLine("  " + ABCECompat.CounterReport());
            if (map != null)
            {
                ABBandMap bands = ABBands.CompOf(map);
                sb.AppendLine("  map: banded=" + (bands != null && bands.Banded)
                    + " viewBand=" + (map != null ? ABBandView.CurrentBand(map) : -1)
                    + " openAirCells=" + CountOpenAir(map));
            }
            Log.Warning(ABLog.Tag + " " + sb.ToString().TrimEndNewlines());
            Messages.Message("AB2: combat report written to log.",
                MessageTypeDefOf.TaskCompletion, false);
        }

        [DebugAction("As above", "AB2: reset combat counters",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2ResetCombatCounters()
        {
            ABShaft.ResetCounters();
            ABCombatAcquisition.ResetCounters();
            ABCombatPosition.ResetCounters();
            ABCombatTargeting.ResetCounters();
            ABRangeOverlay.ResetCounters();
            ABCombatRelay.ResetCounters();
            ABBandLeap.ResetCounters();
            ABCombatAbilities.ResetCounters();
            ABBandSenses.ResetCounters();
            ABBandArrivals.ResetCounters();
            ABCECompat.ResetCounters();
            Messages.Message("AB2: combat counters reset.", MessageTypeDefOf.TaskCompletion,
                false);
        }

        /// <summary>
        /// For the selected pawn (or turret), trace the solver against every attackable thing
        /// on another band. This is the one-stop probe for "why did nothing happen".
        ///
        /// ⚠ IT TRACES THE *REAL* SOLVE AND BYPASSES THE MEMO. A cached answer would print no
        /// trace at all, which would read as "the solver was never asked" - the exact
        /// misreading that costs a test cycle.
        /// </summary>
        [DebugAction("As above", "AB2: why can't this pawn shoot that",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void V2WhyNoShot()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                Log.Warning(ABLog.Tag + " V2 shot probe: this map is not banded.");
                return;
            }
            Thing shooter = null;
            foreach (object o in Find.Selector.SelectedObjects)
            {
                if (o is Thing t && t.Spawned)
                {
                    shooter = t;
                    break;
                }
            }
            if (shooter == null)
            {
                Messages.Message("AB2: select a pawn or turret first.",
                    MessageTypeDefOf.RejectInput, false);
                return;
            }
            Verb verb = VerbOf(shooter);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("V2 shot probe for " + shooter.LabelShortCap + " at "
                + shooter.Position + " (band " + bands.BandOf(shooter.Position) + ", view band "
                + ABBandView.CurrentBand(map) + ")");
            if (verb == null)
            {
                sb.AppendLine("  NO EFFECTIVE RANGED VERB. Nothing below can apply - an "
                    + "unarmed pawn, an unmanned turret, or a melee-only weapon.");
                Log.Warning(ABLog.Tag + " " + sb.ToString().TrimEndNewlines());
                return;
            }
            bool overhead = ABShaft.IsOverheadFire(verb);
            sb.AppendLine("  verb " + verb.GetType().Name + " range " + verb.EffectiveRange
                + " minRange " + verb.verbProps.minRange
                + " requireLOS=" + verb.verbProps.requireLineOfSight
                + " fliesOverhead=" + verb.ProjectileFliesOverhead()
                + " => rule: " + (overhead ? "MAP COORDINATES (levels ignored)" : "BALCONY"));

            int shooterBand = bands.BandOf(shooter.Position);
            int examined = 0;
            List<Thing> targets = map.listerThings.ThingsInGroup(ThingRequestGroup.AttackTarget);
            for (int i = 0; i < targets.Count; i++)
            {
                Thing t = targets[i];
                if (t == null || !t.Spawned || t == shooter)
                {
                    continue;
                }
                if (bands.BandOf(t.Position) == shooterBand)
                {
                    continue; // same band: vanilla's answer, not ours
                }
                examined++;
                sb.AppendLine("  --- " + t.LabelShortCap + " (band " + bands.BandOf(t.Position)
                    + ", hostile=" + shooter.HostileTo(t) + ")");
                sb.Append(ABShaft.Explain(map, shooter.Position, t.Position,
                    verb.EffectiveRange, verb.verbProps.EffectiveMinRange(t, shooter), overhead));
            }
            if (examined == 0)
            {
                sb.AppendLine("  NOTHING ATTACKABLE ON ANOTHER BAND AT ALL. That is the answer: "
                    + "there is no cross-level target to fail against. Spawn something on "
                    + "another level first (`AB2: open all bands` if it is still fogged).");
            }
            Log.Warning(ABLog.Tag + " " + sb.ToString().TrimEndNewlines());
            Messages.Message("AB2: shot probe written to log (" + examined + " cross-band "
                + "candidates).", MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>The verb the thing would actually shoot with, for pawns and turrets alike.
        /// A turret's searcher is its mannable pawn when manned, but the VERB is always the
        /// gun's - which is the distinction that matters here.</summary>
        private static Verb VerbOf(Thing t)
        {
            if (t is Building_Turret turret)
            {
                return turret.AttackVerb;
            }
            if (t is Pawn p)
            {
                return p.TryGetAttackVerb(null, allowManualCastWeapons: true);
            }
            return null;
        }

        private static int CountOpenAir(Map map)
        {
            TerrainDef air = ABDefOf.AB_OpenAir;
            TerrainGrid grid = map.terrainGrid;
            int n = 0;
            foreach (IntVec3 c in map.AllCells)
            {
                if (grid.TerrainAt(c) == air)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
