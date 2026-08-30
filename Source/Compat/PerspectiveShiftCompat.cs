using System;
using System.Collections.Generic;
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
    ///   Avatar.RotateTowardsMouse       - private instance METHOD, void, no args
    ///   Avatar.HandleDropOrInteract     - private instance METHOD, bool,
    ///                                     (IntVec3 cell, bool itemInRange, Thing carriedThing)
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

        /// <summary>Last frame's answer to "is PS's camera lock held", by ANYONE. The
        /// falling edge of this is the only signal PS's "return to character" button
        /// emits - see <see cref="SyncPeek"/>.</summary>
        private static bool lockSeen;

        /// <summary>Re-entrancy guard: <see cref="ReturnToAvatar"/> calls
        /// <c>ABBandView.SetBand</c>, which calls <see cref="SyncPeek"/> straight back.</summary>
        private static bool returning;

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
                    InstallRawMouseFacing();
                    InstallHaulLinkTravel();
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
        /// THE DRAFTED AVATAR MUST FACE THE CURSOR ON SCREEN, NOT THE CELL THE COLUMN SHOWS.
        ///
        /// PS's Avatar.RotateTowardsMouse (idle drafted facing plus the aimAngle the weapon
        /// is drawn at) computes its direction from UI.MouseMapPosition() - which §87's
        /// global click-through DESCENDS whenever the cursor sits over see-through open air.
        /// From any upper level, every cell that is not foundation resolves a whole band
        /// stride down in -z, the direction is dominated by that stride, and the avatar
        /// locks facing south the moment the cursor leaves the platform (field report,
        /// window 15: "doesn't face your cursor while drafted when not aiming on a non
        /// upper floor foundation"). Facing is a SCREEN-SPACE question - the raw cursor is
        /// by definition where the cursor visually is relative to the avatar - so this is
        /// rule 70's flip side: one more caller for §87's opt-out list, installed from here
        /// because the target type is PS's.
        ///
        /// ⚠ THE COMBAT PATH IS DELIBERATELY NOT SUPPRESSED. Avatar_Combat's fire handler
        /// reads UI.MouseCell() as the TARGET cell, and the descended answer is exactly what
        /// makes shooting the level you are looking at work (§82/§66 own the aim visuals).
        /// Suppress only the facing method; aiming already behaves.
        /// </summary>
        private static void InstallRawMouseFacing()
        {
            try
            {
                MethodInfo target = AccessTools.Method(avatarField.FieldType, "RotateTowardsMouse");
                if (target == null)
                {
                    Log.WarningOnce(ABLog.Tag + " Perspective Shift is loaded but"
                        + " Avatar.RotateTowardsMouse was not found; the drafted avatar will"
                        + " face the wrong way while the cursor is over open air.", 0x2B10B4);
                    return;
                }
                HarmonyBoot.Harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PerspectiveShiftCompat),
                        nameof(RawMousePrefix)),
                    finalizer: new HarmonyMethod(typeof(PerspectiveShiftCompat),
                        nameof(RawMouseFinalizer)));
                ABLog.Dev("Perspective Shift raw-mouse facing INSTALLED.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Perspective Shift raw-mouse facing failed to"
                    + " install: " + e.Message, 0x2B10B5);
            }
        }

        /// <summary>Prefix/finalizer pair, not prefix/postfix: an exception inside PS's
        /// method must still pop, or the descend stays suppressed for the rest of the frame
        /// (ABMouseDescend's frame stamp would catch it next frame, but one bad frame of
        /// wrong pointing is one too many when it is free to avoid).</summary>
        private static void RawMousePrefix()
        {
            ABMouseDescend.Push();
        }

        private static void RawMouseFinalizer()
        {
            ABMouseDescend.Pop();
        }

        /// <summary>
        /// A HAULING AVATAR'S CLICK ON A LINK MEANS "TAKE IT", NOT "PUT THIS DOWN ON IT".
        ///
        /// While the avatar carries something, PS routes EVERY left-click through
        /// Avatar.HandleDropOrInteract before any float-menu logic can run - so our
        /// stairs/ladder/elevator travel verbs (§85.23) were unreachable in haul mode and
        /// the click deposited the cargo on the link's own cell instead (field report,
        /// window 15). The prefix diverts exactly one case: a spawned Building_ABStairs2 on
        /// the clicked cell with at least one executable travel option. The order comes
        /// from the SAME helper the float-menu provider uses
        /// (FloatMenuOptionProvider_ABStairsTravel.TravelOptionsFor), so labels,
        /// reachability and the §90 disembark-tile destination cannot drift between the two
        /// entry points. One destination executes immediately; an elevator with several
        /// opens the same destination menu the non-hauling click gets.
        ///
        /// The cargo rides along for free: PS's own HandleCarriedThing keeps reserving the
        /// carried thing while an UNDRAFTED pawn holds any job (a drafted avatar always
        /// drops - PS's rule, untouched), and the carryTracker container travels with the
        /// pawn through the wormhole. No executable option (unlinked staircase, no path)
        /// falls through to PS's vanilla drop behaviour unchanged.
        /// </summary>
        private static void InstallHaulLinkTravel()
        {
            try
            {
                MethodInfo target = AccessTools.Method(avatarField.FieldType, "HandleDropOrInteract");
                if (target == null)
                {
                    Log.WarningOnce(ABLog.Tag + " Perspective Shift is loaded but"
                        + " Avatar.HandleDropOrInteract was not found; a hauling avatar's"
                        + " click on stairs will drop the cargo instead of travelling.",
                        0x2B10B6);
                    return;
                }
                HarmonyBoot.Harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(PerspectiveShiftCompat),
                        nameof(DropOrInteractPrefix)));
                ABLog.Dev("Perspective Shift haul-mode link travel INSTALLED.");
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Perspective Shift haul-mode link travel failed"
                    + " to install: " + e.Message, 0x2B10B7);
            }
        }

        /// <summary>
        /// Foreign-type-free by the folder's rule: __instance is object (it is PS's Avatar),
        /// `cell` binds PS's own parameter by name. Runs regardless of PS's grab range - a
        /// distant link click while hauling previously did nothing at all, and "walk there
        /// and take the stairs" is the only sane reading of it.
        /// </summary>
        private static bool DropOrInteractPrefix(object __instance, IntVec3 cell, ref bool __result)
        {
            try
            {
                Pawn pawn = __instance == null ? null : pawnField.GetValue(__instance) as Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Map == null || !cell.InBounds(pawn.Map))
                {
                    return true;
                }
                Building_ABStairs2 link = null;
                List<Thing> things = pawn.Map.thingGrid.ThingsListAtFast(cell);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i] is Building_ABStairs2 s && s.Spawned)
                    {
                        link = s;
                        break;
                    }
                }
                if (link == null)
                {
                    return true; // not a link: PS's drop/deposit logic proceeds untouched
                }
                List<FloatMenuOption> options =
                    FloatMenuOptionProvider_ABStairsTravel.TravelOptionsFor(pawn, link);
                List<FloatMenuOption> live = new List<FloatMenuOption>();
                for (int i = 0; i < options.Count; i++)
                {
                    FloatMenuOption o = options[i];
                    if (o != null && o.action != null)
                    {
                        live.Add(o); // "(no path)" rows carry a null action by construction
                    }
                }
                if (live.Count == 0)
                {
                    return true; // unlinked or unreachable: old behaviour is the honest one
                }
                if (live.Count == 1)
                {
                    live[0].action();
                }
                else
                {
                    Find.WindowStack.Add(new FloatMenu(live));
                }
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " Perspective Shift haul-mode link travel threw: "
                    + e, 0x2B10B8);
                return true; // a bridge must never take the click down with it
            }
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
            if (!present || returning)
            {
                return; // `returning`: SetBand calls straight back into here
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

                // ⚠⚠ PS'S "RETURN TO CHARACTER" BUTTON IS NOTHING BUT A CLEARED FIELD, AND
                // WE HAD REMOVED THE ONLY THING THAT READ IT.
                //
                // Avatar_UI.DrawCameraLockReturnButton draws only while
                // State.CameraLockPosition.HasValue, and the entire click handler is
                // `State.CameraLockPosition = null`. It works because PS's UpdateCamera then
                // re-centres on the avatar next frame. While the viewed band is not the
                // avatar's we PREFIX UpdateCamera out (§76, rule 24 - remove the other
                // driver rather than out-write it), so the button cleared the field, vanished,
                // and the camera never moved. Reported as "return to character does nothing":
                // avatar downstairs, colonist-bar double-click on someone upstairs, press it.
                //
                // ⚠ WATCH THE FIELD, NOT `lockedByUs`, AND THAT DISTINCTION IS THE WHOLE FIX.
                // In the reported repro the lock was set by PS ITSELF (its
                // JumpToCurrentMapLoc postfix fires on the colonist-bar jump), so lockedByUs
                // is FALSE and a "did we set this" test misses exactly the case that was
                // reported. The falling edge is the signal regardless of who set it.
                //
                // Every other PS path that nulls this field means the same thing anyway -
                // "the camera has re-anchored on the avatar" (a jump whose target IS the
                // avatar's cell, SetAvatar, ClearAvatar, load) - and the correct response to
                // all of them is the same: bring the VIEW back to the avatar's level.
                bool lockNow = LockHeld();
                if (suspendCamera && lockSeen && !lockNow)
                {
                    ReturnToAvatar(map, bands, avatar);
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
                // Re-read rather than reusing `lockNow`: the freeze branch above may have
                // just set the field itself, and recording the pre-write value would make
                // our OWN lock look like a button press on the very next frame.
                lockSeen = LockHeld();
            }
            catch (Exception e)
            {
                Release();
                Log.ErrorOnce(ABLog.Tag + " Perspective Shift peek sync threw: " + e,
                    0x2B10AD);
            }
        }

        /// <summary>Is PS's camera lock held by anyone? False when PS is absent or the
        /// field could not be bound, which correctly reads as "no button to press".</summary>
        private static bool LockHeld()
        {
            try
            {
                return cameraLockField != null && cameraLockField.GetValue(null) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Honour "return to character": move the VIEWED BAND to the avatar's, then hand the
        /// camera back to PS.
        ///
        /// Order matters. The band change goes FIRST and the suspend is released only once it
        /// succeeded: releasing first would let PS's UpdateCamera drag the camera toward a
        /// band our clip refuses to draw, which is the window-7 curtain bug the suspend was
        /// built to prevent. A band that cannot be viewed (an undug level - SetBand refuses
        /// and says so) therefore leaves the peek exactly as it was rather than half-torn-down.
        /// </summary>
        private static void ReturnToAvatar(Map map, ABBandMap bands, Pawn avatar)
        {
            returning = true;
            try
            {
                int band = bands.BandOf(avatar.Position);
                if (band < 0 || !bands.BandExists(band)
                    || !ABBandView.SetBand(map, band, preserveXZ: false))
                {
                    lockSeen = LockHeld();
                    return; // could not follow: stay suspended, keep the view coherent
                }
                ABLog.Dev("Perspective Shift: return-to-character, view band -> " + band + ".");
                Release();
                // Same reason FollowTransit calls it: preserveXZ:false leaves the camera for
                // the follower, and PS lerps 10% a frame, so a band stride would be a long
                // glide through the gutter instead of a cut.
                SnapToAvatar(avatar);
                lockSeen = false;
            }
            catch (Exception e)
            {
                Release();
                Log.ErrorOnce(ABLog.Tag + " Perspective Shift return-to-character threw: "
                    + e, 0x2B10AF);
            }
            finally
            {
                returning = false;
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
            lockSeen = false;
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
