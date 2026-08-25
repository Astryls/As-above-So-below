using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AsAboveSoBelow
{
    /// <summary>
    /// GETS THE VANILLA PAWN ATLAS CACHE BACK FOR BELOW PAWNS.
    ///
    /// Vanilla renders a humanlike from a baked atlas frame instead of walking its render
    /// tree once the camera is far enough out: ParallelGetPreRenderResults sets `useCached`
    /// and returns BEFORE renderTree.ParallelPreDraw, and RenderPawnAt then issues ONE
    /// DrawMesh (the blit) instead of one per body part, apparel item, wound and hediff
    /// overlay. For a below pawn that saving is paid twice over, because our see-below pass
    /// runs all three draw phases SERIALLY ON THE MAIN THREAD - vanilla gets ParallelPreDraw
    /// for free on job workers, we do not.
    ///
    /// ⚠ THIS MOD USED TO TURN THAT CACHE OFF ON PURPOSE. The cached branch draws through
    /// GenDraw.DrawMeshNowOrLater(mesh, POSITION, ROTATION, mat) - no matrix, so
    /// PawnDrawParms.matrix is never read and the depth shrink evaporated. The old fix was
    /// to force `disableCache` for every below pawn, which bought a correct depth cue at the
    /// price of the most expensive render path in the game, for the pawns we draw the most
    /// awkwardly. Three things make the trade unnecessary now:
    ///
    ///   A. The blit is scaled. The full render path draws through the MATRIX overload of
    ///      DrawMeshNowOrLater (PawnRenderTree line 191); the blit uses the position/rotation
    ///      overload. They are DIFFERENT METHODS, so a prefix on the blit's overload can
    ///      never double-scale the normal path - the discrimination is structural, not a
    ///      heuristic. See Patch_GenDraw_ABBlitScale.
    ///   B. The zoom gate is computed instead of hardcoded. Vanilla's `> 18f` is one
    ///      constant covering every display; the honest threshold is a texel count, and a
    ///      below pawn is ALREADY drawn smaller, which lowers it further. See
    ///      CacheZoomThreshold.
    ///   C. First-touch bakes are budgeted, because the frame set is filled lazily by a
    ///      camera render inside the draw call - the classic tiny-average/huge-max trap.
    ///
    /// ⚠ THE FALLBACK DIRECTION IS ALWAYS "MORE FIDELITY, NEVER LESS". Every gate in this
    /// file, when it fails, sends the pawn down the full render path it uses today. A bug
    /// here can cost frame time. It cannot cost correctness.
    /// </summary>
    internal static class ABBelowRenderCache
    {
        /// <summary>Vanilla's own literal in ParallelGetPreRenderResults, and what our hook
        /// returns for every pawn that is not ours to decide.</summary>
        internal const float VanillaZoomThreshold = 18f;

        internal const int ModeOff = 0;

        internal const int ModeAuto = 1;

        internal const int ModeAggressive = 2;

        /// <summary>
        /// Atlas fills per frame, split by what they actually cost.
        ///
        /// A COLD fill takes a frame set from a pool of 32 per 2048-square atlas and, when
        /// the last slot goes, makes vanilla allocate ANOTHER 16 MB render texture - so it
        /// carries an allocation risk a rebake does not. A REBAKE is one camera render into
        /// an existing slot. Both were sharing a single budget of 2, which measured badly:
        /// run #423 deferred 293 of 879 cache-wanting draws, i.e. a third of them were
        /// starved by the budget rather than by anything about the pawn.
        ///
        /// The pawns still waiting render at FULL quality meanwhile, so the only cost of
        /// being wrong here is frame time, in one direction or the other.
        /// </summary>
        private const int MaxColdFillsPerFrame = 4;

        private const int MaxRebakesPerFrame = 8;

        /// <summary>Pixels of atlas per world cell. The frame is PawnTextureAtlas.FrameSize
        /// square and the quad it is mapped onto spans 2 world units (TextureAtlasHelper
        /// .CreateMeshForUV emits vertices at +-1), so the density is FrameSize / 2.
        ///
        /// Read through reflection rather than referencing the const directly: a C# const is
        /// INLINED INTO OUR DLL at compile time, so if Ludeon ever raises the frame size our
        /// build would keep computing thresholds for the old one and quietly serve blurry
        /// pawns. This costs one reflection call at startup.</summary>
        private static readonly float AtlasPixelsPerCell = ResolveAtlasPixelsPerCell();

        /// <summary>True only between BeginPawn and EndPawn - i.e. inside the three draw
        /// phases of ONE below pawn. Everything in this file is inert outside that window,
        /// which is the same arm/disarm discipline BelowDrawOffsetZ and BelowDrawScale use
        /// next door.</summary>
        internal static bool InBelowPass;

        /// <summary>Armed by BeginPawn when the pawn is NOT eligible. Read by
        /// Patch_PawnRenderer_ABBelowDisableCache, which is the only thing that acts on it.</summary>
        internal static bool SuppressCache;

        /// <summary>Depth shrink of the pawn currently being drawn. Separate from
        /// ABBelowDynamicDraw.BelowDrawScale on purpose: that field also drives the matrix
        /// patch on the full path, this one only ever feeds the threshold maths.</summary>
        private static float currentShrink = 1f;

        private static int coldBudget;

        private static int rebakeBudget;

        /// <summary>
        /// ⚠ SNAPSHOTTED ON THE MAIN THREAD, NEVER READ LIVE. CacheZoomThreshold is called
        /// from ParallelGetPreRenderResults, and that method RUNS ON UNITY JOB WORKER
        /// THREADS for every ordinary on-screen pawn (DynamicDrawManager.PreDrawVisibleThings
        /// dispatches DrawPhase.ParallelPreDraw through ManagedJobParallelFor). Touching a
        /// Unity API like Camera.pixelHeight from there throws "can only be called from the
        /// main thread" - and because our hook swallows exceptions to fail open, the damage
        /// would not be a crash but a thrown-and-caught exception PER PAWN PER FRAME, which
        /// is far worse than the cost we came here to remove.
        ///
        /// The InBelowPass gate already keeps workers out (our pass runs in a postfix of
        /// DrawDynamicThings, after PreDrawVisibleThings has joined every worker), so this
        /// is belt and braces - but it is the kind of belt that costs one float copy per
        /// frame and saves a silent 3 fps mystery.
        /// </summary>
        private static float cameraPixelHeight = 1080f;

        /// <summary>Per-def memo of "does this apparel draw something outside the atlas
        /// bake". 0 unknown, 1 no, 2 yes; indexed by ThingDef.index, which DefDatabase
        /// assigns contiguously.</summary>
        private static byte[] wornExtraFlags;

        // ---- diagnostics ------------------------------------------------------

        /// <summary>
        /// ⚠ PERMITTED IS NOT BLITTED, AND CONFLATING THEM COST A TEST RUN.
        ///
        /// The first version of these counters recorded only our own verdict and printed it
        /// as "atlas-cached", which is an UPPER BOUND: after we permit the cache, vanilla's
        /// own chain can still veto it on zoom, carrying, crawling, swimming, dessication,
        /// hediff materials or an active animation. Reading permitted-as-blitted makes a
        /// zoom-threshold problem and a gear-veto problem look identical, which is exactly
        /// the distinction the whole 4K threshold design turns on.
        ///
        /// blittedSum is now counted where the blit actually happens, so permitted minus
        /// blitted isolates vanilla's vetoes and the reason counters isolate ours.
        /// </summary>
        internal static long permittedSum;

        internal static long blittedSum;

        internal static long vetoedSum;

        internal static long vetoOff;

        internal static long vetoNonHumanlike;

        internal static long vetoStairAnim;

        internal static long vetoGear;

        internal static long vetoColdBudget;

        internal static long vetoRebakeBudget;

        /// <summary>Counted by Patch_GenDraw_ABBlitScale, which fires on the one draw call
        /// the cached branch makes.</summary>
        internal static void NoteBlit()
        {
            blittedSum++;
        }

        // ---- frame / pawn lifecycle -------------------------------------------

        /// <summary>Called once at the top of the see-below pass.</summary>
        internal static void BeginFrame()
        {
            coldBudget = MaxColdFillsPerFrame;
            rebakeBudget = MaxRebakesPerFrame;
            Camera cam = Find.Camera;
            if (cam != null && cam.pixelHeight > 0)
            {
                cameraPixelHeight = cam.pixelHeight;
            }
        }

        /// <summary>Arms the decision for ONE pawn. Always paired with EndPawn in a finally.</summary>
        internal static void BeginPawn(Pawn pawn, float shrink)
        {
            InBelowPass = true;
            currentShrink = shrink;
            bool ok = Eligible(pawn);
            SuppressCache = !ok;
            if (ok)
            {
                permittedSum++;
            }
            else
            {
                vetoedSum++;
            }
        }

        internal static void EndPawn()
        {
            InBelowPass = false;
            SuppressCache = false;
            currentShrink = 1f;
        }

        // ---- the decision -----------------------------------------------------

        private static bool Eligible(Pawn pawn)
        {
            try
            {
                ABSettings s = ABMod.Settings;
                if (s == null || s.belowPawnCache == ModeOff)
                {
                    vetoOff++;
                    return false;
                }
                if (pawn == null || !pawn.RaceProps.Humanlike)
                {
                    vetoNonHumanlike++;
                    return false; // vanilla's own gate; animals never take the cached branch
                }
                // The stair animation is a scale AND a lateral shimmy about the pawn's own
                // root, expressed through PawnDrawParms.matrix. The blit carries a uniform
                // scale only, so an animating pawn keeps the full path - it is one pawn, for
                // about a second, and it is the one the player is watching.
                if (ABStairAnim.IsAnimating(pawn))
                {
                    vetoStairAnim++;
                    return false;
                }
                // ⚠ GEAR IS DRAWN OUTSIDE THE BLIT AND CANNOT BE SCALED WITH IT.
                // On the cached branch RenderPawnAt calls DrawEquipmentAndApparelExtras
                // separately, and that path reaches Graphics.DrawMesh DIRECTLY
                // (PawnRenderUtility.DrawEquipmentAiming builds its own TRS) rather than
                // through GenDraw, so the blit prefix cannot reach it. A pawn two levels
                // down at 72% holding a full-size rifle looks broken, so pawns that would
                // show gear take the full path instead.
                //
                // A transpiler on DrawEquipmentAiming/DrawCarriedWeapon would scale it, and
                // was rejected deliberately: Combat Extended and several animation mods
                // already patch exactly those two methods, and this mod's CE bridge is
                // young enough without adding an IL fight over weapon drawing.
                if (WouldDrawGear(pawn))
                {
                    vetoGear++;
                    return false;
                }
                // Deliberately NOT re-tested here: crawling, swimming, carried things,
                // dessication, hediff materials, active animations and portrait/statue
                // flags. Vanilla's own && chain vetoes every one of them AFTER our hook
                // returns, so re-checking them would be duplicated logic that can drift.
                return BakeAffordable(pawn);
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: below render-cache eligibility failed: "
                    + e.Message, 762195902);
                return false;
            }
        }

        /// <summary>
        /// MIRRORS PawnRenderUtility.DrawEquipmentAndApparelExtras EXACTLY, NOT CONSERVATIVELY.
        ///
        /// ⚠ THIS TEST BEING SLOPPY IS WHAT MADE THE CACHE LOOK USELESS. The first version
        /// vetoed any pawn merely HOLDING a weapon, and in RimWorld nearly every colonist
        /// holds one - so run #423 measured a feature that was switched off for most of the
        /// map. But vanilla only DRAWS the weapon when the pawn is aiming at something or
        /// carrying it openly (drafted, hunting, a duty or lord job that demands it). An
        /// undrafted colonist hauling steel with a rifle in their inventory draws no weapon
        /// at all, so there is nothing for the blit to mismatch.
        ///
        /// That is the whole answer to "can we relax this without hurting combat": we do not
        /// relax it, we make it ACCURATE. Every case where vanilla actually draws a weapon -
        /// which is every combat case, since aiming and drafted are two of the three triggers
        /// - still takes the full render path and is pixel-identical to today. The pawns we
        /// win back are the ones whose weapon was never on screen in the first place.
        ///
        /// The alternative that WAS rejected: allowing gear pawns through at shallow depths.
        /// The weapon would be 15% too large AND sit too far from the body, because
        /// DrawEquipmentAiming offsets it by `0.4 + equippedDistanceOffset` in unscaled world
        /// units - so a shrunk pawn gets a detached-looking weapon, worst exactly during the
        /// aiming that combat is made of.
        /// </summary>
        private static bool WouldDrawGear(Pawn pawn)
        {
            if (pawn.equipment?.Primary != null)
            {
                Job job = pawn.CurJob;
                if (job != null && job.def != null && !job.def.neverShowWeapon)
                {
                    // Aiming at something: the weapon is drawn rotated onto the target.
                    if (pawn.stances?.curStance is Stance_Busy busy
                        && !busy.neverAimWeapon && busy.focusTarg.IsValid)
                    {
                        return true;
                    }
                    // Drafted, hunting, or under a duty/lord job that shows the weapon.
                    if (PawnRenderUtility.CarryWeaponOpenly(pawn))
                    {
                        return true;
                    }
                }
            }
            List<Apparel> worn = pawn.apparel?.WornApparel;
            if (worn != null)
            {
                for (int i = 0; i < worn.Count; i++)
                {
                    if (DrawsWornExtras(worn[i]?.def))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Shield belts and their modded cousins draw a bubble in DrawWornExtras,
        /// which the atlas bake does not contain. Whether a given apparel def overrides that
        /// method never changes at runtime, so it is resolved once per def.</summary>
        private static bool DrawsWornExtras(ThingDef def)
        {
            if (def == null)
            {
                return false;
            }
            int idx = def.index;
            if (wornExtraFlags == null || idx >= wornExtraFlags.Length)
            {
                int size = Mathf.Max(idx + 1, DefDatabase<ThingDef>.DefCount);
                Array.Resize(ref wornExtraFlags, size);
            }
            byte flag = wornExtraFlags[idx];
            if (flag != 0)
            {
                return flag == 2;
            }
            bool overrides = false;
            try
            {
                Type t = def.thingClass;
                if (t != null && typeof(Apparel).IsAssignableFrom(t))
                {
                    MethodInfo m = AccessTools.Method(t, "DrawWornExtras");
                    overrides = m != null && m.DeclaringType != typeof(Apparel);
                }
            }
            catch
            {
                overrides = true; // unreadable: assume it draws, and keep the full path
            }
            wornExtraFlags[idx] = (byte)(overrides ? 2 : 1);
            return overrides;
        }

        /// <summary>
        /// ⚠ THE TINY-AVERAGE / HUGE-MAX TRAP, PRE-EMPTED.
        ///
        /// A frame set is filled lazily: GetBlitMeshUpdatedFrame notices isDirty and runs a
        /// full camera render into the atlas RIGHT THERE in the draw call. Handing the cache
        /// a crowd of never-before-cached pawns in one frame therefore trades a steady cost
        /// for a spike, which is the worse deal even when the average improves.
        ///
        /// A pawn with no frame set at all is worse still: it takes a slot from a pool of 32
        /// per 2048-square atlas, and when the last one goes vanilla allocates ANOTHER 16 MB
        /// render texture. Both cases spend from the same small per-frame budget; a pawn that
        /// cannot afford it renders at full quality this frame and tries again next frame.
        /// </summary>
        private static bool BakeAffordable(Pawn pawn)
        {
            if (!GlobalTextureAtlasManager.TryGetPawnFrameSet(pawn, out PawnTextureAtlasFrameSet frameSet,
                    out bool _, allowCreatingNew: false))
            {
                if (coldBudget <= 0)
                {
                    vetoColdBudget++;
                    return false;
                }
                coldBudget--;
                return true;
            }
            bool[] dirty = frameSet?.isDirty;
            if (dirty != null)
            {
                for (int i = 0; i < dirty.Length; i++)
                {
                    if (dirty[i])
                    {
                        if (rebakeBudget <= 0)
                        {
                            vetoRebakeBudget++;
                            return false;
                        }
                        rebakeBudget--;
                        return true;
                    }
                }
            }
            return true; // fully warm: the blit costs one DrawMesh and nothing else
        }

        // ---- the zoom threshold ------------------------------------------------

        /// <summary>
        /// REPLACES VANILLA'S `18f` LITERAL, VIA THE TRANSPILER BELOW.
        ///
        /// The cached blit is lossless exactly while the atlas holds at least as many pixels
        /// per cell as the screen shows. Screen density is pixelHeight / (2 * RootSize),
        /// because CameraDriver sets orthographicSize = RootSize outright. Atlas density is
        /// FrameSize / 2. Solving for the root size at which they meet:
        ///
        ///     RootSize >= shrink * pixelHeight / (2 * atlasPixelsPerCell)
        ///
        /// The `shrink` term is what makes this worth doing at all: a below pawn is drawn at
        /// 85% one level down and 61% three levels down, so it needs proportionally fewer
        /// screen pixels and qualifies proportionally sooner.
        ///
        /// Concretely, with the default 85% falloff: 1080p qualifies at every legal zoom
        /// (the threshold lands under the 11 zoom floor), 1440p at essentially every zoom,
        /// and 2160p from RootSize 14.3 one level down, 12.2 two levels down, and every zoom
        /// three levels down. Vanilla's own 18 is, read this way, simply "lossless at 4K" -
        /// a constant sized for the largest display rather than for the one in use.
        ///
        /// ⚠ RETURNS VANILLA'S CONSTANT FOR EVERY PAWN THAT IS NOT A BELOW PAWN. This hook
        /// sits in a method that runs for every pawn drawn in the game; only the see-below
        /// pass arms it.
        /// </summary>
        internal static float CacheZoomThreshold()
        {
            try
            {
                if (!InBelowPass)
                {
                    return VanillaZoomThreshold;
                }
                ABSettings s = ABMod.Settings;
                if (s == null || s.belowPawnCache == ModeOff)
                {
                    return VanillaZoomThreshold;
                }
                if (s.belowPawnCache == ModeAggressive)
                {
                    return 0f; // every zoom, softening accepted, by explicit player choice
                }
                float px = cameraPixelHeight;
                if (px <= 0f || AtlasPixelsPerCell <= 0f)
                {
                    return VanillaZoomThreshold;
                }
                float shrink = currentShrink > 0f && currentShrink <= 1f ? currentShrink : 1f;
                return shrink * px / (2f * AtlasPixelsPerCell);
            }
            catch
            {
                return VanillaZoomThreshold;
            }
        }

        private static float ResolveAtlasPixelsPerCell()
        {
            int frameSize = 128;
            try
            {
                FieldInfo fi = AccessTools.Field(typeof(PawnTextureAtlas), "FrameSize");
                if (fi != null && fi.IsLiteral)
                {
                    frameSize = Convert.ToInt32(fi.GetRawConstantValue());
                }
            }
            catch
            {
                // Keep the known-good default.
            }
            // The blit quad spans 2 world units, so a frame covers 2 cells.
            return frameSize / 2f;
        }
    }

    /// <summary>
    /// SCALES THE CACHED BLIT.
    ///
    /// The cached branch of RenderPawnAt positions a premade mesh with
    /// GenDraw.DrawMeshNowOrLater(mesh, loc, quat, mat, drawNow) - no matrix anywhere, which
    /// is why PawnDrawParms.matrix (and with it the depth shrink) was inert on that path.
    /// Rerouting the same draw through the MATRIX overload restores it in one line.
    ///
    /// ⚠ WHY THIS CANNOT DOUBLE-SCALE THE NORMAL PATH. The full render path draws every node
    /// through the matrix overload (PawnRenderTree line 191), and the matrix overload is a
    /// DIFFERENT METHOD. So a pawn taking the full path never enters this prefix even while
    /// armed, and the scale it already carries in its own matrix stays applied exactly once.
    /// The same fact means the call we make here cannot re-enter this patch.
    ///
    /// ⚠ ARMED WINDOW ONLY. BelowDrawScale is non-unit exclusively between the arm and the
    /// finally in ABBelowDynamicDraw, i.e. inside the three draw phases of a single below
    /// pawn. The only other GenDraw caller reachable in that window is an Anomaly animation
    /// worker's lens flare, and an animating pawn cannot be on the cached path at all
    /// (vanilla vetoes useCached when currentAnimation is set), so in practice this fires
    /// once per cached below pawn per frame and for nothing else.
    /// </summary>
    [HarmonyPatch(typeof(GenDraw), nameof(GenDraw.DrawMeshNowOrLater),
        new Type[] { typeof(Mesh), typeof(Vector3), typeof(Quaternion), typeof(Material), typeof(bool) })]
    public static class Patch_GenDraw_ABBlitScale
    {
        private static bool Prefix(Mesh mesh, Vector3 loc, Quaternion quat, Material mat, bool drawNow)
        {
            // The blit is the only draw in the game that maps a RENDER TEXTURE (the pawn
            // atlas) as its main texture, so this identifies a real cache hit without
            // guessing - and it counts them even when the depth shrink is off and the scaling
            // branch below returns early.
            if (ABBelowRenderCache.InBelowPass && mat != null && mat.mainTexture is RenderTexture)
            {
                ABBelowRenderCache.NoteBlit();
            }
            float s = ABBelowDynamicDraw.BelowDrawScale;
            if (s >= 0.999f || s <= 0f)
            {
                return true; // not inside a shrunk below-pawn draw: the overwhelming case
            }
            try
            {
                GenDraw.DrawMeshNowOrLater(mesh, Matrix4x4.TRS(loc, quat, new Vector3(s, 1f, s)),
                    mat, drawNow);
                return false;
            }
            catch (Exception e)
            {
                Log.WarningOnce(ABLog.Tag + " V2: scaled blit failed, falling back: "
                    + e.Message, 762195903);
                return true;
            }
        }
    }

    /// <summary>
    /// MOVES VANILLA'S ZOOM GATE FROM A LITERAL TO A COMPUTED THRESHOLD.
    ///
    /// One operand, in one place: the `18f` that ParallelGetPreRenderResults compares
    /// ZoomRootSize against becomes a call to ABBelowRenderCache.CacheZoomThreshold, which
    /// returns that very literal for every pawn except the below pawn currently being drawn.
    /// A postfix cannot do this job - by the time one runs, renderTree.ParallelPreDraw has
    /// already been paid, which is the entire cost we are trying to avoid.
    ///
    /// ⚠ FAILS OPEN, LOUDLY. If the literal cannot be found (a future version, or another
    /// mod transpiling the same method first) the IL is returned untouched and the mod
    /// behaves exactly as it does today. The warning names the method so the next person
    /// knows which single line to re-anchor rather than re-deriving the design.
    /// </summary>
    [HarmonyPatch(typeof(PawnRenderer), "ParallelGetPreRenderResults")]
    public static class Patch_PawnRenderer_ABCacheZoomThreshold
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            MethodInfo zoomGetter = AccessTools.PropertyGetter(typeof(CameraDriver),
                nameof(CameraDriver.ZoomRootSize));
            MethodInfo hook = AccessTools.Method(typeof(ABBelowRenderCache),
                nameof(ABBelowRenderCache.CacheZoomThreshold));
            if (zoomGetter == null || hook == null)
            {
                Log.Warning(ABLog.Tag + " V2: below render cache could not resolve its"
                    + " transpiler targets; below pawns keep the full render path.");
                return code;
            }

            // Strict pass: the literal that is compared against ZoomRootSize itself.
            int hits = 0;
            for (int i = 1; i < code.Count; i++)
            {
                if (!IsThreshold(code[i]) || !code[i - 1].Calls(zoomGetter))
                {
                    continue;
                }
                Retarget(code[i], hook);
                hits++;
            }
            if (hits == 1)
            {
                return code;
            }

            // Loose pass: the compiler reordered something, but if the method contains
            // exactly ONE such literal there is no ambiguity about which one it is.
            if (hits == 0)
            {
                int only = -1;
                int count = 0;
                for (int i = 0; i < code.Count; i++)
                {
                    if (IsThreshold(code[i]))
                    {
                        count++;
                        only = i;
                    }
                }
                if (count == 1)
                {
                    Retarget(code[only], hook);
                    return code;
                }
            }

            Log.Warning(ABLog.Tag + " V2: below render cache found " + hits
                + " anchors for the pawn-atlas zoom threshold in"
                + " PawnRenderer.ParallelGetPreRenderResults (expected 1). The IL is"
                + " unchanged and below pawns keep the full render path; re-anchor the"
                + " ZoomRootSize comparison in ABBelowRenderCache.");
            return code;
        }

        private static bool IsThreshold(CodeInstruction ci)
        {
            return ci.opcode == OpCodes.Ldc_R4
                && ci.operand is float f
                && Mathf.Abs(f - ABBelowRenderCache.VanillaZoomThreshold) < 0.001f;
        }

        /// <summary>Mutates the instruction in place rather than replacing it, so any labels
        /// or exception blocks attached to it stay attached.</summary>
        private static void Retarget(CodeInstruction ci, MethodInfo hook)
        {
            ci.opcode = OpCodes.Call;
            ci.operand = hook;
        }
    }
}
