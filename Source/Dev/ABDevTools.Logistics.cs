using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    // Partial of ABDevTools (logistics diagnostics) — class summary lives in ABDevTools.cs.
    public static partial class ABDevTools
    {
        [DebugAction("As above", "AB: explain haul decision", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ExplainHaulDecision()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            try
            {
                Map cur = Find.CurrentMap;
                IntVec3 cell = UI.MouseMapPosition().ToIntVec3();
                List<Pawn> selP = Find.Selector.SelectedPawns;
                Pawn pawn = (selP != null && selP.Count > 0) ? selP[0] : null;
                if (pawn == null)
                {
                    List<Pawn> cols = cur.mapPawns.FreeColonistsSpawned;
                    if (cols != null && cols.Count > 0)
                    {
                        pawn = cols[0];
                    }
                }
                Thing item = null;
                List<Thing> things = cell.GetThingList(cur);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].def.EverHaulable)
                    {
                        item = things[i];
                        break;
                    }
                }
                if (item == null)
                {
                    Messages.Message("AB: no haulable thing in that cell.", MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                sb.Append("[AB haul decision] ").Append(item.LabelShortCap)
                    .Append(" L").Append(item.MapHeld?.Level() ?? -99)
                    .Append(" curPriority=").Append(StoreUtility.CurrentStoragePriorityOf(item))
                    .Append(" alwaysHaulable=").Append(item.def.alwaysHaulable)
                    .Append(" inValidStorage=").Append(item.IsInValidStorage())
                    .Append(" haulDesignated=")
                    .Append(item.MapHeld?.designationManager.DesignationOn(item, DesignationDefOf.Haul) != null);
                // Per-level vanilla search (carrier-less), whole column.
                LevelComp comp = item.MapHeld?.Levels();
                if (comp != null)
                {
                    StoragePriority cp = StoreUtility.CurrentStoragePriorityOf(item);
                    Faction f = pawn?.Faction ?? Faction.OfPlayer;
                    for (Map m = item.MapHeld; m != null; m = m.Levels()?.upperMap)
                    {
                        bool ok = StoreUtility.TryFindBestBetterStorageFor(item, null, m, cp, f,
                            out IntVec3 _, out IHaulDestination d, needAccurateResult: false);
                        sb.Append(" | L").Append(m.Level()).Append("(up)=").Append(ok ? d.GetStoreSettings().Priority.ToString() : "none");
                        if (m.Levels()?.upperMap == null) break;
                    }
                    for (Map m = comp.lowerMap; m != null; m = m.Levels()?.lowerMap)
                    {
                        bool ok = StoreUtility.TryFindBestBetterStorageFor(item, null, m, cp, f,
                            out IntVec3 _, out IHaulDestination d, needAccurateResult: false);
                        sb.Append(" | L").Append(m.Level()).Append("(dn)=").Append(ok ? d.GetStoreSettings().Priority.ToString() : "none");
                    }
                }
                bool col = ColumnStorage.TryFindBetter(pawn, item, out Map tm, out IntVec3 _,
                    out IHaulDestination _, out StoragePriority tier);
                sb.Append(" || ColumnStorage=")
                    .Append(col ? ("cross to L" + tm.Level() + " @" + tier) : "NO CROSS (best is here/local or nothing better)");
                // TargetLevelFor MUST be evaluated with a pawn standing on the
                // ITEM's level (its early gate rejects a pawn on another map, and
                // FirstHopToward reads the pawn's level for direction) - that is
                // exactly how the real haul giver calls it.
                Map itemMap = item.MapHeld;
                Pawn evalPawn = null;
                List<Pawn> onLevel = itemMap?.mapPawns?.FreeColonistsSpawned;
                if (onLevel != null)
                {
                    for (int i = 0; i < onLevel.Count; i++)
                    {
                        if (onLevel[i] != null && !onLevel[i].Dead)
                        {
                            evalPawn = onLevel[i];
                            break;
                        }
                    }
                }
                if (evalPawn == null)
                {
                    sb.Append(" || (NO colonist on the item's level L").Append(itemMap?.Level() ?? -99)
                        .Append(" - a pawn there is required to push; fetch would bring one)");
                }
                else
                {
                    sb.Append(" || evalPawn=").Append(evalPawn.LabelShort).Append("@L").Append(evalPawn.Map.Level());
                    sb.Append(" exportAllowed=").Append(CrossLevelDemand.ExportAllowed(itemMap, item))
                        .Append(" [").Append(CrossLevelDemand.ExportDiag(itemMap, item)).Append("]");
                    if (col)
                    {
                        bool fh = ColumnStorage.FirstHopToward(evalPawn, tm,
                            out Building_ABStairs _, out Building_ABStairs _);
                        sb.Append(" firstHopToward(L").Append(tm.Level()).Append(")=").Append(fh);
                    }
                    Map tlf = CrossLevelHaul.TargetLevelFor(evalPawn, item, out Building_ABStairs st);
                    sb.Append(" TargetLevelFor=").Append(tlf != null ? ("L" + tlf.Level()) : "null")
                        .Append(st != null ? " stairs " + st.Position : " no-stairs");
                }
            }
            catch (Exception e)
            {
                sb.Append(" EXCEPTION: ").Append(e);
            }
            Log.Warning(sb.ToString());
            Messages.Message("AB haul decision written to log.", MessageTypeDefOf.NeutralEvent, historical: false);
        }

    }
}
