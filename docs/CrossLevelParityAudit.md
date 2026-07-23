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
| Build/remove roof areas | GAP-P2 | Construction detector ignores areaBuildRoof/NoRoof — add area check |
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
| Trade beacon aggregation | GAP-P1 | Selling only sees current map's beacons. Patch TradeUtility.AllLaunchableThingsForTrade (+LaunchThingsOfType) to append linked levels' beacon-covered things |
| Transport pod / shuttle loading | GAP-P1 | LoadTransporters haul is map-scoped; register transporter contents as demand |
| Refueling + turret rearm + growth vats | GAP-P1 | Register refuelable shortfall in CrossLevelDemand (bills pattern) |
| Deterioration/forbid/home area | DONE | Per-map vanilla, correct as-is |

## Needs and daily life
| Mechanic | Status | Notes |
|---|---|---|
| Food, rest (owned bed), medical rest | DONE-V | Needs redirects |
| Recreation | DONE-V | JoyAcrossLevels |
| Meditation (assigned spot/throne) | DONE | Built 2026-07-23 |
| Anima tree / natural focus travel | BY DESIGN (for now) | Only ASSIGNED spots pull pawns across levels |
| Apparel optimization | GAP-P1 | JobGiver_OptimizeApparel never sees wardrobe on another level — idle-time virtual probe w/ cooldown |
| Weapon pickup (opportunistic/policy) | GAP-P2 | Same shape as apparel |
| Drug policy inventory refill | GAP-P2 | Map-scoped stack search |

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
| Non-Ideology gatherings (parties/marriages via GatheringWorker) | GAP-P2 | Separate system from RitualBehaviorWorker; attendance hook needed |
| Mech escort follow / command range | DONE / DONE-V | Escort awaiting verification |
| Mech recharge (Biotech) | GAP-P1 | JobGiver_GetEnergy binds charger map-scoped — bots shut down instead of routing; intercept-and-route |
| Mechanitor repair of downed mech | GAP-P2 | |
| Childcare (feeding, carry to crib) | GAP-P2 | Biotech, map-scoped |
| Hemogen transfusion packs | GAP-P2 | Meals-pattern demand |
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
1. **P1**: trade beacon aggregation; refuel/vat demand; Biotech mech recharge routing; apparel optimization; transporter loading.
2. **P2**: roof areas detector; weapon pickup; drug refill; childcare; mechanitor repair; transfusion.
3. **P3**: Anomaly containment; anima travel; turret gap targeting.
4. ~~VERIFY sweep~~ — completed 2026-07-23 (code-level): research, fire, hunting verified; corpse/container push FIXED; gatherings split out as P2.

## Changelog
- 2026-07-23: audit created; install/uninstall P0 fixed; verify sweep completed; robot bounce fix (probe-gated migration); container-storage push fix.
