# As above, So below — Vanilla Parity & Cross-Level Combat Log

Running record of every feature built toward "everything works as if it's one big map."
Each entry: what was added, how it was tested, the recursive **quad pass** result, and an
**honest** certainty %. Percentages are NOT inflated — anything that needs a human to eyeball
in-game is capped until the user confirms it.

## Methodology

**Recursive quad pass** — for every feature, four angles are exercised and re-run after each fix
until stable:

1. **Functional** — does the happy path do the thing?
2. **Vanilla A/B equivalence** — same outcome as a single map would produce (damage numbers,
   accuracy factors, job flow, save/load), using vanilla mechanics rather than re-implementing them.
3. **Adversarial / edge** — nulls, map removal mid-action, save/load mid-action, no stairs, downed
   pawn, kill-switch trip, out-of-range, roofed target, disposed map, reentrancy.
4. **TPS / performance** — zero recurring cost when idle, no per-tick scans without a cheap gate,
   event-driven, hidden-map throttle where visual-only, fail-open via `ABGuard`.

**Certainty scale**
- **Code-verified** = compiles + code review + dev-tool self-check assertions passed over the bridge.
  Caps at ~85% until a human sees it (rendering, feel, edge timing).
- **User-verified** = the user watched it behave correctly in-game → can reach 95–99%.

**Dev tools** — `Source/Dev/ABDevTools.cs` exposes `[DebugAction]`s under the **"As above"**
category that build controlled cross-level arenas and run self-checks. Self-check output is written
to `docs/SelfTest.log` and summarised via `Log.Warning`/`Log.Error` so it surfaces automatically.

---

## Features

