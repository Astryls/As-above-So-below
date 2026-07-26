using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Confirmation for removing a whole level. Auto-evacuates the colony to the
    /// surface by default; the player can untick that to delete everything on the
    /// level outright, in which case a red loss warning appears. The actual work
    /// runs through ABLevelDeletion on confirm.
    /// </summary>
    public class Dialog_ABDeleteLevel : Window
    {
        private readonly Map level;
        private readonly string levelName;
        private bool evacuate = true;

        private readonly int colonists;
        private readonly int prisoners;
        private readonly int animals;

        public override Vector2 InitialSize => new Vector2(540f, 380f);

        public Dialog_ABDeleteLevel(Map level)
        {
            this.level = level;
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;

            int lvl = level != null ? level.Level() : 0;
            levelName = (lvl > 0 ? "AB_LevelSky" : "AB_LevelBasement").Translate();

            if (level != null && !level.Disposed)
            {
                IReadOnlyList<Pawn> pawns = level.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == null || p.Dead)
                    {
                        continue;
                    }
                    if (p.IsPrisonerOfColony)
                    {
                        prisoners++;
                    }
                    else if (p.Faction == Faction.OfPlayer)
                    {
                        if (p.RaceProps.Animal)
                        {
                            animals++;
                        }
                        else
                        {
                            colonists++;
                        }
                    }
                    else if (p.HostFaction == Faction.OfPlayer)
                    {
                        colonists++;
                    }
                }
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "AB_RemoveLevelTitle".Translate(levelName));
            Text.Font = GameFont.Small;

            float y = 42f;
            Rect bodyRect = new Rect(0f, y, inRect.width, 96f);
            Widgets.Label(bodyRect, "AB_RemoveLevelBody".Translate(levelName));
            y += 100f;

            Widgets.Label(new Rect(0f, y, inRect.width, 26f),
                "AB_RemoveLevelOccupants".Translate(colonists, prisoners, animals));
            y += 30f;

            Rect checkRect = new Rect(0f, y, inRect.width, 28f);
            Widgets.CheckboxLabeled(checkRect, "AB_RemoveLevelEvacuate".Translate(), ref evacuate);
            TooltipHandler.TipRegion(checkRect, "AB_RemoveLevelEvacuateTip".Translate());
            y += 34f;

            int people = colonists + prisoners + animals;
            if (!evacuate && people > 0)
            {
                GUI.color = ColorLibrary.RedReadable;
                Widgets.Label(new Rect(0f, y, inRect.width, 48f), "AB_RemoveLevelLoseWarning".Translate(people));
                GUI.color = Color.white;
            }

            float bw = (inRect.width - 10f) / 2f;
            if (Widgets.ButtonText(new Rect(0f, inRect.height - 35f, bw, 35f), "CancelButton".Translate()))
            {
                Close();
            }
            if (Widgets.ButtonText(new Rect(bw + 10f, inRect.height - 35f, bw, 35f), "AB_RemoveLevelConfirm".Translate()))
            {
                Map target = level;
                bool evac = evacuate;
                Close();
                ABLevelDeletion.DeleteLevel(target, evac);
            }
        }
    }
}
