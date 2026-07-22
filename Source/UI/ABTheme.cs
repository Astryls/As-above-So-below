using System;
using System.Reflection;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Modern Suite parity tokens (see GLOBAL_RULES): shared plate and border
    /// colors, and the suite-wide themable accent resolved from Modern
    /// Notifications by reflection with the contract fallback. Cached per frame.
    /// </summary>
    public static class ABTheme
    {
        public static readonly Color PanelBG = new Color32(0x1B, 0x1F, 0x23, 0xFF);

        public static readonly Color BGL = new Color32(0x2F, 0x33, 0x37, 0xFF);

        public static readonly Color BGD = new Color32(0x0E, 0x10, 0x13, 0xFF);

        public static readonly Color TextDim = new Color(0.62f, 0.65f, 0.70f);

        private static readonly Color AccentFallback = new Color(0.45f, 0.75f, 1f);

        private static PropertyInfo accentProp;
        private static FieldInfo accentField;
        private static bool accentResolved;
        private static int accentFrame = -1;
        private static Color accentCached = new Color(0.45f, 0.75f, 1f);

        public static Color Accent
        {
            get
            {
                int frame = Time.frameCount;
                if (frame == accentFrame)
                {
                    return accentCached;
                }
                accentFrame = frame;
                accentCached = ResolveAccent();
                return accentCached;
            }
        }

        private static Color ResolveAccent()
        {
            try
            {
                if (!accentResolved)
                {
                    accentResolved = true;
                    Type theme = GenTypes.GetTypeInAnyAssembly("ModernNotifications.Theme");
                    if (theme != null)
                    {
                        accentProp = theme.GetProperty("Accent", BindingFlags.Public | BindingFlags.Static);
                        if (accentProp == null)
                        {
                            accentField = theme.GetField("Accent", BindingFlags.Public | BindingFlags.Static);
                        }
                    }
                }
                if (accentProp != null)
                {
                    return (Color)accentProp.GetValue(null);
                }
                if (accentField != null)
                {
                    return (Color)accentField.GetValue(null);
                }
            }
            catch (Exception)
            {
                accentProp = null;
                accentField = null;
            }
            return AccentFallback;
        }
    }
}
