using System;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// THE canonical band-local coordinate rewrite, extracted so that every consumer of the
    /// FIELD row of the slicing rule (§1) shares one definition.
    ///
    /// The rewrite is <c>z' = (z % Slot) + (map.Size.z - bandHeight) / 2</c>:
    ///   - the MODULO puts every band on the same rows, so a map-spanning field (a coast, a
    ///     crater rim, an island falloff) lands in the same place on every level, which is
    ///     what a stacked colony wants;
    ///   - the OFFSET re-centres that window on the field's own centre, giving exactly the
    ///     view an ordinary bandHeight-tall map would have had.
    ///
    /// NO SCALING, deliberately - see the long rationale on
    /// <c>Patch_TileMutatorWorker_Coast_ABBandLocal</c>. Stretching band-local z across the
    /// full stack skews any field that is not axis-aligned and makes displacement noise
    /// anisotropic.
    ///
    /// This existed for months as inline code inside the coast prefix alone. It is a helper
    /// now because THREE more consumers turned up in one afternoon (VEE's LoneIsland and
    /// Crater families), and §14 is explicit that duplication is how an invariant dies: the
    /// see-below preamble grew eight subtly different copies before anyone noticed.
    /// </summary>
    internal static class ABBandLocal
    {
        /// <summary>Band geometry for the map currently being generated, or false when this
        /// is an ordinary unbanded map (in which case every caller must leave vanilla alone).
        ///
        /// Reads the PENDING layout rather than a live ABBandMap because all of these callers
        /// run during map generation, where <c>Banded</c> is still false (§14 engine facts).
        /// </summary>
        internal static bool TryBandGeometry(Map map, out int bandHeight, out int slot, out int offset)
        {
            bandHeight = 0;
            slot = 0;
            offset = 0;
            if (map == null)
            {
                return false;
            }
            if (!ABBandedGeneration.TryPendingSurfaceRect(map, out CellRect surface, out int s) || s <= 0)
            {
                return false;
            }
            int h = surface.Height;
            if (h <= 0 || h >= map.Size.z)
            {
                return false; // unbanded, or a degenerate plan - leave vanilla untouched
            }
            bandHeight = h;
            slot = s;
            offset = (map.Size.z - h) / 2;
            return true;
        }

        /// <summary>Rewrite a sample coordinate into band-local space. Returns false and
        /// leaves the cell untouched when the map is not banded.</summary>
        internal static bool TryRemap(Map map, ref IntVec3 cell)
        {
            if (!TryBandGeometry(map, out _, out int slot, out int offset))
            {
                return false;
            }
            cell = new IntVec3(cell.x, cell.y, (cell.z % slot) + offset);
            return true;
        }

        /// <summary>Same rewrite, for consumers that sample a noise module directly rather
        /// than going through a patchable <c>GetNoiseValue</c>.
        ///
        /// Wrapping the MODULE rather than patching the sampler is what makes the Crater
        /// family fixable at all: it has no single read hook, it calls
        /// <c>outerRim.GetValue(cell)</c> straight out of two different generate passes.
        /// Wrapping repairs the field itself, so every present and future reader of that
        /// field is corrected at once - the "find the ONE virtual everything funnels
        /// through" rule applied to data instead of code.</summary>
        internal sealed class BandLocalModule : ModuleBase
        {
            private readonly ModuleBase inner;

            private readonly double slot;

            private readonly double offset;

            internal BandLocalModule(ModuleBase inner, int slot, int offset)
                : base(0)
            {
                this.inner = inner;
                this.slot = slot;
                this.offset = offset;
            }

            public override double GetValue(double x, double y, double z)
            {
                return inner.GetValue(x, y, (z % slot) + offset);
            }
        }

        /// <summary>Wrap a module in the band-local rewrite. Null-safe and idempotent-ish:
        /// a null module stays null, so a caller can wrap fields it is not certain were
        /// built.</summary>
        internal static ModuleBase Wrap(ModuleBase module, int slot, int offset)
        {
            return module == null ? null : new BandLocalModule(module, slot, offset);
        }
    }
}
