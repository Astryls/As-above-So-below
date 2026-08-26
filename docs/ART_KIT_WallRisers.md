# Nano Banana Pro kit: wall risers (§75)

12 sprites: 4 types x 3 faces (west is auto-mirrored from east by the engine; only
supply a `_west` if a design turns out asymmetric).

Final files, 512 or 1024 px square PNG, transparent background, under
`Textures/Things/Building/Risers/`:

```
AB_WallRiserPower_north.png    AB_WallRiserPower_south.png    AB_WallRiserPower_east.png
AB_WallRiserPipe_north.png     AB_WallRiserPipe_south.png     AB_WallRiserPipe_east.png
AB_WallRiserClimate_north.png  AB_WallRiserClimate_south.png  AB_WallRiserClimate_east.png
AB_WallRiserData_north.png     AB_WallRiserData_south.png     AB_WallRiserData_east.png
```

## What each face is in game

Rotation faces the wall; the sprite is drawn pushed 0.9 tiles onto the wall cell
(vanilla WallLamp mechanics, same offsets).

- **north** = the everyday view: the fixture mounted on the south face of a wall, seen
  head-on. This is the hero sprite; do it first, per type.
- **south** = the back view: the fixture mounted on the far side of a wall, only its
  top sliver visible over the wall's top edge.
- **east** = side profile against a wall to the right of the cell.

Expect one in-game check pass for vertical placement per face (their offsets ride the
wall art), same as the column art loop.

## Workflow (column-reference rail - USER'S CHOICE, session of §75 field test)

Only THREE full generations carry re-render risk: the power riser's north, south and
east faces, each with the POWER COLUMN winner attached as the style/palette reference
(plus optionally two VTEXE loose PNGs, workshop 2016436324:
`Furniture/Dresser_south.png`, `Power/ChemfuelPoweredGenerator.png`). Every other
asset (pipe/climate/data x 3 faces = 9) is a SILHOUETTE-LOCKED EDIT of the matching
power winner, with the same-type column attached for palette - the doctrine's proven
cheap kind.

Generate at 1K, expect 3-4 candidates per asset; drift is a reroll, not a prompt bug.
The in-chat prompt set delivered alongside this doc is the authoritative copy-paste
version of the prompts below.

## Stage 1: power riser, three full prompts

### AB_WallRiserPower_north (hero)

> A sprite for a colony management game, matching the attached reference sprites'
> painterly top-down style exactly. A compact wall-mounted electrical service fixture,
> drawn perfectly straight-on: left and right edges perfectly vertical and parallel,
> horizontal edges stay horizontal, no receding diagonals, no side faces, no
> perspective. No dark contour outlines, no comic or cel or sticker look, no hard
> shading bands, no rim light. Flat `#FF00FF` background, nothing crosses the
> fixture's outline.
>
> The fixture: a flat rectangular mounting plate in dark industrial steel, portrait
> orientation, occupying the central third of the canvas width and the lower two
> thirds of the canvas height. Painted onto the plate, adding no geometry and never
> crossing the plate's outline: one straight vertical strip in dull copper, one fifth
> of the plate's width, running the plate's full height slightly left of centre; a
> small square panel in darker steel at the plate's base, slightly wider than the
> strip, with two short diagonal painted marks; three small painted bolt heads down
> the plate's right side. Colours match the attached power column sprite's palette.

### AB_WallRiserPower_south (back sliver)

> Same style laws and background as before. The same fixture seen from behind a wall:
> only the plate's top edge shows, a low wide cap in dark industrial steel, the same
> width as the hero sprite's plate, about one quarter of the canvas tall, its bottom
> edge sitting at the vertical centre of the canvas, horizontally centred. Painted
> flat onto it: the copper strip's end emerging over the edge and turning down out of
> sight, and one painted seam line along the cap's top. Nothing else on the canvas.

### AB_WallRiserPower_east (side profile)

> Same style laws and background as before. The same fixture in side profile: the
> plate seen edge-on as a single narrow vertical bar in dark industrial steel, one
> sixth of the canvas wide and two thirds of the canvas tall, standing just left of
> the canvas centre, bottom-aligned with the hero sprite's plate. Painted flat onto
> its left face: a thin copper line running its full height. Only the bar is drawn;
> no wall, no ground, nothing else.

## Stage 2: type variants (small-change edits on each power winner, same chat)

Run per face, replacing only the decal set. Orientation guard stays: "Nothing in this
list changes the fixture's proportions or orientation."

- **Pipe:** "Replace the copper strip and base panel with: two parallel painted lines
  in dull brass running the plate's full height, each line with a rounded painted
  highlight; only the two lines read as rounded, the plate stays flat. At the base, a
  small painted square flange where the lines meet the floor edge. Match the attached
  pipe column sprite's palette."
- **Climate:** "Replace the copper strip and base panel with: one straight vertical
  strip in pale galvanized steel, one third of the plate's width, running the plate's
  full height, containing five short horizontal painted lines evenly spaced. Match
  the attached climate column sprite's palette." (Keep this exact geometry wording;
  category words like duct, vent, louvre or grille rotate the fixture.)
- **Data:** "Replace the copper strip and base panel with: one narrow straight
  vertical strip in dark slate, one eighth of the plate's width, running the plate's
  full height, and three tiny painted square dots in pale cyan ascending beside its
  top quarter. Match the attached data column sprite's palette."

## Repair lines (same chat, on a near-miss)

- Perspective drift: "Flatten this back to a straight-on view: left and right edges
  vertical and parallel, no side faces, no perspective, remove the dark contour
  outlines; keep the colours and painted details."
- Proportion drift: "You changed the fixture's proportions. Restore the exact
  portrait plate shape, same height and width, keeping the colours and painted
  details scaled back onto the original plate."
