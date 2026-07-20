# As above, So below - compatibility matrix

Deep scan of the local library (1471 mods: 6 official, 63 local, 1316 workshop, 86 workspace; ~897 active),
triaged by interaction with our systems (pocket-level maps, stair routing, work migration, virtual position
swaps, see-below renderer, tile identity, storyteller wealth, NPC lords). Date: 2026-07-19.

Legend: OK = compatible by construction or bridged - EXPLICIT = we ship dedicated compat code -
WATCH = plausible interaction, verify during play - INCOMPAT = declared incompatible.

## Explicit compat shipped (code in Source/Compat + API)

| Mod | Status | Mechanism |
|---|---|---|
| Vanilla Expanded Framework | EXPLICIT | VEF pipe nets equalize across stairwell links (VEFPipeBridge, signature-isolation pattern) |
| Dubs Bad Hygiene (+ Expanded, Medieval, retextures) | EXPLICIT | Water net equalization at stairs + vertical water pipe (patch-gated defs, research mirrors DBH) |
| Vehicle Framework (+ Alpha Vehicles, VVE when active) | EXPLICIT | Vehicles excluded from all stair routing (type check by name); colonist/humanlike filters already exclude them elsewhere |
| Hospitality (+ Room Service, Spa) | EXPLICIT | Guest exit handled: their Relax duty is never an exit trigger; TakeWoundedGuest + vanilla travel duties route stuck guests down; RegisterExitDuty API covers future duties. Guest rooms belong on the surface (guests do not use stairs mid-visit) |
| Giddy-Up 2 | EXPLICIT | Mounted pawns blocked from transfers (ABGiddyUpCompat) |
| Reverse Commands | EXPLICIT | Cross-level orders replay via ABPendingOrders (direct links only) |
| Fluffy Animal Tab | EXPLICIT | Column pawns appended via RecachePawns |
| Declutter UI | EXPLICIT | Level buttons bypass its global-controls rehost |
| RimJobWorld (+ ~40 addons incl. animations, Sexperience, Menstruation) | EXPLICIT | Sex need satisfies cross-level: JoinInBed + DoQuickie registered in the need-migration engine (assembly-verified type names); partners/beds on other levels are found via virtual scan and the pawn commutes. Solo (Masturbate) and hostile-context givers deliberately excluded. Everything else is same-map pawn state |
| Intimacy - Friends n' Lovers (+ Gender Works) | EXPLICIT | Intimacy need satisfies cross-level: JobGiver_GetIntimacy registered (their giver system mirrors vanilla joy). Inactive in current list; registration activates with the mod |
| Common Sense | EXPLICIT | Decompiled patch surface audited: no JobGiver_Work patch (migration safe); patches JobGiver_GetJoy.TryGiveJob - our cross-level recreation postfix runs at LOW Harmony priority so CS's joy tweaks get first refusal. Opportunistic tasks hook JobTracker (orthogonal). Bill/ingredient patches run consistently under virtual position swaps |
| Hauler's Dream | OK | Carry capacity / move speed stat patches + own jobs; orthogonal to our separate cross-level haul work givers |
| Smarter Construction | OK | Patches construction work giver ordering; under our virtual work scan its checks evaluate against the target map consistently (position swap is coherent). No map-keyed caches found |
| Romance on the Rim | OK | Dates/proposals are same-map jobs with reachability checks; partners on different levels simply date when co-located. Couples converge nightly via cross-level bed ownership. Candidate for a future API-based date-summons |
| Allow Tool / Pick Up And Haul class haulers | OK | Same-map work givers and inventory hauling; our carried-thing transfer detaches explicitly before despawn |
| MultiFloors | INCOMPAT | Competing z-level system (declared incompatibleWith) |
| Z-Levels Beta | INCOMPAT | Competing z-level system (declared incompatibleWith) |

## Watch list (plausible interactions, verify in play)