| # | Feature | Added | Quad pass (F / A-B / Edge / TPS) | Certainty | Vanilla-equivalent? |
|---|---------|-------|----------------------------------|-----------|---------------------|
| 1 | Cross-level RANGED fire engine (`CrossLevelCombat`) + player-directed cross-gap shooting (`JobDriver_ABCrossLevelAttack`) | 2026-07-21 | F: self-test 14/14 + USER-VERIFIED (streak, damage, cadence, right-click, no-stairs fire) | ~92% | YES (user-confirmed) |
| 2 | Combat FEEDBACK: proper attack cursor when hovering below targets while aiming (B), `attacking <name> across levels` report, target line + warm-up aim pie on selected cross-firing pawns | 2026-07-21 | code-reviewed; in-game pending | ~55% | reticle/report mirror vanilla |
| 3 | AI AUTO-ENGAGE (`CrossLevelAutoEngage`): stuck/idle armed hostiles fire up/down through the gap (reposition to the hole's edge; shoot instead of abandoning a pocket level); drafted fire-at-will colonists return fire holding position | 2026-07-21 | self-test written; in-game pending | ~55% | vanilla owns any hostile with a reachable same-map target; only stuck ones cross-fire |

### 1. Cross-level ranged fire (Model B, sky↔surface)
- **What**: A drafted pawn ordered to attack a target on the paired level (through open air, in range,
  with a clear gap line-of-fire) now STANDS AND FIRES across the vertical gap instead of routing down
  the stairs. The projectile spawns on the **target's** map, so its flight, interception, impact,
  armour, damage and rendering are 100% vanilla. Accuracy mirrors
  `ShotReport.AimOnTargetChance_StandardTarget` (shooter+distance, weapon falloff, weather, target
  size, Ideology darkness) with a `GapHeight` vertical separation folded into the shot distance.
  Plunging fire bypasses horizontal cover (physically correct). When there's no gap line-of-fire or
  the target is out of range / on the basement, it falls back to Model A routing.
- **Guarding**: `ABGuard.Combat` kill switch; `crossLevelCombat` setting (default ON); fail-open.
- **Test plan**: dev-tool arena (`AB: cross-gap combat self-test`) spawns a hostile on the surface
  under an open-air hole and a drafted colonist with a ranged weapon on the sky platform beside it,
  asserts `CanCrossGapFire`, fires N shots, and confirms projectiles land on the surface map and the
  target takes damage. Manual: draft a colonist on the sky, B/right-click a raider on the surface.
- **Quad pass (code-review, pre-in-game)**:
  1. **Functional** — full path compiles: right-click/B a paired-level target → early attack-detect in
     `BuildOptions` (now BEFORE the stairs requirement, so fire works with no stairs) →
     `TryStartCrossGapAttack` → `FindFiringCell` → `AB_CrossLevelAttack` job → `FireTick` → `Fire`.
     Not yet run in-game, so not claimed as passing.
  2. **Vanilla A/B** — damage/armour/impact 100% vanilla (real projectile on the target map);
     accuracy mirrors `AimOnTargetChance_IgnoringPosture` (shooter+dist, weapon falloff, weather,
     darkness, target size). Knowingly omitted minor terms: `FactorFromExecution` (7.5× vs a downed
     target ≤3.9 cells), covering-gas, posture. Cover deliberately bypassed (plunging fire).
  3. **Adversarial/edge** — reviewed & guarded: null shooter/target/verb; target dies mid-fire
     (`Valid()` ends job); map removed / disposed (`AreCrossGapPaired` checks `Disposed`, target
     un-spawns); save/load mid-fire (target `Scribe_References`, counters scribed, `Notify_Starting`
     handoff only on fresh start); no stairs (fire still offered); kill switch (`ABGuard.Combat`)
     trip → fail-open; basement excluded (only level-1↔0 pairs).
  4. **TPS/perf** — zero idle cost (work only runs while a pawn holds the job); `FindFiringCell`
     pathfinds hard-bounded (≤600 scanned, ≤64 candidates, ≤16 reach checks) and skipped entirely
     when the pawn already has a line; projectile lives on the target map so vanilla ticks it (no
     extra cost on hidden maps). Watch item: `CanFireFrom` runs one `GenSight` raycast per firing
     pawn per tick — fine for the few pawns a player commands; will be gated when AI auto-engagement
     lands.
- **Run #1 (2026-07-21): self-test 14/14 PASS.** Runtime-verified: pairing, exposure, sky-plane
  line-of-fire, `CanCrossGapFire`, aim chance sane, the cross-map cast (`Fire()` casts reported),
  **projectiles live on the target's map**, surface->sky correctly BLOCKED through an enclosed sky
  cell, sustained attack job started. One stray XML field (`casterCanUseRangedWeapon`, invented)
  was flagged by the loader and removed — non-fatal, def loaded fine.
- **USER-VERIFIED (2026-07-21)**: projectile streak visible, damage lands / raider dies, cadence
  feels vanilla, right-click attack works with no stairs. Certainty ~92% (residual: long-session
  edge timing, save/load mid-burst untested in anger).

### 2–3. Feedback + AI auto-engage (built after user round 1 feedback)
- **User-reported gaps**: no attack reticle over below pawns while aiming; "attacking unknown
  through levels"; raiders never fire upward.
- **Fixes/additions**: `Patch_Targeter_OnGUI_BelowTarget` (attack cursor for below targets — root
  cause: vanilla `CurrentTargetUnderMouse` is current-map-only, so `Verb.OnGUI` got an invalid
  target and drew CannotShoot); `GetReport` override naming the real target; target line +
  vanilla aim pie via `Pawn.DrawExtraSelectionOverlays` postfix (selected pawns only);
  `CrossLevelAutoEngage` scan (250-tick cadence on the sky comp, both directions, capped probes,
  per-pawn cooldown, `HostileDescend` prefers shooting over descending for stuck armed hostiles).
- **Quad pass (code review)**: F: full paths compile, self-test written (hostile up-fire assert +
  drafted return-fire assert). A-B: hostiles with reachable same-map targets keep pure vanilla AI;
  only stuck/idle ones cross-fire; drafted auto-fire is idle-only + fire-at-will + hold position.
  Edge: kill switches, caps (4 engages + 4 probes/scan/direction, 700-tick fail cooldown), vehicle
  + mental-state exclusions, basement excluded. TPS: zero cost when no sky level or no pawns; scan
  is 250-tick, one-field filters, bounded raycasts; overlays draw for selected pawns only;
  targeter patch runs only while actively aiming.
- **Honest certainty: ~55%** until the in-game run.

### 4. Universal targeting hub (psycasts / VPE / mortars / artillery / ICBM-class — capability-based, no named-mod code)
- **What**: every `ITargetingSource` now has a cross-level story, dispatched by capability:
  - **Equipped gun verbs (B)** — unchanged verified fire/route path (now keyed to the actual
    equipped weapon verb, so ability-shoot hybrids don't leak into it).
  - **Any pawn-cast source** (vanilla psycasts, VPE, any modded targeter): a click on a below
    thing accepted by the source's own `targetParams.CanTarget` either casts directly (caster
    already on that level) or **routes-then-`OrderForceTarget`s** — the source runs its fully
    vanilla cast job on arrival. Zero per-effect auditing, zero foreign types → works for any mod.
  - **flyOverhead turret verbs** (mortars, artillery, ICBM-style launchers): **direct cross-level
    bombardment**. Vanilla can't hold a cross-map forced target (`Building_Turret.Tick` clears it),
    so `CrossLevelMortar` keeps its own store and drives shots on the scan cadence: manned/powered/
    loaded/local-threat-free checks, vanilla warmup + burst cooldown from the turret def, vanilla
    forced-miss scatter, real shell consumption, full-distance origin for real flight time. Arc
    rules: sky→surface needs the TARGET column open (roof punch on impact stays vanilla);
    surface→sky needs the SHOOTER column open. Cancel gizmo + target line overlay; hover shows the
    valid cursor both directions.
- **Known limitations (documented)**: cell-targeted ability casts (e.g. Skip destination) across
  levels not yet supported (things only) — mortars handle cells; bombardment orders are NOT saved
  across save/load (cleared with all static combat state by `ABGameComp.FinalizeInit`); direct-fire
  (non-arc) turrets don't shoot across the gap; world-map targeting (true ICBM world strikes) is a
  different system, untouched.
- **Quad pass (code review)**: F: compiles, dispatcher covers all four source classes, self-test
  asserts arc geometry (far target accepted, min-range rejected, order stored). A-B: routing path
  runs each source's own vanilla cast job; mortar timings/scatter/shell use the turret's own def +
  vanilla formulas. Edge: static state cleared on game load; dead turret/target auto-cancel; local
  fights take precedence over bombardment; entries bounded (128). TPS: hover work only while
  targeting; Drive() iterates a tiny player-ordered dict on the existing 250-tick scan; no new
  per-tick patches. **Honest certainty: ~50%** — wide surface, none of it run in-game yet; the
  targeter swallow-the-click interactions (DestinationSelector chains, MultiSelect shift-casts)
  are the risk area.

### 5. Full turret cross-level combat (`CrossLevelTurret`) — vanilla + modded, any faction
- **What**: the mortar-only system generalized to ALL projectile turrets, capability-based:
  - **Arc verbs** (flyOverhead): bombardment as before — cells or things, forced-miss scatter,
    shell consumption, roof punch on impact stays vanilla.
  - **Direct verbs** (mini-turret, autocannon, uranium slug, modded laser/charge turrets whose
    `AttackVerb` is a `Verb_LaunchProjectile`): same gap line-of-fire rules as pawns — footprint-
    aware (`CanCrossGapFire` probes every occupied cell, so a 2x2 autocannon fires from its exposed
    corner) — with mirrored ShotReport accuracy (`HitFactorFromShooter`'s Thing path reads
    ShootingAccuracyTurret). Things only.
  - **Player-ordered** via the turret's own targeter (vanilla forced-target permission model
    untouched — only turrets that offer the gizmo reach us) + **AUTO-ACQUIRE** on the scan: idle,
    ready turrets of ANY faction take the nearest enemy pawn on the paired level — a sky autocannon
    lights up raiders under the hole on its own, and **enemy siege mortars can bombard sky
    platforms**. Local fights always outrank the cross-level order.
  - **Tick-accurate firing driver** (`TickPair` on the sky comp): warmup -> burst at
    `ticksBetweenBurstShots` -> cooldown, all values from the turret's own def/verb — vanilla DPS,
    not scan-granularity DPS. Zero idle cost: single static count early-out when no orders exist.
    Turret top visually tracks the cross-level target (`Top.CurRotation`).
