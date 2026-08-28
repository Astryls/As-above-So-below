using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
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
    ///   State.CameraLockPosition        - public static FIELD, Vector3?
    ///   Avatar.physicsPosition          - public instance FIELD, Vector3?
    ///   Avatar.UpdateCamera             - public instance METHOD, void, no args
    /// </summary>
    internal static class PerspectiveShiftCompat
    {
        private static bool resolved;

        private static bool present;

        private static PropertyInfo isActive;

        private static FieldInfo avatarField;

        private static FieldInfo pawnField;

        /// <summary>State.CameraLockPosition. PS reads this in exactly two places:
        /// UpdatePhysics zeroes moveInput while it HasValue, and HandleSelectorClick
        /// early-returns on it. Both are the freeze we want. Its third effect - pinning the
        /// camera - lives INSIDE UpdateCamera, which is suspended for the whole time we
        /// hold the lock, so setting it buys the freeze without the pin.</summary>
        private static FieldInfo cameraLockField;

        private static FieldInfo physicsPosField;

        /// <summary>Armed while the viewed band is not the avatar's. The prefix below then
        /// skips PS's per-frame camera write outright.</summary>
        private static bool suspendCamera;

        /// <summary>⚠ ONLY release a lock WE set. PS sets the same field from its own
        /// JumpToCurrentMapLoc/PanToMapLoc postfixes (a letter jump, a colonist-bar click),
        /// and clearing one of those because our peek happened to end would silently
        /// unfreeze an avatar PS deliberately froze. Rule 28.</summary>
        private static bool lockedByUs;

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
                cameraLockField = AccessTools.Field(state, "CameraLockPosition");
                physicsPosField = avatarField != null
                    ? AccessTools.Field(avatarField.FieldType, "physicsPosition")
                    : null;
                present = isActive != null && avatarField != null && pawnField != null;
                if (present)
                {
                    InstallCameraSuspend();
                }
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

        /// <summary>True when PS is loaded and its shape bound. Used to hide the peek
        /// setting from everyone who does not run PS.</summary>
        internal static bool Present
        {
            get
            {
                if (!resolved)
                {
                    Resolve();
                }
                return present;
            }
        }

        /// <summary>
        /// SUSPEND PS'S CAMERA RATHER THAN OUT-WRITE IT.
        ///
        /// PS re-centres from a CameraDriver.Update POSTFIX, and our band clamp is
        /// [HarmonyPriority(Priority.Last)] on that same method, so we already get the final
        /// word every frame. That is precisely what made the gutter park: while the viewed
        /// band is not the avatar's, PS's target is a whole Slot away and our clamp's lower
        /// bound (minZ - half * 0.5, the window-7 allowance) is a STABLE equilibrium half a
        /// viewport below the band edge. Winning the write harder cannot fix a disagreement
        /// about where the camera belongs - so for the duration of a peek PS simply does not
        /// get to have an opinion, and vanilla panning plus our clamp own the view alone.
        /// Rule 24: clamping a value while something else still drives its target builds an
        /// oscillator; here we remove the other driver.
        /// </summary>
        private static void InstallCameraSuspend()
        {
            try
            {
                MethodInfo target = AccessTools.Method(avatarField.FieldType, "UpdateCamera");
                if (target == null)
                {
                    Log.WarningOnce(ABLog.Tag + " Perspective Shift is loaded but"
                        + " Avatar.UpdateCamera was not found; peeking at another level will"
                        + " leave the camera stranded in the gutter.", 0x2B10AB);
                    return;
                }
                HarmonyBoot.Harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PerspectiveShiftCompat),
                        nameof(SuspendCameraPrefix)));
                ABLog.Dev("Perspective Shift camera suspend INSTALLED.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Perspective Shift camera suspend failed to"
                    + " install: " + e.Message, 0x2B10AC);
            }
        }

        /// <summary>Parameterless and foreign-type-free on purpose: this is a prefix on a
        /// method of a type this assembly must never name in a signature.</summary>
        private static bool SuspendCameraPrefix()
        {
            return !suspendCamera;
        }

        /// <summary>
        /// Reconcile the peek state. Called every frame from the band clamp (so the state
        /// can never stick after the avatar dies, the player leaves PS's playstyle, or the
        /// map changes) and once more directly from SetBand, so entering a peek arms the
        /// suspend in the SAME frame rather than one frame late.
        /// </summary>
        internal static void SyncPeek(Map map)
        {
            if (!resolved)
            {
                Resolve();
            }
            if (!present)
            {
                return;
            }
            try
            {
                Pawn avatar = AvatarPawn();
                if (avatar == null || !avatar.Spawned || map == null || avatar.Map != map)
                {
                    Release();
                    return;
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    Release();
                    return;
                }
                if (bands.BandOf(avatar.Position) == ABBandView.CurrentBand(map))
                {
                    Release();
                    return;
                }
                suspendCamera = true;
                ABSettings set = ABMod.Settings;
                bool freeze = set == null || set.psFreezeAvatarWhilePeeking;
                if (freeze && !lockedByUs && cameraLockField != null
                    && cameraLockField.GetValue(null) == null)
                {
                    // The VALUE is inert while the camera is suspended (nothing reads it but
                    // the code we are skipping). Parking it on the avatar means that if the
                    // suspend is ever lost mid-peek, PS resumes on the avatar rather than on
                    // some stale cell.
                    cameraLockField.SetValue(null, avatar.Position.ToVector3Shifted());
                    lockedByUs = true;
                }
                else if (!freeze && lockedByUs)
                {
                    ClearOurLock();
                }
            }
            catch (Exception e)
            {
                Release();
                Log.ErrorOnce(ABLog.Tag + " Perspective Shift peek sync threw: " + e,
                    0x2B10AD);
            }
        }

        /// <summary>
        /// A TRANSIT MUST CUT, NOT GLIDE.
        ///
        /// PS's UpdateCamera ends in Vector3.Lerp(rootPos, target, 0.1f) whenever its
        /// cameraEasing setting is on. Ten percent a frame is a pleasant follow across a
        /// room and a long sightseeing tour across a band stride, which is what the field
        /// report described as "the camera pans and DOES go to the correct location".
        /// FollowTransit deliberately leaves the camera to the follower (preserveXZ:false)
        /// on the assumption that it re-centres within a frame; PS does not, so we close the
        /// distance ourselves and the lerp has nothing left to travel.
        ///
        /// physicsPosition MUST be cleared as well. It is PS's own smoothed stand-in for the
        /// pawn's position and it outranks pawn.Position in UpdateCamera's target
        /// (lock ?? physics ?? position) - left stale it holds the PRE-transit cell, so the
        /// camera would glide back down to the level just departed. Nulling it is PS's own
        /// convention for "this pawn moved discontinuously"; UpdatePhysics rebuilds it from
        /// pawn.Position next frame.
        /// </summary>
        internal static void SnapToAvatar(Pawn avatar)
        {
            if (!resolved)
            {
                Resolve();
            }
            if (!present || avatar == null || !avatar.Spawned)
            {
                return;
            }
            try
            {
                if (AvatarPawn() != avatar)
                {
                    return; // somebody else's transit; the follower is not on this pawn
                }
                ClearOurLock();
                suspendCamera = false;
                object avatarObj = avatarField.GetValue(null);
                if (avatarObj != null && physicsPosField != null)
                {
                    physicsPosField.SetValue(avatarObj, null);
                }
                CameraDriver cam = Find.CameraDriver;
                if (cam != null)
                {
                    cam.SetRootPosAndSize(
                        new Vector3(avatar.Position.x + 0.5f, 0f, avatar.Position.z + 0.5f),
                        cam.ZoomRootSize);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " Perspective Shift transit snap threw: " + e,
                    0x2B10AE);
            }
        }

        private static void Release()
        {
            suspendCamera = false;
            ClearOurLock();
        }

        private static void ClearOurLock()
        {
            if (!lockedByUs)
            {
                return;
            }
            lockedByUs = false;
            try
            {
                cameraLockField?.SetValue(null, null);
            }
            catch
            {
                // Releasing a freeze must never throw; the worst case is handled by PS's own
                // SetAvatar/ClearAvatar, both of which null this field.
            }
        }
    }
}
