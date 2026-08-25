# Upstream report: blended terrains drop `tags` from one side of the pair, and vanilla then culls tag-gated plants on smoothed cells

Paste target: Smooth Terrain workshop page (3502765685) comments / bug thread.
Written window 8. Mechanism verified against Smooth Terrain 1.6 dll and NANAME Floors 1.6 dll by decompile; vanilla path verified in 1.6 source. Not yet posted.

---

Hi, author of "As above, So below II" here. While checking compatibility between our mods I found a small data issue in the blended terrain defs that has a quiet gameplay consequence in vanilla too. Reporting it here because the one-line fix fits naturally in Smooth Terrain, though the root is in NANAME's def builder.

**The issue.** `NanameFloors.BlendedTerrainUtil.BlendInner` builds a blend def by reflection: floats and ints are averaged, but every other field is copied from the COVER terrain only. That includes `TerrainDef.tags`. So every blend carries the tags of one side of its pair and loses the other side's. Your own `TerrainSmoother.ValidateAndHardenDef` already repairs the fields where "one side only" is wrong, `affordances` gets a union and `passability` gets the max, but `tags` is not touched.

**Why it matters.** Terrain tags gate wild plants: `PlantProperties.wildTerrainTags` is enforced by `PlantUtility.CanEverPlantAt`, and, more sharply, by `TerrainGrid.DoTerrainChangedEffects`, which runs on every `SetTerrain` and immediately `Destroy()`s any plant standing on the cell whose `wildTerrainTags` no longer overlap the new terrain's tags (it does the same when the new fertility drops below the plant's `fertilityMin`, and blends take the MIN fertility of the pair). So when `ApplySmoothing` replaces a corner cell:

1. Any tag-gated wild plant standing on that cell is silently deleted at that moment, at map generation and also live when a player presses "Smooth current map".
2. The cell stays permanently ineligible for those species afterwards, because `CanEverPlantAt` checks the same tags at regrowth time.

Vanilla content is affected on its own: Core's swamp plants (`Plants_Wild_Swamp.xml`) and Odyssey's water plants (`Plants_Water.xml`) are tag-gated, so swamp and water-edge corners cull them. Tag-heavy flora mods (the Biomes! series patches tags onto vanilla terrains and gates most of its plants on them) amplify it a lot.

**Suggested fix.** In `ValidateAndHardenDef`, union the two components' `tags` the same way you already union `affordances`:

```csharp
def.tags = (request.BaseTerrain.tags ?? new List<string>())
    .Union(request.CoverTerrain.tags ?? new List<string>()).ToList();
```

Union is permissive (a plant may keep standing on a cell that is now mostly the other terrain), but that is consistent with how the blends already treat affordances, and it prevents both the silent deletion and the permanent regrowth hole. One caution: `TerrainDef.IsWater` is `HasTag("Water")`, so after the union a soil-and-shallow-water blend counts as water for tag checks; from a quick look that side effect is benign, but you know the blend semantics best.

Happy to provide more detail or test a beta. Thanks for the mod!