- **Review-caught bugs fixed pre-run**: phantom-burst consumption on the unmanned/unpowered hold
  path (tri-state fire result); self-test tick loop stalled by the paused TickManager (now-override
  param); Position-only revalidation breaking multi-cell turrets (footprint-aware everywhere).
- **Limits**: beam/non-projectile turrets excluded; auto targets pawns only; orders not saved
  across save/load; a same-column pair drives its own turrets only (stale entries from destroyed
  columns age out on game load).
- **Self-test**: targeting-hub test extended — mini-turret at the hole edge must auto-acquire the
  hostile below and put real projectiles on the surface via 300 simulated driver ticks.
- **Honest certainty: ~50%** until run.

### Round-2 combat fixes (2026-07-21, user-reported: auto-engage dead, raiders flee, visuals missing)
Root cause across all three: **reaction cadence**. The 250-tick scan + 700-tick fail cooldown
reacted 10–40× slower than vanilla (10–25 tick hunts), reading as "does not engage"; and nothing
ever routed surface hostiles UP, so assault lords saw zero targets and gave up ("raiders run").
1. **Colonist overwatch at vanilla cadence**: postfix on `JobDriver_Wait.CheckForAutoAttack` —
   fires the moment vanilla's own auto-attack finds nothing same-map (Stance_Busy = vanilla
   engaged, skip). Fire-at-will respected, hold-position, 60-tick per-pawn retry gate. The old
   scan path for colonists is REMOVED (no doubled code).
2. **Turret acquisition at vanilla cadence**: postfix on `Building_TurretGun.TryFindNewTarget`
   (vanilla's own 15-tick idle hunt) — probes the paired level exactly when vanilla gives up,
   120-tick per-turret retry gate. The 250-tick `AcquireAuto` scan stays as a backstop for
   `Building_Turret` subclasses that bypass the vanilla hunt.
3. **Hostile ASCENT** (`ScanGroundHostiles`, the descent mirror): surface hostiles that are stuck
   (idle / bashing the immortal stairs / no reachable target) now shoot up through the gap when
   ranged, otherwise take the stairs toward the linked level with player pawns or buildings and
   join/start the assault there on arrival. Runs on the ground comp at the existing hostile-scan
   cadence, gated on hostiles-present + stairs-present. Already-fleeing raids stay fled (vanilla).
4. **Always-on engagement visuals** (replaces the selection-gated overlays, which also carried the
   see-below y = -2.5 into the line endpoints — the "no line" bug): every active cross-level
   shooter (pawn or turret) draws an altitude-clamped target line whenever either end is on the
   viewed map, aim pie while warming, drawn per frame from `MapComponentUpdate` with empty-set
   early-outs (idle cost: two count reads). Plus a targeting-time rotating **crosshair** at the
   below target's shifted render position (`DrawTargetHighlightWithLayer`), so aiming across the
   gap reads exactly like same-map aiming.
- **Honest certainty: ~60%** — root causes match the reports cleanly and cadences are now
  vanilla-anchored, but none of it has been run in-game yet.

### Round-3 fixes (2026-07-21, from run #3 + user screenshots)
1. `IsArc` read the LOADED shell → an unloaded mortar classified as direct-fire and rejected cell
   orders (self-test 2/14 fail). Now shell-independent: `requireLineOfSight == false` (mortar-class
   signature) or fly-over default/loaded projectile.
2. Dev arenas hand-painted rooftop terrain with no backing roof; the rooftop reconcile sweep
   correctly reverted it and destroyed the mortar (leavings warning). Arenas now build legitimate
   platforms (`MakePlatform`: rooftop terrain + constructed roof below).
3. **Aperture model replaces shooter-exposure** (user report: mid-platform sky turrets never
   engaged): an elevated muzzle's path is above the sky plane only for ~its first quarter, so the
   real requirement is an open-air cell within the first HALF of the sky-plane line - not
   shooter-adjacency. Shooting down now needs: victim's column open (strict - matches see-below
   visibility) + aperture toward it. Shooting up: muzzle unroofed on the surface + target at an
   edge (what is visible from below). Mid-platform turrets and snipers engage properly.
