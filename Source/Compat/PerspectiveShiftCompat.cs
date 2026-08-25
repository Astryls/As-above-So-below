using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Perspective Shift (ferny.PerspectiveShift): tell the band view who
    /// the avatar is.
    ///
    /// Perspective Shift is an avatar camera: in its playstyle mode the camera IS one pawn,
    /// re-centred on it every frame from a CameraDriver.Update postfix that writes the
    /// camera transform directly. When that pawn takes the stairs, the pawn changes band but
    /// the VIEWED band does not, and the two camera systems then want different things: PS
    /// drags the camera to the pawn's new band, our clip refuses to draw anything outside
    /// the viewed band, and the player sees the band curtain with a lone selection bracket
    /// on it (field report, window 7). The fix is not to fight harder in the clamp - it is
    /// to treat the avatar's transit as the player's own decision to change levels, exactly
    /// like a colonist-bar double-click, and switch the viewed band with it
    /// (ABBandView.FollowTransit).
    ///
    /// Detection and the single read are reflection-only, same rules as every bridge in
    /// this folder (see the banner in DubsMintMinimapCompat for why no foreign type may
    /// appear in any signature and why this class carries no patch attribute): resolved
    /// lazily once, inert forever if any member is missing, and the per-call cost when PS
    /// is absent is one boolean test.
    ///
    /// Member shape as of PS 1.6 (verified against their shipped Source/):
    ///   PerspectiveShift.State          - public static class
    ///   State.IsActive                  - public static bool PROPERTY (frame-cached; false
    ///                                     when the avatar pawn is null or dead)
    ///   State.Avatar                    - public static FIELD, type PerspectiveShift.Avatar
    ///   Avatar.pawn                     - public instance FIELD
    /// </summary>
    internal static class PerspectiveShiftCompat
    {
        private static bool resolved;

        private static bool present;

        private static PropertyInfo isActive;

        private static FieldInfo avatarField;

        private static FieldInfo pawnField;

        private static void Resolve()
        {
            resolved = true;
            try
            {
                Type state = AccessTools.TypeByName("PerspectiveShift.State");
                if (state == null)
                {
                    return; // mod absent: stay inert, log nothing (ghost-warning rule)
                }
                isActive = AccessTools.Property(state, "IsActive");
                avatarField = AccessTools.Field(state, "Avatar");
                pawnField = avatarField != null
                    ? AccessTools.Field(avatarField.FieldType, "pawn")
                    : null;
                present = isActive != null && avatarField != null && pawnField != null;
                if (!present)
                {
                    // The mod IS loaded but its shape moved - say so once, because the
                    // symptom this bridge prevents (curtain screen on the avatar's stair
                    // climb) is otherwise unattributable in the field.
                    Log.WarningOnce(ABLog.Tag + " Perspective Shift is loaded but its"
                        + " State/Avatar members were not found; the view will not follow"
                        + " the avatar across levels.", 0x2B10A9);
                }
                else
                {
                    ABLog.Dev("Perspective Shift bridge resolved.");
                }
            }
            catch (Exception e)
            {
                present = false;
                Log.WarningOnce(ABLog.Tag + " Perspective Shift bridge failed to resolve: "
                    + e.Message, 0x2B10AA);
            }
        }

        /// <summary>The live avatar pawn, or null when PS is absent, inactive (director
        /// mode), or the avatar is dead. Safe to call from anywhere on the main thread.</summary>
        internal static Pawn AvatarPawn()
        {
            if (!resolved)
            {
                Resolve();
            }
            if (!present)
            {
                return null;
            }
            try
            {
                if (!(isActive.GetValue(null) is bool active) || !active)
                {
                    return null;
                }
                object avatar = avatarField.GetValue(null);
                return avatar == null ? null : pawnField.GetValue(avatar) as Pawn;
            }
            catch
            {
                return null; // a cosmetic follow must never take a transit down with it
            }
        }
    }
}
