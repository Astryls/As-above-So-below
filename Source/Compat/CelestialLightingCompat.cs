using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// Soft compat with Celestial Lighting (joof.celestiallighting), a purely visual lighting
    /// overhaul. Detection and binding are by reflection only; neither mod references the
    /// other's assembly, and every failure path here falls open to "Celestial Lighting is not
    /// installed".
    ///
    /// ⚠ CELESTIAL LIGHTING IS COSMETIC BY DESIGN. It never writes glowGrid or any gameplay
    /// light value, and nothing in this file may change that. Everything below moves vertex
    /// COLOURS on a mesh; no band-resolution result is ever fed back into a grid.
    ///
    /// THE PROBLEM, IN TWO HALVES.
    ///
    /// 1. WE OWN THE LIGHTING OVERLAY AND THEY CANNOT REACH IT.
    ///    Patch_LightingOverlay_ABSuppressOnBanded turns vanilla's overlay off for the WHOLE
    ///    banded map, and SectionLayer_ABBelowLighting draws in its place - including on the
    ///    surface band, since SourceIndex returns the cell itself where nothing shows through.
    ///    Their two overlay passes are postfixes on SectionLayer_LightingOverlay.Regenerate,
    ///    and our layer takes its geometry from the static Bake(...), which reaches
    ///    GenerateLightingOverlay directly and never routes through Regenerate. So on a banded
    ///    map their indoor occlusion and their lamp shadows were not merely wrong, they were
    ///    ABSENT EVERYWHERE - painting a canvas nobody draws. We own the mesh, so we invoke
    ///    them: OverlayPasses.IndoorSkyOcclusion (alpha) then VectorLightFill (rgb), which is
    ///    their own documented order, folded into the tail of BuildColors.
    ///
    /// 2. THEIR OWN LAYERS DRAW, BUT SAMPLE THE SKY BAND.
    ///    Night desaturation, eave shade and the sun-shadow mesh are not suppressed here, so
    ///    they render - and resolve glow, roof, edifice and room at the cell they are drawing
    ///    into. On a see-through cell that is open air: unroofed, empty, no room. Exactly the
    ///    class of bug BuildColors exists to fix, in three more places.
    ///
    /// ONE HOOK FOR BOTH. Their CellResolver takes a cell-to-cell delegate, and every per-cell
    /// read in all five consumers routes through it, so registering our band mapping once fixes
    /// the independent layers AND makes the two passes we invoke resolve correctly. Null means
    /// identity on their side, so an ordinary map is untouched.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CelestialLightingCompat
    {
        public const string PackageId = "joof.celestiallighting";

        private const string ResolverType = "CelestialLighting.CellResolver";
        private const string PassesType = "CelestialLighting.OverlayPasses";

        /// <summary>Their overlay passes, bound once. Null when Celestial Lighting is absent or
        /// its API has moved.</summary>
        private static Func<Map, CellRect, Color32[], bool> indoorSkyOcclusion;

        private static Func<Map, CellRect, Color32[], bool> vectorLightFill;

        private static bool active;

        /// <summary>Optional diagnostic reach-ins. Not required for binding: their absence costs the
        /// dev report, not the feature.</summary>
        private static FieldInfo logNextField;

        private static MethodInfo forceRebuild;

        private static string bindReport = "never attempted";

        /// <summary>True once binding succeeded. Read on the render path, so it is a plain
        /// field test rather than a lazy resolve.</summary>
        public static bool Active => active;

        static CelestialLightingCompat()
        {
            try
            {
                if (!ModsConfig.IsActive(PackageId) && !ModsConfig.IsActive(PackageId + "_steam"))
                {
                    bindReport = "Celestial Lighting is not active in this load";
                    ABLog.Dev("Celestial Lighting compat: not present");
                    return;
                }
                Bind();
            }
            catch (Exception e)
            {
                // A binding failure must never cost us the map. Their effects stay wrong on
                // banded maps; ours keep working exactly as they did before this file existed.
                active = false;
                Log.ErrorOnce(ABLog.Tag + " Celestial Lighting compat bind threw (compat disabled,"
                    + " everything else unaffected): " + e, 762195897);
            }
        }

        private static void Bind()
        {
            Type resolver = AccessTools.TypeByName(ResolverType);
            Type passes = AccessTools.TypeByName(PassesType);
            if (resolver == null || passes == null)
            {
                bindReport = "CellResolver=" + (resolver != null) + " OverlayPasses=" + (passes != null)
                    + " - their build predates the integration hook";
                ABLog.Dev("Celestial Lighting compat: active but CellResolver/OverlayPasses not"
                    + " found - their build predates the integration hook. Skipping.");
                return;
            }

            // ⚠ THE DELEGATE TYPES ARE SHARED, SO THESE ARE ORDINARY ASSIGNMENTS.
            // Func<Map, IntVec3, IntVec3> resolves to the same runtime type in both assemblies
            // (System.Core plus Assembly-CSharp), which is the whole reason this integration
            // needs no reference in either direction. Only the FIELD is reached reflectively.
            FieldInfo resolveCell = AccessTools.Field(resolver, "ResolveCell");
            FieldInfo bandOf = AccessTools.Field(resolver, "BandOf");
            FieldInfo externallyOwned = AccessTools.Field(resolver, "OverlayExternallyOwned");

            MethodInfo occlusion = AccessTools.Method(passes, "IndoorSkyOcclusion",
                new[] { typeof(Map), typeof(CellRect), typeof(Color32[]) });
            MethodInfo fill = AccessTools.Method(passes, "VectorLightFill",
                new[] { typeof(Map), typeof(CellRect), typeof(Color32[]) });

            if (resolveCell == null || bandOf == null || externallyOwned == null
                || occlusion == null || fill == null)
            {
                bindReport = "hook incomplete: resolveCell=" + (resolveCell != null)
                    + " bandOf=" + (bandOf != null) + " owned=" + (externallyOwned != null)
                    + " occlusion=" + (occlusion != null) + " fill=" + (fill != null);
                ABLog.Dev("Celestial Lighting compat: hook present but incomplete"
                    + " (resolveCell=" + (resolveCell != null)
                    + " bandOf=" + (bandOf != null)
                    + " owned=" + (externallyOwned != null)
                    + " occlusion=" + (occlusion != null)
                    + " fill=" + (fill != null)
                    + "). Skipping rather than half-binding.");
                return;
            }

            resolveCell.SetValue(null, (Func<Map, IntVec3, IntVec3>)ResolveCell);
            bandOf.SetValue(null, (Func<Map, IntVec3, int>)BandOf);
            externallyOwned.SetValue(null, (Func<Map, bool>)OverlayExternallyOwned);

            indoorSkyOcclusion = (Func<Map, CellRect, Color32[], bool>)Delegate.CreateDelegate(
                typeof(Func<Map, CellRect, Color32[], bool>), occlusion);
            vectorLightFill = (Func<Map, CellRect, Color32[], bool>)Delegate.CreateDelegate(
                typeof(Func<Map, CellRect, Color32[], bool>), fill);

            // Diagnostic reach-ins, best effort.
            Type fillPatch = AccessTools.TypeByName("CelestialLighting.Patch_VectorLightFill");
            logNextField = fillPatch == null ? null : AccessTools.Field(fillPatch, "LogNext");
            forceRebuild = AccessTools.Method("CelestialLighting.VectorShadowRedraw:ForceRebuild");

            active = true;
            bindReport = "BOUND";
            ABLog.Dev("Celestial Lighting compat: BOUND (cell resolver + 2 overlay passes)");
        }

        /// <summary>
        /// One-click chain report: did we bind, do we own the overlay here, what does the cell under
        /// the mouse resolve to, and what do their two passes actually do to it.
        ///
        /// Arms their §20 dump and forces a whole-map rebuild so the dump fires on the next
        /// regenerate - which on a banded map is the only way to see inside a pass that is invoked
        /// from BuildColors rather than from their own postfix.
        /// </summary>
        [DebugAction("As above", "AB2: celestial lighting report",
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void CelestialReport()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(ABLog.Tag + " Celestial Lighting compat report");
            sb.AppendLine("  installed: " + (ModsConfig.IsActive(PackageId)
                || ModsConfig.IsActive(PackageId + "_steam")));
            sb.AppendLine("  bind: " + bindReport + "  (active=" + active + ")");
            sb.AppendLine("  banded map: " + ABBands.Banded(map)
                + "   rendering guard: " + ABGuard.On(ABGuard.Rendering));
            sb.AppendLine("  overlay externally owned (their postfixes skip): "
                + OverlayExternallyOwned(map));
            sb.AppendLine("  skyGlow: " + (map.skyManager?.CurSkyGlow ?? -1f).ToString("F3")
                + "   (§20 fill outdoors scales by 1-skyGlow, so daylight = ZERO by design)");

            IntVec3 c = UI.MouseCell();
            if (c.InBounds(map))
            {
                ABBandMap bands = ABBands.CompOf(map);
                IntVec3 resolved = ResolveCell(map, c);
                sb.AppendLine("  mouse cell " + c + " band=" + BandOf(map, c)
                    + (bands != null && bands.Banded ? " gutter=" + bands.InGutter(c) : ""));
                sb.AppendLine("    resolves to " + resolved + " band=" + BandOf(map, resolved)
                    + (resolved == c ? "  (IDENTITY - opaque, bottom band or seam)" : "  (SEE-THROUGH)"));
                sb.AppendLine("    resolved cell roofed=" + map.roofGrid.Roofed(resolved)
                    + "  (roofed cells keep full fill regardless of daylight)");
            }

            ProbeSection(map, sb);

            if (logNextField != null && forceRebuild != null)
            {
                logNextField.SetValue(null, true);
                forceRebuild.Invoke(null, null);
                sb.AppendLine("  ARMED their §20 dump and forced a whole-map rebuild -"
                    + " look for a '[CelestialLighting §20 fill]' line next.");
            }
            else
            {
                sb.AppendLine("  could not arm their §20 dump (logNext=" + (logNextField != null)
                    + " forceRebuild=" + (forceRebuild != null) + ")");
            }

            Log.Message(sb.ToString());
        }

        /// <summary>
        /// ⚠ THE DECISIVE PROBE. Runs the exact pipeline the drawn mesh runs, on a throwaway copy of
        /// the mouse cell's section, and prints that cell's centre vertex after each stage.
        ///
        /// ALPHA is sky cover: HIGHER = the overlay darkens the cell MORE. RGB is the light colour.
        /// So "too dark" is a rising alpha or a falling RGB, and this says which stage did it - our
        /// own colour pass, their §7b occlusion, or their §20 fill (which SUBTRACTS vanilla's
        /// corner-wrap leak before adding its own fill, and can therefore darken as well as brighten).
        /// </summary>
        private static void ProbeSection(Map map, StringBuilder sb)
        {
            if (!active)
            {
                sb.AppendLine("  probe skipped: compat not bound");
                return;
            }

            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                sb.AppendLine("  probe skipped: map is not banded");
                return;
            }

            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map))
            {
                sb.AppendLine("  probe skipped: mouse is off the map");
                return;
            }

            try
            {
                CellRect rect = new CellRect(c.x / 17 * 17, c.z / 17 * 17, 17, 17);
                rect.ClipInsideMap(map);

                int w = rect.Width;
                int h = rect.Height;
                int firstCenterInd = (w + 1) * (h + 1);
                int vi = firstCenterInd + (c.z - rect.minZ) * w + (c.x - rect.minX);

                Color32[] probe = SectionLayer_ABBelowLighting.BuildColors(map, bands, rect);
                if (vi < 0 || vi >= probe.Length)
                {
                    sb.AppendLine("  probe skipped: vertex index out of range");
                    return;
                }

                sb.AppendLine("  PROBE at " + c + " (section " + rect.minX + "," + rect.minZ + ")");
                sb.AppendLine("    AB colours alone : " + Fmt(probe[vi]));

                bool wroteOcclusion = indoorSkyOcclusion(map, rect, probe);
                sb.AppendLine("    after their §7b  : " + Fmt(probe[vi])
                    + "   (wrote=" + wroteOcclusion + ")");

                bool wroteFill = vectorLightFill(map, rect, probe);
                sb.AppendLine("    after their §20  : " + Fmt(probe[vi])
                    + "   (wrote=" + wroteFill + ")");
                sb.AppendLine("    §20 wrote=False means the pass declined; the armed dump below says"
                    + " which early return it took.");
            }
            catch (Exception e)
            {
                sb.AppendLine("  probe threw: " + e.Message);
            }
        }

        private static string Fmt(Color32 col)
        {
            return "r=" + col.r + " g=" + col.g + " b=" + col.b + " a=" + col.a;
        }

        /// <summary>
        /// Cell -> the cell whose content is actually drawn there.
        ///
        /// ⚠ MUST STAY IDENTICAL TO SectionLayer_ABBelowLighting.SourceIndex, INCLUDING
        /// requireUnfogged: FALSE. Their passes write into the same vertex array ours does. If
        /// this resolved a different cell than the colour underneath it, the lamp fill and the
        /// glow it is correcting would describe two different levels and the disagreement would
        /// show up as light that does not match the ground it lands on.
        /// </summary>
        private static IntVec3 ResolveCell(Map map, IntVec3 cell)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return cell;
            }
            if (!ABBands.TryResolveVisibleFrom(map, bands, cell, requireUnfogged: false,
                    out IntVec3 below, out _))
            {
                return cell;
            }
            return below;
        }

        /// <summary>
        /// Band id, used on their side to keep light and line-of-sight inside one level.
        ///
        /// ⚠ A GUTTER CELL RETURNS NEGATIVE, WHICH IS WHAT KEEPS LIGHT OFF THE SEAM. The seam
        /// rows are impassable open air with everything cleared, so to any flood or ray march
        /// they read as an open corridor spanning the map into the bands above and below - the
        /// §49.1 leak, in a new place. A negative band never lights and is never lit, so their
        /// DDA cannot walk it even if a lamp sits right against the edge.
        /// </summary>
        private static int BandOf(Map map, IntVec3 cell)
        {
            ABBandMap bands = ABBands.CompOf(map);
            if (bands == null || !bands.Banded)
            {
                return 0;
            }
            if (!cell.InBounds(map) || bands.InGutter(cell))
            {
                return -1;
            }
            return bands.BandOf(cell);
        }

        /// <summary>
        /// True when this file will invoke their overlay passes itself, so their postfixes skip
        /// vanilla's undrawn mesh.
        ///
        /// ⚠ ABGuard.Rendering IS PART OF THE PREDICATE ON PURPOSE, AND IT IS MUTABLE. When the
        /// guard trips, Patch_LightingOverlay_ABSuppressOnBanded stops suppressing and vanilla's
        /// overlay becomes visible again - so ownership genuinely returns to them and their
        /// postfixes must resume. This is safe in a way the deleted "skip vanilla's bake" prefix
        /// was NOT: we never skip the bake, so vanilla's mesh is always fully baked and valid.
        /// The worst case after a guard trip is a correctly-lit overlay missing their decoration
        /// until the next section dirty, not the empty mesh that trap produced.
        /// </summary>
        private static bool OverlayExternallyOwned(Map map)
        {
            return active && ABGuard.On(ABGuard.Rendering) && ABBands.Banded(map);
        }

        /// <summary>
        /// Fold their two overlay contributions into our vertex colours. Called from the tail of
        /// BuildColors with the finished array.
        ///
        /// Order is theirs: indoor occlusion (ALPHA only) then vector light fill (RGB only).
        /// They are channel-disjoint, so the order is documented rather than load-bearing - but
        /// it is pinned here for the same reason it is pinned there: a future edit to either that
        /// started touching the other's channel would fail silently and intermittently.
        ///
        /// Exceptions are swallowed per call rather than tripping ABGuard.Rendering: a fault in
        /// a cosmetic third-party pass should cost that pass, not our whole below-view.
        /// </summary>
        public static void ApplyOverlayPasses(Map map, CellRect rect, Color32[] colors)
        {
            if (!active || map == null || colors == null)
            {
                return;
            }
            try
            {
                indoorSkyOcclusion(map, rect, colors);
                vectorLightFill(map, rect, colors);
            }
            catch (Exception e)
            {
                active = false;
                Log.ErrorOnce(ABLog.Tag + " Celestial Lighting overlay pass threw; their overlay"
                    + " contributions are disabled for this session. Our lighting is unaffected. "
                    + e, 762195898);
            }
        }
    }
}
