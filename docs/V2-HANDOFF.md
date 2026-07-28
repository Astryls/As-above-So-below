# As above, So below II — V2 Handoff

> **Read this first in a fresh context.** The living version of this document is the
> **Schematic** (`.modmixer/schematic.json`), which is kept current as work lands. This file
> is the repo-side copy; if the two disagree, trust the Schematic.
>
> `ARCHITECTURE.md` in the repo root describes **V1**, which no longer exists. Stale.
> `docs/HELICOPTER_VIEW.md` is a regenerable whole-codebase snapshot
> (`bash docs/generate-helicopter-view.sh`).

**V1 is deleted** (156 files / 40,713 LOC). Current tree: **46 files / 9,070 LOC** — 37 in
`Source/V2/`, 9 in `Source/Core/` (a shared tier, not V1: `ABGuard`, `ABLog`/`ABMod`,
`ABDefOf`, `ABSettings`, `ABGameComp`, `ABGameHooks`, `ABBlame`, `ABPawnCooldown`,
`HarmonyBoot`).

---

## 1. Architecture

One `Map`, size `(w, 1, bandCount × Slot)`. Bands stack along **+z**, each a contiguous
`CellIndices` range; `band = surfaceBand + level`, bottom-to-top basement / surface / sky. An
impassable, permanently-fogged **gutter** separates bands. Live layout: `200×768`, bandCount 3,
bandHeight 200, slot 256, gutter 56, surfaceBand 1.

`Slot` is rounded up to a multiple of **64** — terrain shaders sample from world position, so a
non-aligned offset renders identical terrain at a different texture phase in the see-below view.

### The one insight everything rests on

`Region.Neighbors` is **topological, not spatial**. Two distant regions sharing a `RegionLink`
are adjacent as far as RimWorld is concerned. Both ends are `RegionType.Portal` (the stair
subclasses `Building_Door`), and `Portal` fails `ShouldBeInTheSameRoom`, so the link conducts
connectivity **without merging rooms, temperature or vacuum**. `Reachability.CanReach` is
managed region-BFS and works unpatched, which transitively fixes `ClosestThingReachable`,
`RegionTraverser`, storage search and every `WorkGiver_Scanner`.

Verified in play: colonists cross bands for `HaulToContainer`, `HaulToCell`, `Ingest`, `Clean`,
`BuildRoof`, `FinishFrame`, `LayDown`, `SocialRelax` — with no hauling code written.

### ⚠ Portal regions are ONE CELL

`RegionTypeUtility.IsOneCellRegion(Portal)` is **true**. Every door cell is its own Portal
region, so a 2×2 stairwell is **four Portal regions per end** and a wormhole must link **every
cell pair**. Linking only the building's `Position` leaves the rest of the footprint conducting
nothing, and which cell a pawn enters decides whether the stairs work — the classic "works
sometimes" bug. `ABWormhole.Pair` holds a `List<RegionLink>`; `AB2: band info` prints
`links armed = N/M cells` with an explicit `<-- PARTIAL` warning.

`AllowsMultipleRegionsPerDistrict(Portal)` is **false**, and vanilla's largest door is 2×1
(Ornate Door). The 3×3 grand staircase is 9 Portal regions — beyond anything vanilla ships.
Watch the `N/M` line.

### The dividing line

| Class | Outcome |
|---|---|
| Graph / logic — reachability, storage, work scanning, hauling, needs, reservations | **Free** |
| Geometry / presentation — LOS, range, targeting, path lines, ghosts, projectiles, labels, selection | **Not free** |

Correct each geometry surface **at its own draw/compute site**. Never patch `Thing.DrawPos`
globally — the see-below renderer prints below things at real positions and then translates the
emitted vertices, so a pre-localized DrawPos double-shifts. That was V1's `DrawPosOffsetPatcher`.

### A third category: reachability-as-proxy

Some vanilla code uses `CanReach` to mean *"is this a sensible place"* rather than *"can I get
there"*, and cross-band reachability leaks straight in. Confirmed in
`WanderUtility.GetColonyWanderRoot`, which picks idle wander roots from gather spots, colonist
buildings and colonist positions gated only on `CanReach` — so idle pawns **commuted across
bands to stand somewhere**. (The 35-cell guard only protects the early return; the fallbacks are
unbounded.) Fixed by clamping the root to the pawn's own band.