4. **Selected-attacker target highlight** (user report: "target selection circle" missing):
   vanilla draws its on-target crosshair from job-target machinery that cannot hold a cross-map
   target; the engagement-visuals pass now draws `DrawTargetHighlightWithLayer` at the target's
   rendered (shifted) position whenever the cross-firing pawn or turret is selected.

### Run #4 results + round-4 fixes (2026-07-21)
**USER-VERIFIED this run**: mid-platform sky turrets auto-engage hostiles below (aperture model);
raiders climb the stairs and assault upstairs instead of fleeing (hostile ascent). Mortar path
still untested in-game.
**User-diagnosed root cause for the "missing circle"**: our targeter paths filtered below targets
to hostiles/wild only — vanilla's B-targeter accepts ANY pawn (deliberate friendly fire). The
circle test was "shoot my own colonist below": the click was silently rejected, no job, no circle.
1. **Friendly-fire parity**: the click dispatcher and hover now validate below targets with the
   SOURCE's own `targetParams` (self excluded) — identical accept-set to vanilla, colonists
   included. Right-click float-menu attack options intentionally keep the hostile/wild filter
   (vanilla's right-click is filtered the same way).
2. **Target marker hardened**: selected cross-level attackers (pawns + turrets) now draw vanilla's
   crosshair PLUS a red circle outline at the victim's rendered position.
3. **Cross-level goto ghost** (round-4 report: no "location preview"): vanilla's
   `MultiPawnGotoController` is current-map-only and never sees below pawns. New hover preview:
   a single below-selected drafted pawn hovering open air from the sky draws the vanilla-style
   pawn ghost + goto circle at the shifted destination cell (suppressed while targeting; hover-only
   cost; multi-pawn formation preview documented as not covered — cross-level orders are
   single-pawn by design).

### 6. Wild-animal cross-level wandering (`CrossLevelAnimals`)
- **What** (user-confirmed design): animals treat stairs as landscape. AMBIENT: on a slow cadence
  (1200-tick due, 15% roll, 12k-tick column spacing, basement cap 4, comfort-temperature check,
  no predators mid-hunt / manhunters), at most one wild surface animal wanders down into the
  basement. ESCAPE: wild animals on pocket levels leave for the surface when hungry (<25% food or
  malnutrition) or when their randomized ~2-4h linger window ends - runs inside the existing
  pocket scan via a policy hook, so wildlife never starves below and pocket levels never
  accumulate a zoo. Sky is never an ambient destination; colony/pen animals are never touched
  (faction filter); pets keep the master-follow rule. Linger resets on each stair arrival, closing
  the visit loop (descend -> linger -> leave). Setting `crossLevelAnimalWander` (default ON),
  ABGuard.HostileMove, state cleared on game load.
- **Quad pass (code review)**: F: both rules compile, self-test asserts ambient descent job,
  hungry-escape job, and linger retention. A-B: zero vanilla behavior overridden - pure addition
  gated to wild (factionless) animals. Edge: bounded stores, load-clear, mental-state/vehicle/
  mount exclusions inherited from the pocket scan. TPS: ground-comp due 1200 with two-field
  early-outs; the wildlife count walk only runs after the 15% roll; per-animal cost in the pocket
  scan is one dict lookup + one need read.
- **Self-test**: `AB: animal-wander self-test` - spawns linked stairs (asserts the spawn-link),
  a surface wanderer (asserts the descent job), a hungry + a content visitor below (asserts escape
  vs linger).
- **Run #8: self-test 8/8 PASS**; **run #9: USER-VERIFIED** - the full live loop (walk down,
  climb out, timed departure) confirmed in-game, plus the round-5 confirms (right-click-hold goto
  ghost, single mortar target marker). **Certainty ~92%** (residual: long-session ecology balance).

---

