using System;
using RimWorld;
using Verse;

namespace AsAboveSoBelow
{
    /// <summary>
    /// §56r  BORROW THE SURFACE BAND'S CONTEXT INTO THE ANCHOR ROWS.
    ///
    /// ⚠⚠ THIS IS THE THIRD AND LAST PART OF THE LANDFORM FIX, AND IT IS THE ONE THAT MAKES
    /// THE PREVIEW AND THE COLONY AGREE. §56n stopped GL painting the surface band; §56m
    /// stopped the whole-slice lift importing basement terrain. Neither addressed the
    /// remaining half: GL still DECIDES what to author by reading the map at the ANCHOR rows,
    /// and those rows are a different slice of the world.
    ///
    /// MEASURED (run #507, GL_Coast, 126x896): GL's <c>TerrainAt</c> returns null under any
    /// full-fillage edifice. The anchor rows held 6,092 of them against the surface band's
    /// 4,929 - a completely different rock pattern, because GenStep_RocksFromGrid (order 200)
    /// ran over the whole column before the terrain mutator (order 220). GL therefore
    /// authored 1,893 cells where the SAME landform on the SAME tile authored 3,619 during a
    /// preview - previews never run RocksFromGrid, so their anchor rows are edifice-free.
    /// The coastline stopped wherever basement rock happened to be.
    ///
    /// GL's TerrainAt reads exactly four map-space inputs at the evaluation cell - BiomeAt,
    /// GetEdifice, Fertility and Caves - and all four must answer for the SURFACE band while
    /// the landform module is still sampled band-local. The generator closure uses ONE
    /// IntVec3 for both purposes, so the coordinate cannot be split (that is the generic
    /// <c>TransformIntoMapSpace</c> trap §56.2 documents). Instead we make the anchor rows
    /// TEMPORARILY ANSWER LIKE THE SURFACE BAND, let GL decide, then put everything back.
    ///
    /// ⚠ THE WINDOW IS GL'S METHOD BODY AND NOTHING ELSE. Taken in the transplant prefix,
    /// released at the TOP of the postfix - before the terrain lift, which writes through
    /// <c>TerrainGrid.SetTerrain</c> and must never see a borrowed grid. <c>Restore</c> is
    /// idempotent and runs from a finally; <c>AssertNoneOutstanding</c> shouts if a borrow
    /// ever outlives its window, because a leaked one would leave the basement reporting the
    /// surface's rock for the rest of generation.
    ///
    /// ⚠ ONLY THE ANCHOR ROWS ARE WRITTEN. The surface band is read and never modified, so a
    /// failure in here cannot damage the level the player actually lives on. That asymmetry
    /// is the whole safety argument for touching a live grid at all.
    /// </summary>
    internal sealed class ABGLContextBorrow
    {
        private Map map;

        private int z0;

        private int h;

        private int w;

        private Building[] savedEdifice;

        private float[] savedFertility;

        private float[] savedCaves;

        private BiomeDef[] savedBiome;

        private Action<IntVec3, BiomeDef> biomeWrite;

        private bool active;

        /// <summary>Live borrows. A leak is a correctness failure, not a curiosity, so it is
        /// counted rather than hoped about (rule 15: assert always).</summary>
        private static int outstanding;

        internal int Cells
        {
            get { return w * h; }
        }

        internal static ABGLContextBorrow Take(Map map, int z0, int h)
        {
            // z0 <= 0 means the surface band IS the anchor - nothing to borrow, and the
            // transplant does not run either.
            if (map == null || h <= 0 || z0 <= 0)
            {
                return null;
            }
            var b = new ABGLContextBorrow
            {
                map = map,
                z0 = z0,
                h = h,
                w = map.Size.x
            };
            try
            {
                b.TakeInner();
                b.active = true;
                outstanding++;
                return b;
            }
            catch (Exception e)
            {
                // ⚠ HALF-TAKEN IS THE ONLY TRULY DANGEROUS STATE: some grids swapped, some
                // not, and nobody holding a handle to put them back. Unwind immediately and
                // fall back to the (wrong but understood) basement-context behaviour.
                try
                {
                    b.RestoreInner();
                }
                catch
                {
                }
                Log.ErrorOnce(ABLog.Tag + " V2: GL context borrow failed (" + e.Message
                    + "); the landform will decide from basement rows this run.", 762195902);
                return null;
            }
        }

