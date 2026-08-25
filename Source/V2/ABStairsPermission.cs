using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE SECOND HALF OF THE MANHUNTER FIX, AND WITHOUT IT THE FIRST HALF DOES NOTHING.
    ///
    /// Restoring `FreePassage => false` and vanilla `PawnCanOpen` on the stairwell looks
    /// like the whole fix, and it is not, because of the middle branch of
    /// `Building_Door.CanPhysicallyPass`:
    ///
    ///     if (!FreePassage &amp;&amp; !PawnCanOpen(p))
    ///     {
    ///         if (Open) { return p.HostileTo(this); }   // &lt;-- here
    ///         return false;
    ///     }
    ///     return true;
    ///
    /// ⚠ A HOSTILE PAWN MAY WALK THROUGH AN *OPEN* DOOR IT COULD NOT OPEN ITSELF. That is
    /// correct for vanilla - a raider should be able to stroll through the door a colonist
    /// left standing open - but a stairwell is PERMANENTLY `Open` by our own hand.
    /// `SpawnSetup` sets `openInt = true` and pins `ticksSinceOpen`, and the pin is
    /// one-way: the def ticks Normal these days (doors must tick to equalize temperature,
    /// see AB2_LinkBase), but `AlwaysOpen => true` keeps every close path unreachable, so
    /// a link is Open from its first tick to its last. That latch is what stops pawns
    /// freezing beside a stairwell. So the very field that
    /// fixes pathing is the field that hands every manhunter a free pass, and the two
    /// requirements collide inside a method we cannot override: `CanPhysicallyPass` is
    /// `public bool`, not `public virtual bool`.
    ///
    /// Hence one narrow postfix. It removes ONLY the hostile-through-an-open-door clause,
    /// and only for our stairwells: if vanilla said yes while both FreePassage and
    /// PawnCanOpen said no, the yes came from that clause and nothing else.
    ///
    /// ⚠⚠ AND IT MUST BE REMOVED FOR ANIMALS ONLY. The first version of this file stripped
    /// the clause for every pawn and BROKE RAIDS - raiders stopped using stairs entirely.
    /// The reason is a chain that is easy to get wrong in exactly this order:
    ///   * `Building_Door.CheckFaction` defaults to TRUE, so on a player-built stairwell
    ///     `PawnCanOpen` ends at `GenAI.MachinesLike(Faction, p)` - false for ANY hostile.
    ///   * `Pawn.CanOpenAnyDoor` is NOT "is this pawn humanlike". It is wild-man state,
    ///     `lord.LordJob.CanOpenAnyDoor(this)`, mutant def, or `kindDef.canOpenAnyDoor`. A
    ///     raider whose lord is staging or sieging answers FALSE.
    /// So for a hostile humanlike, `PawnCanOpen` is routinely false, and the
    /// hostile-through-an-open-door clause was the ENTIRE mechanism by which raiders used a
    /// permanently-open stairwell. Stripping it for everyone did not just close the animal
    /// loophole, it sealed the stairs against raids.
    ///
    /// The rule we actually want is simpler than the door metaphor: A STAIRWELL IS A HOLE IN
    /// THE FLOOR. Anything that walks upright can use one; an animal cannot, unless it is
    /// yours and being led. So the clause is stripped for `RaceProps.Animal` and left alone
    /// for humanlikes, mechanoids and entities, which keeps raids, mech clusters and
    /// Anomaly threats behaving exactly as vanilla would.
    ///
    /// Colony animals are unaffected either way: `MachinesLike(player, colonyAnimal)` is
    /// true, so `PawnCanOpen` returns true and this postfix never reaches its decision.
    ///
    /// ⚠ THIS IS THE RIGHT METHOD TO PATCH BECAUSE EVERY GATE FUNNELS THROUGH IT.
    /// `Region.Allows` (region-level traversal), `PathUtility` (per-cell pathing),
    /// `RCellFinder`, `CellFinder` and `AttackTargetFinder` all ask this one question, so a
    /// single postfix covers pathing, reachability, target selection and cell finding at
    /// once. Patching the pathfinder instead would have fixed movement and left the AI
    /// still believing it could reach the colony.
    ///
    /// ⚠ NO REACHABILITY CACHE FLUSH IS NEEDED, which is worth stating because rule 3 makes
    /// it the obvious worry. `ReachabilityCache` is keyed on `TraverseParms`, and
    /// TraverseParms carries the pawn - so a manhunter and a colonist never share a cached
    /// verdict about the same pair of regions. Region membership itself is unchanged: a
    /// stairwell is still a Portal, still an edifice, still in the same regions. Only the
    /// per-pawn answer moved.
    /// </summary>
    [HarmonyPatch(typeof(Building_Door), nameof(Building_Door.CanPhysicallyPass))]
    public static class Patch_BuildingDoor_ABStairsHostileClause
    {
        private static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
        {
            // Cheapest possible rejection first: this runs on every door query in the game,
            // for every pawn, on every map, banded or not.
            if (!__result || !(__instance is Building_ABStairs2))
            {
                return;
            }
            try
            {
                // ⚠ `p` CAN BE NULL HERE. Region.Allows passes `tp.pawn`, and a TraverseParms
                // built for a non-pawn traversal has none. Vanilla's own body would throw on
                // that path; we must not be the one that starts throwing.
                if (p == null)
                {
                    return;
                }
                if (__instance.FreePassage || __instance.PawnCanOpen(p))
                {
                    return; // a legitimate yes - leave vanilla's answer alone
                }
                // The only remaining source of `true` is the hostile-through-open clause.
                // ⚠ ANIMALS ONLY. See the banner: taking it from hostile humanlikes stops
                // raiders using stairs at all, because for them that clause IS the mechanism.
                if (p.RaceProps != null && p.RaceProps.Animal)
                {
                    __result = false;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " stairwell permission check failed; falling back "
                    + "to vanilla door behaviour. " + e.Message, 0x5741A2);
            }
        }
    }
}
