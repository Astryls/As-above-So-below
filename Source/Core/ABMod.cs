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

        /// <summary>
        /// Rebake the map mesh when a setting that lives in VERTICES changed.
        ///
        /// The depth falloff is baked at print time (that is the whole reason it is free
        /// per frame), so the running map keeps drawing the old scale until its sections
        /// regenerate. Doing it here rather than in the slider means one regeneration when
        /// the window closes instead of one per frame of a drag - RegenerateEverythingNow
        /// rebuilds every section of a map that may be seven levels tall.
        /// </summary>
        public override void WriteSettings()
        {
            base.WriteSettings();
            if (!ABSettings.ConsumeBakedVisualDirty()
                || Current.ProgramState != ProgramState.Playing)
            {
                return;
            }
            try
            {
                Map map = Find.CurrentMap;
                if (map?.mapDrawer != null)
                {
                    map.mapDrawer.RegenerateEverythingNow();
                }
            }
            catch (System.Exception e)
            {
                Log.Warning(ABLog.Tag + " could not rebake the map mesh after a visual"
                    + " setting change (it will refresh as sections dirty): " + e.Message);
            }
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
