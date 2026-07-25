using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Z-Levels beta parity: a bomb blast strong enough on the sky level punches a
    /// hole through a constructed rooftop. Rather than destroying the sky terrain
    /// directly (which would leave the roof below intact and incoherent), we remove
    /// the constructed roof on the ground map below at that cell. That fires the
    /// existing roof-change cascade (OnGroundRoofChanged): the rooftop above reverts
    /// to open air and its contents fall through, exactly like mining out the roof.
    ///
    /// Scoped by construction: AB_RoofSurface only ever exists over a CONSTRUCTED
    /// roof (natural rock surfaces above mountains keep their rock terrain), so the
    /// terrain check keeps natural mountain roofs immune. Kill switch: RoofSync.
    /// </summary>
    [HarmonyPatch(typeof(DamageWorker), "ExplosionDamageTerrain")]
    internal static class Patch_DamageWorker_ExplosionDamageTerrain
    {
        // Roughly a mortar shell or a triggered IED; lighter blasts scorch but hold.
        private const float RoofPunchThreshold = 40f;

        private static void Postfix(Explosion explosion, IntVec3 c)
        {
            if (!ABGuard.On(ABGuard.RoofSync) || explosion == null
                || explosion.damType != DamageDefOf.Bomb)
            {
                return;
            }
            try
            {
                Map sky = explosion.Map;
                if (sky == null || sky.Disposed || sky.Level() != 1
                    || !c.InBounds(sky)
                    || sky.terrainGrid.TerrainAt(c) != ABDefOf.AB_RoofSurface)
                {
                    return;
                }
                Map ground = sky.LowerMap();
                if (ground == null || ground.Disposed || !c.InBounds(ground)
                    || ground.roofGrid.RoofAt(c) == null)
                {
                    return;
                }
                float damage = explosion.GetDamageAmountAt(c);
                // Stratum HP-tracks every roof (its Vanilla_Roofs patch covers
                // vanilla defs too). Route the blast into its integrity system
                // - no flat threshold, its per-roof threshold/armor decide -
                // and let its 0-HP collapse call SetRoof(null), which fires
                // our normal rooftop cascade.
                if (ABStratumCompat.TryDamageRoof(ground, c, damage, null))
                {
                    return;
                }
                if (damage < RoofPunchThreshold)
                {
                    return;
                }
                // Cascades through map events: rooftop above reverts to air,
                // contents fall. Idempotent: guarded on there being a roof.
                ground.roofGrid.SetRoof(c, null);
            }
            catch (Exception e)
            {
                ABGuard.Disable(ABGuard.RoofSync, e, "bomb rooftop punch");
            }
        }
    }
}
