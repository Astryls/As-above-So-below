using System.Collections.Generic;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Verb-agnostic accessors that let the cross-level combat pipeline treat vanilla
    /// Verb_LaunchProjectile weapons and Combat Extended Verb_LaunchProjectileCE weapons
    /// uniformly. The whole geometry / line-of-fire / job machinery is typed to base
    /// <see cref="Verb"/> and reads range, props and the projectile through here; the
    /// only place the two diverge is the actual launch (CrossLevelCombat.Fire).
    ///
    /// All CE-specific work is delegated to ABCECompat and gated on ABCECompat.Active,
    /// so nothing here forces CE to load when it is absent.
    /// </summary>
    internal static class ABVerb
    {
        /// <summary>A ranged (non-melee) projectile-launching verb: vanilla, or - when CE
        /// is loaded - a CE projectile verb. Not arc/lob (those are handled separately).</summary>
        internal static bool IsProjectileVerb(Verb v)
        {
            if (v == null || v.verbProps == null || v.verbProps.IsMeleeAttack)
            {
                return false;
            }
            if (v is Verb_LaunchProjectile)
            {
                return true;
            }
            return ABCECompat.Active && ABCECompat.IsCEVerb(v);
        }

        /// <summary>The pawn's best ranged projectile verb (equipped weapon first, then
        /// any innate verb), vanilla or CE, or null for melee-only pawns.</summary>
        internal static Verb GetRangedVerb(Pawn p)
        {
            if (p == null)
            {
                return null;
            }
            Verb eq = p.equipment?.PrimaryEq?.PrimaryVerb;
            if (IsProjectileVerb(eq))
            {
                return eq;
            }
            List<Verb> all = p.verbTracker?.AllVerbs;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (IsProjectileVerb(all[i]))
                    {
                        return all[i];
                    }
                }
            }
            return null;
        }

        /// <summary>The projectile ThingDef this verb would fire right now (vanilla
        /// Verb_LaunchProjectile.Projectile, or the CE loaded-ammo projectile), or null.</summary>
        internal static ThingDef ProjectileOf(Verb v)
        {
            if (v is Verb_LaunchProjectile lp)
            {
                return lp.Projectile;
            }
            if (ABCECompat.Active && ABCECompat.IsCEVerb(v))
            {
                return ABCECompat.ProjectileOf(v);
            }
            return null;
        }
    }
}
