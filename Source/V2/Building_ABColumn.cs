using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE TYPED UTILITY COLUMN (§62). One buildable per ABColumnType, replacing the
    /// thirty riser defs. The column is the ONLY visible piece: it stands on its own
    /// level, holds a roof like vanilla's Column, and the cell one Slot up receives
    /// NOTHING visible, nothing selectable and nothing blocking - only the invisible
    /// carrier that makes the level above connectable at that spot.
    ///
    /// ⚠ TOGGLING A NETWORK IS SPAWN/DESPAWN, NEVER FLICK (rule 27). A spawned carrier is
    /// a native member of its host's network: vanilla power registers transmitters in
    /// SpawnSetup, PipeSystem registers connectors, and every Dubwise CompPipe dirties its
    /// grid from PostSpawnSetup and PostDeSpawn alike. Spawning and despawning are the two
    /// operations every host is already forced to handle correctly, so the riser era's
    /// per-host flick plumbing (Building_ABPowerBreaker, CompABRiserSwitch, PokeRebuild)
    /// is deleted rather than ported.
    ///
    /// ⚠ CARRIERS ARE SHARED WHERE COLUMNS STACK. Column A on band 0 owns carriers in its
    /// cell c0 and above in c1; a column B stacked on band 1 stands IN c1 and wants the
    /// same carrier there. EnsureCarrierAt is idempotent, and RemoveCarrierAt declines to
    /// despawn a carrier any OTHER column still claims - the claimants of a cell are a
    /// column standing in it and a column standing one Slot below it.
    /// </summary>
    public class Building_ABColumn : Building
    {
        private List<string> enabled = new List<string>();

        /// <summary>First-construction auto-connect has run. Scribed so a player who
        /// deliberately disconnected everything does not get re-connected on load (rule 8).</summary>
        private bool autoEnabled;

        /// <summary>THIS column laid the roof on its own cell. Scribed, and the only thing
        /// that authorizes removing it again (rule 8).</summary>
        private bool roofPlaced;

        public ABColumnType ColumnType =>
            def.GetModExtension<ABColumnTypeExt>()?.columnType ?? ABColumnType.Pipe;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (enabled == null)
            {
                enabled = new List<string>();
            }
            // A network can vanish between sessions when its host mod is removed. Prune
            // rather than error: the carrier def is gone with it, so there is nothing to
            // spawn and nothing to despawn.
            enabled.RemoveAll(id => ABColumnNetworks.ById(id)?.carrier == null);

            if (!respawningAfterLoad && !autoEnabled)
            {
                // Fresh construction connects everything already researched. The gizmos
                // exist to DISCONNECT; a column that does nothing until five toggles are
                // clicked would read as broken.
                autoEnabled = true;
                List<ABNetwork> nets = ABColumnNetworks.All;
                for (int i = 0; i < nets.Count; i++)
                {
                    if (nets[i].type == ColumnType && nets[i].carrier != null
                        && nets[i].ResearchDone && !enabled.Contains(nets[i].id))
                    {
                        enabled.Add(nets[i].id);
                    }
                }
            }
            EnsureRoof();
            EnsureCarriers();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            RemoveCarriers(map);
            RemoveRoof(map);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref enabled, "AB_enabledNetworks", LookMode.Value);
            Scribe_Values.Look(ref autoEnabled, "AB_autoEnabled");
            Scribe_Values.Look(ref roofPlaced, "AB_roofPlaced");
            if (Scribe.mode == LoadSaveMode.PostLoadInit && enabled == null)
            {
                enabled = new List<string>();
            }
        }

        // ------------------------------------------------------------------- gizmos

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            ABColumnType type = ColumnType;
            List<ABNetwork> nets = ABColumnNetworks.All;
            for (int i = 0; i < nets.Count; i++)
            {
                ABNetwork n = nets[i];
                if (n.type != type || n.carrier == null)
                {
                    continue;
                }
                Command_Toggle cmd = new Command_Toggle
                {
                    defaultLabel = "AB2_ColumnToggleNet".Translate(n.LabelCap),
                    defaultDesc = "AB2_ColumnToggleNetDesc".Translate(n.LabelCap),
                    icon = IconFor(n),
                    isActive = () => enabled.Contains(n.id),
                    toggleAction = () => Toggle(n)
                };
                if (!n.ResearchDone)
                {
                    cmd.Disable("AB2_ColumnNeedsResearch".Translate(ResearchLabel(n)));
                }
                yield return cmd;
            }
        }

        private static Texture2D IconFor(ABNetwork n)
        {
            // The host's own conduit icon: already localized, already recognizable.
            return n.template != null && n.template.uiIcon != null
                ? n.template.uiIcon
                : BaseContent.BadTex;
        }

        private static string ResearchLabel(ABNetwork n)
        {
            List<ResearchProjectDef> req = n.Research;
            if (req == null || req.Count == 0)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < req.Count; i++)
            {
                if (req[i] == null || req[i].IsFinished)
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(req[i].LabelCap);
            }
            return sb.ToString();
        }

        private void Toggle(ABNetwork n)
        {
            if (enabled.Contains(n.id))
            {
                enabled.Remove(n.id);
                RemoveCarrierAt(Map, Position, n);
                if (TryUpCell(out IntVec3 up))
                {
                    RemoveCarrierAt(Map, up, n);
                }
            }
            else
            {
                enabled.Add(n.id);
                EnsureCarrierAt(Map, Position, n);
                if (TryUpCell(out IntVec3 up))
                {
                    EnsureCarrierAt(Map, up, n);
                }
            }
        }

        // ------------------------------------------------------------------- report

        /// <summary>
        /// One line of fact per column (rules 15 and 31).
        ///
        /// "The columns do not connect" has at least six causes that are IDENTICAL in game:
        /// no band above, the network not enabled, the host's research not done, the lower
        /// carrier missing, the UPPER carrier missing, or the pair present and the merge
        /// itself failing. Only the last is a bug in the adapters. This prints which.
        /// </summary>
        public static string ColumnReport(Map map)
        {
            if (map == null)
            {
                return "    (no current map)";
            }
            StringBuilder sb = new StringBuilder();
            ABBandMap bands = ABBands.CompOf(map);
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            int found = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!(all[i] is Building_ABColumn col))
                {
                    continue;
                }
                found++;
                bool hasUp = col.TryUpCell(out IntVec3 up);
                sb.AppendLine("    " + col.def.defName + " at " + col.Position
                    + "  band=" + (bands != null && bands.Banded ? bands.BandOf(col.Position).ToString() : "UNBANDED MAP")
                    + "  type=" + col.ColumnType);
                sb.AppendLine("        roof:    placedByUs=" + col.roofPlaced
                    + "  now=" + (map.roofGrid.RoofAt(col.Position)?.defName ?? "none"));
                sb.AppendLine("        upCell:  " + (hasUp
                    ? up.ToString() + "  terrain=" + (map.terrainGrid.TerrainAt(up)?.defName ?? "?")
                    : "NONE - top band, gutter, or off map. Nothing can be carried up."));
                if (col.enabled.Count == 0)
                {
                    sb.AppendLine("        enabled: (none) - every network of this type is "
                        + "either research-locked or was switched off");
                    continue;
                }
                for (int j = 0; j < col.enabled.Count; j++)
                {
                    ABNetwork n = ABColumnNetworks.ById(col.enabled[j]);
                    if (n == null)
                    {
                        sb.AppendLine("        " + col.enabled[j] + ": NETWORK NO LONGER EXISTS");
                        continue;
                    }
                    sb.AppendLine("        " + n.id
                        + ": carrierHere=" + (CarrierAt(map, col.Position, n) != null)
                        + " carrierAbove=" + (hasUp
                            ? (CarrierAt(map, up, n) != null).ToString()
                            : "n/a"));
                }
            }
            return found == 0 ? "    (no columns built on this map)" : sb.ToString().TrimEnd();
        }

        // ---------------------------------------------------------------------- roof

        /// <summary>
        /// Give the level above a floor tile to stand on, THE §45 WAY: a real constructed
        /// roof on the column's own cell, never terrain magic.
        ///
        /// ⚠ `holdsRoof` MEANS "CAN SUPPORT A ROOF", NOT "MAKES ONE". Vanilla only ever
        /// auto-roofs enclosed rooms, so a column standing in the open has no roof over it
        /// - and `ABSkySync.Resolve` then walks past its roof branches to the edifice one
        /// and answers `AB_WallTop`: buildable, but IMPASSABLE. That is an invisible ledge
        /// where the design calls for a walkable connector tile.
        ///
        /// Writing the roof is the whole fix. `Patch_RoofGrid_ABSyncAbove` watches every
        /// roof write and re-derives the cell above, which now takes the constructed-roof
        /// branch and becomes `AB_RoofSurface`. Doing it with a REAL roof rather than a
        /// terrain override is what keeps the player's vanilla remove-roof area able to
        /// trim it, and keeps us clear of rule 28 - no permanent fiction for anyone else
        /// to read as fact.
        ///
        /// ⚠ AN EXISTING ROOF IS NEVER OVERWRITTEN. A column inside a real room, or under
        /// natural rock, changes nothing at all.
        /// </summary>
        private void EnsureRoof()
        {
            if (Map == null || roofPlaced || Map.roofGrid.RoofAt(Position) != null)
            {
                return;
            }
            Map.roofGrid.SetRoof(Position, RoofDefOf.RoofConstructed);
            roofPlaced = true;
        }

        /// <summary>Take back only what this column laid. A player who deleted the roof
        /// himself leaves `roofPlaced` true and an absent roof, and both paths no-op -
        /// removal never re-creates and never deletes someone else's ceiling (rule 8).
        /// Clearing it on the way out also spares vanilla a collapse check on a lone
        /// one-cell roof whose only support just vanished.</summary>
        private void RemoveRoof(Map map)
        {
            if (map == null || !roofPlaced)
            {
                return;
            }
            if (map.roofGrid.RoofAt(Position) == RoofDefOf.RoofConstructed)
            {
                map.roofGrid.SetRoof(Position, null);
            }
            roofPlaced = false;
        }

        // ------------------------------------------------------------------ carriers

        private void EnsureCarriers()
        {
            bool hasUp = TryUpCell(out IntVec3 up);
            for (int i = 0; i < enabled.Count; i++)
            {
                ABNetwork n = ABColumnNetworks.ById(enabled[i]);
                if (n?.carrier == null)
                {
                    continue;
                }
                EnsureCarrierAt(Map, Position, n);
                if (hasUp)
                {
                    EnsureCarrierAt(Map, up, n);
                }
            }
        }

        private void RemoveCarriers(Map map)
        {
            if (map == null)
            {
                return;
            }
            bool hasUp = TryUpCell(out IntVec3 up);
            for (int i = 0; i < enabled.Count; i++)
            {
                ABNetwork n = ABColumnNetworks.ById(enabled[i]);
                if (n?.carrier == null)
                {
                    continue;
                }
                RemoveCarrierAt(map, Position, n);
                if (hasUp)
                {
                    RemoveCarrierAt(map, up, n);
                }
            }
        }

        private static void EnsureCarrierAt(Map map, IntVec3 cell, ABNetwork n)
        {
            if (map == null || !cell.InBounds(map) || CarrierAt(map, cell, n) != null)
            {
                return;
            }
            GenSpawn.Spawn(n.carrier, cell, map);
        }

        /// <summary>⚠ Carriers are destroyable=false, so Destroy() LOGS AN ERROR by design
        /// (it is what makes them wipe-proof). DeSpawn is the only legal teardown.</summary>
        private void RemoveCarrierAt(Map map, IntVec3 cell, ABNetwork n)
        {
            if (map == null || !cell.InBounds(map))
            {
                return;
            }
            if (OtherColumnClaims(map, cell, n.id))
            {
                return;
            }
            Thing carrier = CarrierAt(map, cell, n);
            if (carrier != null && carrier.Spawned)
            {
                carrier.DeSpawn();
            }
        }

        private static Thing CarrierAt(Map map, IntVec3 cell, ABNetwork n)
        {
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def == n.carrier)
                {
                    return things[i];
                }
            }
            return null;
        }

        /// <summary>A carrier at <paramref name="cell"/> is claimed by a column standing IN
        /// the cell (its own-cell carrier) or by a column one Slot BELOW it (its up-cell
        /// carrier). Ask both before despawning shared plumbing out from under a stack.</summary>
        private bool OtherColumnClaims(Map map, IntVec3 cell, string network)
        {
            if (ClaimantAt(map, cell, network))
            {
                return true;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            IntVec3 below = new IntVec3(cell.x, 0, cell.z - bands.Slot);
            return below.InBounds(map) && !bands.InGutter(below) && ClaimantAt(map, below, network);
        }

        private bool ClaimantAt(Map map, IntVec3 cell, string network)
        {
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Building_ABColumn col && col != this
                    && col.enabled.Contains(network))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Any detected network of this column's type with a live carrier
        /// def? False = the connect-toggle list is necessarily empty (§75.c).</summary>
        private bool TypeHasNetworks()
        {
            List<ABNetwork> nets = ABColumnNetworks.All;
            for (int i = 0; i < nets.Count; i++)
            {
                if (nets[i].type == ColumnType && nets[i].carrier != null)
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryUpCell(out IntVec3 up)
        {
            up = IntVec3.Invalid;
            if (Map == null)
            {
                return false;
            }
            ABBandMap bands = ABBands.CompOf(Map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            IntVec3 c = new IntVec3(Position.x, 0, Position.z + bands.Slot);
            if (!c.InBounds(Map) || bands.InGutter(c)
                || bands.BandOf(c) != bands.BandOf(Position) + 1)
            {
                return false;
            }
            up = c;
            return true;
        }

        // ------------------------------------------------------------------- inspect

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder(base.GetInspectString());
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            bool any = false;
            StringBuilder names = new StringBuilder();
            for (int i = 0; i < enabled.Count; i++)
            {
                ABNetwork n = ABColumnNetworks.ById(enabled[i]);
                if (n == null)
                {
                    continue;
                }
                if (any)
                {
                    names.Append(", ");
                }
                names.Append(n.LabelCap);
                any = true;
            }
            if (any)
            {
                sb.Append("AB2_ColumnInspect_Connected".Translate(names.ToString()).ToString());
            }
            else if (TypeHasNetworks())
            {
                sb.Append("AB2_ColumnInspect_None".Translate().ToString());
            }
            else
            {
                // Rules 31 and 33 (§75.c): the old line pointed at connect toggles
                // that do not exist when ZERO networks of this type are detected -
                // routine for Pipe/Climate/Data in vanilla-only sessions, where the
                // only host network is vanilla power. Name the clause that declined
                // instead of advertising controls the player cannot find.
                sb.Append("AB2_ColumnInspect_NoNetworks".Translate(ColumnType.ToString()).ToString());
            }
            if (!TryUpCell(out _))
            {
                sb.AppendLine();
                sb.Append("AB2_ColumnNoAbove".Translate().ToString());
            }
            return sb.ToString();
        }
    }
}
