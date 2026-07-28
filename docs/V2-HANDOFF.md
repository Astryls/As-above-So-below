# As above, So below II — V2 Handoff

> **Read this first.** V2 is a ground-up rearchitecture of the mod. V1's ~42.5k LOC still
> exists on disk (everything in `Source/` *outside* `Source/V2/`) but is **superseded and
> inert on banded maps**. All new work goes in `Source/V2/`.
>
> `ARCHITECTURE.md` in the repo root describes **V1** and is stale. It is kept for the
> reasoning it records about why V1 was replaced.

---

## 1. The architecture

One `Map`, size `(w, 1, bandCount × Slot)`. Bands stack along **+z**, each occupying a
contiguous `CellIndices` range (row-major `z * sizeX + x`). `band = surfaceBand + level`, so
bottom-to-top reads basement / surface / sky. An impassable, permanently-fogged **gutter**
separates bands.

`Slot` is rounded **up to a multiple of 64** (a 250-high band → slot 256, gutter 6). This is
load-bearing, not cosmetic: RimWorld's terrain shaders sample from **world position**, so a
non-aligned vertical offset makes identical terrain render with a different texture phase in
the see-below view.

### The one insight everything rests on

`Region.Neighbors` is **topological, not spatial**. Two spatially distant regions that share
a `RegionLink` are adjacent as far as RimWorld is concerned. Both ends of a stairwell are
`RegionType.Portal` (the stair is a `Building_Door` subclass), and `Portal` fails
`RegionAndRoomUpdater.ShouldBeInTheSameRoom` — so the link **conducts connectivity without
merging rooms, temperature or vacuum**.

`Reachability.CanReach` is managed region-BFS, so it works **unpatched**, and that
transitively fixes `GenClosest.ClosestThingReachable`, `RegionTraverser`, storage search and
**every `WorkGiver_Scanner` in the game**.

> **Proven in-game:** a colonist hauled steel out of a *sealed* chamber whose only connection
> to the world was a synthetic `RegionLink`, with zero hauling code written.

### The dividing line (learned the hard way)

| Class | V2 outcome |
|---|---|
| **Graph / logic** — reachability, storage, work scanning, hauling, needs, reservations | **Free** |
| **Geometry / presentation** — LOS, range, targeting, path lines, ghosts, projectiles, labels, selection | **Not free.** Bands fake vertical adjacency with 256 cells of distance, and every 2D-cell-space computation has to be told |

Each geometry surface is corrected **at its own draw/compute site**.

**Do NOT patch `Thing.DrawPos` globally** as a universal lever. The see-below renderer prints
below things at their *real* positions and then translates the emitted vertices, so a
pre-localized `DrawPos` would double-shift everything it draws. That trap is precisely what
V1's `DrawPosOffsetPatcher` was.

### Hard 1.6 constraint

The pathfinder is **jobified** — `PathFinderJob` (`IJob` over `NativeArray` +
`NativePriorityQueue`) and `PathGridJob` (`IJobParallelFor`). A* neighbour expansion is **not
patchable**. All custom traversal lives at `Pawn_PathFollower`. `PathRequest.ValidateInt`
gates on `pawn.CanReach`, so reachability and path production must be widened together or you
get silent `PawnPath.NotFound` loops.

---

## 2. What's built (`Source/V2/`, ~6.2k LOC, 30 files)

**Core model**
- `ABBandMap` — persisted layout, slot alignment, `Translate(cell, band)`
- `ABBands` — allocation-free CWT-cached facade (`BandOf`, `SameBand`, `LevelOf`, `RectOfBand`)
- **`ABWormhole`** — synthetic `RegionLink`, mandatory re-arm postfix on
  `RegionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms`, `DebugDump`
- `ABWormholePather` — `StartPath` segmentation + **per-tick position sweep** that completes transits

**Generation**
- `ABBandedGeneration` — size clamp → inflate `mapSize.z` → carve non-surface bands → fix player start spot
- `ABSkyBandGen` — mountain: solid-mass projection, 8-way BFS edge distance, meadow-Perlin classification into ledge / wall / plateau
- `ABMapSizeLimit` — 200×200 cap

