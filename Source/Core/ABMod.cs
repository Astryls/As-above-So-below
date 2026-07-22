using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    public class ABMod : Mod
    {
        public static ABSettings Settings { get; private set; }

        /// <summary>The mod's content pack, exposed so dev tools can resolve
        /// RootDir for writing self-test reports back into the mod folder.</summary>
        public static ModContentPack ModContent { get; private set; }

        public ABMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ABSettings>();
            ModContent = content;
        }

        public override string SettingsCategory() => "As above, So below";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.DoWindowContents(inRect);
        }
    }

    public static class ABLog
    {
        public const string Tag = "[As above, So below]";

        public static void Dev(string msg)
        {
            if (ABMod.Settings != null && ABMod.Settings.verboseLogging)
            {
                Log.Message(Tag + " " + msg);
            }
        }
    }
}
