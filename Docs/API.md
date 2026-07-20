# As above, So below - modder API (v1)

Two integration surfaces: pure XML (no reference needed) and a C# static API
(`AsAboveSoBelow.ABApi`). Everything is fail-open and null-safe. PackageId to
depend on (or soft-reference): `astryl.AsAboveSoBelow`.

## XML: make your own vertical link

Any ThingDef becomes a full vertical link - far end auto-spawned on build,
both ends collapse together, registered in the column - by using our thing
class and extension. No C# required:

```xml
<ThingDef ParentName="BuildingBase">
  <defName>MyMod_ServiceHatch</defName>
  <thingClass>AsAboveSoBelow.Building_ABStairs</thingClass>          <!-- pawns can climb -->
  <!-- or AsAboveSoBelow.Building_ABUtilityLink for networks-only -->
  <modExtensions>
    <li Class="AsAboveSoBelow.ABStairsExtension">
      <deltaLevel>-1</deltaLevel>              <!-- -1 down, +1 up -->
      <counterpartDef>MyMod_ServiceHatchTop</counterpartDef>
      <climbFactor>1.2</climbFactor>           <!-- climb time multiplier -->
      <utilityOnly>false</utilityOnly>         <!-- true: never pawns, no heat leak -->
      <bridgeWater>true</bridgeWater>          <!-- DBH water equalization at this cell -->
      <bridgeVef>true</bridgeVef>              <!-- VEF pipe nets equalization -->
    </li>
  </modExtensions>
  <placeWorkers><li>AsAboveSoBelow.PlaceWorker_ABStairs</li></placeWorkers>
  <!-- add CompProperties_Battery with compClass AsAboveSoBelow.CompABPowerBridge
       (+ tickerType Normal) to carry power across the link -->
</ThingDef>
```

Notes: links are immortal by our convention (`<useHitPoints>false</useHitPoints>`)
so raid AI uses them instead of smashing them - recommended for yours too.
Building either end can open the level (basement/sky) exactly like our stairs.

## XML: let your incident hit pocket levels

By default every incident executed against a sky/basement map is redirected to
the column's surface. Opt out per IncidentDef:

```xml
<IncidentDef>
  <defName>MyMod_CaveTremor</defName>
  ...
  <modExtensions>
    <li Class="AsAboveSoBelow.ABIncidentLevelPolicy">
      <allowOnPocketLevels>true</allowOnPocketLevels>
    </li>
  </modExtensions>
</IncidentDef>
```

## C#: AsAboveSoBelow.ABApi (static)

Queries (never throw; non-column maps read as level 0):

```csharp
int   ABApi.ApiVersion;                  // 1
int   ABApi.GetLevel(Map map);           // 0 surface, +1 sky, -1 basement
bool  ABApi.IsLevelMap(Map map);         // true for our sky/basement maps
Map   ABApi.GetGroundMap(Map map);       // column surface (identity for surface)
Map   ABApi.GetUpperMap(Map map);        // one level up, or null
Map   ABApi.GetLowerMap(Map map);        // one level down, or null
IEnumerable<Map> ABApi.GetColumnMaps(Map map);  // surface, sky, basement
```

Movement:

```csharp
Building ABApi.GetStairsToward(Pawn pawn, Map target);          // nearest usable, reach-checked
bool     ABApi.TrySendPawnToward(Pawn pawn, Map target, bool forced = false);
// Orders the full walk-climb-transfer. Non-player pawns get lord handling
// (released from map-scoped lords, re-lorded on arrival). False if no stairs.
```

Events:

```csharp
ABApi.LevelCreated    += (Map m) => { ... };            // after generation, not on load
ABApi.LevelRemoved    += (Map m) => { ... };
ABApi.PawnTransferred += (Pawn p, Map from, Map to) => { ... };
```

Extensibility:

```csharp
ABApi.RegisterExitDuty("MyGuestLeaveDuty");
// NPCs on pocket levels holding this duty are routed to the surface and
// given an exit-map lord, instead of pacing forever at the map edge that
// pocket maps do not have.
```

Soft-reference pattern (no hard dependency): resolve `AsAboveSoBelow.ABApi`
via `AccessTools.TypeByName` and call the statics reflectively, or gate a
compilation reference behind `ModsConfig.IsActive("astryl.AsAboveSoBelow")`.

## Guarantees

- Signatures on ABApi and both DefModExtensions are stable within ApiVersion 1.
- All raisers swallow subscriber exceptions (logged as warnings) - your bug
  cannot disable our systems, and vice versa: our kill switches never disable
  API queries.
- Pocket levels are real maps: mapPawns, listers, regions, rooms, weather all
  live. If your mod works on a normal map, it works on a level; the API only
  matters when you care about the column structure or vertical movement.