**Un-audited siblings:** joy/recreation spot choice (`SocialRelax` crossings observed),
meditation focus, animal grazing and pen logic.

### Hard 1.6 constraints

- The pathfinder is **jobified** (`PathFinderJob` : `IJob`; `PathGridJob` : `IJobParallelFor`).
  A* neighbour expansion is not patchable; custom traversal lives at `Pawn_PathFollower`.
- `PathFinder` has **no `FindPath`**. The managed surface is `FindPathNow` and
  `PathFinderTick`. Harmony cannot see inside the Burst job, so pathfinding cost is only
  measurable end-to-end.
- `PathUtility.BlocksDiagonalMovement` trips on **unwalkable** cells; doors are not
  special-cased. Sky bands are full of impassable `AB_OpenAir`, so diagonal-only links are a
  real hazard.

---

## 2. Measured performance

Driven through **Modern Dev Suite** (`astryl.ModernDevSuite`) over its loopback API —
`POST 127.0.0.1:8787/api/v1/commands` with `startRun` / `stopRun`. Runs land in
`<savedata>/ModernDevSuite/Runs/*.json`. Only **Live tab → Run full sweep** needs a human.

| Stage | Total mod cost / 2000 frames |
|---|---|
| Baseline | **3,791 ms** (58 patches) |
| Overlay + re-arm gate + V1 delete | **740.9 ms** (22 patches) |
| Event re-arm + lighting bake | **122.4 ms** |
| Same build, 17 colonists | **215.8 ms** — fps 59.2, f95 16.69 ms |

1. **`OverlayDrawer.DrawAllOverlays`: 1.406 → 0.0018 ms/frame.** Was sweeping every cell of the
   translated view rect with a `ThingsListAtFast` per open-air cell; now iterates vanilla's own
   `overlayHandles` dictionary (never cleared, so a postfix reads it safely).
2. **Wormhole re-arm: 0.339 ms/frame → 0, and no longer a patch.** Was a postfix on
   `TryRebuildDirtyRegionsAndRooms` (~4,500 calls/frame, early-outs on `!AnyDirty`). Gating it
   with a prefix recovered only 22%, because the gate *added a second patch to a very hot
   method*. Now subscribes to `MapEvents.RegionsRoomsChanged`, invoked on the last line of that
   method on the only path that actually rebuilt.
3. **Vanilla `SectionLayer_LightingOverlay` bake: 25 calls → 0.** Suppressing `Visible` stops
   the draw but not the bake — `Section.TryUpdate` does not check `Visible`, only
   `RegenerateDirtyLayers` does.
4. **V1 deleted.** It was never inert, only idle: its patches stayed live on
   `WorldObject.get_Tile` (2.6M calls), `Map.get_IsPlayerHome` (532k),
   `ThinkNode_JobGiver.TryIssueJobPackage` (111k), `PawnRenderer.ParallelGetPreRenderResults`
   and `Thing.get_DrawPos`.

Measured **not** a problem: `SectionLayer_ABBelowV2.Regenerate` ≈ **0.9 µs/call**. Stripping
below-layers from basement/gutter sections is a memory-only win.

### Pathfinding — the original open question, answered

| Scenario | Banded | Vanilla control | Ratio |
|---|---|---|---|
| `ai.pathfind` | **556.6 µs** (188/200 found) | 944.6 µs | **0.59×** |
| `ai.reachability` | 1.152 µs (4999/5000) | 0.393 µs | 2.9× |
| `world.regions` | 0.339 µs (19995/20000) | 0.071 µs | 4.8× |

**There is no per-path regression** — banded is consistently faster per path across three runs.
The real banded tax is reachability and region lookup, roughly proportional to 3× the
regions/cells. Caveat: this measures path *requests*; `PathGridJob`'s grid rebuild is a separate
cost this does not isolate.

---

## 3. Lessons that cost real time

1. **Classify by behaviour, not directory.** `SectionLayer_ABMountainCap` (803 LOC) lived in
   V1's `Source/Rendering/` but is live V2 code. Deleting it removed the sky band's
   mountain-mass rendering. **`Section` instantiates every `SectionLayer` subclass reflectively,
   so deleting one is invisible to the compiler — a green build proves nothing.** Before any
   prune: grep candidates for `ABBands`/`Banded`, then separately check every `SectionLayer`
   subclass.
