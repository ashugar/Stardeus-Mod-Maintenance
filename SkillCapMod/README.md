# Skill Cap Mod

Adds **Game Menu → Settings → Skill Cap**, available before a save is loaded.

- Default maximum: `25` (Core behavior).
- Accepts whole numbers and scientific notation.
- `1e308` is the effectively-unlimited setting.
- True levels use arbitrary-size integers and are stored in the mod's save data.
- Core's byte field receives a safe compatibility projection, so it cannot overflow.
- Levels 1–50 retain their ordinary numeric scale. Above 50, effectiveness grows logarithmically: every additional gain remains positive, but increasingly large level multipliers are needed to make it noticeable.
- Core work-speed curves are extended beyond their former endpoint. Combat/stat curves continue to use their own authored limits on the logarithmic level scale.
- The existing `SkillLevelUpSpeed` tunable is still honored, so the installed Fast Level Up Skill mod can continue to multiply training speed.
- Job Matrix hover text uses the exact sidecar level, progress, and next level, so it agrees with the extended level shown in the cell instead of Core's projected byte.

The Job Matrix repair is optional and fail-closed. It installs only when the exact private `AddCellTooltip(BeingData, JobSlot, WJobMatrixPriority)` method, reviewed `Game.dll` module, raw method/closure IL, delegate assignment, and expected skill reads all match Core `0.15.0-rc335.3217`. A mismatch logs one warning and leaves Core's native tooltip in place; required progression and save patches remain active under their separate Harmony owner. Building a tooltip uses a mutation-free snapshot and cannot create, normalize, or save sidecar state.

Lowering the configured cap clamps higher skills when that save is next loaded (or immediately if a game is already open). Removing the mod leaves Core's compatibility value in the save; Stardeus will apply its own current cap on the next load.

## Reading the source

The code is deliberately commented as a reference project. A useful reading order is:

1. `SkillCapSettingsPage.cs` and `SkillCapSettings.cs` — native main-menu registration and global preferences.
2. `SkillCapMath.cs` and `SkillCurveMath.cs` — exact parsing, logarithmic scaling, and Core-compatible projection.
3. `SkillCapRuntime.cs` — the sidecar state that connects exact levels to live Core `JobSlot` objects.
4. `SkillCapPatches.cs` — the required Harmony boundaries where Core reads or changes skill state.
5. `SkillCapOptionalPatches.cs` — the separately owned, binary-guarded Job Matrix hover integration.
6. `SkillCapSys.cs` — Core lifecycle integration, independent patch-owner teardown, and additive per-save data.
7. `Tests/Program.cs` — dependency-free checks for the most important numeric guarantees.

The central compatibility rule is that the mod never changes Core's serialized byte field. It keeps the true level in mod-owned save data and writes a safe projection into Core, so untouched Core systems and other mods still receive a valid value. Harmony prefixes are used only where Core's byte-capped implementation must be replaced; postfixes are preferred where Core can perform its original validation and side effects first.

This is a proof-of-concept and reference implementation. The optional Job Matrix guard deliberately supports only the reviewed Core `0.15.0-rc335.3217` / `Game.dll` implementation; a later Core build must be re-reviewed before its fingerprints are updated. Static source review, a successful build, numeric tests, and matching binary guards do not prove that the changed tooltip was exercised in a fresh game. In-game hover checks, save/load, and long-run progression remain separate runtime validation steps.