| Mod | Why | Expected behavior |
|---|---|---|
| FPS+ RimThreaded (ACTIVE in library!) | Threaded ticking vs our cross-map systems | UNSUPPORTED - declared. Kill switches fail open, but threading violations are its bug surface, not ours |
| Perspective Shift | Camera projection changes | See-below renderer derives queues at runtime; should coexist. Verify visually |
| Gastronomy | Restaurants | Per-level restaurants by design (documented limitation) |
| Smarter Visitors | Alters visitor leave timing | Exit assist triggers off duties, not timers - compatible in principle |
| Trader/Transport Airships (joeownage) | Landing ship world objects | Land on surface (pocket incident redirect + Map_PlayerHome targeting) |
| Pick Up And Haul (inactive) | Inventory hauling vs carried-cargo transfer | Our transfer detaches carried thing explicitly; PUAH inventory survives despawn. Verify when activated |
| Real Ruins (inactive) | Injects map-gen steps globally | Our generators have fixed genStep lists; verify it respects MapGeneratorDef scoping when activated |
| Sky Islands | Own floating-island world gen | Different system (world tiles, not pocket maps). No overlap expected |
| Hyperdrive / Odyssey gravships | Moving colony map | Our tile identity reads ground.Tile dynamically, links are map references (survive moves). UNTESTED - documented |
| Better Map Sizes / Change map edge limit | Odd map sizes | Levels clone the ground map's size 1:1 - compatible by construction |
| LightsOut | Power draw manipulation | Power bridge is a battery comp; standard net participant |
| Combat Extended (inactive) | Combat rework | Combat is map-local; no known interaction. Untested |
| Dubs Mint Minimap (inactive) | Minimap | Shows current map only - fine |
| Prison Labor-type flows (Imprisonment On The Go active) | Prisoner handling | Prisoners are not routed cross-level by us (documented) |

## Compatible by construction (the long tail)

Everything else in the library falls into classes with no interaction surface:
- Content packs (apparel, weapons, hair, genes, xenotypes, races, furniture, floors, food, music, retextures,
  sounds): ~70 percent of the library. Operate on defs/pawn-local state; pocket levels are ordinary maps.
- UI mods (tabs, HUDs, menus, bars incl. the astryl Modern* suite): read the current map or global state;
  our one-colony UI patches aggregate at the data layer beneath them.
- Pawn behavior tweaks (traits, moods, social, skills): pawn-local.
- World/quest content (Outposts, quests, factions, Empire Refactored): world-object based; our pocket
  parents carry the column tile and are never storyteller targets.
- Performance mods (Performance Optimizer, FPS Stabilizer, RocketMan-class): our recurring costs are
  plain-bool gated ticks; nothing for them to mis-cache. RimThreaded excepted (above).

## Cross-level recreation (feature, this pass)

Joy now migrates like food and rest: when the joy scan ends null on a level, the same giver re-runs
virtually at each linked stairwell exit; if the other level offers ANY joy source, the pawn takes the
stairs and re-rolls on arrival. 600t per-pawn retry cooldown; colonists only; lords/drafted excluded.

## Modded needs engine (feature, this pass)

Generalization of the joy mechanism for ANY mod-added need: registered ThinkNode_JobGiver types get
the virtual re-scan + stairs commute when they return null. One low-priority postfix on the base
TryIssueJobPackage (JobGiver_Work overrides it - untouched); cost with nothing registered is one
static bool per giver evaluation. Built-in registrations: RJW, Intimacy. Public:
ABApi.RegisterNeedJobGiver.

## Known incompatibility surface to re-check each RimWorld update

- Anything patching JobGiver_Work.TryIssueJobPackage (we postfix it for migration).
- Anything replacing CaravanFormingUtility.StartFormingCaravan (we trim+rejoin there).
- Anything patching WorldObject.Tile or storyteller wealth getters.
- Competing pocket-map systems targeting PocketMapParent lifecycle.
