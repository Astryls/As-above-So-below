using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Noise;

namespace AsAboveSoBelow
{
    /// <summary>
    /// True average terrain colors, the Map Preview technique (m00nl1ght,
    /// studied 2026-07-22): blit the terrain texture onto a 1x1 RenderTexture
    /// and read the single pixel back - the GPU's downsample IS the average -
    /// then multiply by the material and graphic tints. Computed lazily per
    /// def, session-cached, with hand-picked fallbacks when anything throws
    /// (headless/odd drivers). Main thread only.
    /// </summary>
    internal static class ABTrueColors
    {
        private static readonly Dictionary<TerrainDef, Color> cache = new Dictionary<TerrainDef, Color>();

        internal static Color Of(TerrainDef def, Color fallback)
        {
            if (def == null)
            {
                return fallback;
            }
            if (cache.TryGetValue(def, out Color c))
            {
                return c;
            }
            Color result = fallback;
            try
            {
                Texture2D tex = def.graphic?.MatSingle?.mainTexture as Texture2D;
                if (tex != null)
                {
                    RenderTexture rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                    Texture2D read = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false);
                    RenderTexture prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    Graphics.Blit(tex, rt);
                    read.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0);
                    read.Apply(updateMipmaps: false);
                    Color pixel = read.GetPixel(0, 0);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    UnityEngine.Object.Destroy(read);
                    result = pixel * def.graphic.MatSingle.color * def.graphic.color;
                    result.a = 1f;
                }
            }
            catch
            {
                result = fallback;
            }
            cache[def] = result;
            return result;
        }
    }

    /// <summary>
    /// Generation preview for the settings window: a seeded synthetic surface
    /// sample run through the SAME classification math as real sky-level gen
    /// (meadow cutoff + scale, terrace curve, outcrops, hidden valleys,
    /// tarns, soil), rendered as two framed panels - the upper level, and the
    /// lower level it projects from. Open-air cells in the upper panel show
    /// the lower pixel dimmed, exactly the in-game see-below read. Rebuilds
    /// are debounced while sliders drag (lore: debounce rebakes); the panel
    /// footnote is honest that this is an illustrative sample, not the
    /// player's actual next map.
    /// </summary>
    internal static class ABGenPreview
    {
        private const int W = 130;
        private const int H = 88;

        private static int seed = 762195842;
        private static Texture2D upperTex;
        private static Texture2D lowerTex;
        private static int builtHash = -1;
        private static int pendingHash = -1;
        private static float pendingAt;
        private const float DebounceSeconds = 0.25f;

        private static int SettingsHash(ABSettings s)
        {
            unchecked
            {
                int h = seed;
                h = h * 31 + (s.naturalPeaks ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(s.peakMeadowCutoff * 1000f);
                h = h * 31 + Mathf.RoundToInt(s.peakMeadowScale * 10000f);
                h = h * 31 + s.peakTerraceMax;
                h = h * 31 + Mathf.RoundToInt(s.peakOutcropDensity * 100f);
                h = h * 31 + Mathf.RoundToInt(s.peakTarns * 100f);
                h = h * 31 + Mathf.RoundToInt(s.peakHiddenValleys * 100f);
                h = h * 31 + Mathf.RoundToInt(s.peakSoilFraction * 100f);
                h = h * 31 + Mathf.RoundToInt(s.peakVegetation * 100f);
                return h;
            }
        }

        internal static void Reroll()
        {
            seed = Rand.Int;
            builtHash = -1;
        }

        internal static void Draw(Rect rect, ABSettings s)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(10f);
            // Debounced rebuild.
            int hash = SettingsHash(s);
            float now = Time.realtimeSinceStartup;
            if (hash != builtHash)
            {
                if (hash != pendingHash)
                {
                    pendingHash = hash;
                    pendingAt = now;
                }
                if (upperTex == null || now - pendingAt >= DebounceSeconds)
                {
                    try
                    {
                        Rebuild(s);
                    }
                    catch (Exception e)
                    {
                        ABGuard.Disable(ABGuard.Ui, e, "generation preview");
                        return;
                    }
                    builtHash = hash;
                }
            }
            float y = inner.y;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inner.x, y, inner.width, 22f), "AB_PreviewTitle".Translate());
            y += 24f;
            float chrome = 24f + 2f * 20f + 34f + 30f + 16f + 20f;
            float panelH = Mathf.Max(120f, (inner.height - chrome) / 2f);
            DrawPanel(new Rect(inner.x, y, inner.width, 20f + panelH), "AB_PreviewUpper".Translate(), upperTex);
            y += 20f + panelH + 6f;
            DrawPanel(new Rect(inner.x, y, inner.width, 20f + panelH), "AB_PreviewLower".Translate(), lowerTex);
            y += 20f + panelH + 8f;
            DrawLegend(new Rect(inner.x, y, inner.width, 34f));
            y += 36f;
            if (Widgets.ButtonText(new Rect(inner.x, y, inner.width, 28f), "AB_PreviewReroll".Translate()))
            {
                Reroll();
            }
            y += 30f;
            GUI.color = new Color(1f, 1f, 1f, 0.55f);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inner.x, y, inner.width, 16f), "AB_PreviewNote".Translate());
            GUI.color = Color.white;
        }

        private static void DrawPanel(Rect rect, string caption, Texture2D tex)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 18f), caption);
            GUI.color = Color.white;
            Rect img = new Rect(rect.x, rect.y + 20f, rect.width, rect.height - 20f);
            Widgets.DrawBoxSolid(img, new Color(0.06f, 0.06f, 0.07f));
            if (tex != null)
            {
                GUI.DrawTexture(img.ContractedBy(1f), tex, ScaleMode.StretchToFill);
            }
            Widgets.DrawBox(img, 1);
        }

        private static void DrawLegend(Rect rect)
        {
            (Color color, string key)[] entries =
            {
                (MeadowColor, "AB_LegendMeadow"),
                (RockColor, "AB_LegendRock"),
                (LedgeColor, "AB_LegendLedge"),
                (WaterColor, "AB_LegendTarn"),
                (HiddenColor, "AB_LegendHidden"),
                (AirColor, "AB_LegendAir")
            };
            float cw = rect.width / 3f;
            for (int i = 0; i < entries.Length; i++)
            {
                float x = rect.x + (i % 3) * cw;
                float y = rect.y + (i / 3) * 17f;
                Widgets.DrawBoxSolid(new Rect(x, y + 3f, 10f, 10f), entries[i].color);
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                Widgets.Label(new Rect(x + 14f, y - 3f, cw - 16f, 20f), entries[i].key.Translate());
                GUI.color = Color.white;
            }
        }

        // Palette (true colors where a def exists, fallbacks from Map
        // Preview's own hand-picked table).
        private static Color GrassColor => new Color(0.36f, 0.44f, 0.26f);
        private static Color MeadowColor => ABTrueColors.Of(TerrainDefOf.Soil, GenColor.FromHex("6D5B49"));
        private static Color GravelColor => ABTrueColors.Of(TerrainDefOf.Gravel, GenColor.FromHex("6D5B49"));
        private static Color RockColor => new Color(0.27f, 0.26f, 0.25f);
        private static Color RockDeepColor => new Color(0.16f, 0.155f, 0.15f);
        private static Color LedgeColor => ABTrueColors.Of(ABDefOf.AB_MountainTop, new Color(0.33f, 0.32f, 0.31f));
        private static Color WaterColor => ABTrueColors.Of(TerrainDefOf.WaterShallow, GenColor.FromHex("434F50"));
        private static Color WaterDeepColor => ABTrueColors.Of(TerrainDefOf.WaterDeep, GenColor.FromHex("3A434D"));
        private static Color HiddenColor => new Color(0.13f, 0.15f, 0.11f);
        private static Color AirColor => new Color(0.10f, 0.11f, 0.13f);

        private const byte KOut = 0;
        private const byte KLedge = 1;
        private const byte KWall = 2;
        private const byte KPlateau = 3;

        private static void Rebuild(ABSettings s)
        {
            Rand.PushState(seed);
            try
            {
                BuildTextures(s);
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static float Noise01(Perlin p, int x, int z)
        {
            return Mathf.Clamp01((float)(p.GetValue(x, 0.0, z) + 1.0) * 0.5f);
        }

        private static void BuildTextures(ABSettings s)
        {
            int n = W * H;
            // 1. Synthetic surface: one central mountain mass, noise-eroded.
            Perlin massNoise = new Perlin(0.055, 2.0, 0.5, 5, Rand.Int, QualityMode.Medium);
            Perlin groundNoise = new Perlin(0.07, 2.0, 0.5, 4, Rand.Int, QualityMode.Medium);
            bool[] solid = new bool[n];
            Vector2 center = new Vector2(W * 0.5f, H * 0.5f);
            float maxR = Mathf.Min(W, H) * 0.52f;
            for (int z = 0; z < H; z++)
            {
                for (int x = 0; x < W; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, z), center) / maxR;
                    solid[z * W + x] = Noise01(massNoise, x, z) * 0.65f + (1f - d) * 0.55f > 0.62f;
                }
            }
            // 2. Edge distance (8-connected BFS from open cells).
            int[] dist = new int[n];
            Queue<int> q = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                dist[i] = solid[i] ? int.MaxValue : 0;
                if (!solid[i])
                {
                    q.Enqueue(i);
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int cx = i % W;
                int cz = i / W;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H)
                        {
                            continue;
                        }
                        int ni = nz * W + nx;
                        if (dist[ni] > dist[i] + 1)
                        {
                            dist[ni] = dist[i] + 1;
                            q.Enqueue(ni);
                        }
                    }
                }
            }
            // 3. Classification with the REAL settings math.
            float cutoff = Mathf.Clamp(s.peakMeadowCutoff, 0.45f, 0.75f);
            float scale = Mathf.Clamp(s.peakMeadowScale, 0.012f, 0.048f);
            int terraceMax = Mathf.Clamp(s.peakTerraceMax, 1, 6);
            Perlin meadow = new Perlin(scale * 2.2, 2.0, 0.5, 5, Rand.Int, QualityMode.Medium);
            Perlin terrace = new Perlin(0.077, 2.0, 0.5, 4, Rand.Int, QualityMode.Medium);
            byte[] kind = new byte[n];
            List<int> plateau = new List<int>();
            bool naturalistic = s.naturalPeaks;
            for (int i = 0; i < n; i++)
            {
                if (!solid[i])
                {
                    kind[i] = KOut;
                    continue;
                }
                int x = i % W;
                int z = i / W;
                if (naturalistic && Noise01(meadow, x, z) > cutoff)
                {
                    if (dist[i] <= 1)
                    {
                        kind[i] = KLedge;
                    }
                    else
                    {
                        kind[i] = KPlateau;
                        plateau.Add(i);
                    }
                    continue;
                }
                float tn = Noise01(terrace, x, z);
                int tw = 1;
                if (tn >= 0.5f && terraceMax > 1)
                {
                    tw = Mathf.Clamp(1 + Mathf.FloorToInt(Mathf.Pow(Mathf.InverseLerp(0.5f, 1f, tn), 1.6f) * terraceMax), 1, terraceMax);
                }
                kind[i] = dist[i] <= tw ? KLedge : KWall;
            }
            // 4. Outcrops: random lumps on the plateau.
            if (naturalistic && plateau.Count >= 60 && s.peakOutcropDensity > 0.001f)
            {
                int lumps = Mathf.Max(1, Mathf.RoundToInt(plateau.Count / 900f * Mathf.Clamp(s.peakOutcropDensity, 0f, 2f)));
                for (int l = 0; l < lumps; l++)
                {
                    Lump(kind, plateau[Rand.Range(0, plateau.Count)], Rand.RangeInclusive(9, 42), KWall, KPlateau);
                }
                plateau.RemoveAll(i => kind[i] != KPlateau);
            }
            // 5. Hidden valleys: pockets not reachable from the rim.
            bool[] reached = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (kind[i] == KLedge)
                {
                    reached[i] = true;
                    q.Enqueue(i);
                }
            }
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int cx = i % W;
                int cz = i / W;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx;
                        int nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= W || nz >= H)
                        {
                            continue;
                        }
                        int ni = nz * W + nx;
                        if (!reached[ni] && (kind[ni] == KPlateau || kind[ni] == KLedge))
                        {
                            reached[ni] = true;
                            q.Enqueue(ni);
                        }
                    }
                }
            }
            bool[] hidden = new bool[n];
            float keep = Mathf.Clamp(s.peakHiddenValleys, 0f, 1f);
            bool[] pocketSeen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (kind[i] != KPlateau || reached[i] || pocketSeen[i])
                {
                    continue;
                }
                // Flood this pocket, decide its fate once.
                List<int> pocket = new List<int>();
                Queue<int> pq = new Queue<int>();
                pq.Enqueue(i);
                pocketSeen[i] = true;
                while (pq.Count > 0)
                {
                    int pi = pq.Dequeue();
                    pocket.Add(pi);
                    int cx = pi % W;
                    int cz = pi / W;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            int nz = cz + dz;
                            if (nx < 0 || nz < 0 || nx >= W || nz >= H)
                            {
                                continue;
                            }
                            int ni = nz * W + nx;
                            if (!pocketSeen[ni] && !reached[ni] && kind[ni] == KPlateau)
                            {
                                pocketSeen[ni] = true;
                                pq.Enqueue(ni);
                            }
                        }
                    }
                }
                bool sealedPocket = Rand.Chance(keep);
                for (int pi = 0; pi < pocket.Count; pi++)
                {
                    hidden[pocket[pi]] = sealedPocket;
                }
            }
            // 6. Tarns.
            bool[] water = new bool[n];
            bool[] deep = new bool[n];
            float tarnDensity = Mathf.Clamp(s.peakTarns, 0f, 2f);
            if (naturalistic && tarnDensity > 0.001f && plateau.Count >= 120)
            {
                float expected = plateau.Count / 1400f * tarnDensity;
                int tarnCount = Mathf.FloorToInt(expected) + (Rand.Chance(expected - Mathf.FloorToInt(expected)) ? 1 : 0);
                for (int t = 0; t < tarnCount; t++)
                {
                    int c = plateau[Rand.Range(0, plateau.Count)];
                    int placed = LumpMark(water, kind, c, Rand.RangeInclusive(8, 26), KPlateau);
                    if (placed >= 14)
                    {
                        LumpMark(deep, kind, c, Mathf.Max(4, placed / 4), KPlateau);
                    }
                }
            }
            // 7. Paint.
            Perlin soil = new Perlin(0.12, 2.0, 0.5, 4, Rand.Int, QualityMode.Medium);
            float soilFrac = Mathf.Clamp(s.peakSoilFraction, 0f, 0.5f);
            float veg = Mathf.Clamp(s.peakVegetation, 0f, 2f);
            Color32[] lower = new Color32[n];
            Color32[] upper = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                int x = i % W;
                int z = i / W;
                // Lower level: grass field + the rock mass footprint.
                Color lc;
                if (solid[i])
                {
                    lc = dist[i] <= 1 ? RockColor * 1.25f : (dist[i] >= 4 ? RockDeepColor : RockColor);
                }
                else
                {
                    float gn = Noise01(groundNoise, x, z);
                    lc = gn > 0.72f ? MeadowColor : GrassColor * (0.9f + gn * 0.25f);
                    if (Rand.Chance(0.012f * veg))
                    {
                        lc = new Color(0.18f, 0.30f, 0.14f);
                    }
                }
                lc.a = 1f;
                lower[i] = lc;
                // Upper level.
                Color uc;
                switch (kind[i])
                {
                    case KOut:
                        // Open air: the lower level seen through the gap, dimmed
                        // exactly like the in-game below view.
                        uc = lc * 0.5f;
                        break;
                    case KLedge:
                        uc = LedgeColor;
                        break;
                    case KWall:
                        uc = dist[i] >= 4 ? RockDeepColor : RockColor;
                        break;
                    default:
                        if (water[i])
                        {
                            uc = deep[i] ? WaterDeepColor : WaterColor;
                        }
                        else
                        {
                            float sn = Noise01(soil, x, z);
                            uc = sn > 1f - soilFrac ? MeadowColor
                                : (sn < 0.22f ? GravelColor * 0.85f : GravelColor);
                            // Meadow green wash + vegetation flecks.
                            uc = Color.Lerp(uc, GrassColor, 0.45f);
                            if (Rand.Chance(0.03f * veg))
                            {
                                uc = new Color(0.20f, 0.33f, 0.16f);
                            }
                        }
                        if (hidden[i])
                        {
                            uc = Color.Lerp(uc, HiddenColor, 0.75f);
                        }
                        break;
                }
                uc.a = 1f;
                upper[i] = uc;
            }
            Upload(ref lowerTex, lower);
            Upload(ref upperTex, upper);
        }

        /// <summary>Irregular blob: mark from[] cells of kind fromKind to
        /// toKind via a drunken fill around the seed.</summary>
        private static void Lump(byte[] kind, int seedCell, int size, byte toKind, byte fromKind)
        {
            int cx = seedCell % W;
            int cz = seedCell / W;
            for (int i = 0; i < size; i++)
            {
                int x = cx + Rand.RangeInclusive(-3, 3);
                int z = cz + Rand.RangeInclusive(-3, 3);
                if (x < 0 || z < 0 || x >= W || z >= H)
                {
                    continue;
                }
                int idx = z * W + x;
                if (kind[idx] == fromKind)
                {
                    kind[idx] = toKind;
                }
            }
        }

        private static int LumpMark(bool[] mark, byte[] kind, int seedCell, int size, byte requireKind)
        {
            int cx = seedCell % W;
            int cz = seedCell / W;
            int placed = 0;
            for (int i = 0; i < size; i++)
            {
                int x = cx + Rand.RangeInclusive(-3, 3);
                int z = cz + Rand.RangeInclusive(-3, 3);
                if (x < 0 || z < 0 || x >= W || z >= H)
                {
                    continue;
                }
                int idx = z * W + x;
                if (kind[idx] == requireKind && !mark[idx])
                {
                    mark[idx] = true;
                    placed++;
                }
            }
            return placed;
        }

        private static void Upload(ref Texture2D tex, Color32[] pixels)
        {
            if (tex == null)
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, mipChain: false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    name = "AB_GenPreview"
                };
            }
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
        }
    }
}
