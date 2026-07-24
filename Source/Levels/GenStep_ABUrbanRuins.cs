using System;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Basement environment: Ancient urban ruins facility. Runs directly after
    /// AB_SolidRock. When the basement type is UrbanRuins and Ancient urban
    /// ruins is loaded, it stamps a random underground facility centered in the
    /// solid rock (so colonists mine to it from the stairwell landing) and
    /// strips AUR's own entrance/exit portals, leaving our stairs as the only
    /// way in. No-op for every other basement type, so it is mutually exclusive
    /// with the cavern carve by settings. Fails open to the plain solid-rock
    /// basement; the whole step is behind the LevelGen kill switch.
    /// </summary>
    public class GenStep_ABUrbanRuins : GenStep
    {
        public override int SeedPart => 762195845;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!ABGuard.On(ABGuard.LevelGen))
            {
                return;
            }
            ABSettings settings = ABMod.Settings;
            if (settings == null || settings.basementType != BasementEnv.UrbanRuins
                || !AncientUrbanRuinsCompat.Active)
            {
                return;
            }
            // Def wiring restricts this to AB_Basement, but guard the level sign
            // like the cavern carve in case a modder reuses the generator.
            LevelMapGen.Context ctx = LevelMapGen.CurrentContext;
            if (ctx != null && ctx.levelToGenerate >= 0)
            {
                return;
            }
            try
            {
                AncientUrbanRuinsCompat.TryStampFacility(map, settings.urbanRuinsOccupants);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.LevelGen, e, "urban ruins basement stamp");
            }
        }
    }
}
