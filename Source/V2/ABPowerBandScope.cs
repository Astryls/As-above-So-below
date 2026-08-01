using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// VANILLA POWER, CLAMPED TO THE CONNECTOR'S OWN BAND.
    ///
    /// THE BUG THIS FIXES SHIPPED. `PowerConnectionMaker.ConnectMaxDist` is 6, applied as
    /// `ExpandedBy(6)` in BOTH `BestTransmitterForConnector` and
    /// `PotentialConnectorsForTransmitter`, with no line-of-sight, room, region or
    /// reachability test of any kind - a connector binds to whatever transmitter is nearest
    /// by raw x/z distance. Our gutter is TWO rows on the 126 and 190 tiers, so the floor of
    /// the band above is THREE cells away and the search reaches about four rows into it.
    /// A workbench near the top edge of the surface silently draws power from a conduit on
    /// the level above, through solid rock, and the player is given no cue at all because
    /// vanilla's power UI has no concept of a band.
    ///
    /// \u26a0 THE RADIUS LEAKS; THE FLOOD FILL DOES NOT. Worth stating because the two look
    /// like one system and only one of them is wrong:
    ///   - `PowerNetMaker.ContiguousPowerBuildings` grows a net over
    ///     `GenAdj.CellsAdjacentCardinal`, which is a ONE cell step. It cannot cross a
    ///     two-row gutter, so nets never merged and the symptom was never "my grids joined".
    ///   - Nothing can be built in the gutter to bridge it either: `CarveGutters` writes
    ///     `AB_OpenAir`, whose TerrainDef declares NO affordances at all, and every
    ///     transmitter inherits `terrainAffordanceNeeded` Light from BuildingBase.
    /// So the entire defect is the two radius searches, and the entire fix is to clip them.
    ///
    /// \u26a0 AND IT IS TIER-DEPENDENT, WHICH IS WHY NOBODY REPORTED IT. The band-to-band gap is
    /// 3 cells at 126 and 190, 7 at 250, 21 at 300. A reach of 6 is INVISIBLE on the two
    /// largest tiers and present on the two most-played ones. Any future test of this must
    /// run at 190 or 126; a clean run at 300 proves nothing. This is the "check that your
    /// configuration can express the difference" rule in its power-grid form.
    ///
    /// WHY POSTFIXES AND NOT A TRANSPILER OR A REPLACEMENT PREFIX. A transpiler inserting a
    /// clip after the rect is built would be smaller, but it puts us in a queue with every
    /// other mod that rewrites these methods, and power is exactly the area where other mods
    /// transpile. A replacement prefix would take ownership of vanilla logic we do not want
    /// to own. Instead each patch does the least it can:
    ///   - the candidate ENUMERATION is filtered, which is lossless because its caller
    ///     connects every candidate it is handed;
    ///   - the NEAREST-transmitter search is only re-run when vanilla's answer actually came
    ///     from another band, which on an unbanded map, and on the overwhelming majority of
    ///     calls on a banded one, costs a single integer compare.
    /// </summary>
    public static class ABPowerBandScope
    {
        /// <summary>Vanilla's `PowerConnectionMaker.ConnectMaxDist`, which is private.
        ///
        /// \u26a0 A COPIED CONSTANT IS A SILENT DRIFT RISK. If Ludeon ever changes theirs this
        /// stays 6 and our re-search would consider a different set of cells than vanilla
        /// did - which would show up as a connector that vanilla wanted to attach and we
        /// then refused to re-home. It is copied rather than reflected because it is a
        /// compile-time constant (`private const int`), so it is inlined into their IL and
        /// there is no field left to read at runtime: reflection would find nothing. Checked
        /// against 1.6.</summary>
        internal const int ConnectMaxDist = 6;

        /// <summary>The band rect to confine a search to, or false when this map is not
        /// banded (every ordinary colony, quest site, caravan and pocket map).
        ///
        /// One place so the two patches below cannot drift apart on what "same band" means -
        /// the failure mode where one of them clips and the other does not is exactly the
        /// half-fixed state that looks like an intermittent bug.</summary>
        internal static bool TryBandScope(Map map, IntVec3 cell, out CellRect band, out int bandIndex)
        {
            band = default(CellRect);
            bandIndex = 0;
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            bandIndex = bands.BandOf(cell);
            band = bands.RectOfBand(bandIndex);
            return true;
        }

        /// <summary>
        /// Vanilla's own nearest-transmitter loop with ONE extra clip on the search rect.
        ///
        /// Deliberately a line-for-line mirror of `BestTransmitterForConnector`, including
        /// the `allowWireConnection` test and the `disallowedNets` filter, because it has to
        /// return the answer vanilla would have returned had the band been the whole map.
        /// Anything it drops that vanilla keeps is a connector that mysteriously refuses to
        /// power up.
        ///
        /// \u26a0 A CROSS-BAND ANSWER CANNOT SIMPLY BE NULLED OUT. The obvious postfix - "if the
        /// transmitter vanilla picked is on another band, return null" - is wrong, and wrong
        /// in a way that is easy to miss because it tests fine in an empty room: vanilla
        /// picks the NEAREST transmitter, so a cross-band one can win over a perfectly legal
        /// in-band conduit a cell or two further away. Nulling would disconnect an appliance
        /// that has a real conduit right next to it. The search has to actually be re-run
        /// with the band applied, which is why this method exists at all.
        /// </summary>
        internal static CompPower BestWithinBand(IntVec3 connectorPos, Map map,
            List<PowerNet> disallowedNets, CellRect band)
        {
            CellRect rect = CellRect.SingleCell(connectorPos)
                .ExpandedBy(ConnectMaxDist)
                .ClipInsideMap(map)
                .ClipInsideRect(band);

            float bestSq = 999999f;
            CompPower best = null;
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                for (int x = rect.minX; x <= rect.maxX; x++)
                {
                    Building transmitter = new IntVec3(x, 0, z).GetTransmitter(map);
                    if (transmitter == null || transmitter.Destroyed)
                    {
                        continue;
                    }
                    CompPower pc = transmitter.PowerComp;
                    if (pc == null || !pc.TransmitsPowerNow)
                    {
                        continue;
                    }
                    if (transmitter.def.building != null && !transmitter.def.building.allowWireConnection)
                    {
                        continue;
                    }
                    if (disallowedNets != null && disallowedNets.Contains(pc.transNet))
                    {
                        continue;
                    }
                    float d = (transmitter.Position - connectorPos).LengthHorizontalSquared;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = pc;
                    }
                }
            }
            return best;
        }
    }

    /// <summary>
    /// The nearest-transmitter search: re-run inside the band whenever vanilla's answer came
    /// from outside it.
    ///
    /// Structured as "detect, then redo" rather than "always redo" so that the cost on the
    /// normal path is one `BandOf` (an integer divide) and nothing else. Returning early on
    /// an unbanded map keeps every other colony in the game at zero cost beyond the postfix
    /// dispatch itself.
    /// </summary>
    [HarmonyPatch(typeof(PowerConnectionMaker), nameof(PowerConnectionMaker.BestTransmitterForConnector))]
    public static class Patch_PowerConnectionMaker_ABBandLocalBest
    {
        private static void Postfix(IntVec3 connectorPos, Map map, List<PowerNet> disallowedNets,
            ref CompPower __result)
        {
            if (__result == null || map == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                if (!ABPowerBandScope.TryBandScope(map, connectorPos, out CellRect band, out int bandIndex))
                {
                    return; // not a banded map
                }
                ABBandMap bands = ABBands.CompOf(map);
                Thing host = __result.parent;
                if (host == null || bands.BandOf(host.Position) == bandIndex)
                {
                    return; // vanilla's pick was already band-local - the overwhelming case
                }
                __result = ABPowerBandScope.BestWithinBand(connectorPos, map, disallowedNets, band);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "power connector band scope", map);
            }
        }
    }

    /// <summary>
    /// The candidate enumeration a newly placed transmitter offers itself to.
    ///
    /// Filtering is exactly right here where nulling would have been wrong above, and the
    /// reason is the caller: `ConnectAllConnectorsToTransmitter` attaches EVERY candidate
    /// that currently has no parent, so there is no nearest-wins semantics to distort.
    /// Dropping the cross-band entries removes connections that should never have been
    /// offered and changes nothing else - a connector we drop here is simply left for its
    /// own band's transmitter to claim.
    ///
    /// \u26a0 THIS PATCHES THE ITERATOR STUB, NOT ITS MoveNext. A plain HarmonyPatch on a
    /// C# iterator targets the little method that returns the compiler-generated state
    /// machine, so `__result` is the whole sequence and can be wrapped. That is what makes
    /// "wrap the DATA instead of the reader" available here at all; targeting the body would
    /// have needed MethodType.Enumerator and a transpiler.
    ///
    /// \u26a0 AND BECAUSE THE WRAPPER IS LAZY, ITS BODY RUNS OUTSIDE THE TRY/CATCH BELOW.
    /// Deferred execution means an exception thrown inside BandLocal would surface in
    /// vanilla's own foreach, where our guard cannot see it and where it would look like a
    /// vanilla power bug. The body is therefore kept to null checks and one integer compare,
    /// with the band component captured up front while we are still inside the guarded
    /// region. Do not grow it.
    /// </summary>
    [HarmonyPatch(typeof(PowerConnectionMaker), "PotentialConnectorsForTransmitter")]
    public static class Patch_PowerConnectionMaker_ABBandLocalConnectors
    {
        private static void Postfix(CompPower b, ref IEnumerable<CompPower> __result)
        {
            if (__result == null || !ABGuard.On(ABGuard.Utilities))
            {
                return;
            }
            try
            {
                Thing host = b?.parent;
                Map map = host?.Map;
                if (map == null)
                {
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                __result = BandLocal(__result, bands, bands.BandOf(host.Position));
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.Utilities, e, "power transmitter band scope", b?.parent);
            }
        }

        private static IEnumerable<CompPower> BandLocal(IEnumerable<CompPower> source,
            ABBandMap bands, int band)
        {
            foreach (CompPower candidate in source)
            {
                Thing t = candidate?.parent;
                if (t != null && bands.BandOf(t.Position) != band)
                {
                    continue;
                }
                yield return candidate;
            }
        }
    }
}