        private void TakeInner()
        {
            CellIndices ci = map.cellIndices;
            int n = w * h;

            // z-outer/x-inner everywhere below: these grids are indexed z * sizeX + x, so
            // the inner loop walks one contiguous row.
            Building[] edifices = map.edificeGrid != null ? map.edificeGrid.InnerArray : null;
            if (edifices != null)
            {
                savedEdifice = new Building[n];
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int a = ci.CellToIndex(x, j);
                        savedEdifice[x * h + j] = edifices[a];
                        edifices[a] = edifices[ci.CellToIndex(x, z0 + j)];
                    }
                }
            }

            MapGenFloatGrid fert = MapGenerator.Fertility;
            if (fert != null)
            {
                savedFertility = new float[n];
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        IntVec3 a = new IntVec3(x, 0, j);
                        savedFertility[x * h + j] = fert[a];
                        fert[a] = fert[new IntVec3(x, 0, z0 + j)];
                    }
                }
            }

            MapGenFloatGrid caves = MapGenerator.Caves;
            if (caves != null)
            {
                savedCaves = new float[n];
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        IntVec3 a = new IntVec3(x, 0, j);
                        savedCaves[x * h + j] = caves[a];
                        caves[a] = caves[new IntVec3(x, 0, z0 + j)];
                    }
                }
            }

            // Biomes only when GL's own grid is present; without it map.BiomeAt is uniform
            // and there is nothing to borrow.
            if (GeologicalLandformsCompat.TryBiomeAccessors(map,
                    out Func<IntVec3, BiomeDef> read, out Action<IntVec3, BiomeDef> write))
            {
                biomeWrite = write;
                savedBiome = new BiomeDef[n];
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        savedBiome[x * h + j] = read(new IntVec3(x, 0, j));
                        BiomeDef sb = read(new IntVec3(x, 0, z0 + j));
                        if (sb != null)
                        {
                            write(new IntVec3(x, 0, j), sb);
                        }
                    }
                }
            }
        }

        /// <summary>Idempotent - safe to call from a finally and again from a caller that is
        /// not sure whether the finally already ran.</summary>
        internal void Restore()
        {
            if (!active)
            {
                return;
            }
            active = false;
            outstanding--;
            try
            {
                RestoreInner();
            }
            catch (Exception e)
            {
                Log.ErrorOnce(ABLog.Tag + " V2: GL context borrow could not be fully"
                    + " restored: " + e, 762195904);
            }
        }

        private void RestoreInner()
        {
            CellIndices ci = map.cellIndices;

            Building[] edifices = map.edificeGrid != null ? map.edificeGrid.InnerArray : null;
            if (savedEdifice != null && edifices != null)
            {
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        edifices[ci.CellToIndex(x, j)] = savedEdifice[x * h + j];
                    }
                }
            }

            MapGenFloatGrid fert = MapGenerator.Fertility;
            if (savedFertility != null && fert != null)
            {
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        fert[new IntVec3(x, 0, j)] = savedFertility[x * h + j];
                    }
                }
            }

            MapGenFloatGrid caves = MapGenerator.Caves;
            if (savedCaves != null && caves != null)
            {
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        caves[new IntVec3(x, 0, j)] = savedCaves[x * h + j];
                    }
                }
            }

            if (savedBiome != null && biomeWrite != null)
            {
                for (int j = 0; j < h; j++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        BiomeDef b = savedBiome[x * h + j];
                        if (b != null)
                        {
                            biomeWrite(new IntVec3(x, 0, j), b);
                        }
                    }
                }
            }
        }

        /// <summary>A non-zero count here means a borrow escaped its window, which would
        /// leave the basement answering with the surface band's rock for the rest of
        /// generation - silent, and catastrophic to diagnose later.</summary>
        internal static void AssertNoneOutstanding(string where)
        {
            if (outstanding != 0)
            {
                Log.Error(ABLog.Tag + " V2: " + outstanding + " GL context borrow(s) still"
                    + " outstanding at " + where + ". THE ANCHOR ROWS ARE STILL LYING.");
                outstanding = 0;
            }
        }
    }
}
