using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Gizmo/UI textures, loaded once on the main thread. [StaticConstructorOnStartup]
    /// is load-bearing: ContentFinder texture loads off the main thread throw, and gizmo
    /// getters run during play, so the fetch must happen here.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ABTex
    {
        /// <summary>User-supplied icons (eye over stairs + direction arrow) for the
        /// "view level" gizmo on stairs, ladders and elevators.</summary>
        public static readonly Texture2D ViewLevelUp = ContentFinder<Texture2D>.Get("UI/AB_ViewLevelUp");
        public static readonly Texture2D ViewLevelDown = ContentFinder<Texture2D>.Get("UI/AB_ViewLevelDown");
    }
}
