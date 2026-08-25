using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// V2 - which band the player is looking at, and keeping the camera inside it.
    ///
    /// Band isolation is mostly FREE: MapDrawer.DrawMapMesh only draws sections that
    /// overlap the camera's ViewRect, so a band the camera cannot see costs nothing to
    /// render. Clamping the camera to the current band's z-range is therefore the whole
    /// of "hide the other levels" - no section-layer surgery, and above all no
    /// DrawPosOffsetPatcher (V1 had to patch hundreds of DrawPos getters on
    /// ParallelPreDraw worker threads purely because the level below was a different Map;
    /// here it is the same map, so the problem does not exist).
    /// </summary>
    public static class ABBandView
    {
        // The band being viewed lives on ABBandMap.viewBand. It used to be a static
        // Dictionary<int,int> keyed by map.uniqueID here, which was never cleared - and
        // uniqueID restarts at 0 for each new game, so every new or loaded colony inherited
        // the previous colony's band. See ABBandMap.viewBand.

        public static int CurrentBand(Map map)
        {
            if (map == null)
            {
                return 0;
            }
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return 0;
            }
            if (bands.viewBand >= 0 && bands.BandExists(bands.viewBand))
            {
                return bands.viewBand;
            }
            return bands.surfaceBand;
        }

        public static int CurrentLevel(Map map)
        {
            ABBandMap bands = ABBands.CompOf(map);
            return bands == null || !bands.Banded ? 0 : CurrentBand(map) - bands.surfaceBand;
        }

        /// <summary>Switch bands, preserving the in-band position and the zoom. Because
        /// bands are aligned 1:1 the camera lands on exactly the cell above/below the one
        /// it was looking at, which is what makes the column read as a single place.</summary>
        public static bool SetBand(Map map, int band, bool preserveXZ = true)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded || !bands.BandExists(band))
            {
                return false;
            }
            // LOOKING UP IS FREE; LOOKING DOWN NEEDS STAIRS.
            //
            // You can see a mountain from the ground, so gating the view of a level ABOVE
            // the surface behind construction was arbitrary - the terrain up there is
            // simply visible. What lies BELOW is genuinely unknown until something digs
            // into it, so that gate stays and doubles as the reveal.
            if (band < bands.surfaceBand && !bands.IsOpen(band))
            {
                Messages.Message("AB_LevelNotDug".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return false;
            }
            int old = CurrentBand(map);
            bands.viewBand = band;
            Patch_CameraDriver_ABClipViewToBand.Invalidate();
            if (preserveXZ && Find.CameraDriver != null)
            {
                IntVec3 look = CameraCell(map);
                if (bands.BandOf(look) == old)
                {
                    IntVec3 moved = bands.Translate(look, band);
                    if (moved.InBounds(map))
                    {
                        Find.CameraDriver.SetRootPosAndSize(
                            new Vector3(moved.x + 0.5f, 0f, moved.z + 0.5f),
                            Find.CameraDriver.ZoomRootSize);
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// THE VIEW FOLLOWS A FOLLOWED PAWN ACROSS LEVELS. Called from both wormhole carry
        /// sites, immediately after the teleport.
        ///
        /// A transit moves a pawn to a band the camera may not be viewing. For an ordinary
        /// hauler that is fine - the player was not watching it in particular, and yanking
        /// the view every time anyone used the stairs would make a busy stairwell
        /// unwatchable. But when the camera is LOCKED to that pawn the situation inverts:
        /// Perspective Shift's avatar camera (and vanilla's own follow-selected mode, which
        /// Simple Camera Setting drives) re-centre on the pawn every frame, so after a
        /// transit the camera is dragged towards a band the view refuses to draw, and the
        /// player gets the band curtain with a selection bracket floating on it (field
        /// report, window 7). For a locked camera the pawn's transit IS the player's own
        /// level change - the same reading Patch_CameraJumper_ABBandJump already gives a
        /// colonist-bar double-click - so the viewed band switches with it.
        ///
        /// Deliberately NOT fired for plain selection without follow: selecting a miner and
        /// sending him downstairs while watching the surface is normal play, and vanilla's
        /// camera does not chase selection either.
        /// </summary>
        public static void FollowTransit(Pawn pawn)
        {
            try
            {
                if (pawn == null || !pawn.Spawned)
                {
                    return;
                }
                Map map = pawn.Map;
                if (map == null || map != Find.CurrentMap)
                {
                    return; // never yank the view for a transit on a map not being watched
                }
                ABBandMap bands = ABBands.CompOf(map);
                if (bands == null || !bands.Banded)
                {
                    return;
                }
                int band = bands.BandOf(pawn.Position);
                if (band == CurrentBand(map) || !bands.BandExists(band))
                {
                    return;
                }
                bool locked = PerspectiveShiftCompat.AvatarPawn() == pawn;
                if (!locked)
                {
                    CameraDriver cam = Find.CameraDriver;
                    locked = cam != null && cam.config != null && cam.config.followSelected
                        && Find.Selector != null && Find.Selector.IsSelected(pawn);
                }
                if (!locked)
                {
                    return;
                }
                // preserveXZ:false for the same reason the colonist-bar jump uses it: the
                // follower re-centres on the pawn within a frame, so preserving the old
                // in-band position would only add a visible double move.
                ABLog.Dev("V2: view following " + pawn.LabelShort + " to band " + band + ".");
                SetBand(map, band, preserveXZ: false);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: FollowTransit threw: " + e, 762195937);
            }
        }

        /// <summary>
        /// LAND ON THE COLONY, whatever the start spot ended up saying.
        ///
        /// Vanilla's Game.InitNewGame finishes with
        /// <c>JumpToCurrentMapLoc(MapGenerator.PlayerStartSpot)</c>, which is correct on an
        /// ordinary map because nothing moves the colony after the spot is chosen. On a
        /// banded map two things can, and both run AFTER the pawns are on the ground:
        /// RescueStrandedColonists relocates anything the pod scatter threw across a gutter
        /// into a band the carve is about to erase, and the carve itself can make the
        /// recorded spot unstandable. Either way the camera obeys a cell the colony is no
        /// longer standing on, and the player opens their new game looking at empty ground -
        /// intermittently, because it depends on where the scatter happened to land.
        ///
        /// Rather than chase the discrepancy, the camera is aimed at the pawns THEMSELVES,
        /// which is what the start spot was only ever a proxy for. That is the same answer
        /// the LOAD path already reaches in ABBandMap.FinalizeInit, for the same stated
        /// reason ("camera does not land on pawns as expected"); this closes the matching
        /// hole on the NEW-GAME path, which had no equivalent.
        ///
        /// Runs from GameComponent.StartedNewGame, which is the last thing InitNewGame does
        /// and therefore the only hook that lands AFTER vanilla's own camera jump. A
        /// GameComponent.FinalizeInit hook would be overwritten by it.
        ///
        /// rememberedCameraPos is written by hand as well as the live camera. The deferred
        /// delegate in ABBandMap.FinalizeInit judges by that field rather than the camera
        /// (deliberately - it cannot rely on the ordering of vanilla's own restore), so
        /// leaving it stale would let the load-path fixer immediately undo this one.
        /// </summary>
        public static bool LandOnColony(Map map)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded)
            {
                return false; // ordinary map: vanilla's jump is already right
            }
            try
            {
                IntVec3 anchor = IntVec3.Invalid;
                int best = int.MaxValue;
                int sumX = 0;
                int sumZ = 0;
                int n = 0;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    sumX += p.Position.x;
                    sumZ += p.Position.z;
                    n++;
                }
                if (n == 0)
                {
                    // No free colonists at all - a mech or animal start, or a scenario that
                    // spawns nothing. Any player thing is a better aim point than a start
                    // spot nobody is standing on.
                    foreach (Thing t in map.listerThings.AllThings)
                    {
                        if (t.Faction != null && t.Faction.IsPlayer && t.Spawned)
                        {
                            sumX += t.Position.x;
                            sumZ += t.Position.z;
                            n++;
                        }
                    }
                }
                if (n == 0)
                {
                    return false;
                }
                IntVec3 centroid = new IntVec3(sumX / n, 0, sumZ / n);

                // Aim at the pawn NEAREST the centroid, not the centroid itself: with pods
                // scattered around a lake or a rock face the mean of the positions can be a
                // cell nobody is anywhere near, and framing the group on a real member is
                // both closer to vanilla's intent and never inside a mountain.
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    int d = (p.Position - centroid).LengthHorizontalSquared;
                    if (d < best)
                    {
                        best = d;
                        anchor = p.Position;
                    }
                }
                if (!anchor.IsValid)
                {
                    anchor = centroid;
                }
                if (!anchor.InBounds(map))
                {
                    return false;
                }

                JumpTo(map, anchor);
                if (map.rememberedCameraPos != null && Find.CameraDriver != null)
                {
                    map.rememberedCameraPos.rootPos =
                        new Vector3(anchor.x + 0.5f, 0f, anchor.z + 0.5f);
                    map.rememberedCameraPos.rootSize = Find.CameraDriver.ZoomRootSize;
                }
                ABLog.Dev("V2: new game camera landed on the colony at " + anchor
                    + " (band " + bands.BandOf(anchor) + ", start spot was "
                    + (MapGenerator.PlayerStartSpotValid
                        ? MapGenerator.PlayerStartSpot.ToString() : "invalid") + ").");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning(ABLog.Tag + " V2: could not land the camera on the colony: " + e);
                return false;
            }
        }

        public static void JumpTo(Map map, IntVec3 cell)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                CameraJumper.TryJump(new GlobalTargetInfo(cell, map));
                return;
            }
            int band = bands.BandOf(cell);
            // CUT across bands, PAN within one - the same rule Patch_CameraJumper_ABBandJump
            // applies to vanilla jumps. That patch cannot rescue THIS call on its own:
            // viewBand is already switched by the time its prefix compares bands, so a
            // cross-band jump looks same-band to it and vanilla's default Pan survives -
            // seen from the link gizmos as the camera sweeping through the gutter and the
            // intervening level (field report, window 8). Decide the mode BEFORE writing
            // viewBand.
            CameraJumper.MovementMode mode = band != bands.viewBand
                ? CameraJumper.MovementMode.Cut
                : CameraJumper.MovementMode.Pan;
            bands.viewBand = band;
            Patch_CameraDriver_ABClipViewToBand.Invalidate();
            CameraJumper.TryJump(new GlobalTargetInfo(cell, map), mode);
        }

        private static IntVec3 CameraCell(Map map)
        {
            Vector3 p = Find.CameraDriver.MapPosition.ToVector3();
            IntVec3 c = new IntVec3(Mathf.RoundToInt(p.x), 0, Mathf.RoundToInt(p.z));
            return c.InBounds(map) ? c : map.Center;
        }

        public static bool TryStep(Map map, int delta)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return false;
            }
            return SetBand(map, CurrentBand(map) + delta);
        }

        /// <summary>World-space z bounds of the current band. The camera must keep its
        /// whole VIEW inside these, not just its centre - see the clamp below.</summary>
        public static bool TryBandBounds(Map map, out float minZ, out float maxZ)
        {
            minZ = 0f;
            maxZ = 0f;
            ABBandMap bands = ABBands.CompOf(map);
            if (map == null || bands == null || !bands.Banded)
            {
                return false;
            }
            CellRect r = bands.RectOfBand(CurrentBand(map));
            minZ = r.minZ;
            maxZ = r.maxZ + 1;
            return true;
        }
    }

    /// <summary>
    /// HIDES THE OTHER LEVELS, whatever the camera does.
    ///
    /// Clipping the view rect to the current band is the whole mechanism, and it is one
    /// patch because vanilla funnels both halves of "what is on screen" through this one
    /// property:
    ///   - MapDrawer.ViewRect is `Find.CameraDriver.CurrentViewRect.ExpandedBy(1)...`, and
    ///     DrawMapMesh only calls Section.DrawSection for sections overlapping it, so
    ///     terrain, buildings, plants and items outside the band stop drawing.
    ///   - DynamicDrawManager.ComputeCulledThings builds its cull job from the same
    ///     property, so pawns and other dynamic things outside the band are culled too.
    ///
    /// Patching here rather than at either draw site avoids touching
    /// ThingCullDetails - a PRIVATE nested struct, which a postfix cannot name in its
    /// signature - and avoids a per-thing loop on a per-frame path.
    ///
    /// This is what makes any edge overhang visually harmless: the neighbouring band simply
    /// is not drawn, so the player sees empty space past the edge of the level they are on.
    ///
    /// ALWAYS ACTIVE as of the baked-bounds rework. It used to be gated on the free-pan
    /// setting, on the reasoning that a strictly clamped camera makes the clip a no-op.
    /// That reasoning died with the setting: per-level `panMargin` values mean the view is
    /// *expected* to overhang, so the clip is now the load-bearing guarantee rather than a
    /// redundant one. It is memoised per frame, so being unconditional costs one branch.
    ///
    /// The returned rect is modified, not the cached `lastViewRect` field, so vanilla's
    /// per-frame cache is not corrupted.
    /// </summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.CurrentViewRect), MethodType.Getter)]
    public static class Patch_CameraDriver_ABClipViewToBand
    {
        // Per-frame memo, mirroring vanilla's own lastViewRectGetFrame caching right next
        // door. CurrentViewRect is read several times a frame by vanilla and three more
        // times by this mod, and resolving the bounds costs TWO ConditionalWeakTable
        // probes (TryBandBounds and CurrentBand each call ABBands.CompOf). Recomputing that
        // per call on a per-frame render path is exactly the kind of cost this mod has
        // measured and removed before.
        private static int cachedFrame = -1;

        private static bool cachedActive;

        private static int cachedLo;

        private static int cachedHi;

        /// <summary>Called when the viewed band changes, so the clip cannot lag a frame
        /// behind a level switch.</summary>
        public static void Invalidate()
        {
            cachedFrame = -1;
        }

        private static void Postfix(ref CellRect __result)
        {
            try
            {
                if (cachedFrame != Time.frameCount)
                {
                    cachedFrame = Time.frameCount;
                    cachedActive = false;
                    Map map = Find.CurrentMap;
                    if (map != null
                        // Gravship rendering encapsulates its own bounds into the view
                        // downstream of this; leave that path alone rather than clipping a
                        // rect it is about to extend for a different purpose.
                        && !WorldComponent_GravshipController.GravshipRenderInProgess
                        && ABBandView.TryBandBounds(map, out float minZ, out float maxZ))
                    {
                        cachedActive = true;
                        cachedLo = Mathf.RoundToInt(minZ);
                        cachedHi = Mathf.RoundToInt(maxZ) - 1;
                    }
                }
                if (!cachedActive)
                {
                    return;
                }
                int lo2 = cachedLo;
                int hi2 = cachedHi;
                if (__result.minZ >= lo2 && __result.maxZ <= hi2)
                {
                    return; // already inside the band - the common case, and free
                }
                // NEVER hand back an empty or inverted rect.
                //
                // The first version collapsed a fully off-band view to `maxZ = minZ - 1`,
                // i.e. Height 0. That rect does not stay contained: MapDrawer feeds it
                // through ExpandedBy(1), and SectionLayer_SunShadows.GetSunShadowsViewRect
                // shifts its edges by the light vector and re-clips - so a degenerate rect
                // propagates into vanilla geometry and the Burst cull job rather than
                // simply drawing nothing. Clamping to a single valid row at the band edge
                // draws just as little and stays a well-formed rect everywhere downstream.
                CellRect r = __result;
                if (r.maxZ < lo2)
                {
                    r.minZ = lo2;
                    r.maxZ = lo2;
                }
                else if (r.minZ > hi2)
                {
                    r.minZ = hi2;
                    r.maxZ = hi2;
                }
                else
                {
                    r.minZ = Mathf.Max(r.minZ, lo2);
                    r.maxZ = Mathf.Min(r.maxZ, hi2);
                }
                __result = r;
            }
            catch
            {
                // Never let a view-rect tweak break rendering; worst case the neighbouring
                // band shows for a frame.
            }
        }
    }

    /// <summary>
    /// Holds the camera to the current band, using the BAKED per-level bounds in
    /// ABCameraBounds rather than a player-facing setting.
    ///
    /// Run #7 caught the naive version: clamping only rootPos (the view CENTRE) still let
    /// the viewport overhang the band edge, so the gutter and the neighbouring level were
    /// visible as a strip along the top/bottom of the screen. The camera is orthographic,
    /// so the visible half-height in world units IS RootSize - the centre must therefore
    /// stay RootSize away from each band edge, and the zoom must not exceed half the band
    /// height or no centre position can satisfy that. That remains the derived default
    /// (`maxZoom &lt;= 0`, `panMargin == 0`); a level may now widen either deliberately,
    /// because the view clip above guarantees the overhang shows empty space and never the
    /// neighbouring level.
    /// </summary>
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.Update))]
    public static class Patch_CameraDriver_ABClampToBand
    {
        /// <summary>
        /// ⚠ LAST AMONG Update POSTFIXES, ON PURPOSE. Camera mods overwhelmingly do their
        /// work in a postfix of this same method - Perspective Shift slams `rootPos` to its
        /// avatar there, Simple Camera Setting re-centres on the followed pawn there - and
        /// whoever runs last decides where the camera is. A band clamp that runs first is
        /// not a clamp; it is a suggestion that the next postfix overwrites, and the view
        /// then leaves the band while the view-rect clip keeps the band the only thing
        /// drawn, which reads as the map going blank. Priority.Last is not a courtesy here,
        /// it is the ordering the invariant depends on.
        /// </summary>
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(CameraDriver __instance)
        {
            try
            {
                // Not while loading. The camera updates every frame throughout a load, and
                // Map.ExposeData runs ConstructComponents() FIRST - so this per-frame path
                // can reach a half-built ABBandMap whose bandCount already passes the Banded
                // check while the state RectOfBand reads is not wired yet (observed: NRE in
                // RectOfBand during load-in). Same rule CompOf uses for its cache, one layer
                // out.
                if (Current.ProgramState != ProgramState.Playing
                    || Scribe.mode != LoadSaveMode.Inactive
                    || !ABGuard.On(ABGuard.Camera))
                {
                    return;
                }
                Map map = Find.CurrentMap;
                if (map == null || !ABBandView.TryBandBounds(map, out float minZ, out float maxZ))
                {
                    return;
                }
                if (ABCameraBounds.CalibrationUnlocked)
                {
                    // The calibration window is open: stand down so the camera can be
                    // pushed PAST the baked limits. A tool that could only reach the
                    // limits already in force could not help choose better ones.
                    return;
                }
                float bandHeight = maxZ - minZ;
                ABCameraBounds.Limits lim = ABCameraBounds.For(ABBandView.CurrentLevel(map));
                ABSettings set = ABMod.Settings;
                bool moved = false;

                // Derived default: never zoom out further than the band can fill.
                if (set == null || set.clampZoomToLevel)
                {
                    float maxSize = lim.maxZoom > 0f ? lim.maxZoom : bandHeight * 0.5f;
                    // ⚠ THE TARGET FIRST, AND THIS IS THE WHOLE FLICKER FIX. Clamping only
                    // the current size leaves `desiredSize` parked wherever the player (or a
                    // camera mod's widened sizeRange) put it, and Update closes that gap
                    // again every single frame BEFORE this postfix runs. See
                    // ABCameraBounds.DesiredSize.
                    if (ABCameraBounds.DesiredSize != null
                        && ABCameraBounds.DesiredSize(__instance) > maxSize)
                    {
                        ABCameraBounds.DesiredSize(__instance) = maxSize;
                    }
                    if (ABCameraBounds.RootSize(__instance) > maxSize)
                    {
                        ABCameraBounds.RootSize(__instance) = maxSize;
                        moved = true;
                    }
                }

                float half = ABCameraBounds.RootSize(__instance);
                Vector3 p = ABCameraBounds.RootPos(__instance);
                float margin = Mathf.Max(0f, lim.panMargin);
                // ⚠ THE MARGIN IS CAPPED SO THE BAND CAN NEVER LEAVE THE SCREEN ENTIRELY.
                //
                // panMargin was calibrated at far zoom (63.5 cells against a viewport
                // half-height of ~63), where it means "the level may sit half a screen away
                // from centre". At CLOSE zoom the same absolute number is catastrophic: a
                // follow camera chasing a pawn past the seam parks the view centre dozens of
                // cells beyond the band edge with a viewport only ~10 cells tall, the band
                // is entirely off-screen, and the player sees nothing but curtain (window-7
                // field report: black screen with one selection bracket). Bounding the
                // centre to half a viewport past the edge guarantees at least a quarter of
                // the screen always shows the level, while leaving the calibrated far-zoom
                // freedom untouched - at zoom 63 the calibrated bound is the tighter one.
                float lo = Mathf.Max(minZ + half - margin, minZ - half * 0.5f);
                float hi = Mathf.Min(maxZ - half + margin, maxZ + half * 0.5f);
                float clamped = lo > hi ? (minZ + maxZ) * 0.5f : Mathf.Clamp(p.z, lo, hi);
                if (!Mathf.Approximately(clamped, p.z))
                {
                    p.z = clamped;
                    ABCameraBounds.RootPos(__instance) = p;
                    moved = true;
                }

                // The renderer reads the GameObject, not these fields. Vanilla already
                // pushed the PRE-clamp values onto it at the end of Update, so without this
                // every clamp shows up a frame late and loses to any mod that writes the
                // camera transform itself.
                if (moved)
                {
                    ABCameraBounds.ApplyToGameObject(__instance);
                }
            }
            catch (Exception e)
            {
                // ⚠ THIS IS A PER-FRAME POSTFIX. A bare Log.Error here wrote one line every
                // frame for as long as the fault lasted: the dev log filled, Player.log grew
                // without bound, and the resulting I/O was itself enough to make the game
                // unplayable - so the logging turned a cosmetic camera bug into a hang.
                // Every other error path in this mod is ErrorOnce or guard-switched; this one
                // was the exception. Tripping the switch also stops the throw recurring at
                // all, and the settings panel offers a re-arm.
                ABGuard.Disable(ABGuard.Camera, e, "V2 camera band clamp");
            }
        }
    }
}