## Tranche summary (2026-07-21, runs #1-#9)
User-verified as working identically to vanilla ("as if one big map"): cross-level ranged combat
(player-ordered + AI + turrets + mortars + friendly fire + full targeting feedback), hostile
ascent/descent with lord handoff, universal targeting hub, wild-animal wandering. All features
quad-passed, kill-switched, zero-idle-cost audited, and logged above with honest certainties.
Remaining roadmap: ritual attendance across levels; ongoing vanilla-API parity audit; deferred:
cell-targeted ability casts, beam turrets, scribed turret orders, H/B multi-select formations.

### 7. Ritual attendance across levels (`ABRitualAttendance`) - the last roadmap tranche
- **What** (found organically in the soak run: "rituals can't see pawns on other levels"):
  1. CANDIDATES - `CanStartRitualNow` and `Dialog_BeginRitual.CreateRitualRoleAssignments` both
     read the ritual map's `FreeColonistsAndPrisonersSpawned`; a scoped flag (ThreadStatic, set by
     prefix/finalizer pairs on both methods) makes the getter's postfix return a MERGED COPY
     appending column-mates with a usable stair route. Vanilla's cached list is never mutated
     (self-test asserts no cache corruption). Role holders below now count for gating AND appear
     in the begin dialog.
  2. ATTENDANCE - a prefix on `RitualBehaviorWorker.TryExecuteOn` intercepts starts with off-map
     participants: everyone rides the stairs, a message announces the gather, and the fully
     VANILLA start re-runs (reentrancy-flagged) the moment all participants stand on the ritual
     map. Stages/roles/spectators/outcomes stay untouched vanilla. 5-in-game-hour timeout with a
     cancel message; pending list bounded (8) and cleared on load; per-tick cost is one count read.
- **Documented limits**: prisoners and animals do not cross levels for rituals; behavior workers
  that override TryExecuteOn/CanStartRitualNow without calling base bypass the system (vanilla's
  all call base); an obligation consumed by a timed-out gather is lost (obligations regenerate).
- **Quad pass (code review)**: F: compiles; self-test asserts the scope merge (in/out/cache-safe)
  and stair routing. A-B: candidate accept-set is vanilla's own plus reachability; execution is
  literally vanilla, only delayed. Edge: kill switch + setting, timeout, dead-participant
  tolerance, bounded state, load-clear. TPS: zero idle (ThreadStatic bool read on the getter,
  count read per tick). **Honest certainty ~55%** until run - the risk areas are the getter patch
  (cached-list semantics) and dialog UX with off-map candidates (portraits, warnings).

### Run-12 fixes (2026-07-21)
1. **CRASH (ritual spot click, no dialog)**: the ritual candidate merge read a LINKED map's
   `FreeColonistsAndPrisonersSpawned` from inside the getter's own postfix while the scope flag was
   still set → postfix fired again for that map → merged from ITS links → read the first map →
   unbounded recursion → stack overflow (uncatchable, instant process death). Fixed with a
   [ThreadStatic] reentrancy guard: inner reads return raw vanilla lists. Self-tests could not
   catch it because they call the scope directly on one map; the gizmo path recursed through the
   column cycle.
2. **Mountain cap second border**: the cap's fill-skip (explored below rock face → show the rock
   print) applied to INTERIOR cells too, so surface exploration of deeper rock rows carved a
   creeping second border past the ledge. The skip now additionally requires the cell to be on the
   mass BOUNDARY (a cardinal neighbour not cap): outer lip keeps the ledge look, interior is
   complete vanilla-fog coverage per the user's directive. Regen-time cost: 4 terrain reads per
   cap cell.

### Run-13 fixes (2026-07-21)
1. **Cap fog mismatch (R1 continued)**: the cap's fog fill is semi-transparent - filling INTERIOR
   cells over live below-prints (explored rock/scree) made the mass lighter, textured, and
   outlined by the prints' edges. The below-things layer now prints under cap cells ONLY on the
   mass boundary (shared `IsMassBoundary` helper keeps fill and print agreeing): interior fill
   sits on bare terrain again = the verified flat-fog look; the ledge keeps its rock print.
2. **Ritual gating reachability (BUG2)**: `RitualObligationTargetFilter.GetBlockingIssues` checks
   `mustBeAbleToReachTarget` roles with same-map-only `pawn.CanReach` - an assigned role holder on
   a linked level always read "must be able to reach ritual target". The small base loop is
   replaced verbatim with a cross-level-aware reach test (off-map pawn + usable stair route =
   reachable; the gather machinery walks them over). Subclass overrides that call base inherit it.

### Run-16: cap redesigned to ROCK-TOP model (final spec via reference photo)
The target was never fog-colored: the user's reference is the flat gray "plain" top that vanilla
granite shows when exposed - stone-type-aware, gap-free. The fog-illusion fill (and the black
base) were the wrong model entirely. `SectionLayer_ABMountainCap` rewritten:
- WALL/edifice cells emit NOTHING - vanilla rock wall sprites + vanilla fog render natively
  (pixel-vanilla by construction).
- Open mass cells (ledge interior, mined tunnels, bare cap terrain) get one flat opaque quad in
  the LOCAL rock type's color (mined floors via leave-terrain mapping; bare cells from the nearest
  rock wall's def color; map-rock fallback). Uniform, per-stone, border-free.
