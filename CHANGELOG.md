# Changelog

### Current Status

Player.log warnings caused by stale mod configurations have been addressed for all currently supported mod versions.

Remaining warnings fall into one of three categories:

- Stardeus Early Access research validation warnings.
- MolecularAssembler energy-to-material conversion validation warnings.
- Stardeus Core "Ability ID Work" warnings.

These are currently documented but intentionally not modified by the script.

## v1.1.0

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
