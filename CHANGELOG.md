# Changelog

### Current Status

All known load-blocking compatibility issues have been resolved for currently supported mods.

Remaining log entries are primarily:

- Stardeus Early Access / Core validation warnings.
- Runtime warnings requiring gameplay testing.
- Mod-specific balance or economic validation warnings.
- Experimental branch issues under active development.

These are documented and reviewed individually before being added to the repair process.

### Milestone

All currently supported mods now load successfully under
Stardeus v0.15 private branch testing.

Future updates will focus primarily on:

- Runtime testing
- Gameplay validation
- New schema changes introduced during v0.15 development
- Reduction of remaining non-critical warnings

## v2.0.1 Stardeus v0.15 Private/Beta

### Chromanite
- Adds missing GroupId fields to material definitions to restore inventory categorization.

## v2.0.0 Stardeus v0.15 Private/Beta

### Better Storage
- Repairs stale StorageCold research references.
- Removes blocked full Core definition overrides.

### Orbotron
- Removes deprecated Species.Type field.
- Removes deprecated SpawnRequirements.Check field.

### MetalHusksRevisited
- Repairs stale Planter research references.

### Molecular Assembler
- Removes deprecated Craftable.Level fields.
- Adds IgnoreEnergyOutput validation flag.
- Adds IgnoreMarket validation flag.

### Chromanite
- Removes deprecated MatType.NameKey fields.
- Removes deprecated Craftable.Level fields.
- Renames IgnoreStockMarket to IgnoreMarket.
- Removes deprecated EnergyOutputComment fields.
- Removes deprecated FavGroupId fields.
- Removes deprecated ProductOf array fields.
- Repairs IsTemperatureResistent -> IsTemperatureResistant typo.

### Androids Expanded
- Replaces deprecated Looks wrapper with direct SpeciesLooksDef format.
- Removes full ObeyChip Core override blocked by v0.15.
- Removes deprecated StasisWakeUpChance field.
- Restores Gearss -> Gears typo fix.
- Repairs MakeshiftJaw -> MakeShiftJaw casing mismatch.
- Removes deprecated Species.Type field safely.
- Removes deprecated NamePartsComment field.
- Removes deprecated Comment fields.
- Removes deprecated gender/attraction blocks.
- Temporarily removes deprecated AdjustsSkills fields pending investigation of the v0.15 replacement for body-part skill bonuses.

## v1.1.0 Stardeus v0.14 Public

### Androids Expanded
- Added Quantum Nexus support for Mind Transfer and Machine Learning.
- Added Quantum Nexus Core support for robotic lifeforms.
- Added PowerHand attack timing fix to eliminate charge-time warnings.
- Removed deprecated ObjNoFlash references.
- Improved MakeShiftJaw filename/content normalization validation output.

### Molecular Assembler
- Added ExtraInfo component restoration.
- Updated deprecated Flammable curve values to current Core format.
- Added missing BaseExplosionLevel property.
- Added missing TileTransform CoverPercent, InPort, and OutPort properties.
- Added IgnoreMarket support to MolecularAssemblable recipes.
- Documented intentional MolecularAssemblable validation warnings.

### Script Improvements
- Added per-mod enable/disable flags.
- Reorganized repairs into independently selectable modules.
- Improved validation and status output.
- Added additional comments and documentation.
- Maintained rerun-safe operation for all repairs.

### Validation
- Added detection of deprecated Flammable keys.
- Added reporting for unknown Flammable keys requiring investigation.
- Added research ID validation across supported mods.

### Notes
- Research validation warnings are intentionally ignored until Stardeus reaches Version 1.0.
- MolecularAssembler input/output validation warnings are intentional and currently require engine-level support to suppress cleanly.
- Core Ability ID "Work" warnings are considered a Stardeus Core issue and are not modified by this script.

## v1.0.0

- Initial public release.
- Added modular mod repair framework.
- Added Androids Expanded repairs.
- Added Molecular Assembler repairs.
- Added MetalHusks Revisited, Ironhusk, Chromanite, and Fast Ironhusk repairs.
- Added validation checks for research IDs and Flammable keys.
