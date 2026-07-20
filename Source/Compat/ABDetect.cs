using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Postfix-insensitive mod detection shared by every soft-compat module.
    ///
    /// Why not ModsConfig.IsActive: that is a raw hash-set lookup on the
    /// EFFECTIVE packageId. When a workshop mod is copied into the local Mods
    /// folder, both copies share an authored packageId and RimWorld
    /// disambiguates by giving the Steam copy a "_steam" suffix - so depending
    /// on which copy the player activates, a plain IsActive("Author.Mod")
    /// check can return false while the mod is loaded and running (found live
    /// 2026-07-20: the Rimefeller bridge went silent for a local Rimefeller
    /// copy while the NAME-based XML FindMod patch still applied its defs -
    /// tanks existed, equalization never ran). ModLister keeps a
    /// postfix-insensitive index precisely for this; use it everywhere.
    /// </summary>
    public static class ABDetect
    {
        public static bool Active(string packageId)
        {
            return ModLister.GetActiveModWithIdentifier(packageId, ignorePostfix: true) != null;
        }
    }
}