**Renderer**
- `SectionLayer_ABBelowV2` — below terrain (faithful port of vanilla's per-cell print incl. edge fades, snow/sand, pollution, Underwall), things, air mask, fog
- `SectionLayer_ABBelowLighting` — **one** overlay with the glow *source* substituted per cell
- `SectionLayer_ABBelowShadows` — `staticSunShadowHeight` skirts
- `ABBelowDynamicDraw` — below pawns via `Thing.DrawNowAt`

**Interaction**
- `Building_ABStairs2` (+ ladders, MORTON art) · `ABStairsOrders` (float menu)
- `ABBelowClickThrough` (right-click + selection) · `ABBelowMultiSelect` (drag box + double-click)
- `ABBelowOverlays` (labels) · `ABBelowSelectionDraw` (brackets, path lines, forbidden)
- `ABBandView` (camera clamp) · `ABBandJump` (colonist bar) · `ABBandInput` (PageUp/Down, Ctrl+wheel)
- `ABUIGeometry` (job lines, targeting cursor)

**Combat**
- `ABCombatV2` — shoot line: adjacent bands only, range via translated target +1, 12-cell drift cap, LOS through an open-air hole
- `ABCombatGeometry` — accuracy (`ShotReport`) + weapon aim angle
- `ABCombatAcquisition` — postfix on `AttackTargetFinder.BestShootTargetFromCurrentPosition`, **gated on a null result** so same-band targets always win

**Debug** — `ABV2Debug` (bisect switches, transit/combat logging) · `ABDevTools.V2` ·
`ABV2DefCleanup` (strips V1's nine vertical-link buildings from the architect menu)

### V1 interlock

V1 goes inert by itself on a banded map — all its machinery keys off
`map.Levels()?.upperMap/lowerMap`, which stay null. One explicit guard added in
`LevelMapGen.GetOrGenerate`. V1 self-tests fail on banded maps **by design** ("sky level
exists" = false).

---

## 3. Engine lessons (non-obvious; each cost hours)

1. **`dontRender` terrain silently suppresses shadows.** `SectionLayer_Terrain` swaps in
   `MatBases.ShadowMask` — the void mask used outside the map. `AB_OpenAir` is `dontRender`,
   so every see-through cell was a shadow dead zone. Fixed with a postfix disabling that
   submesh on banded maps.
2. **Terrain shaders use world-position UVs.** Vanilla writes *no* uvs for terrain quads;
   `Printer_Plane` writes 0..1 uvs and produces a muddy smear. Build terrain quads by hand.
3. **Never depend on `PatherArrived` firing at an exact cell.** The pather ends legs in many
   ways (re-issued path, interrupting job, stopping short). Three fixes failed on that
   assumption before switching to a per-tick position sweep.
4. **Never `Clear` a transit record on same-band `StartPath`.** Segmentation rewrites the
   destination *into* the pawn's own band, so that branch sees its own leg. Records expire on
   a 4000-tick timeout instead.
5. **Aiming uses `Face` / `FaceCell`, not `FaceTarget`.** `UpdateRotation` reads
   `stance_Busy.focusTarg` directly.
6. **Vanilla emits THREE sun-shadow skirts** (west/east/south), no north skirt, and **each
   uses a different triangle winding**. A generic helper produces jagged sawtooth.
7. **Lighting: two complementary bakes cause vignetting.** Overlay vertices are shared
   between adjacent cells, so each mesh contaminates the other's quads. One bake with the
   glow source substituted per cell is the correct shape.
8. Misc — `menuHidden` doesn't exist on `ThingDef` (use `<designationCategory />`) · steam
   geysers are `destroyable=false` (DeSpawn, don't Destroy) · `GenStep_Fog` fogs the whole
   map so the sky band must be explicitly unfogged · `GenRadial` caps at ~79.8 ·
   `MapDrawLayer.map` is private, reach the map via `SectionLayer.section`.

### Process lesson

**Measuring beat reasoning every single time.** The `ShadowMask` discovery, the lighting
double-darkening and the transit-record clearing were each found by a diagnostic *after*
multiple failed rounds of reading source. Reach for a dev action early.

Also: **emit one self-contained log message per event.** Separate `Log` calls from the same
helper share a stack signature and get folded by the log monitor, which hides everything
after the first — that cost two full rounds on its own.

### Diagnostics (all present, all earned their keep)

| Action | Answers |
|---|---|
| `AB2: band info` | layout + full wormhole state incl. `CanReach` |
| `AB2: below layer report` | per-submesh verts / tris / queue / material |
| `AB2: lighting report` | glow values + whether vanilla's overlay is suppressed |
| `AB2: toggle transit logging` | every step of a cross-band trip |
| `AB2: toggle combat logging` | shoot line + projectile origin translation |
| `AB2: bisect - toggle below terrain/things/air mask/lighting` | isolates which layer occludes what |
| `AB2: spike - build wormhole chamber` | sealed-room parity test |
| `AB2: open all bands`, `AB2: place stairs up/down here` | setup helpers |

---

## 4. Open work

### Bugs
- **Combat (highest priority).** Cross-band shots: pawn "fires straight down", projectile
  doesn't hit. Shoot line, accuracy, aim angle and projectile origin are all patched, but the
  result is still wrong. **Combat logging is instrumented and unread** — run
  `AB2: toggle combat logging` and check whether `shootline OK` and `projectile origin` both
  appear with sane numbers. If they do, the leading hypothesis is that **projectiles in the
  band below are not drawn from above** (the below-draw pass covers pawns only).
- **Colonists sometimes don't spawn on generation.** The start-spot search now has
  strict → relaxed → seed passes with a loud warning on the last. Unconfirmed whether fixed.

### Audits requested
- Performance testing via **Modern Dev Suite**
- **Quadruple-pass** performance/optimization audit of V2 code
- **Map-generation** optimization audit (currently ~3× slower: vanilla generates over the
  whole tall map, then bands are carved)
- **Compatibility/compliance audit:** MissileGirl, PrePatcher, Faster Game Loading,
  Performance Esmolas, Performance Optimizer, Clean Pathfinding 2, Kingfisher
- **Deep pathfinding performance audit.** `PathGridJob` is `IJobParallelFor` over **all**
  cells, so a banded map costs ~3× versus V1 (where only maps with pathing pawns paid at
  all). **This is the one place V2 is measurably worse, and it is still unmeasured.** The
  200×200 cap is a mitigation, not a fix.

### Deferred features
- **Delete V1** (~42.5k LOC / 165 files) — was gated on combat parity
- **Transplant** — generate a normal map at full vanilla fidelity and move it into band 1.
  Fixes the coastline caveat (map-edge features can land in a carved band, so **use inland
  tiles for now**), save migration, and true-lazy banding
- Turret cross-band targeting · drafted move ghost · per-band biome consumers (wild animals,
  ambient sound, disease MTB) · sky richness (outcrops, hidden valleys) · V1's
  `SectionLayer_ABWallFacade` / `WallReveal` cliff faces

---

## 5. Publishing state

**Disconnected from the original Workshop upload.** `About/PublishedFileId.txt` (was
`3767572810`) is **deleted**, so this publishes as a new item. Renamed
**"As above, So below II"**, packageId **`astryl.AsAboveSoBelow2`**, and marked
`incompatibleWith` the original `astryl.AsAboveSoBelow` — two competing level models on one
colony.

### ⚠ Before publishing
1. **`About.xml` `<description>` is still V1's** and describes the pocket-map architecture
   ("nothing ever wanders a pocket level forever") plus performance claims that do not yet
   hold. It is user-owned copy and needs a rewrite.
2. **V1 code still ships in the assembly.** Delete it first.
3. `About/known-issues.json` is V1's.

### Settings
Map size capped at **200×200** by default. A toggle above the tabs unlocks it and reveals a
bold red performance warning. Oversized options in the new-colony dialog are locked using
vanilla's own `disabled` radio-button styling, with a tooltip pointing at mod settings.
Enforced **twice** — chooser *and* generation — so nothing can bypass it.

### Testing rule
`isolated=true` always. Companion **`astryl.ModernDevTools`**; **not** Melee Animation.
Palette pins get culled for `Playing`-gated debug actions — use the Debug Actions menu.
**V2 banding happens at generation, so testing needs a NEW colony** (quicktest qualifies).
Map generation is ~3× slower — expected, not a hang. **Use inland tiles until transplant
lands.**
