# Cross-level parity audit (2026-07-23)

Goal: the column plays like ONE BIG MAP — every vanilla-style interaction a
player attempts across levels either works with vanilla feel, or is an
explicit, documented design exclusion. Statuses: **DONE-V** (built +
user-verified), **DONE** (built, awaiting verification), **FIXED-TODAY**,
**GAP-P1/P2/P3** (missing, prioritized), **VERIFY** (probably covered,
needs a check), **BY DESIGN** (deliberate exclusion).

Doctrine (run #70): most gaps are map-scoped searches — either a scan
(`GenClosest`, listers) or a bound-target resolution (station, charger,
beacon, bed) with a null-check but no `Map` check. Scans get demand caches +
migration; bound targets get the intercept-and-route treatment. Any
third-party giver may emit unstartable cross-map jobs once stairs move
pawns: intercept non-null results whose target map differs, replace with
routing or NoJob.

## Work and construction
| Mechanic | Status | Notes |
|---|---|---|
| Deliver materials to blueprints/frames | DONE-V | Demand push/pull + construction supply giver + "Bring X and build Y" forced order |
| Finish frames, general construction | DONE-V | Priority-aware migration |
| Install minified thing on another level | FIXED-TODAY | "Bring X and install it here" order + automatic mini ferry in supply giver (was: "No path") |
| Reinstall built building across levels | BY DESIGN (two-step) | Uninstall migrates workers to it; resulting mini then ferries |
| Uninstall/deconstruct/smooth/remove floor | DONE-V | Designation detector |
| Repair | DONE | RepairableBuildings detector |
| Build/remove roof areas | DONE (2026-07-23) | Roof areas light the construction detector |
| Mining | DONE-V | Designation detector |
| Growing (zones + planters) | DONE | Zone/planter detector |
| Research | VERIFIED (code) | Detector: bench present + active project |
| Cleaning | DONE | Filth detector (home area) |

## Hauling, storage, logistics
| Mechanic | Status | Notes |
|---|---|---|
| Storage-priority hauling across levels | DONE-V | Virtual StoreUtility verdicts, 600t cache |
| Idle fetch from linked levels | DONE-V | Fetch giver + demand haul back |
| Bill ingredient pull | DONE-V | Shortfall demand (toggleable) |
| Patient/prisoner meal pull | DONE-V | Buffer demand (toggleable) |
| Corpse burial in cross-level graves | FIXED-TODAY | Push side searched store CELLS only; now TryFindBestBetterStorageFor — graves, caskets, and modded container storage (Deep Storage style) count |
| Trade beacon aggregation | DONE (2026-07-23) | Column-wide AllLaunchableThingsForTrade (reentrancy-guarded) + LaunchThingsOfType fulfills debt across the column. Caravan (physical) trading and pawn selling stay per-level by design |
| Transport pod / shuttle loading | DONE (2026-07-23) | Load manifests register shortfall demand; local load giver takes over once goods land |
| Refueling + turret rearm + growth vats | DONE (2026-07-23) | Auto-refuel shortfall to target level registers demand (supplyFuel toggle); covers vat nutrition |
| Deterioration/forbid/home area | DONE | Per-map vanilla, correct as-is |

## Needs and daily life
| Mechanic | Status | Notes |
|---|---|---|
| Food, rest (owned bed), medical rest | DONE-V | Needs redirects |
| Recreation | DONE-V | JoyAcrossLevels |
| Meditation (assigned spot/throne) | DONE | Built 2026-07-23 |
| Anima tree / natural focus travel | BY DESIGN (for now) | Only ASSIGNED spots pull pawns across levels |
| Apparel optimization | DONE (2026-07-23) | Virtual probe of the vanilla giver on linked levels (shared 2000t gear cooldown; optimize window reset on route) |
| Weapon pickup (opportunistic) | DONE (2026-07-23) | Probe pattern; unarmed pawns only |
| Drug policy inventory refill | DONE (2026-07-23) | JobGiver_MoveDrugsToInventory probe pattern |

## Medical, warden, animals
| Mechanic | Status | Notes |
|---|---|---|
| Rescue/capture/tend/surgery/feed | DONE-V | Medical flows + migration |
| Prisoner take-to-bed/feed/convert/recruit | DONE-V | Warden verified both directions |
| Pet follow, pet food redirect | DONE-V | Conservative policy |
| Taming/training/slaughter on other levels | DONE | Handling detector (desigs + animals) |
| Pen animals crossing | BY DESIGN | Rope/pen semantics stay per-level |
| Hunting designations | VERIFIED (code) | Detector present; hunting resolves locally after migration |

## Combat and threats
| Mechanic | Status | Notes |
|---|---|---|
| Through-gap combat (sky<->surface) | DONE-V | True LOS combat, engagement lines |
| Drafted cross-level orders/attack | DONE-V | Right-click + Achtung drags (drags DONE, unverified) |
| Turrets targeting through gaps | BY DESIGN (for now) | Manual pawn combat only |
| Opt-in threats, hostile descend, AA/pods | DONE-V | |

## Social, ideology, DLC
| Mechanic | Status | Notes |
|---|---|---|
| Social interactions cross-level | DONE-V | |
| Ritual attendance | DONE-V | Ideology rituals (incl. weddings/funerals as precepts) |
| Non-Ideology gatherings (parties/marriages/speeches) | DONE (2026-07-23) | Join-node postfix routes to joinable lords on linked levels (rituals excluded — own module) |
| Mech escort follow / command range | DONE / DONE-V | Escort awaiting verification |
| Mech recharge (Biotech) | DONE (2026-07-23) | Charger giver postfix routes to usable chargers on linked levels (usability checked under virtual swap) |
| Mechanitor repair of damaged mechs | DONE (2026-07-23) | RepairMech work type lights up on levels with damaged player mechs (migration handles the trip) |
| Childcare (feeding, carry to crib) | DONE (2026-07-23) | Childcare detector (babies present) + baby food buffer demand |
| Surgery ingredients incl. hemogen packs | DONE (2026-07-23) | Surgery bills on pawns register ingredient shortfalls (medicine, packs, body parts flow to the patient's level) |
| Anomaly containment/study cross-level | GAP-P3 | Document "keep platforms on the entity's level" until built |
| Royalty throne/meditation | DONE | 2026-07-23 |

## World and misc
| Mechanic | Status | Notes |
|---|---|---|
| Caravan forming across the column | DONE-V | |
| Pod transit between levels | DONE-V | AA interception verified |
| Column wealth, world integration, alerts | DONE-V | Alerts iterate all maps natively |
| Quest shuttles/drops | BY DESIGN | Surface arrivals |
| Firefighting emergencies cross-level | VERIFIED (code) | Emergency pre-check counts fires on linked levels |

## Robots / third-party workers
| Mechanic | Status | Notes |
|---|---|---|
| Misc. Robots haul + return/dock | DONE-V | |
| Robots++ work migration | FIXED-TODAY (v2) | Run #71 bounce: summary-only gate lured bots to undoable work; now probe-gated with the robot's own giver list (colonist discipline) |
| Cross-map dock-job intercept | DONE | Run #70 fix |

## Priority queue
1. ~~P1~~ / ~~P2~~ — ALL BUILT 2026-07-23 (batch build), in-game verification pending.
2. **P3 (excluded by design until a dedicated pass)**: Anomaly containment (entity-holder cross-map semantics); anima travel (assigned spots only for now); turret gap targeting (conflicts with the sky<->surface manual-combat rule).
3. ~~VERIFY sweep~~ — completed 2026-07-23 (code-level): research, fire, hunting verified; corpse/container push FIXED; gatherings built.

## Changelog
- 2026-07-23: audit created; install/uninstall P0 fixed; verify sweep completed; robot bounce fix (probe-gated migration); container-storage push fix.
- 2026-07-23 (batch build): trade beacons, refuel/vat demand, transporter loading, mech recharge, apparel/weapon/drug probes, roof areas, gatherings, childcare, mech repair detector, surgery ingredient demand — all P1+P2 built; compile green.
