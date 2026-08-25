# Publish checklist (As above, So below II)

Workshop item **3776015553**. Rebuilt after the original checklist was lost with the
old tree. Work top to bottom; nothing here is optional.

> **⚠ UPLOADS GO THROUGH MODMIXER'S PUBLISH UTILITY, NOT THE IN-GAME UPLOADER.**
> The utility pushes **files + preview image only**. The description and the change
> notes are NOT uploaded by it: paste those into the Steam **web** editor yourself.

---

## 1. Code state

- [ ] Every window-5 item verified in game, or knowingly shipped unverified (record which).
- [ ] **Fresh RELEASE build.** `dotnet build -c Release` in `Source/`. The workspace
      normally holds DEV builds; do not ship one.
      `OutputPath` is pinned to `..\Assemblies\`, so Release overwrites the same dll.
- [ ] Build is **0 warnings, 0 errors**.
- [ ] `Assemblies/` contains **only** `AsAboveSoBelow.dll`. No `.pdb`
      (`DebugType=none` in the csproj already enforces this), no stray dlls.
- [ ] **Never ship `0Harmony.dll`.** Harmony is a mod dependency, declared in About.xml.

## 2. Log hygiene

- [ ] No unconditional `Log.Message` / `Log.Warning` on a path that runs in ordinary play.
      Dev instruments belong behind `ABMod.Settings.verboseLogging` (that is what
      `ABLog.Dev` does) or behind an explicit debug action.
      *Known past offender: `ABGenProfile.Report`, which dumped a 40-line warning block
      on every single map generation. Gated in window 5.*
- [ ] Kill-switch and watchdog warnings are fine: they are diagnostics for real faults.

## 3. Metadata

- [ ] `About/About.xml`: name, packageId `astryl.AsAboveSoBelow2`, `supportedVersions` = 1.6.
- [ ] Description is the USER's copy. Do not rewrite it during publish prep.
- [ ] Title carries its **two glyphs** (`◆ … ◆`).
- [ ] `About/PublishedFileId.txt` still reads `3776015553`. Losing this file publishes a
      SECOND, duplicate Workshop item.
- [ ] `About/Preview.png` present and current.
- [ ] `modDependencies` (Harmony) and `incompatibleWith` still accurate.

## 4. Strip before publishing (FOUR MOVES)

Move these OUT of the mod folder, publish, then move them back. **Move, never delete:**
`Source/` is the working tree.

1. [ ] `Source/`
2. [ ] `Tools/`
3. [ ] `docs/`  ← this file and `UPDATE_NOTES.txt` live here
4. [ ] `.git/`  (only if the tree has been re-initialised as a repo; absent right now)

Also confirm:
- [ ] `.modmixer/` is never uploaded (the utility excludes it, but eyeball the file list).
- [ ] No `Source/obj` or `Source/bin` anywhere in what goes up.

## 5. Publish

- [ ] Run the Modmixer publish utility.
- [ ] Steam **web** editor: paste `docs/UPDATE_NOTES.txt` into the Change Notes field.
- [ ] Steam **web** editor: description only if it actually changed.
- [ ] Move the four stripped folders back.
- [ ] Subscribe-and-load smoke test from the Workshop copy, not the workspace copy.

## 6. After

- [ ] Update the Schematic: mark the window shipped, open the next window's ledger.