2. **A benchmark that surprises you is usually broken.** MDS's `ai.pathfind` lied three ways in
   succession: diluted by no-op iterations, contaminated by in-loop sampling, then truncated by
   `break` instead of `continue` (723 of 20,000 samples). Sample outside the stopwatch; divide
   by effective iterations.
3. **Nested instrumentation inflates outer timings.** A run arming 227 methods reported
   `ABBelowV2.Regenerate` 154× higher than one arming 29. Low-target run for layer cost,
   high-target run for attribution; never compare absolutes across differently-armed runs.
4. **`PawnRenderer.GetBodyPos` discards `drawLoc`** for a humanlike pawn in a bed and recomputes
   from `pawn.Position` — sleeping colonists rendered perfectly, one band away, off screen.
   Lying animals were fine (the else-branch uses `drawLoc`), which made it look bed-specific.
   Fixed with a postfix gated on exactly that branch; a blanket postfix double-shifts standing
   pawns.
5. **`RenderPawnAt` self-heals only when `!results.valid`.** Below pawns are culled so never get
   `EnsureInitialized`/`ParallelPreDraw`, yet `results.valid` stays true from when they were
   last on screen. Run all three `DynamicDrawPhaseAt` phases at the translated location.
6. **A malformed `About.xml` silently drops a mod.** RimWorld logs
   `XmlException: Data at the root level is invalid`, falls back to default metadata, and the
   packageId then fails to match ModsConfig. No "failed to find mod" error. Check the log's
   first 30 lines when a companion mod is mysteriously absent.
7. **Never mutate a collection you are enumerating from a callback.** `TickTransits` called
   `Carry` inside `foreach (pending)`; `Carry` → `StartPath` → `TrySegment` → adds to `pending`
   → `InvalidOperationException` out of `GameComponentTick`, killing the whole sweep and
   stranding every other transit. Now three phases: decide / remove / carry.
8. **A diagnostic missing its discriminating field costs cycles.** Transit lines without `job=`
   could not separate "idle pawn commuting to wander" (bug) from "pawn crossing to haul"
   (feature). One field settled it. Worse, a verdict line that printed a conclusion the data
   contradicted actively misled.