- Boundary band over explored faces still yields to the rock-face print (the ledge lip).
- Deleted: fog material fill, black base, the 3x3 rim decal-cover tiling, over-walls queue - the
  layer is now one quad per open mass cell at one queue.

### Run-17: cap fill = TRUE VANILLA LINKED ATLAS (final reference: unfogged surface granite group)
Solid-color quads were still not the spec - the reference is vanilla's CONNECTED rock texture.
The fill now uses the walls' own machinery: per open mass cell, a link mask from which neighbours
continue the mass (walls or mass cells, map edge = linked; Graphic_Linked's N=1 E=2 S=4 W=8
order) selects the tile via `MaterialAtlasPool.SubMaterialFromAtlas` on the rock def's atlas base
(inner graphic of its linked wrapper, def-tinted; leave-terrain -> rock DEF map added to
LevelSync). Atlas edge tiles provide the pale lip + outline natively, so the mass reads as ONE
connected vanilla texture per stone type - modded rocks included via their own graphics. Quads
now carry UVs; 16 queue-clones per rock cached.

### Run-18 fix: corner fillers (2026-07-21)
The "holes as if it's an edge piece" grid = Graphic_LinkedCornerFiller's whole reason to exist:
the atlas tiles leave rounded corners that vanilla covers with extra corner quads. The fill now
mirrors vanilla's corner pass exactly: each fully-linked diagonal (both adjacent cardinals + the
diagonal linked, direction-explicit like vanilla) gets a 0.5-size quad at offset 0.3536 toward
the corner (+0.09 north nudge), sampling the tile's solid point (0.5, 0.6), emitted after the
main quad in the same submesh. Map-edge stretch variant skipped (off-map = linked, normal-size
filler). Probe tool now also reports IsMassCell + the 4-bit link mask for any clicked cell, so a
residual artifact converts to data in one click.

### Run-19 fixes (2026-07-21)
1. **Seam dashes**: vanilla `Printer_Plane` TILTS every plane - north verts +0.01 altitude - for
   deterministic overlap at row seams; my flat quads lost that overlap. Both quad emitters now
   carry the north bias. The fill now replicates PrintPlane geometry, atlas tile selection, and
   the corner pass exactly.
2. **Torch turned the cell to rock floor**: the fill skipped ALL edifices; only natural rock
   walls should skip (they render themselves). Torches/furniture/built walls keep the fill
   beneath them like furniture on any floor.

### Run-20 fix: rock type sourced from the GROUND column (2026-07-21)
User mouseover diagnosis: discolored patches were "rough-hewn limestone" (sky-side mined
leave-terrain) over a SLATE ground - the sky genstep noise-picks its own wall rocks, so sky-side
typing produced patchwork. The fill's rock def now comes from the GROUND map at the column
(standing rock, else its mined leave-terrain, else a cardinal neighbour), with the sky-side
mined mapping kept for eligibility only. Bonus: ground-sourced typing merges large regions into
one material = one seamless submesh, which also removes the residual cross-material seam dashes.

### Run-21: color/picking USER-CONFIRMED; dash isolation A/B shipped (2026-07-21)
Ground-sourced rock typing confirmed correct in-game. Residual: a regular per-junction dash grid.
Remote analysis exonerated the UVs (match Printer_Plane exactly), the atlas window math (verified
against MaterialAtlasPool: scale 0.1875, quadrant + 1/32 padding), and the wall-reveal layer
(rooftop-rim-gated, can never emit on cap). Rather than a fourth blind fix: shipped
`AB: toggle cap corner fillers` - a live A/B that disables this layer's corner pass with instant
regen. One toggle in-game attributes the dashes to our corner geometry or to another layer.

### Run-22: A/B verdict + the deterministic interior fix (2026-07-21)
The toggle proved the dashes are the atlas tiles' ROUNDED CORNERS, only partially covered by
corner fillers (off = larger gaps). Fix sidesteps the corner dance entirely: FULLY-INTERIOR cells
(all cardinals + diagonals linked) now draw a flat quad sampling the tile's solid point - no
corners exist, so interior junctions are seamless BY CONSTRUCTION. Atlas tiles with their correct
rounded lips remain only on the mass edge; edge cells keep the vanilla corner-filler pass with
direction-explicit diagonal links. Same material/submesh throughout a rock region - no
cross-material seams.

### Run-23: interior seamless USER-CONFIRMED; edge band completed the same way (2026-07-21)
Interior flat quads verified in-game. The outer edge band still showed atlas corner gaps at its
mass-side junctions - completed with the same by-construction move: each edge cell draws a solid
under-quad inset 0.35 from AIR-facing sides only (base runs fully to every linked side = mass-side
gaps impossible), then the atlas tile over it - the wavy transparent silhouette survives on the
air side. Vanilla-mirror corner fillers deleted (redundant under the base); the A/B toggle now
gates the edge base for isolation.

### Run-15 results + fix (2026-07-21)
**RITUALS USER-VERIFIED** - the full summon flow works (gather message, stairs, vanilla ceremony
on arrival). The attendance tranche - and with it the LAST item of the original roadmap - is done.
**Cap tone, root cause found via the photo**: the off-tone region was MINED-OUT floor, which by
old design filled in the source rock's own (lighter) color. Overruled per user directive: mined
floors now use the identical opaque-base + fog composite as the unmined cap, so walls, interior,
and tunnels read as ONE uniform vanilla-fog tone (mining debris - real sky things - renders on
top). Rock-colored fill machinery removed.

