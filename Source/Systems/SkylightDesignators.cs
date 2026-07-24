using RimWorld;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Skylight zone painting, vanilla build-roof-area language: two cell
    /// designators in the Zone architect category (added via XML patch). The
    /// painted set is the DESIRED state; WorkGiver_ABSkylights reconciles in
    /// both directions, so "clear" is also the removal order for built glass.
    /// Hidden entirely on maps with no level below (basements, ordinary
    /// colonies without levels) and while the feature toggle is off.
    /// </summary>
    public class Designator_ABSkylightExpand : Designator_Cells
    {
        public override bool DragDrawMeasurements => true;

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

        public Designator_ABSkylightExpand()
        {
            defaultLabel = "AB_SkylightExpandLabel".Translate();
            defaultDesc = "AB_SkylightExpandDesc".Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Designators/AB_SkylightExpand");
            soundDragSustain = SoundDefOf.Designate_DragAreaAdd;
            soundDragChanged = SoundDefOf.Designate_DragZone_Changed;
            soundSucceeded = SoundDefOf.Designate_ZoneAdd_Roof;
            useMouseIcon = true;
        }

        public override bool Visible =>
            base.Visible && SkylightSystem.FeatureOn && ABGuard.On(ABGuard.Areas)
            && SkylightSystem.MapEligible(Map);

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map) || c.Fogged(Map))
            {
                return false;
            }
            SkylightMapComp comp = SkylightSystem.CompFor(Map);
            if (comp == null || comp.IsPlanned(c))
            {
                return false;
            }
            return SkylightSystem.CellAllowed(Map, c);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            SkylightSystem.CompFor(Map)?.SetPlanned(c, true);
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            SkylightSystem.CompFor(Map)?.MarkForDraw();
        }
    }

    public class Designator_ABSkylightClear : Designator_Cells
    {
        public override bool DragDrawMeasurements => true;

        public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

        public Designator_ABSkylightClear()
        {
            defaultLabel = "AB_SkylightClearLabel".Translate();
            defaultDesc = "AB_SkylightClearDesc".Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Designators/AB_SkylightClear");
            soundDragSustain = SoundDefOf.Designate_DragAreaDelete;
            soundDragChanged = SoundDefOf.Designate_DragZone_Changed;
            soundSucceeded = SoundDefOf.Designate_ZoneDelete;
            useMouseIcon = true;
        }

        public override bool Visible =>
            base.Visible && SkylightSystem.FeatureOn && ABGuard.On(ABGuard.Areas)
            && SkylightSystem.MapEligible(Map);

        public override AcceptanceReport CanDesignateCell(IntVec3 c)
        {
            if (!c.InBounds(Map))
            {
                return false;
            }
            SkylightMapComp comp = SkylightSystem.CompFor(Map);
            return comp != null && comp.IsPlanned(c);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            SkylightSystem.CompFor(Map)?.SetPlanned(c, false);
        }

        public override void SelectedUpdate()
        {
            GenUI.RenderMouseoverBracket();
            SkylightSystem.CompFor(Map)?.MarkForDraw();
        }
    }
}