9. Misc: `menuHidden` doesn't exist on `ThingDef` · steam geysers are `destroyable=false`
   (DeSpawn, don't Destroy) · `GenStep_Fog` fogs the whole map · `GenRadial` caps at ~79.8 ·
   `MapDrawLayer.map` is private, reach the map via `SectionLayer.section` · emit ONE
   self-contained log message per event, because separate `Log` calls from one helper share a
   stack signature and get folded by the monitor.

**Process: measure, don't reason.** Pre-measurement the suspected hot spots were the see-below
layers; they turned out nearly free. The stairs had *four* distinct causes and every guess cost
a cycle. Reach for a probe early.

---

## 4. Diagnostics

| Action | Answers |
|---|---|
| `AB2: band info` | layout, wormhole state, `links armed = N/M cells` |
| `AB2: transit health` | in-flight transits: age, job, distToNear, near-anchor band |
| `AB2: below pawn report` | per-pawn DRAW/SKIP verdict with reason, posture, inBed, drawPos.y |
| `AB2: why is this pawn stuck` | selected pawn: `CanReach` vs `FindPathNow`, all 8 neighbours |
| **`ABStuckWatchdog`** | automatic; unmoved 180 ticks near an anchor while `pather.Moving`. Classifies CONNECTIVITY / RE-TARGETING / BLOCKED and names the next-cell occupant or door state |
| `AB2: toggle transit logging` | every step of a crossing, including `job=` |
| `AB2: below layer report` · `AB2: lighting report` · bisect toggles | render isolation |

Transit logging and the watchdog **reset on every launch** — re-arm them each run.

---

## 5. Vertical links

| Def | Footprint | Cardinal approaches |
|---|---|---|
| `AB2_LadderDown/Up` | 1×1 | 4 |
| `AB2_StairsDown/Up` | 2×2 | 8 |
| `AB2_GrandStairsDown/Up` | 3×3 | 12 |

Transit is a **teleport** (`Carry`), so there is no travel time for quality to scale — "grand"
buys throughput, not speed.

All inherit `AB2_LinkBase` (`DoorBase` + `Building_ABStairs2`), which overrides `blueprintClass`
to `Blueprint_Build` and sets `useBlueprintGraphicAsGhost`; each def supplies its own
`blueprintGraphicData`. **Without that override they inherit `Blueprint_Door` plus the
`Door_Blueprint` texture and every link renders as a generic door while being placed.**

`LandingCell` prefers the anchor cell when free (vanilla door behaviour: occupy briefly, walk
on) and steps aside only when occupied. `ArriveRadius = LandingRadius + 1`, tied together
deliberately: a pawn blocked by a just-landed pawn must still count as arrived.

---

## 6. Open work

### Bugs
- **Transient stall near stairwells.** Watchdog verdict **BLOCKED**: `still for 204 ticks |
  job=GotoWander | dest 1 cell away | CanReach=True | path=FOUND (2 nodes) | destChanges=1 |
  pendingTransit=False`. Not connectivity, not transit, not re-targeting. Self-resolves in
  seconds. The watchdog now reports `nextCell` occupant/door state — **read that line next.**
  The new 2×2/3×3 footprints may fix it outright by widening approaches.
- **Combat (highest priority, untouched recently).** Cross-band shots: pawn "fires straight
  down", projectile doesn't hit. **Two separate problems** — (a) *visual*:
  `ABBelowDynamicDraw` iterates pawns only, so projectiles below are confirmed never drawn from
  above; (b) *hit*: impact resolves in `Projectile.Tick`, independent of draw, so fixing (a)
  makes it look right while still missing.
- **Colonists sometimes don't spawn on generation.** Unconfirmed.

### Deferred, with reasons
- **Elevator (3-level shaft).** Requested. Needs TWO counterparts on one building — a second
  scribed reference plus a rework of `TryEstablish`'s single-`counterpart` assumption.
  Deliberately not bundled with the region-linking rewrite so a regression stays attributable.
- **Transplant** — generate a normal map at full vanilla fidelity and move it into band 1.
  Fixes the coastline caveat (**use inland tiles for now**), save migration, true-lazy banding.
- Turret cross-band targeting · drafted move ghost · per-band biome consumers · sky richness.

### Perf backlog
Map generation ~3× slower · isolate `PathGridJob` grid-rebuild cost · compatibility audit not
started (MissileGirl, PrePatcher, Faster Game Loading, Performance Esmolas, Performance
Optimizer, Clean Pathfinding 2, Kingfisher).

### `ABBandEnv` — read before touching biomes
V2's one genuine regression versus V1: `Map.Biome` is get-only and derived from the world tile,
so per-band biome must be fed to each consumer explicitly (done for `WildPlantSpawner` density,
`GenTemperature`, and two `CellFinder` edge-cell patches). **A contextual `map.Biome` getter
driven by an ambient "current cell" latch is deliberately rejected** — it is the
lying-to-vanilla-behind-a-global-latch pattern that made V1 unmaintainable.

---

## 7. Publishing state

Disconnected from the original Workshop upload: `About/PublishedFileId.txt` deleted, renamed
**"As above, So below II"**, packageId **`astryl.AsAboveSoBelow2`**, marked `incompatibleWith`
the original.

### ⚠ Before publishing
1. **`About.xml` `<description>` is still V1's** and makes **false functional claims** — it
   promises elevators, vertical conduit/water/duct/chem shafts, psycast cross-level targeting,
   caravans, Hospitality guest tours, trader airships and "zero recurring cost until you build a
   level". A store-page accuracy problem, not a copy edit. User-owned text.
2. `About/known-issues.json` is V1's (18 entries).
3. `ARCHITECTURE.md` and `docs/HELICOPTER_VIEW.md` describe V1.

### Settings
`ABSettings` was rewritten V2-only: 9 fields, each with a live caller. Map size capped at
**200×200**, unlock toggle behind a bold red warning, enforced at the chooser *and* at
generation.

### Testing rule
`isolated=true` always. Companions **`astryl.ModernDevSuite` + `astryl.ModernDevTools`**.
Palette pins get culled for `Playing`-gated debug actions — use the Debug Actions menu.
**Banding happens at generation, so testing needs a NEW colony** (quicktest qualifies). Map
generation is ~3× slower — expected, not a hang. **Use inland tiles until transplant lands.**