### Run-14 fixes (2026-07-21)
1. **Ritual started immediately (no summon)**: `TryExecuteOn` is VOID; the intercept prefix
   declared `ref bool __result`, so Harmony REJECTED the patch at boot and HarmonyBoot skipped the
   class with only a startup warning - the gather machinery never existed at runtime. Prefix
   corrected (no __result). Lesson banked to lore: verify the target's return type in the index
   before writing a prefix; a skipped patch class is a silently-dead feature.
2. **Stairwell heat exchange REMOVED** (user directive): the ClimateExchange tick, its cadence
   field, and the settings checkbox are gone (setting field stays scribed for old configs; pocket
   ambient temperature via ClimatePatches is unaffected).
3. **Cap tone (R1 round 3)**: the fog fill is semi-transparent, so the cap TERRAIN underneath
   tinted the mass gray-green - mismatching the true fog tone visible through adjacent open air
   (the below mask's opaque black), which also produced the border seam. Filled cap cells now
   emit an opaque near-black base quad under the fog quad (same queue, submitted first), making
   the composite converge on the vanilla fogged look regardless of underlay. If the tone still
   reads off in-game, the next step is sampling the exact fog composite - flagged for the run.

### Round-5 fixes + open regression (2026-07-21)
**USER-VERIFIED run #5**: friendly-fire targeting, target markers, mortars firing.
1. Doubled target marker → single vanilla crosshair (the extra red circle read as two UIs).
2. Goto ghost timing → vanilla press-preview-release: right-click-HOLD over open air shows the
   pawn ghost + goto circle at the shifted destination (`ABBelowGotoDrag`), release issues the
   goto (`PawnGotoAction` + `BestOrderedGotoDestNear`, ColonistOrdered sound). Replaces both the
   instant-order behaviour for pure moves and the wrong hover-preview. Attack clicks keep the
   immediate path. (csproj: + UnityEngine.InputLegacyModule for Input.GetMouseButton.)
3. **RESOLVED (run #6): mountain-ledge gray bands** — not reproducible after the round-5 build;
   user confirms fixed. Best supported theory: earlier dev arenas hand-painted rooftop terrain
   with no backing roof near the map centre; reconcile + roof events then contaminated nearby
   terrain state on those maps. The `MakePlatform` fix removed the source; fresh maps are clean.
   `AB: probe ledge cell` stays in the toolbox for any recurrence.

### Hardening pass 2 (2026-07-21, pre-animal-wander deep scan: TPS / doubled code / defs)
**Defs audit**: every def file re-read. All `giverClass`/`driverClass`/`thingClass` references
resolve to real classes (the two suspicious WorkGivers live in `MedicalAcrossLevels.cs`). Run #1
runtime-validated the parse (exactly one error class, already fixed). No further invented fields.
**Doubled code**: gun-verb classification was duplicated between the click dispatcher and the
hover patch (divergence risk) → extracted to `CrossLevelCombat.IsEquippedGunVerb`, both callers
switched. Accepted duplication (documented, dev-only, no TPS impact): the three self-tests'
`Check`/report scaffolding in ABDevTools.
**TPS fixes**:
1. `JobDriver_ABCrossLevelAttack` resolved the ranged verb (equipment + verb list walk) EVERY tick
   via both `Valid()` and `FireTick` → now cached, re-resolved on the 15-tick revalidate cadence,
   dropped instantly if the source equipment is destroyed.
2. `CrossLevelAutoEngage` direction early-outs: hostile scan skips entirely when the target level
   has no player pawns (the common case — empty sky), colonist scan skips when the target level has
   no hostiles (attackTargetsCache count, O(1)). Before this, every scan walked pawn lists and paid
   reachability probes even with nothing to shoot at.
3. `Patch_Targeter_ProcessInputEvents` prefix reordered: the event-type check (cheapest, most
   selective) now runs before guard/settings reads — it fires for every input event while targeting.
**Accepted micro-costs (documented, not bugs)**: sort-comparator lambda allocations in the two
nearest-target sorts (scan-cadence only); GetGizmos iterator wrapper on selected turrets (vanilla
allocates the same way); `GenUI.TargetsAtMouse` allocation per GUI frame while targeting (vanilla
parity); TickPair iterating the global entry dict once per sky comp (entries are player-scale).
**Zero-idle-cost audit re-confirmed**: TickPair static-count early-out; scans gated by pawn-count
checks; no new per-tick patches; hidden-map throttles untouched; all combat state cleared on load.

### Review pass (2026-07-21, pre-run-2 recursive pass over features 1–3 code)
Cold re-read of all combat code found and fixed **5 bugs + 3 optimizations**:
1. **Missing re-aim between bursts** (parity): `warmedUp` never reset → fire rate too high. Now
   aim → burst → cooldown → re-aim, like a real stance cycle.
2. **Warmup ignored `AimingDelayFactor`** (parity): careful-shooter/trigger-happy now scale aim time.
3. **Cooldown now uses vanilla `VerbProperties.AdjustedCooldown(verb, pawn)`** instead of a
   hand-rolled stat read (correct for tools + modded weapons).
4. **Per-tick GenSight raycast in FireTick throttled** to every 15 ticks; every actual shot is still
   fully validated inside `Fire()`, whose failure now ends the job (was silently ignored).
5. **`PendingTargets` unbounded growth** on failed job starts → bounded (clear past 64).
6. **Below-click cell probe now inverse-maps through `ScreenToBelowPos`** — raw click cells missed
   shifted below sprites (depth shift / parallax).
7. **Drafted-colonist auto-engage now skips pawns with a live same-map threat** (≤40 cells, capped
   cache walk) — vanilla overwatch owns those; we no longer blind a pawn to closer danger.
8. **Projectile spawn distance capped at 2 cells** (was 3) — shrinks the window where a surface
   wall could intercept a plunging shot the sky-plane LOS model says is clear. Plus: cached
   `TargetingParameters.ForAttackAny()` in the hover patch (was allocating per GUI frame); removed
   a dead field.
Known accepted deviations (documented, not bugs): miss-scatter uses the size-factored aim chance
(vanilla passes the pre-size value — slightly tighter scatter on big targets); `FactorFromExecution`
/ covering-gas / posture omitted from aim; AI cross-fire pawns can tunnel-vision while melee-rushed
(same-map threat guard is scan-time only) — revisit after run 2 if it reads badly.

### Dev tools shipped this session
- `AB: ensure sky + basement` — generate the two pocket levels for the current column.
- `AB: cross-gap combat self-test` — builds an open-air arena (hostile on the surface under a hole,
  drafted armed colonist on a sky platform beside it), asserts pairing / line-of-fire / the cross-map
  cast / projectiles landing on the surface / enclosed-cell blocking, then leaves a live plunge-fire
  demo running. Writes `docs/SelfTest.log`; summarises via `Log.Warning`/`Log.Error`.

### QOL/BUG pass (2026-07-21, post-run-15: VCHE compat + mech/bond/home parity)
**QOL1 - Vanilla Chemfuel Expanded riser** (`Patches/VCHE_VerticalPipes.xml`): VCHE rides the VEF
PipeSystem (`VCHE_ChemfuelNet` + `VCHE_DeepchemNet`), so the generic vertical duct already bridged it
with butted pipes; the new dedicated riser matches the Rimefeller pattern instead - each end carries
two hidden 100-cap PipeSystem buffers (bars/gizmos suppressed), so the shaft IS a tank on both nets,
pipes connect straight to it, and no per-level tank is needed. Zero new C#: `bridgeVef` extension +
the existing VEFPipeBridge direct-feed/equalize path cover both nets generically.
**BUG1 - mechs across levels**: audit found exactly ONE map-equality gate in the vanilla mechanitor
stack: `MechanitorUtility.InMechanitorCommandRange` (`mech.MapHeld != overseer.MapHeld -> false`),
which blocks drafted goto/attack for a mech one level from its mechanitor. Patched: same column
re-runs vanilla `CanCommandTo` (bounds + flat 24.9 radius -> a command cylinder through the column).
`CompOverseerSubject.State` / `ControlledPawns` / work-mode think nodes are all map-agnostic, so the
reported instant "dormant self-charging" does NOT reproduce in any static path (`JobDefOf.SelfShutdown`
creators: JobGiver_SelfShutdown [low-energy flag or SelfShutdown work mode], GetEnergy_SelfShutdown
[Recharge mode], Need_MechEnergy [energy=0]) - `AB: mech overseer self-test` builds the repro
(mechlinked colonist + bonded lifter transferred to a basement pocket) and logs the exact think-tree
giver + job at full/25% energy so the culprit branch names itself in one run. Suspects if it still
repros: third-party think-tree edits, or Recharge work mode + no reachable charger.
**BUG2 - psychic bond distance** (Highmates): `ThoughtWorker_PsychicBondProximity.NearPsychicBondedPerson`
ends on MapHeld equality; postfixed so same-column counts as near. The hediff's distance stage
(consciousness penalty) keys off the same static, so one patch fixes mood + hediff.
**BUG3 - "not at the colony"**: levels are pocket maps -> `Map.IsPlayerHome` false -> royalty
expectations, guest forbidden rules, and modded separation/"away from colony" thoughts treated level
pawns as travellers. Postfix: a level reports its column ground's verdict, EXCEPT during the alert
readout (per-map alerts would nag per level: LowFood/NeedMealSource/NeedDefenses evaluate each home
map in isolation) and during quest generation (`QuestGen.Working`) so quest map pickers stay on the
surface. Caller sweep verdicts: ThreatDivert already pins level==0; ForbidUtility/Expectations/royalty
flips are corrections; MapParent.Abandon new-colony-thought edge accepted.
New: `crossLevelSocial` setting (bond + home, default on), `ABGuard.Social` kill switch; mech range
rides `crossLevelOrders` + `ABGuard.Movement`. Dev tools: `AB: mech overseer self-test`,
`AB: bond + home self-test`.
